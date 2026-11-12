using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    private static ControlledOpenManifestDto WithWorkingFiles(ControlledOpenManifestDto source, IReadOnlyList<ControlledOpenFileDto> files) =>
        new ControlledOpenManifestDto { Id = source.Id, ProjectId = source.ProjectId, ProjectCode = source.ProjectCode,
            RootDocumentId = source.RootDocumentId, RootVersionId = source.RootVersionId, RootRevision = source.RootRevision,
            RootRelativePath = source.RootRelativePath, ForEdit = source.ForEdit, UsesLatestModelReferences = source.UsesLatestModelReferences,
            Files = files.ToList(), Warnings = source.Warnings,
            ReferenceIssues = OpenReferenceFindings.Merge(source.ReferenceIssues) };

    private static ControlledOpenManifestDto WorkingAcquisitionScope(ControlledOpenManifestDto source, ISet<Guid> drawings) =>
        WithWorkingFiles(source, source.Files.Where(file => DocumentKindFromPath(file.FileName) != CadDocumentKind.Drawing
            || drawings.Contains(file.DocumentId)).ToArray());

    private ControlledOpenFileDto CommittedAssemblySource(string directory, ControlledOpenManifestDto working, ControlledOpenManifestDto latest)
    {
        if (working.ProjectId != latest.ProjectId || working.RootDocumentId != latest.RootDocumentId
            || working.RootVersionId == latest.RootVersionId || !PathsEqual(working.RootRelativePath, latest.RootRelativePath)) return null;
        var source = working.Files.SingleOrDefault(file => file.IsRoot && file.DocumentId == working.RootDocumentId);
        var stored = latest.Files.SingleOrDefault(file => file.IsRoot && file.DocumentId == latest.RootDocumentId);
        if (source == null || stored == null || DocumentKindFromPath(source.FileName) != CadDocumentKind.Assembly
            || source.IsNativeIsolated || stored.IsNativeIsolated || source.NativeIsolation != null || stored.NativeIsolation != null
            || source.VersionId != working.RootVersionId || stored.VersionId != latest.RootVersionId || !WorkingHeadPathMatches(source, stored)) return null;
        var path = ControlledReferenceMaterializer.LocalPath(directory, source);
        return WorkingReuseBindingMatches(path, working.ProjectId, source) && WorkingReuseCommittedMemoryMatches(path, stored) ? source : null;
    }

    private static ControlledOpenManifestDto WithCommittedAssemblyRoot(ControlledOpenManifestDto working,
        ControlledOpenManifestDto latest, ISet<Guid> drawings)
    {
        var files = new List<ControlledOpenFileDto>();
        foreach (var file in WorkingAcquisitionScope(latest, drawings).Files)
        {
            var previous = working.Files.SingleOrDefault(candidate => candidate.DocumentId == file.DocumentId);
            // The root's submitted bytes are proved by the live receipt. Other
            // documents keep their existing binding and pass the normal checks.
            files.Add(file.IsRoot ? file : previous ?? file);
        }
        files.AddRange(working.Files.Where(file => DocumentKindFromPath(file.FileName) == CadDocumentKind.Drawing
            && !drawings.Contains(file.DocumentId)));
        return WithWorkingFiles(latest, files);
    }

    private ArchiveMemoryCopy VerifyCommittedAssemblySource(string directory, Guid projectId, ControlledOpenFileDto source, ControlledOpenFileDto stored)
    {
        var path = ControlledReferenceMaterializer.LocalPath(directory, source);
        if (!WorkingReuseBindingMatches(path, projectId, source) || !WorkingReuseCommittedMemoryMatches(path, stored))
            throw new InvalidOperationException("总装存档后，窗口或本地文件已发生变化。已保留图档，请核对后再获取权限。");
        return archiveMemoryCopies[path];
    }

    private IModelDoc2 SynchronizeCommittedAssemblyRoot(string directory, ControlledOpenManifestDto manifest,
        ControlledOpenFileDto source, ControlledOpenFileDto stored)
    {
        var path = ControlledReferenceMaterializer.LocalPath(directory, source);
        var workspace = PdmDocumentIdentityStore.CurrentWorkspaceRoot;
        if (string.IsNullOrWhiteSpace(manifest.ProjectCode)
            || !IsPathWithinDirectory(path, ControlledWorkspaceManager.ProjectViewDirectory(workspace, manifest.ProjectCode)))
            throw new InvalidOperationException("总装工作区路径需要核对，已保留窗口和图档，尚未获取编辑权限。");
        // Use the same backup and atomic replacement as post-archive cache sync.
        // The only loaded path eligible here is the root with exact live evidence.
        var copy = VerifyCommittedAssemblySource(directory, manifest.ProjectId, source, stored);
        void SynchronizeFile()
        {
            if (!ControlledWorkspaceManager.SynchronizeConfirmedCache(workspace, manifest.ProjectCode, path,
                stored.DocumentId, manifest.ProjectId, copy.CommittedVersion, copy.CopyPath, copy.SourceHash, Array.Empty<string>()))
                throw new IOException("当前文件身份或提交副本需要核对。");
        }
        try
        {
            try { SynchronizeFile(); }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33)
            {
                // SOLIDWORKS can keep a non-delete-sharing handle even in read-only
                // mode. Reattach in the original frame from the exact archived bytes.
                VerifyCommittedAssemblySource(directory, manifest.ProjectId, source, stored);
                if (!copy.Model.Visible || copy.Model.ForceReleaseLocks() != 1) throw;
                VerifyCommittedAssemblySource(directory, manifest.ProjectId, source, stored);
                SynchronizeFile();
                if (!copy.MatchesNativeState())
                    throw new IOException("重新载入前发现窗口有新的修改，已保留当前窗口。");
                if (copy.Model.ReloadOrReplace(true, null, true) != (int)swComponentReloadError_e.swReloadOkay)
                    throw new IOException("SolidWorks未能在原窗口载入已存档总装。");
            }
            var model = FindLoadedDocument(path);
            if (!ReferenceEquals(copy.Model, model) || !WorkingHeadDiskMatches(directory, manifest.ProjectId, stored)
                || (!model.IsOpenedReadOnly() && (!model.SetReadOnlyState(true) || !model.IsOpenedReadOnly())))
                throw new IOException("当前窗口和新工作基准需要核对。");
            LogOperation("原窗口获取权限前已同步刚存档的总装基准 file=" + path + " version=" + stored.VersionId);
            return model;
        }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is COMException)
        {
            LogDiagnostic("原窗口获取权限前同步已存档总装", error);
            throw new InvalidOperationException("暂时无法在原窗口同步已存档总装。当前窗口和图档已保留，请保留新增修改，再从工作区打开最新版本后获取权限。");
        }
    }

    private ControlledOpenManifestDto ReconcileRequestedWorkingArchives(string directory, ControlledOpenManifestDto previous,
        ControlledOpenManifestDto latest, ISet<Guid> drawings)
    {
        var reconciled = ReconcileCompletedWorkingArchives(directory, WorkingAcquisitionScope(previous, drawings), WorkingAcquisitionScope(latest, drawings));
        var files = reconciled.Files.Concat(previous.Files.Where(file => DocumentKindFromPath(file.FileName) == CadDocumentKind.Drawing
            && !drawings.Contains(file.DocumentId))).ToList();
        foreach (var drawing in latest.Files.Where(file => DocumentKindFromPath(file.FileName) == CadDocumentKind.Drawing
            && !drawings.Contains(file.DocumentId) && files.All(existing => existing.DocumentId != file.DocumentId)))
        {
            // Adopt only a confirmed local receipt; absent/unrelated drawings are
            // neither downloaded nor prerequisites for this model checkout.
            var completed = CompletedWorkingFile(directory, latest, drawing);
            if (completed != null) files.Add(completed);
        }
        return WithWorkingFiles(previous, files);
    }

    private async Task<ControlledOpenManifestDto> ReconcileStoredWorkingMembershipAsync(string directory,
        ControlledOpenManifestDto previous, ControlledOpenManifestDto latest, ISet<Guid> drawings)
    {
        if (previous.ProjectId != latest.ProjectId || previous.RootDocumentId != latest.RootDocumentId
            || previous.RootVersionId != latest.RootVersionId || !PathsEqual(previous.RootRelativePath, latest.RootRelativePath)
            || latest.Files.GroupBy(file => file.DocumentId).Any(group => group.Count() != 1)) return previous;
        var oldModels = previous.Files.Where(file => DocumentKindFromPath(file.FileName) != CadDocumentKind.Drawing)
            .Select(file => file.DocumentId).OrderBy(id => id).ToArray();
        var newModels = latest.Files.Where(file => DocumentKindFromPath(file.FileName) != CadDocumentKind.Drawing)
            .Select(file => file.DocumentId).OrderBy(id => id).ToArray();
        if (oldModels.SequenceEqual(newModels)) return previous;
        var oldRoot = previous.Files.SingleOrDefault(file => file.DocumentId == previous.RootDocumentId);
        var newRoot = latest.Files.SingleOrDefault(file => file.DocumentId == latest.RootDocumentId);
        if (oldRoot == null || newRoot == null || oldRoot.VersionId != newRoot.VersionId
            || !WorkingConfigurationMatches(oldRoot, newRoot)) return previous;
        var rootPath = ControlledReferenceMaterializer.LocalPath(directory, oldRoot);
        if (!WorkingReuseBindingMatches(rootPath, previous.ProjectId, oldRoot)
            || !ControlledReferenceMaterializer.Matches(rootPath, oldRoot)) return previous;
        if (!WorkingReuseFileBytesMatch(oldRoot, newRoot)
            && !WorkingReuseStoredSourceVersionMatches(oldRoot, newRoot,
                (await apiClient.GetVersionsAsync(newRoot.DocumentId, lifetime.Token)).FirstOrDefault(item => item.Id == newRoot.VersionId))) return previous;
        var files = new List<ControlledOpenFileDto>();
        foreach (var current in latest.Files)
        {
            var before = previous.Files.FirstOrDefault(file => file.DocumentId == current.DocumentId);
            var optionalDrawing = DocumentKindFromPath(current.FileName) == CadDocumentKind.Drawing && !drawings.Contains(current.DocumentId);
            if (optionalDrawing)
            {
                if (before != null) files.Add(before); // Do not replace unrelated local drawing windows or contents.
                else
                {
                    var completed = CompletedWorkingFile(directory, latest, current);
                    if (completed != null) files.Add(completed);
                }
                continue;
            }
            if (before == null || before.VersionId != current.VersionId)
            {
                var completed = CompletedWorkingFile(directory, latest, current);
                if (completed == null) return previous;
                files.Add(completed); continue;
            }
            if (!WorkingConfigurationMatches(before, current)
                || !string.Equals(before.RelativePath ?? before.FileName, current.RelativePath ?? current.FileName, StringComparison.OrdinalIgnoreCase)) return previous;
            var path = ControlledReferenceMaterializer.LocalPath(directory, before);
            // The caller verifies current bytes and any live draft before acquiring.
            // Preserve a child's own unsent edits while refreshing the parent's membership.
            if (!WorkingReuseBindingMatches(path, previous.ProjectId, before)) return previous;
            if (!WorkingReuseFileBytesMatch(before, current)
                && !WorkingReuseStoredSourceVersionMatches(before, current,
                    (await apiClient.GetVersionsAsync(current.DocumentId, lifetime.Token)).FirstOrDefault(item => item.Id == current.VersionId))) return previous;
            files.Add(before);
        }
        LogOperation("已按当前已存档总装基准同步工作清单 old=" + previous.Files.Count + " current=" + files.Count + " deletedLocalFiles=0");
        return WithWorkingFiles(previous, files);
    }

    private ControlledOpenFileDto CompletedWorkingFile(string directory, ControlledOpenManifestDto manifest, ControlledOpenFileDto file)
    {
        var path = ControlledWorkspaceManager.PlannedFilePath(directory, file);
        if (!File.Exists(path)) return null;
        if (!completedWorkingArchives.TryGetValue(path, out var receipt) || receipt.ProjectId != manifest.ProjectId
            || receipt.DocumentId != file.DocumentId || receipt.VersionId != file.VersionId || receipt.Username != authenticatedUsername
            || receipt.ServerScope != apiClient.EditJournalScope || !string.Equals(receipt.StoredSha256, file.Sha256, StringComparison.OrdinalIgnoreCase)
            || receipt.StoredLength != file.FileLength || !PdmDocumentIdentityStore.TryReadLocalMetadata(path, out var binding)
            || binding.DocumentId != receipt.DocumentId || binding.ProjectId != receipt.ProjectId || binding.VersionId != receipt.VersionId
            || binding.Sha256 != receipt.SourceSha256 || binding.FileLength != receipt.SourceLength
            || new FileInfo(path).Length != receipt.SourceLength || ComputeFileHash(path) != receipt.SourceSha256) return null;
        return new ControlledOpenFileDto { DocumentId = file.DocumentId, VersionId = file.VersionId, FileName = file.FileName,
            RelativePath = file.RelativePath, Configuration = file.Configuration, IsRoot = file.IsRoot, Revision = file.Revision,
            Sha256 = receipt.SourceSha256, FileLength = receipt.SourceLength, NativeIsolation = file.NativeIsolation, IsNativeIsolated = file.IsNativeIsolated };
    }

    private async Task RefreshCommittedWorkingMembershipsAsync(IEnumerable<Guid> documentIds)
    {
        var ids = new HashSet<Guid>(documentIds);
        foreach (var context in controlledOpenManifests.Where(pair => ids.Contains(pair.Value.RootDocumentId)).ToArray())
        {
            try
            {
                var latest = await apiClient.CreateControlledOpenManifestAsync(context.Value.RootDocumentId, null, false, true, lifetime.Token,
                    allowMissingComponents: true, useLatestModelReferences: context.Value.UsesLatestModelReferences);
                var reconciled = ReconcileRequestedWorkingArchives(context.Key, context.Value, latest, new HashSet<Guid>());
                controlledOpenManifests[context.Key] = await ReconcileStoredWorkingMembershipAsync(context.Key, reconciled, latest,
                    new HashSet<Guid>()); // A baseline update does not acquire or update any drawing.
            }
            catch (Exception error) { LogDiagnostic("图档已存档，工作清单稍后重试同步；原文件和窗口保留", error); }
        }
    }

    private async Task<string> WorkingReuseVerifiedDifferencesAsync(ControlledOpenManifestDto before, ControlledOpenManifestDto current)
    {
        var conflicts = new List<string>();
        foreach (var file in current.Files)
        {
            var original = before.Files.FirstOrDefault(item => item.DocumentId == file.DocumentId);
            if (original != null && original.VersionId == file.VersionId && WorkingConfigurationMatches(original, file)
                && string.Equals(original.FileName, file.FileName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(original.RelativePath ?? original.FileName, file.RelativePath ?? file.FileName, StringComparison.OrdinalIgnoreCase))
            {
                if (WorkingReuseFileBytesMatch(original, file)) continue;
                var exact = (await apiClient.GetVersionsAsync(file.DocumentId, lifetime.Token)).FirstOrDefault(item => item.Id == file.VersionId);
                if (WorkingReuseStoredSourceVersionMatches(original, file, exact)) continue;
            }
            conflicts.Add(file.FileName + "（基准需核对）");
        }
        conflicts.AddRange(before.Files.Where(file => !current.Files.Any(item => item.DocumentId == file.DocumentId))
            .Select(file => file.FileName + "（已不属于服务器当前装配结构，本地文件保留）"));
        if (before.RootVersionId != current.RootVersionId) conflicts.Insert(0, Path.GetFileName(before.RootRelativePath) + "（总装基准版本已变化）");
        return conflicts.Count == 0 ? "工作清单范围需核对" : string.Join("、", conflicts.Take(6));
    }
}
