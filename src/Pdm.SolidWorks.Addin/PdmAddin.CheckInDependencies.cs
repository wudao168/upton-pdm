using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    private IReadOnlyDictionary<string, string> ReadPdmResolvedDependencyAliases(CadTreeNode owner, Array saved,
        ICollection<string> scopePaths)
    {
        var scope = new HashSet<string>(scopePaths, StringComparer.OrdinalIgnoreCase);
        var paths = PdmDependencyEntries(saved, owner.FullPath).SelectMany(entry => entry.Value).ToArray();
        if (paths.All(scope.Contains)) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var previousDirectory = application.GetCurrentWorkingDirectory();
        try
        {
            // SearchFlag resolves relocated files; it can also change the native
            // current directory. Never leave that change in the user's session.
            var resolved = application.GetDocumentDependencies2(owner.FullPath, false, true, false) as Array;
            return BuildPdmDependencyAliases(saved, resolved, owner.FullPath, scope);
        }
        finally
        {
            if (!application.SetCurrentWorkingDirectory(previousDirectory ?? string.Empty))
                throw new InvalidOperationException("引用核对后未能恢复SolidWorks原工作目录，已停止存档，原文件和窗口均保留。");
        }
    }

    private static IReadOnlyList<KeyValuePair<string, string[]>> PdmDependencyEntries(Array values, string ownerPath)
    {
        if (values != null && values.Length % 2 != 0)
            throw new InvalidOperationException("无法完整读取图档引用清单，尚未上传：" + Path.GetFileName(ownerPath));
        var result = new List<KeyValuePair<string, string[]>>();
        for (var index = 0; values != null && index < values.Length; index += 2)
        {
            var key = Convert.ToString(values.GetValue(index));
            if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("原生引用名称为空，尚未上传：" + Path.GetFileName(ownerPath));
            var paths = (Convert.ToString(values.GetValue(index + 1)) ?? "").Split('|')
                .Select(path => ArchiveReferencePath.Resolve(path, Path.GetDirectoryName(ownerPath), "解析原生引用", ownerPath)).ToArray();
            result.Add(new KeyValuePair<string, string[]>(key, paths));
        }
        return result;
    }

    private static IReadOnlyDictionary<string, string> BuildPdmDependencyAliases(Array saved, Array resolved,
        string ownerPath, ISet<string> scope)
    {
        var current = PdmDependencyEntries(resolved, ownerPath).ToLookup(entry => entry.Key, StringComparer.OrdinalIgnoreCase);
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in PdmDependencyEntries(saved, ownerPath))
            foreach (var source in entry.Value.Where(path => !scope.Contains(path)))
            {
                // Match the native dependency key in both reads, then require the
                // exact resolved target to belong to the confirmed file inventory.
                // Merely finding a same-name file in a directory is insufficient.
                var targets = current[entry.Key].SelectMany(candidate => candidate.Value)
                    .Where(path => string.Equals(Path.GetFileName(path), Path.GetFileName(source), StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (targets.Length > 1)
                    throw new InvalidOperationException("同一原生引用解析到多个文件，未按同名自动替换：" + Path.GetFileName(ownerPath)
                        + " → " + Path.GetFileName(source));
                if (targets.Length != 1 || !scope.Contains(targets[0]) || !File.Exists(targets[0])) continue;
                if (aliases.TryGetValue(source, out var existing) && !string.Equals(existing, targets[0], StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("原生引用的实际目标存在冲突，尚未上传：" + Path.GetFileName(source));
                aliases[source] = targets[0];
            }
        return aliases;
    }

    private void ValidatePdmNativeDependencyPaths(IReadOnlyList<BatchOperationItem> selected, CancellationToken token)
    {
        var nodes = selected.SelectMany(item => EnumerateArchiveNodes(item.Node)).Concat(EnumerateArchiveNodes(currentTree))
            .Where(node => !string.IsNullOrWhiteSpace(node.FullPath)).ToArray();
        var scope = new HashSet<string>(nodes.Select(node => node.FullPath), StringComparer.OrdinalIgnoreCase);
        foreach (var owner in selected.Select(item => item.Node))
        {
            token.ThrowIfCancellationRequested();
            if (!new[] { ".SLDPRT", ".SLDASM", ".SLDDRW" }.Contains(Path.GetExtension(owner.FullPath), StringComparer.OrdinalIgnoreCase)
                || FindLoadedDocument(owner.FullPath)?.GetSaveFlag() == true) continue;
            var saved = application.GetDocumentDependencies2(owner.FullPath, false, false, false) as Array;
            var aliases = ReadPdmResolvedDependencyAliases(owner, saved, scope);
            foreach (var source in PdmDependencyEntries(saved, owner.FullPath).SelectMany(entry => entry.Value))
            {
                if (scope.Contains(source) || aliases.ContainsKey(source) || File.Exists(source)
                    || TryGetRecoveredArchiveReference(owner.FullPath, source, out _)
                    || NativeArchiveManifest.VerifiedEmbeddedReferences(owner, new[] { source }).Contains(source)
                    || nodes.Any(node => PathsEqual(node.SourceReferencePath, source))
                    || new SolidWorksReferenceTreeScanner(application).IsVerifiedBrokenContextReference(owner.FullPath, source)) continue;
                if (RecordMissingSourceWarning(owner, source)) continue;
                var loaded = FindLoadedDocument(owner.FullPath);
                if (loaded != null && RecordMissingSourceWarning(owner, source, designSources: NativeDesignDependencies.ReadManaged(loaded))) continue;
                throw new InvalidOperationException("存档前引用检查未通过：" + owner.FileName + " → " + Path.GetFileName(source)
                    + "，原生解析未找到本次已确认的文件。尚未进行图档准备或上传，请先定位该引用并保存。");
            }
        }
    }

    // Check in saved bytes and a controlled reference graph. Reading dependencies
    // must not open, rebuild, break links, rename or save the user's CAD files.
    private async Task<IReadOnlyList<BatchOperationItem>> CompletePdmCheckInDependenciesAsync(
        IReadOnlyList<BatchOperationItem> selected, Guid projectId, CancellationToken token)
    {
        var items = selected.ToList();
        var nodes = selected.SelectMany(item => EnumerateArchiveNodes(item.Node))
            .Concat(EnumerateArchiveNodes(currentTree))
            .Where(node => node.Status != CadReferenceStatus.Virtual && !node.IsStoredImport
                && !string.IsNullOrWhiteSpace(node.FullPath))
            .GroupBy(node => Path.GetFullPath(node.FullPath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(node => node.DocumentId.HasValue).First(),
                StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<CadTreeNode>(selected.Select(item => item.Node));
        var inspected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<DocumentDto> projectDocuments = null;
        var scanner = new SolidWorksReferenceTreeScanner(application);
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var owner = pending.Dequeue();
            if (!inspected.Add(owner.FullPath)) continue;
            if (!new[] { ".SLDPRT", ".SLDASM", ".SLDDRW" }
                .Contains(Path.GetExtension(owner.FullPath), StringComparer.OrdinalIgnoreCase)) continue;
            owner.ReferenceWarnings.Clear();
            ArchiveSession.Current?.ReportFile(0, 0, owner.FileName, "检入前正在核对受控引用；保留原文件和设计关联…");
            var before = ArchiveFileFingerprint.Read(owner.FullPath);
            var values = application.GetDocumentDependencies2(owner.FullPath, false, false, false) as Array;
            var nativeAliases = ReadPdmResolvedDependencyAliases(owner, values, nodes.Keys);
            if (values != null && values.Length % 2 != 0)
                throw new InvalidOperationException("无法完整读取检入文件的引用清单，尚未上传：" + owner.FileName);
            var references = (values ?? Array.Empty<object>()).Cast<object>().Where((value, index) => index % 2 == 1)
                .SelectMany(value => (Convert.ToString(value) ?? "").Split('|')).Where(value => !string.IsNullOrWhiteSpace(value)).ToList();
            var loaded = FindLoadedDocument(owner.FullPath);
            var designSources = loaded == null ? Array.Empty<string>() : NativeDesignDependencies.ReadManaged(loaded).ToArray();
            if (loaded != null)
            {
                references.AddRange(designSources);
                references.AddRange(ArchiveResourceIsolation.ReadLinkedSources(loaded,
                    (source, reason) => RecordReferenceWarning(owner, source, reason)));
            }
            references.AddRange(owner.EmbeddedDesignDependencies);
            foreach (var source in references.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var path = ArchiveReferencePath.Resolve(source, Path.GetDirectoryName(owner.FullPath), "检入引用核对", owner.FullPath);
                if (TryGetRecoveredArchiveReference(owner.FullPath, path, out var recovered)) path = recovered;
                if (nativeAliases.TryGetValue(path, out var actual))
                {
                    PluginDiagnosticLog.Record("检入引用解析", owner.FullPath + "；保存地址=" + path + "；原生解析目标=" + actual);
                    path = actual;
                }
                if (NativeArchiveManifest.IsInterconnectCachePath(path) || IsSolidWorksTemporaryVirtualComponentPath(path))
                {
                    // Embedded content belongs to its native parent, not a separate
                    // file registration. Its ownership is checked by the scanner.
                    if (!NativeArchiveManifest.VerifiedEmbeddedReferences(owner, new[] { path }).Contains(path)
                        && !EnumerateArchiveNodes(owner).Any(node => PathsEqual(node.FullPath, path)
                        && (node.Status == CadReferenceStatus.Virtual || node.IsStoredImport)))
                        throw new InvalidOperationException("引用临时组件的保存归属尚未确认，未开始上传：" + owner.FileName + " → " + Path.GetFileName(path));
                    continue;
                }
                if (PathsEqual(owner.FullPath, path)) continue;
                nodes.TryGetValue(path, out var dependency);
                // A recorded occurrence gives an exact source-to-working-file mapping;
                // a same-name file by itself is never an identity proof.
                if (dependency == null)
                    dependency = nodes.Values.FirstOrDefault(node => !string.IsNullOrWhiteSpace(node.SourceReferencePath)
                        && PathsEqual(node.SourceReferencePath, path));
                if (dependency == null && owner.DocumentId.HasValue)
                {
                    var controlled = controlledOpenManifests.Where(context => context.Value.ProjectId == projectId
                        && context.Value.Files.Any(file => file.DocumentId == owner.DocumentId
                            && PathsEqual(owner.FullPath, Path.Combine(context.Key, file.RelativePath ?? file.FileName))))
                        .SelectMany(context => context.Value.Files.Where(file => string.Equals(file.FileName, Path.GetFileName(path), StringComparison.OrdinalIgnoreCase))
                            .Select(file => new { File = file, Path = Path.Combine(context.Key, file.RelativePath ?? file.FileName) }))
                        .Where(candidate => nodes.TryGetValue(candidate.Path, out var local) && local.DocumentId == candidate.File.DocumentId)
                        .GroupBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToArray();
                    if (controlled.Length == 1) dependency = nodes[controlled[0].Path];
                }
                if (dependency == null && !File.Exists(path))
                {
                    if (scanner.IsVerifiedBrokenContextReference(owner.FullPath, path)) continue;
                    if (RecordMissingSourceWarning(owner, path, designSources: designSources)) continue;
                    PluginDiagnosticLog.Record("检入缺失依赖", owner.FullPath + " → " + path);
                    throw new InvalidOperationException("检入文件仍引用未收集的文件，尚未上传：" + owner.FileName
                        + " → " + Path.GetFileName(path) + "。请在SolidWorks中定位该引用并保存后重试；详细路径已记录在日志中。");
                }
                if (dependency != null && !dependency.DocumentId.HasValue
                    && dependency.Role == CadReferenceRole.ExternalFileDependency && !File.Exists(dependency.FullPath)
                    && RecordMissingSourceWarning(owner, path, designSources: designSources)) continue;
                if (dependency == null)
                {
                    dependency = new CadTreeNode { FullPath = path, SourceReferencePath = path, FileName = Path.GetFileName(path),
                        DisplayName = Path.GetFileNameWithoutExtension(path), DrawingNumber = Path.GetFileNameWithoutExtension(path),
                        Kind = DocumentKindFromPath(path), Role = CadReferenceRole.ExternalFileDependency,
                        InstancePath = owner.InstancePath + "/受控引用/" + Path.GetFileName(path) };
                    if (PdmDocumentIdentityStore.TryReadLocalMetadata(path, out var binding))
                    {
                        if (binding.ProjectId != projectId)
                            throw new InvalidOperationException("依赖文件属于其他PLM项目，不能自动改归属：" + dependency.FileName);
                        projectDocuments = projectDocuments ?? await apiClient.GetDocumentsAsync(projectId, token);
                        var registered = projectDocuments.FirstOrDefault(document => document.Id == binding.DocumentId);
                        if (registered == null) throw new InvalidOperationException("依赖文件的受控身份已失效，请从项目工作区重新获取：" + dependency.FileName);
                        ApplyRegisteredDocumentToMatchingInstances(dependency, registered);
                        dependency.OpenedVersionId = binding.VersionId;
                        dependency.OpenedRevision = dependency.CurrentRevision = dependency.Revision = binding.Revision;
                    }
                    nodes.Add(path, dependency);
                }
                if (!File.Exists(dependency.FullPath) || new FileInfo(dependency.FullPath).Length == 0)
                    throw new InvalidOperationException("检入所需的依赖文件缺失或为空，尚未上传：" + dependency.FileName);
                if (!owner.Children.Concat(owner.ReferencedModels).Any(node => PathsEqual(node.FullPath, dependency.FullPath)))
                {
                    var reference = new CadTreeNode { FullPath = dependency.FullPath, SourceReferencePath = source,
                        FileName = dependency.FileName, DisplayName = dependency.DisplayName, DrawingNumber = dependency.DrawingNumber,
                        Kind = dependency.Kind, Role = CadReferenceRole.ExternalFileDependency, Configuration = dependency.Configuration,
                        InstancePath = owner.InstancePath + "/受控引用/" + dependency.FileName };
                    CopyPdmIdentity(dependency, reference);
                    reference.OpenedVersionId = dependency.OpenedVersionId;
                    owner.Children.Add(reference);
                }
                // Existing controlled references keep their recorded version.
                // Unchanged dependencies never gain a checkout or a new version.
                var alreadySelected = items.Any(item => PathsEqual(item.Node.FullPath, dependency.FullPath));
                var locallyChanged = dependency.DocumentId.HasValue && PdmDocumentIdentityStore.TryReadLocalMetadata(dependency.FullPath, out var baseline)
                    && baseline.DocumentId == dependency.DocumentId && baseline.VersionId.HasValue
                    && !string.Equals(ArchiveFileFingerprint.Read(dependency.FullPath), baseline.Sha256, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(ArchiveFileFingerprint.Read(dependency.FullPath), baseline.ResolvedReferenceSha256, StringComparison.OrdinalIgnoreCase);
                if (!alreadySelected && locallyChanged && !IsCheckedOutByCurrentUser(dependency))
                    throw new InvalidOperationException("依赖文件有未提交修改但尚未获取编辑权限，已停止检入并保留文件：" + dependency.FileName);
                if (!alreadySelected && (!dependency.DocumentId.HasValue || locallyChanged))
                {
                    items.Add(new BatchOperationItem(dependency, 1));
                    pending.Enqueue(dependency);
                    LogOperation("检入收集新增或已修改依赖：" + owner.FileName + " → " + dependency.FileName);
                }
                if (nodes.Count > 2000)
                    throw new InvalidOperationException("检入依赖超过2000份，请核对文件引用范围；尚未上传任何文件。");
            }
            if (!string.Equals(before, ArchiveFileFingerprint.Read(owner.FullPath), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("文件在引用核对期间发生变化，尚未上传，请保存后重新检查：" + owner.FileName);
            await Task.Yield();
        }
        ValidateBatchFileNames(items);
        return DistinctBatchItems(items);
    }
}
