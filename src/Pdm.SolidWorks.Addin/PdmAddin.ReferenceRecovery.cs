using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SolidWorks.Interop.swconst;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    private sealed class RecoveredArchiveReference
    {
        internal string OwnerHash, Path, Hash, Scope;
        internal string Owner, Source;
        internal CadTreeNode Node;
    }
    private readonly Dictionary<string, RecoveredArchiveReference> recoveredArchiveReferences =
        new Dictionary<string, RecoveredArchiveReference>(StringComparer.OrdinalIgnoreCase);

    private bool TryGetRecoveredArchiveReference(string owner, string source, out string path)
    {
        path = null;
        if (!recoveredArchiveReferences.TryGetValue(owner + "|" + source, out var found)) return false;
        if (found.Scope != apiClient?.EditJournalScope || !File.Exists(found.Path)
            || found.OwnerHash != ArchiveFileFingerprint.Read(owner) || found.Hash != ArchiveFileFingerprint.Read(found.Path))
            throw new InvalidOperationException("自动补齐后图档或引用文件发生变化，请重新检查存档范围：" + Path.GetFileName(owner));
        path = found.Path;
        return true;
    }

    private async Task<IReadOnlyList<BatchOperationItem>> RecoverPdmReferencesAsync(
        IReadOnlyList<BatchOperationItem> selected, Guid projectId, CancellationToken token)
    {
        var items = selected.ToList();
        var nodes = selected.SelectMany(item => EnumerateArchiveNodes(item.Node)).Concat(EnumerateArchiveNodes(currentTree))
            .Where(node => !string.IsNullOrWhiteSpace(node.FullPath)).GroupBy(node => node.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<CadTreeNode>(selected.Select(item => item.Node));
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var problems = new List<string>();
        var local = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var indexedRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<ArchiveRemoteReference> remote = null;
        var imported = new Dictionary<string, CadTreeNode>(StringComparer.OrdinalIgnoreCase);
        var materialized = new Dictionary<string, Dictionary<string, CadTreeNode>>(StringComparer.OrdinalIgnoreCase);
        var packageOwners = new Dictionary<string, Dictionary<string, CadTreeNode>>(StringComparer.OrdinalIgnoreCase);
        string importRoot = null;
        string ImportRoot()
        {
            if (importRoot == null)
            {
                var projectCode = availableProjects.FirstOrDefault(project => project.Id == projectId)?.Code ?? projectId.ToString("N");
                importRoot = Path.Combine(ControlledWorkspaceManager.ProjectViewDirectory(controlledWorkspace.WorkspaceRoot, projectCode),
                    "引用补齐", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(importRoot);
            }
            return importRoot;
        }
        void Bind(CadTreeNode owner, string source, CadTreeNode target, bool recovered = false)
        {
            if (!owner.Children.Concat(owner.ReferencedModels).Any(node => PathsEqual(node.FullPath, target.FullPath)))
            {
                var link = new CadTreeNode { FullPath = target.FullPath, SourceReferencePath = source, FileName = target.FileName,
                    DisplayName = target.DisplayName, DrawingNumber = target.DrawingNumber, Kind = target.Kind,
                    Role = CadReferenceRole.ExternalFileDependency, Configuration = target.Configuration,
                    InstancePath = owner.InstancePath + "/受控引用/" + target.FileName };
                CopyPdmIdentity(target, link); link.OpenedVersionId = target.OpenedVersionId;
                owner.Children.Add(link);
            }
            if (recovered)
                recoveredArchiveReferences[owner.FullPath + "|" + source] = new RecoveredArchiveReference {
                    Owner = owner.FullPath, Source = source, Node = target,
                    OwnerHash = ArchiveFileFingerprint.Read(owner.FullPath), Path = target.FullPath,
                    Hash = ArchiveFileFingerprint.Read(target.FullPath), Scope = apiClient?.EditJournalScope };
            nodes[target.FullPath] = target;
            if (owner.NativeFileScopePaths != null) owner.NativeFileScopePaths.Add(target.FullPath);
            if (!target.DocumentId.HasValue && !items.Any(item => PathsEqual(item.Node.FullPath, target.FullPath)))
                items.Add(new BatchOperationItem(target, 1));
            var unchangedControlled = target.DocumentId.HasValue && PdmDocumentIdentityStore.TryReadLocalMetadata(target.FullPath, out var baseline)
                && baseline.DocumentId == target.DocumentId && baseline.VersionId.HasValue
                && (string.Equals(ArchiveFileFingerprint.Read(target.FullPath), baseline.Sha256, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(ArchiveFileFingerprint.Read(target.FullPath), baseline.ResolvedReferenceSha256, StringComparison.OrdinalIgnoreCase));
            if (recovered || !unchangedControlled || items.Any(item => PathsEqual(item.Node.FullPath, target.FullPath))) pending.Enqueue(target);
            if (recovered)
                LogOperation("引用自动补齐：" + owner.FileName + " → " + target.FileName + "；来源=" + source + "；受控副本=" + target.FullPath);
        }
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var owner = pending.Dequeue();
            if (!visited.Add(owner.FullPath) || !IsPdmNativeFile(owner.FullPath)) continue;
            owner.ReferenceWarnings.Clear();
            // Unsaved structures are checked again after the normal save step.
            if (FindLoadedDocument(owner.FullPath)?.GetSaveFlag() == true) continue;
            ArchiveSession.Current?.ReportFile(0, 0, owner.FileName, "正在批量查找并核对图档引用…");
            var saved = application.GetDocumentDependencies2(owner.FullPath, false, false, false) as Array;
            var aliases = ReadPdmResolvedDependencyAliases(owner, saved, nodes.Keys);
            var sources = PdmDependencyEntries(saved, owner.FullPath).SelectMany(entry => entry.Value).ToList();
            var loaded = FindLoadedDocument(owner.FullPath);
            var designSources = loaded == null ? Array.Empty<string>() : NativeDesignDependencies.ReadManaged(loaded).ToArray();
            sources.AddRange(designSources);
            sources.AddRange(owner.EmbeddedDesignDependencies);
            foreach (var source in sources.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                token.ThrowIfCancellationRequested();
                var path = source;
                if (packageOwners.TryGetValue(owner.FullPath, out var fixedFiles)
                    && fixedFiles.TryGetValue(Path.GetFileName(path), out var fixedReference)
                    && !PathsEqual(owner.FullPath, fixedReference.FullPath))
                { Bind(owner, source, fixedReference, true); continue; }
                if (aliases.TryGetValue(path, out var native)) path = native;
                if (PathsEqual(path, owner.FullPath)) continue;
                if (NativeArchiveManifest.VerifiedEmbeddedReferences(owner, new[] { path }).Contains(path)
                    || nodes.TryGetValue(path, out var embedded) && (embedded.Status == CadReferenceStatus.Virtual || embedded.IsStoredImport)) continue;
                if (nodes.TryGetValue(path, out var known) && File.Exists(path))
                { Bind(owner, source, known); continue; }
                if (NativeArchiveManifest.VerifiedEmbeddedReferences(owner, new[] { path }).Contains(path)
                    || NativeArchiveManifest.IsInterconnectCachePath(path) && nodes.Values.Any(node => node.IsStoredImport && PathsEqual(node.FullPath, path))
                    || new SolidWorksReferenceTreeScanner(application).IsVerifiedBrokenContextReference(owner.FullPath, path)) continue;
                var existingAlias = nodes.Values.FirstOrDefault(node => PathsEqual(node.SourceReferencePath, path) && File.Exists(node.FullPath));
                if (existingAlias != null) { Bind(owner, source, existingAlias); continue; }
                var recoveryKey = owner.FullPath + "|" + source;
                if (recoveredArchiveReferences.TryGetValue(recoveryKey, out var cached))
                {
                    if (cached.Scope == apiClient?.EditJournalScope && cached.OwnerHash == ArchiveFileFingerprint.Read(owner.FullPath)
                        && File.Exists(cached.Path) && cached.Hash == ArchiveFileFingerprint.Read(cached.Path))
                    {
                        var cachedNode = NewRecoveredReferenceNode(cached.Path, source, owner);
                        RestorePersistedDocumentIdentities(cachedNode);
                        Bind(owner, source, cachedNode, true); continue;
                    }
                    recoveredArchiveReferences.Remove(recoveryKey);
                }
                if (imported.TryGetValue(path, out var copied)) { Bind(owner, source, copied, true); continue; }
                var candidates = new List<Tuple<string, string, ArchiveRemoteReference>>();
                if (File.Exists(path)) candidates.Add(Tuple.Create(path, ArchiveFileFingerprint.Read(path), (ArchiveRemoteReference)null));
                else
                {
                    var roots = ReadArchiveReferenceSearchRoots(nodes.Values, sources)
                        .Where(root => !indexedRoots.Any(indexed => PathsEqual(root, indexed)
                            || root.StartsWith(indexed.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))).ToArray();
                    if (roots.Length > 0)
                    {
                        PluginDiagnosticLog.Record("引用搜索范围", "本机目录及子目录：" + string.Join("；", roots)
                            + "；同时搜索当前账号可读的PLM项目及标准结构原文。");
                        var additional = await Task.Run(() => IndexArchiveReferenceFiles(roots, token), token);
                        foreach (var entry in additional)
                        {
                            if (!local.TryGetValue(entry.Key, out var matches)) local[entry.Key] = matches = new List<string>();
                            matches.AddRange(entry.Value.Where(pathFound => !matches.Contains(pathFound, StringComparer.OrdinalIgnoreCase)));
                        }
                        indexedRoots.UnionWith(roots);
                    }
                    if (local.TryGetValue(Path.GetFileName(path), out var found))
                        foreach (var file in found) candidates.Add(Tuple.Create(file, ArchiveFileFingerprint.Read(file), (ArchiveRemoteReference)null));
                    // Search all readable project/standard catalogs once per operation.
                    if (remote == null) remote = await apiClient.ReadReferenceSearchCatalogAsync(token);
                    foreach (var match in remote.Where(candidate => string.Equals(candidate.FileName, Path.GetFileName(path), StringComparison.OrdinalIgnoreCase)))
                    {
                        if (match.Document != null && match.Version == null)
                            match.Version = (await apiClient.GetVersionsAsync(match.Document.Id, token, 1)).FirstOrDefault();
                        if (match.Document != null && match.Version == null) continue;
                        candidates.Add(Tuple.Create(match.Description, match.Sha256, match));
                    }
                }
                var hashes = candidates.Select(candidate => candidate.Item2).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (hashes.Length != 1)
                {
                    if (hashes.Length == 0 && RecordMissingSourceWarning(owner, path,
                        indexedRoots.Concat(new[] { "当前账号可读PLM项目及标准结构原文" }), designSources)) continue;
                    problems.Add(owner.FileName + " → " + Path.GetFileName(path) + (hashes.Length == 0
                        ? "：在已配置目录和当前可读PLM库中未找到原始文件；原引用位置：" + path
                        : "：找到多个不同内容的候选，未自动替换。候选：" + string.Join("；", candidates.Select(candidate => candidate.Item1))));
                    continue;
                }
                var chosen = candidates.OrderByDescending(candidate => candidate.Item3?.Document?.ProjectId == projectId)
                    .ThenBy(candidate => candidate.Item3 == null ? 0 : 1).First();
                CadTreeNode target;
                if (chosen.Item3 == null)
                {
                    var folder = Path.Combine(ImportRoot(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
                    var destination = Path.Combine(folder, Path.GetFileName(path));
                    CopyArchiveReferenceBytes(chosen.Item1, destination, chosen.Item2);
                    target = NewRecoveredReferenceNode(destination, chosen.Item1, owner);
                    if (PdmDocumentIdentityStore.TryReadLocalMetadata(chosen.Item1, out var localBinding) && localBinding.ProjectId == projectId)
                    {
                        if (!localBinding.VersionId.HasValue || !string.Equals(chosen.Item2, localBinding.Sha256, StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(chosen.Item2, localBinding.ResolvedReferenceSha256, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("找到的项目引用原件含有未提交修改，未将其误登记为新增文件：" + target.FileName);
                        target.DocumentId = localBinding.DocumentId; target.OpenedVersionId = localBinding.VersionId;
                        target.OpenedRevision = target.CurrentRevision = target.Revision = localBinding.Revision;
                        target.HasStoredVersion = target.StoredVersionStateKnown = true;
                        if (!PdmDocumentIdentityStore.TryWriteControlledVersion(destination, localBinding.DocumentId, projectId,
                            localBinding.VersionId.Value, localBinding.Revision, localBinding.Sha256, localBinding.FileLength ?? new FileInfo(destination).Length))
                            throw new IOException("补齐引用的原项目版本记录未能保存：" + target.FileName);
                        ControlledReferenceMaterializer.CopyResolvedBinding(chosen.Item1, destination);
                        File.SetAttributes(destination, File.GetAttributes(destination) | FileAttributes.ReadOnly);
                    }
                }
                else
                {
                    var candidate = chosen.Item3;
                    var packageKey = candidate.Document != null ? "document:" + candidate.Document.Id + ":" + candidate.Version.Id
                        : "standard:" + candidate.StandardRevision.Id;
                    if (!materialized.TryGetValue(packageKey, out var package))
                    {
                        package = await MaterializeArchiveReferenceAsync(candidate, projectId, ImportRoot(), token);
                        materialized[packageKey] = package;
                        foreach (var file in package.Values)
                        {
                            nodes[file.FullPath] = file;
                            packageOwners[file.FullPath] = package;
                        }
                    }
                    target = package[Path.GetFileName(path)];
                }
                imported[path] = target;
                Bind(owner, source, target, true);
            }
        }
        if (problems.Count > 0)
        {
            foreach (var problem in problems) PluginDiagnosticLog.Record("引用补齐清单", problem);
            throw new InvalidOperationException("引用自动补齐尚未完成，共" + problems.Count + "项。尚未上传任何图档，原文件与智能关联均保留：\r\n"
                + string.Join("\r\n", problems.Take(30)) + (problems.Count > 30 ? "\r\n其余项目已完整记录在过程日志中。" : ""));
        }
        return DistinctBatchItems(items);
    }

    private IReadOnlyList<BatchOperationItem> RestoreRecoveredArchiveReferences(CadTreeNode root, IReadOnlyList<BatchOperationItem> selected)
    {
        var pending = new Queue<CadTreeNode>(EnumerateArchiveNodes(root));
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (pending.Count > 0)
        {
            var owner = pending.Dequeue();
            if (!visited.Add(owner.FullPath) || !IsPdmNativeFile(owner.FullPath)) continue;
            var records = recoveredArchiveReferences.Values.Where(record => record.Scope == apiClient?.EditJournalScope
                && PathsEqual(record.Owner, owner.FullPath)).ToArray();
            if (records.Length == 0) continue;
            var saved = application.GetDocumentDependencies2(owner.FullPath, false, false, false) as Array;
            var sources = new HashSet<string>(PdmDependencyEntries(saved, owner.FullPath).SelectMany(entry => entry.Value), StringComparer.OrdinalIgnoreCase);
            foreach (var record in records.Where(record => sources.Contains(record.Source)))
            {
                if (!File.Exists(record.Path) || record.Hash != ArchiveFileFingerprint.Read(record.Path))
                    throw new InvalidOperationException("已补齐的引用副本发生变化，原图档已保留，请重新检查：" + Path.GetFileName(record.Path));
                record.OwnerHash = ArchiveFileFingerprint.Read(owner.FullPath);
                if (!owner.Children.Concat(owner.ReferencedModels).Any(node => PathsEqual(node.FullPath, record.Path)))
                {
                    var link = NewRecoveredReferenceNode(record.Path, record.Source, owner);
                    CopyPdmIdentity(record.Node, link); link.OpenedVersionId = record.Node.OpenedVersionId;
                    link.Children.AddRange(record.Node.Children); owner.Children.Add(link);
                }
                if (root.NativeFileScopePaths != null) root.NativeFileScopePaths.Add(record.Path);
                pending.Enqueue(record.Node);
            }
        }
        var recoveredPaths = new HashSet<string>(recoveredArchiveReferences.Values.Select(record => record.Path), StringComparer.OrdinalIgnoreCase);
        var reachable = new HashSet<string>(EnumerateArchiveNodes(root).Select(node => node.FullPath), StringComparer.OrdinalIgnoreCase);
        // A normal save can remove a reference. Remove only automatically collected
        // dependencies which no longer belong to the submitted graph.
        return selected.Where(item => PathsEqual(item.Node.FullPath, root.FullPath) || !recoveredPaths.Contains(item.Node.FullPath)
            || reachable.Contains(item.Node.FullPath)).ToArray();
    }

    private static bool IsPdmNativeFile(string path) => new[] { ".SLDPRT", ".SLDASM", ".SLDDRW", ".SLDLFP" }
        .Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static CadTreeNode NewRecoveredReferenceNode(string path, string source, CadTreeNode owner) => new CadTreeNode {
        FullPath = path, SourceReferencePath = source, FileName = Path.GetFileName(path), DisplayName = Path.GetFileNameWithoutExtension(path),
        DrawingNumber = Path.GetFileNameWithoutExtension(path), Kind = DocumentKindFromPath(path), Role = CadReferenceRole.ExternalFileDependency,
        InstancePath = owner.InstancePath + "/补齐文件/" + Path.GetFileName(path) };

    private static void CopyArchiveReferenceBytes(string source, string target, string hash)
    {
        using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None)) input.CopyTo(output);
        if (!string.Equals(hash, ArchiveFileFingerprint.Read(target), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("引用文件在复制期间发生变化，尚未上传：" + Path.GetFileName(source));
    }

    private IReadOnlyList<string> ReadArchiveReferenceSearchRoots(IEnumerable<CadTreeNode> nodes, IEnumerable<string> references)
    {
        var roots = nodes.Where(node => node.Status != CadReferenceStatus.Virtual && !node.IsStoredImport)
            .Select(node => Path.GetDirectoryName(node.FullPath)).Concat(references.Select(Path.GetDirectoryName)).ToList();
        roots.AddRange((application.GetSearchFolders((int)swSearchFolderTypes_e.swDocumentType) ?? "").Split(';'));
        roots.AddRange((application.GetUserPreferenceStringListValue((int)swUserPreferenceStringValue_e.swFileLocationsDesignLibrary) ?? "").Split(';'));
        return roots.Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path)).Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static Dictionary<string, List<string>> IndexArchiveReferenceFiles(IEnumerable<string> roots, CancellationToken token)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<string>(roots);
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested(); var directory = pending.Dequeue();
            if (!visited.Add(directory)) continue;
            try
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    token.ThrowIfCancellationRequested();
                    if (!result.TryGetValue(Path.GetFileName(file), out var paths)) result[Path.GetFileName(file)] = paths = new List<string>();
                    paths.Add(file);
                }
                foreach (var child in Directory.EnumerateDirectories(directory)) pending.Enqueue(child);
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            { throw new IOException("引用搜索目录未能完整读取，尚未上传：" + directory + "。请检查目录可访问性后重试。", error); }
        }
        return result;
    }

    private async Task<Dictionary<string, CadTreeNode>> MaterializeArchiveReferenceAsync(ArchiveRemoteReference candidate,
        Guid projectId, string root, CancellationToken token)
    {
        var result = new Dictionary<string, CadTreeNode>(StringComparer.OrdinalIgnoreCase);
        var folder = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        if (candidate.Document != null)
        {
            var manifest = await apiClient.CreateControlledOpenManifestAsync(candidate.Document.Id, candidate.Version.Id, false, false, token);
            if (manifest.Files.Any(file => string.IsNullOrWhiteSpace(file.FileName) || Path.GetFileName(file.FileName) != file.FileName)
                || manifest.Files.GroupBy(file => file.FileName, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() != 1))
                throw new InvalidDataException("PLM引用包包含越界文件名或同名冲突，尚未收集或上传任何图档。");
            var source = await controlledWorkspace.PrepareReadOnlyAsync(manifest, false, token);
            foreach (var file in manifest.Files)
            {
                var from = ControlledReferenceMaterializer.LocalPath(Path.GetDirectoryName(source), file);
                var target = Path.Combine(folder, file.FileName); CopyArchiveReferenceBytes(from, target, ArchiveFileFingerprint.Read(from));
                var node = NewRecoveredReferenceNode(target, from, new CadTreeNode());
                if (manifest.ProjectId == projectId)
                {
                    node.DocumentId = file.DocumentId; node.OpenedVersionId = file.VersionId;
                    node.OpenedRevision = node.CurrentRevision = node.Revision = file.Revision;
                    node.HasStoredVersion = node.StoredVersionStateKnown = true;
                    if (!PdmDocumentIdentityStore.TryWriteControlledVersion(target, file.DocumentId, projectId, file.VersionId, file.Revision, file.Sha256, file.FileLength))
                        throw new IOException("引用图档的版本关联记录未能保存，尚未上传：" + file.FileName);
                    ControlledReferenceMaterializer.CopyResolvedBinding(from, target);
                    File.SetAttributes(target, File.GetAttributes(target) | FileAttributes.ReadOnly);
                }
                result.Add(file.FileName, node);
            }
        }
        else
            foreach (var file in PdmApiClient.StandardReferenceSourceFiles(candidate.StandardRevision))
            {
                var path = await apiClient.DownloadStandardReferenceAsync(candidate.StandardRevision.Id, file, folder, token);
                result.Add(file.FileName, NewRecoveredReferenceNode(path, path, new CadTreeNode()));
            }
        return result;
    }
}
