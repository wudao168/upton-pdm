using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    private async Task PrepareRetainedLocalReferencesAsync(string rootPath, ControlledOpenManifestDto manifest)
    {
        manifest.RetainedLocalReferences.Clear();
        var owners = manifest.Files.Where(file => manifest.ResumedDocumentIds.Contains(file.DocumentId)
            && manifest.RetainedWorkingHashes.ContainsKey(file.DocumentId)
            && DocumentKindFromPath(file.FileName) != CadDocumentKind.Part).ToArray();
        if (owners.Length == 0) return;
        var directory = Path.GetDirectoryName(rootPath);
        var known = new HashSet<string>(manifest.Files.Select(file => file.FileName), StringComparer.OrdinalIgnoreCase);
        var pending = new Dictionary<string, RetainedLocalReference>(StringComparer.OrdinalIgnoreCase);
        var identities = new Dictionary<string, PdmControlledFileMetadata>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void RequireOwner(ControlledOpenFileDto file)
        {
            var path = ControlledReferenceMaterializer.LocalPath(directory, file);
            if (!localEdits.TryGetValue(file.DocumentId, out var edit) || edit.SessionId != checkoutSessionId
                || edit.Closed || edit.DiscardPending || edit.PendingClose || edit.RecoveryRequired
                || !PathsEqual(edit.SourcePath, path) || !WorkingReuseBindingMatches(path, manifest.ProjectId, file)
                || !File.Exists(path) || !string.Equals(ComputeFileHash(path), manifest.RetainedWorkingHashes[file.DocumentId], StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("恢复新增引用时原编辑基准发生变化，文件和草稿已保留：" + file.FileName);
        }
        async Task Read(string owner, ControlledOpenFileDto archivedOwner = null)
        {
            if (!visited.Add(owner)) return;
            HashSet<string> archivedReferences = null;
            foreach (var source in PdmDependencyEntries(application.GetDocumentDependencies2(owner, false, false, false) as Array, owner)
                .SelectMany(entry => entry.Value))
            {
                if (known.Contains(Path.GetFileName(source)) || IsSolidWorksTemporaryVirtualComponentPath(source)
                    || NativeArchiveManifest.IsInterconnectCachePath(source)
                    || !new[] { ".SLDPRT", ".SLDASM" }.Contains(Path.GetExtension(source), StringComparer.OrdinalIgnoreCase)) continue;
                if (!File.Exists(source))
                {
                    // A missing entry in the server file list is not evidence of
                    // a locally added component. Compare exact native references
                    // against this owner's verified archive, never a basename or
                    // a findings file produced from the edited working copy.
                    if (archivedOwner != null)
                    {
                        if (archivedReferences == null)
                            archivedReferences = await ReadArchivedDirectReferencesAsync(archivedOwner);
                        RequireOwner(archivedOwner);
                        if (archivedReferences.Any(path => PathsEqual(path, source)))
                        {
                            RecordArchivedMissingReference(manifest, owner, source);
                            continue;
                        }
                        var toolbox = FindArchivedToolboxTemplate(source, archivedReferences);
                        if (toolbox != null)
                        {
                            RecordArchivedMissingReference(manifest, owner, source, toolbox);
                            continue;
                        }
                    }
                    throw new InvalidOperationException("上次编辑新增的本地引用尚未入库，原文件缺失，草稿已保留。请恢复该原文件后重试：" + source);
                }
                if (manifest.RequireWorkspaceRetainedReferences && !IsPathWithinDirectory(source, directory))
                    throw new InvalidOperationException("已保存的装配引用位于当前受控工作区之外，请核对后重新打开：" + source);
                ControlledCacheMaintenance.RequireOrdinaryPath(source, manifest.RequireWorkspaceRetainedReferences ? directory : Path.GetPathRoot(source));
                if (PdmDocumentIdentityStore.TryReadLocalMetadata(source, out var binding))
                {
                    if (binding.ProjectId != manifest.ProjectId || binding.VersionId.HasValue && !IsPathWithinDirectory(source, directory))
                        throw new InvalidOperationException("上次编辑引用了已登记但不在当前服务器清单中的图档，请从PLM获取并核对其版本，未将其当作新增文件：" + Path.GetFileName(source));
                    identities[Path.GetFileName(source)] = binding;
                }
                var record = new RetainedLocalReference { SourcePath = Path.GetFullPath(source), Sha256 = ComputeFileHash(source), FileLength = new FileInfo(source).Length,
                    RequireExactDeferredPath = manifest.RequireWorkspaceRetainedReferences };
                if (pending.TryGetValue(record.FileName, out var prior))
                {
                    if (!PathsEqual(prior.SourcePath, record.SourcePath))
                        throw new InvalidOperationException("本地编辑存在不同目录的同名新增引用，已保留所有文件，请核对后恢复：" + record.FileName);
                    continue;
                }
                var peer = Path.GetFullPath(Path.Combine(directory, record.FileName));
                if (!PathsEqual(peer, source) && File.Exists(peer))
                {
                    ControlledCacheMaintenance.RequireOrdinaryPath(peer, directory);
                    if (PdmDocumentIdentityStore.TryReadLocalMetadata(peer, out var peerBinding)
                        || new FileInfo(peer).Length != record.FileLength || !string.Equals(ComputeFileHash(peer), record.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("工作区存在不同内容或已登记的同名新增文件，已保留两份文件，请核对：" + record.FileName);
                    record.WorkspacePath = peer;
                }
                pending.Add(record.FileName, record);
                if (Path.GetExtension(source).Equals(".SLDASM", StringComparison.OrdinalIgnoreCase)) await Read(source);
            }
        }
        foreach (var file in owners) { RequireOwner(file); await Read(ControlledReferenceMaterializer.LocalPath(directory, file), file); }
        foreach (var file in owners) RequireOwner(file);
        if (pending.Count == 0) return;
        var documents = await apiClient.GetDocumentsAsync(manifest.ProjectId, lifetime.Token);
        foreach (var record in pending.Values)
        {
            var document = documents.FirstOrDefault(item => string.Equals(item.FileName, record.FileName, StringComparison.OrdinalIgnoreCase));
            identities.TryGetValue(record.FileName, out var binding);
            if (binding != null && (document == null || document.Id != binding.DocumentId || document.ProjectId != manifest.ProjectId))
                throw new InvalidOperationException("新增引用的项目或图档身份不一致，文件和草稿已保留：" + record.FileName);
            if (document?.StoredVersionCount > 0)
            {
                if (binding?.VersionId == null || document.Kind != (int)CadDocumentKind.Part || !IsPathWithinDirectory(record.SourcePath, directory))
                    throw new InvalidOperationException("恢复引用与已入库图档同名，请从PLM获取其工作版本，未按新图档处理：" + record.FileName);
                var version = (await apiClient.GetVersionsAsync(document.Id, lifetime.Token)).FirstOrDefault();
                if (version == null || version.Id != binding.VersionId || !VersionMatchesLocalFile(version, record.SourcePath, record.Sha256))
                    throw new InvalidOperationException("新装入的PLM零件不是已核对的当前版本，文件和草稿已保留：" + record.FileName);
                record.Managed = true;
                localEdits.TryGetValue(document.Id, out var edit);
                record.Editable = WorkingReuseDraftMatches(record.SourcePath, new ControlledOpenFileDto
                    { DocumentId = document.Id, VersionId = version.Id }, document, edit, checkoutSessionId, authenticatedUsername);
            }
            else if (document != null && !string.IsNullOrWhiteSpace(document.CheckedOutBy)
                && (!string.Equals(document.CheckedOutBy, authenticatedUsername, StringComparison.OrdinalIgnoreCase) || document.CheckoutSessionId != checkoutSessionId))
                throw new InvalidOperationException("新增引用的登记记录正在其他会话使用，文件和草稿已保留：" + record.FileName);
        }
        foreach (var file in owners) RequireOwner(file);
        foreach (var record in pending.Values)
        {
            if (record.Paths.Any(path => !record.Matches(path)))
                throw new InvalidOperationException("新增本地引用在恢复期间发生变化，文件和草稿已保留，请重试：" + record.FileName);
            manifest.RetainedLocalReferences.Add(record.FileName, record);
        }
        manifest.LocalCompatibilityNotes.Add("已恢复 " + pending.Count + " 个本次编辑新增的引用；新增图档或装配关系仍待确认存档，未生成新的服务器版本。");
        LogOperation("恢复已保存编辑中的本地新增引用 files=" + pending.Count + " registered=0 uploaded=0");
    }

    private async Task<HashSet<string>> ReadArchivedDirectReferencesAsync(ControlledOpenFileDto file)
    {
        var canonical = await DownloadArchivedReferenceBaselineAsync(file);
        return ReadArchivedDirectReferencePaths(canonical, file.FileName);
    }

    private HashSet<string> ReadArchivedDirectReferencePaths(string canonical, string fileName)
    {
        var dependencies = application.GetDocumentDependencies2(canonical, false, false, false) as Array;
        if (dependencies == null)
            throw new InvalidOperationException("尚未读到存档基准的引用清单，无法确认缺件是否为本次新增；工作文件与草稿均保留：" + fileName);
        return new HashSet<string>(PdmDependencyEntries(dependencies, canonical)
            .SelectMany(entry => entry.Value), StringComparer.OrdinalIgnoreCase);
    }

    private async Task<string> DownloadArchivedReferenceBaselineAsync(ControlledOpenFileDto file)
    {
        var stagingId = Guid.NewGuid().ToString("N");
        var staging = ManagedCadPaths.ChooseStagingDirectory(
            Path.Combine(ArchiveNativeProcessWorkspace.ReferenceBaselineDirectory, stagingId),
            stagingId, new[] { file.RelativePath ?? file.FileName });
        var canonical = await apiClient.DownloadControlledOpenFileAsync(file, staging, lifetime.Token);
        if (!ControlledReferenceMaterializer.Matches(canonical, file))
            throw new InvalidOperationException("缺失引用的存档基准尚未核对，工作文件与草稿均保留：" + file.FileName);
        return canonical;
    }

    private string FindArchivedToolboxTemplate(string missing, IEnumerable<string> archivedReferences)
    {
        if (!string.Equals(Path.GetExtension(missing), ".SLDPRT", StringComparison.OrdinalIgnoreCase)) return null;
        var browser = SolidWorksToolboxPaths.BrowserDirectory(application);
        if (browser == null) return null;
        var configuration = Path.GetFileNameWithoutExtension(missing);
        var matched = archivedReferences.Where(path => SolidWorksToolboxPaths.IsBrowserPart(path, browser) && File.Exists(path))
            .Where(path => ((application.GetConfigurationNames(path) as Array)?.OfType<string>() ?? Enumerable.Empty<string>())
                .Contains(configuration, StringComparer.OrdinalIgnoreCase)).Take(2).ToArray();
        return matched.Length == 1 ? matched[0] : null;
    }

    private void RecordArchivedMissingReference(ControlledOpenManifestDto manifest, string owner, string source, string toolbox = null)
    {
        if (File.Exists(source) || Directory.Exists(source) || FindLoadedDocument(source) != null)
            throw new IOException("存档引用未在受控清单中，原路径存在文件或已打开窗口，未作为缺件放行：" + Path.GetFileName(source));
        if (toolbox != null)
        {
            if (!manifest.VerifiedMissingToolboxReferences.TryGetValue(owner, out var references))
                manifest.VerifiedMissingToolboxReferences[owner] = references = new List<ArchiveMissingReferencePolicy.ToolboxReferenceEvidence>();
            references.Add(new ArchiveMissingReferencePolicy.ToolboxReferenceEvidence(source, toolbox, Path.GetFileName(owner), true));
        }
        manifest.ReferenceIssues = OpenReferenceFindings.Merge((manifest.ReferenceIssues ?? new List<OpenReferenceIssueDto>())
            .Concat(new[] { new OpenReferenceIssueDto { FileName = Path.GetFileName(source), OriginalPath = source,
                InstancePath = Path.GetFileName(owner), IsToolbox = toolbox != null, Reason = toolbox == null ? "存档基准中的引用文件缺失，已保留原引用，未猜测替代文件。"
                    : "Toolbox原文件缺失，已核对存档模板的配置并保留原引用。" } }));
        LogOperation("已核对存档原有缺失引用，未认定为本次编辑新增：" + source);
    }
}
