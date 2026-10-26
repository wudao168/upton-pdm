using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Upton.Pdm.LocalSettings;

namespace Upton.Pdm.SolidWorks;

internal sealed partial class ControlledWorkspaceManager
{
    internal Action<string, int, int> PreparationProgress { get; set; }
    private readonly PdmApiClient apiClient;
    private readonly string workspaceRoot;
    internal string WorkspaceRoot => workspaceRoot;

    public ControlledWorkspaceManager(PdmApiClient apiClient)
        : this(apiClient, WorkspaceSettingsStore.GetWorkspaceRoot())
    {
        machineFallbackEnabled = true;
    }

    internal ControlledWorkspaceManager(PdmApiClient apiClient, string workspaceRoot)
    {
        this.apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        this.workspaceRoot = Path.GetFullPath(workspaceRoot ?? throw new ArgumentNullException(nameof(workspaceRoot)));
    }

    public async Task<string> PrepareReadOnlyAsync(
        ControlledOpenManifestDto manifest,
        bool historicalPreview,
        CancellationToken cancellationToken)
    {
        try { return await PrepareReadOnlyCoreAsync(manifest, historicalPreview, cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
        {
            PluginDiagnosticLog.Record("PLM版本优先", "旧只读缓存不可用，改为重新取回；旧文件不拦截打开：" + error);
            return await DownloadFreshServerCopyAsync(manifest, historicalPreview, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<string> PrepareReadOnlyCoreAsync(
        ControlledOpenManifestDto manifest, bool historicalPreview, CancellationToken cancellationToken)
    {
        if (manifest?.Files == null || manifest.Files.Count == 0)
        {
            throw new InvalidDataException("PLM返回的打开清单为空。");
        }

        var target = SelectReadOnlyTarget(ReadOnlyDirectory(manifest, historicalPreview), manifest);
        if (Directory.Exists(target) && ValidateWorkspace(target, manifest.Files))
        {
            SetWorkspaceAttributes(target, manifest, Array.Empty<Guid>());
            verifiedSnapshotDirectories[manifest.RootDocumentId] = target;
            return ResolveRootPath(target, manifest);
        }

        var parent = Path.GetDirectoryName(target) ?? throw new InvalidDataException("受控工作区路径无效。");
        EnsureNoUnarchivedDrawingIsRemoved(target, manifest.Files);
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(workspaceRoot, ".uplm", "Staging", manifest.Id.ToString("N"));
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        Directory.CreateDirectory(staging);
        try
        {
            await DownloadManifestFilesAsync(manifest, staging, cancellationToken).ConfigureAwait(false);
            if (!ValidateWorkspace(staging, manifest.Files))
            {
                throw new InvalidDataException("受控工作区整体校验失败，未打开图档。");
            }

            ReplaceDirectory(staging, target);
            SetWorkspaceAttributes(target, manifest, Array.Empty<Guid>());
            verifiedSnapshotDirectories[manifest.RootDocumentId] = target;
            return ResolveRootPath(target, manifest);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }

    public async Task<string> PrepareWorkingCopyAsync(
        ControlledOpenManifestDto manifest,
        IReadOnlyCollection<Guid> editableDocumentIds,
        CancellationToken cancellationToken)
    {
        var target = WorkingDirectory(manifest);
        if (Directory.Exists(target) && ValidateWorkspace(target, manifest.Files))
        {
            SetWorkspaceAttributes(target, manifest, editableDocumentIds ?? Array.Empty<Guid>());
            return ResolveRootPath(target, manifest);
        }
        var readOnlyRootPath = await PrepareReadOnlyAsync(manifest, false, cancellationToken).ConfigureAwait(false);
        var source = Path.GetDirectoryName(readOnlyRootPath) ?? throw new InvalidDataException("只读工作区路径无效。");
        return CopyWorkingContext(manifest, source, editableDocumentIds);
    }

    internal string TryPrepareWorkingContext(ControlledOpenManifestDto manifest, string source,
        IReadOnlyCollection<Guid> editableDocumentIds)
    {
        return !string.IsNullOrWhiteSpace(source) && ValidateWorkspace(source, manifest.Files)
            ? CopyWorkingContext(manifest, source, editableDocumentIds) : null;
    }

    private string CopyWorkingContext(ControlledOpenManifestDto manifest, string source, IReadOnlyCollection<Guid> editableDocumentIds)
    {
        var target = WorkingDirectory(manifest);
        var editableIds = new HashSet<Guid>(editableDocumentIds ?? Array.Empty<Guid>());
        if (Directory.Exists(target) && ValidateWorkspace(target, manifest.Files))
        {
            SetWorkspaceAttributes(target, manifest, editableIds);
            return ResolveRootPath(target, manifest);
        }

        if (Directory.Exists(target) && HasPotentialLocalChanges(target, manifest.Files, editableIds))
        {
            throw new InvalidDataException("本地工作区存在可能尚未提交的文件，已停止用最新受控版本覆盖。请先提交存档、放弃编辑或备份本地文件。");
        }

        Directory.CreateDirectory(target);
        MergeManifestFiles(source, target, manifest.Files);
        if (!ValidateWorkspace(target, manifest.Files))
        {
            throw new InvalidDataException("最新受控文件复制校验失败，未更新本地工作区。");
        }
        SetWorkspaceAttributes(target, manifest, editableIds);
        return ResolveRootPath(target, manifest);
    }

    public Task<string> PrepareHistoricalDerivedCopyAsync(ControlledOpenManifestDto manifest, CancellationToken cancellationToken)
        => PrepareServerWorkingCopyAsync(manifest, Array.Empty<Guid>(), true, cancellationToken);

    public string ApplyHistoricalDerivedPermissions(
        ControlledOpenManifestDto manifest,
        IReadOnlyCollection<Guid> editableDocumentIds)
    {
        var target = HistoricalDerivedDirectory(manifest);
        if (!Directory.Exists(target) || !HasAllManifestFiles(target, manifest.Files))
        {
            throw new InvalidDataException("历史版本派生工作区不完整，未更改文件权限。");
        }

        SetWorkspaceAttributes(target, manifest, editableDocumentIds);
        return ResolveRootPath(target, manifest);
    }

    public string PromoteToEditable(ControlledOpenManifestDto manifest, string readOnlyRootPath)
    {
        var source = Path.GetDirectoryName(readOnlyRootPath) ?? throw new InvalidDataException("只读工作区路径无效。");
        if (!ValidateWorkspace(source, manifest.Files))
        {
            throw new InvalidDataException("只读工作区在获取权限前发生变化，未创建编辑工作区。");
        }

        var target = WorkingDirectory(manifest);
        if (Directory.Exists(target) && ValidateWorkspace(target, manifest.Files))
        {
            SetWorkspaceAttributes(target, manifest, new[] { manifest.RootDocumentId });
            return ResolveRootPath(target, manifest);
        }

        if (Directory.Exists(target)
            && HasPotentialLocalChanges(target, manifest.Files, Array.Empty<Guid>()))
        {
            throw new InvalidDataException("本地工作区存在可能尚未提交的文件，已停止创建编辑副本。请先提交存档、放弃编辑或备份本地文件。");
        }
        Directory.CreateDirectory(target);
        MergeManifestFiles(source, target, manifest.Files);
        if (!ValidateWorkspace(target, manifest.Files))
        {
            throw new InvalidDataException("编辑工作区复制校验失败。");
        }
        SetWorkspaceAttributes(target, manifest, new[] { manifest.RootDocumentId });
        return ResolveRootPath(target, manifest);
    }

    public string GetWorkingRootPath(ControlledOpenManifestDto manifest) =>
        ResolveRootPath(WorkingDirectory(manifest), manifest);

    public string ApplyWorkingPermissions(
        ControlledOpenManifestDto manifest,
        IReadOnlyCollection<Guid> editableDocumentIds,
        bool preserveLocalChanges = false)
    {
        var target = WorkingDirectory(manifest);
        var valid = preserveLocalChanges
            ? Directory.Exists(target) && HasAllManifestFiles(target, manifest.Files)
            : Directory.Exists(target) && ValidateWorkspace(target, manifest.Files);
        if (!valid)
        {
            throw new InvalidDataException("编辑工作区校验失败，未更改文件权限。");
        }

        SetWorkspaceAttributes(target, manifest, editableDocumentIds);
        return ResolveRootPath(target, manifest);
    }

    public bool TryGetExistingWorkingRoot(
        ControlledOpenManifestDto manifest,
        out string rootPath,
        out bool matchesControlledFiles)
    {
        rootPath = string.Empty;
        matchesControlledFiles = false;
        var target = WorkingDirectory(manifest);
        if (!Directory.Exists(target) || !HasAllManifestFiles(target, manifest.Files))
        {
            return false;
        }

        rootPath = ResolveRootPath(target, manifest);
        matchesControlledFiles = ValidateWorkspace(target, manifest.Files);
        return matchesControlledFiles
            || HasPotentialLocalChanges(target, manifest.Files, Array.Empty<Guid>());
    }

    public IReadOnlyList<string> GetWorkingFilePaths(ControlledOpenManifestDto manifest) =>
        manifest.Files.Select(file => ManifestPath(WorkingDirectory(manifest), file)).ToArray();

    public string GetWorkingFilePath(ControlledOpenManifestDto manifest, Guid documentId)
    {
        var file = manifest.Files.FirstOrDefault(item => item.DocumentId == documentId)
            ?? throw new FileNotFoundException("编辑工作区中未找到所选图档。");
        return ManifestPath(WorkingDirectory(manifest), file);
    }

    public string GetReadOnlyDirectory(ControlledOpenManifestDto manifest, bool historicalPreview = false) =>
        ReadOnlyDirectory(manifest, historicalPreview);

    internal static string SelectReadOnlyTarget(string preferred, ControlledOpenManifestDto manifest)
    {
        bool Valid(string path)
        {
            try { return ValidateWorkspace(path, manifest.Files); }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            { PluginDiagnosticLog.Record("只读副本恢复", "旧副本当前不可校验，保留后另行取回：" + path + "；" + error); return false; }
        }
        if (!Directory.Exists(preferred) || Valid(preferred)) return preferred;
        // F11/I10: retain a changed cache, including new drawings, and fetch a fresh
        // immutable copy. A broken old cache must not prevent opening server content.
        var parent = Path.GetDirectoryName(preferred);
        var prefix = Path.GetFileName(preferred) + ".isolated-";
        foreach (var candidate in Directory.EnumerateDirectories(parent, prefix + "*"))
            if (Valid(candidate)) return candidate;
        var target = Path.Combine(parent, prefix + manifest.Id.ToString("N"));
        if (Directory.Exists(target)) target += "-" + Guid.NewGuid().ToString("N");
        PluginDiagnosticLog.Record("只读副本恢复", "原目录及本地内容已保留，另行取回服务器副本：" + target);
        return target;
    }

    private string SnapshotRoot(ControlledOpenManifestDto manifest)
    {
        return Path.Combine(
            workspaceRoot,
            ".uplm",
            "Snapshots",
            SafeSegment(manifest.ProjectCode),
            manifest.RootDocumentId.ToString("N"));
    }

    private string ReadOnlyDirectory(ControlledOpenManifestDto manifest, bool historicalPreview)
    {
        var root = Path.Combine(SnapshotRoot(manifest), historicalPreview ? "ReadOnly" : "Latest", manifest.RootVersionId.ToString("N"));
        if (historicalPreview) return root;
        // A parent can retain its version while a child advances. Each complete file
        // combination gets an immutable cache directory instead of replacing open files.
        var shape = manifest.RootVersionId + "|" + manifest.RootDocumentId + "|" + string.Join("|", manifest.Files.OrderBy(file => file.DocumentId)
            .Select(file => file.DocumentId + ":" + file.VersionId + ":" + file.Sha256));
        using (var hash = System.Security.Cryptography.SHA256.Create())
            return Path.Combine(SnapshotRoot(manifest), "Latest", BitConverter.ToString(hash.ComputeHash(System.Text.Encoding.UTF8.GetBytes(shape))).Replace("-", "").Substring(0, 32));
    }

    private string WorkingDirectory(ControlledOpenManifestDto manifest)
    {
        if (serverWorkingDirectories.TryGetValue(manifest.RootDocumentId, out var prepared)) return prepared;
        var project = ProjectViewDirectory(workspaceRoot, manifest.ProjectCode);
        var legacyRoot = Path.Combine(project, manifest.RootRelativePath ?? "");
        if (PdmDocumentIdentityStore.TryReadLocalMetadata(legacyRoot, out var metadata)
            && metadata.DocumentId == manifest.RootDocumentId) return project;
        return Path.Combine(project, "装配工作区", manifest.RootDocumentId.ToString("N"));
    }

    private string HistoricalDerivedDirectory(ControlledOpenManifestDto manifest) =>
        serverDerivedDirectories.TryGetValue(manifest.RootDocumentId, out var prepared) ? prepared : Path.Combine(
            workspaceRoot,
            ".uplm",
            "Derived",
            SafeSegment(manifest.ProjectCode),
            manifest.RootDocumentId.ToString("N"),
            manifest.RootVersionId.ToString("N"),
            manifest.Id.ToString("N"));

    internal static string ProjectViewDirectory(string root, string projectCode) =>
        Path.Combine(root, "View", SafeSegment(projectCode));

    private static string ResolveRootPath(string directory, ControlledOpenManifestDto manifest)
    {
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, manifest.RootRelativePath ?? string.Empty));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
        {
            throw new FileNotFoundException("受控工作区中未找到根图档。", path);
        }
        return path;
    }

    private static bool ValidateWorkspace(string directory, IEnumerable<ControlledOpenFileDto> files)
    {
        foreach (var file in files)
        {
            var path = ManifestPath(directory, file);
            if (!File.Exists(path)) return false;
            if (!ControlledReferenceMaterializer.Matches(path, file))
            {
                return false;
            }
        }
        return true;
    }

    internal static void EnsureNoUnarchivedDrawingIsRemoved(string directory, IEnumerable<ControlledOpenFileDto> files)
    {
        if (!Directory.Exists(directory)) return;
        var controlled = new HashSet<string>(files.Select(file => ManifestPath(directory, file)), StringComparer.OrdinalIgnoreCase);
        var draft = Directory.EnumerateFiles(directory, "*.SLDDRW", SearchOption.AllDirectories)
            .FirstOrDefault(path => !controlled.Contains(Path.GetFullPath(path)));
        if (draft != null)
            throw new InvalidDataException("预览目录中有新建工程图，已保留原文件并停止替换目录。请先将该工程图存档或另存到工作目录：" + draft);
    }

    private static bool HasAllManifestFiles(string directory, IEnumerable<ControlledOpenFileDto> files) =>
        files.All(file => File.Exists(ManifestPath(directory, file)));

    private static bool HasPotentialLocalChanges(
        string directory,
        IReadOnlyCollection<ControlledOpenFileDto> files,
        IReadOnlyCollection<Guid> editableDocumentIds)
    {
        var editableIds = new HashSet<Guid>(editableDocumentIds ?? Array.Empty<Guid>());
        foreach (var file in files)
        {
            var path = ManifestPath(directory, file);
            if (!File.Exists(path)) continue;
            var matches = ControlledReferenceMaterializer.Matches(path, file);
            var hasMetadata = PdmDocumentIdentityStore.TryReadLocalMetadata(path, out var metadata);
            var matchesBase = hasMetadata && metadata.DocumentId == file.DocumentId
                && !string.IsNullOrWhiteSpace(metadata.Sha256)
                && (string.Equals(ComputeSha256(path), metadata.Sha256, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(ComputeSha256(path), metadata.ResolvedReferenceSha256, StringComparison.OrdinalIgnoreCase));
            if (!matches
                && (!matchesBase
                    || editableIds.Contains(file.DocumentId)
                    || (File.GetAttributes(path) & FileAttributes.ReadOnly) == 0))
            {
                return true;
            }
        }

        return false;
    }

    private static void SetWorkspaceAttributes(
        string directory,
        ControlledOpenManifestDto manifest,
        IReadOnlyCollection<Guid> editableDocumentIds)
    {
        var editableIds = new HashSet<Guid>(editableDocumentIds ?? Array.Empty<Guid>());
        foreach (var file in manifest.Files)
        {
            var path = ManifestPath(directory, file);
            var attributes = File.GetAttributes(path) | FileAttributes.ReadOnly;
            if (editableIds.Contains(file.DocumentId)) attributes &= ~FileAttributes.ReadOnly;
            if (File.GetAttributes(path) != attributes) File.SetAttributes(path, attributes);
        }
    }

    private void ReplaceDirectory(string source, string target)
    {
        if (!Directory.Exists(target))
        {
            Directory.Move(source, target);
            return;
        }

        var backup = RecoveryDirectory("snapshot");
        var originalAttributes = SnapshotFileAttributes(target);
        var backupCompleted = false;
        var keepRecovery = false;
        try
        {
            CopyDirectory(target, backup);
            backupCompleted = true;
            SynchronizeDirectory(source, target);
            DeleteDirectory(backup);
        }
        catch (Exception updateException)
        {
            if (!backupCompleted)
            {
                throw;
            }

            try
            {
                SynchronizeDirectory(backup, target);
                RestoreFileAttributes(target, originalAttributes);
            }
            catch (Exception rollbackException)
            {
                keepRecovery = true;
                throw new AggregateException("工作区更新失败，并且未能完整回滚；恢复副本已保留在：" + backup, updateException, rollbackException);
            }

            throw;
        }
        finally
        {
            if (!keepRecovery && Directory.Exists(backup)) DeleteDirectory(backup);
        }
    }

    private static void SynchronizeDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        var sourceRoot = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var targetRoot = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var expectedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var expectedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { string.Empty };

        foreach (var sourceDirectory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            var relativePath = RelativePath(sourceRoot, sourceDirectory);
            expectedDirectories.Add(relativePath);
            Directory.CreateDirectory(Path.Combine(targetRoot, relativePath));
        }

        foreach (var sourceFile in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var relativePath = RelativePath(sourceRoot, sourceFile);
            expectedFiles.Add(relativePath);
            var destination = Path.Combine(targetRoot, relativePath);
            var destinationDirectory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrWhiteSpace(destinationDirectory)) Directory.CreateDirectory(destinationDirectory);
            CopyFileAtomically(sourceFile, destination);
        }

        foreach (var targetFile in Directory.GetFiles(target, "*", SearchOption.AllDirectories))
        {
            if (expectedFiles.Contains(RelativePath(targetRoot, targetFile))) continue;
            File.SetAttributes(targetFile, FileAttributes.Normal);
            File.Delete(targetFile);
        }

        foreach (var targetDirectory in Directory.GetDirectories(target, "*", SearchOption.AllDirectories)
            .OrderByDescending(path => path.Length))
        {
            if (expectedDirectories.Contains(RelativePath(targetRoot, targetDirectory))) continue;
            Directory.Delete(targetDirectory, true);
        }
    }

    private void MergeManifestFiles(
        string source,
        string target,
        IReadOnlyCollection<ControlledOpenFileDto> files)
    {
        var backup = RecoveryDirectory("working");
        var changed = new List<Tuple<string, string, FileAttributes?>>();
        var previousBindings = new Dictionary<string, PdmControlledFileMetadata>(StringComparer.OrdinalIgnoreCase);
        var keepRecovery = false;
        try
        {
            foreach (var file in files)
            {
                var sourcePath = ManifestPath(source, file);
                var targetPath = ManifestPath(target, file);
                if (File.Exists(targetPath)
                    && new FileInfo(sourcePath).Length == new FileInfo(targetPath).Length
                    && string.Equals(ComputeSha256(sourcePath), ComputeSha256(targetPath), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var relativePath = RelativePath(
                    Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    targetPath);
                var backupPath = Path.Combine(backup, relativePath);
                FileAttributes? attributes = null;
                if (File.Exists(targetPath))
                {
                    attributes = File.GetAttributes(targetPath);
                    Directory.CreateDirectory(Path.GetDirectoryName(backupPath));
                    File.Copy(targetPath, backupPath, true);
                }
                changed.Add(Tuple.Create(targetPath, backupPath, attributes));
                PdmDocumentIdentityStore.TryReadLocalMetadata(targetPath, out var previousBinding);
                previousBindings[targetPath] = previousBinding;
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath));
                CopyFileAtomically(sourcePath, targetPath);
                ControlledReferenceMaterializer.CopyResolvedBinding(sourcePath, targetPath);
            }
        }
        catch (Exception updateException)
        {
            try
            {
                foreach (var item in changed.AsEnumerable().Reverse())
                {
                    if (item.Item3.HasValue)
                    {
                        CopyFileAtomically(item.Item2, item.Item1);
                        File.SetAttributes(item.Item1, item.Item3.Value);
                    }
                    else if (File.Exists(item.Item1))
                    {
                        File.SetAttributes(item.Item1, FileAttributes.Normal);
                        File.Delete(item.Item1);
                    }
                    PdmDocumentIdentityStore.RestoreLocalMetadata(item.Item1, previousBindings[item.Item1]);
                }
            }
            catch (Exception rollbackException)
            {
                keepRecovery = true;
                throw new AggregateException("项目工作区更新失败，并且未能完整回滚；恢复副本已保留在：" + backup, updateException, rollbackException);
            }
            throw;
        }
        finally
        {
            if (!keepRecovery && Directory.Exists(backup)) DeleteDirectory(backup);
        }
    }

    private string RecoveryDirectory(string category) => Path.Combine(
        workspaceRoot,
        ".uplm",
        "Recovery",
        string.Concat(category, "-", DateTime.UtcNow.ToString("yyyyMMddHHmmss"), "-", Guid.NewGuid().ToString("N")));

    private static void CopyFileAtomically(string source, string target)
    {
        if (File.Exists(target)
            && new FileInfo(source).Length == new FileInfo(target).Length
            && string.Equals(ComputeSha256(source), ComputeSha256(target), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var temporary = Path.Combine(Path.GetDirectoryName(target), ".pdm-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                input.CopyTo(output);
            }

            if (File.Exists(target))
            {
                File.SetAttributes(target, File.GetAttributes(target) & ~FileAttributes.ReadOnly);
                File.Replace(temporary, target, null, true);
            }
            else
            {
                File.Move(temporary, target);
            }
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static IReadOnlyDictionary<string, FileAttributes> SnapshotFileAttributes(string directory)
    {
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            .ToDictionary(path => RelativePath(root, path), File.GetAttributes, StringComparer.OrdinalIgnoreCase);
    }

    private static void RestoreFileAttributes(string directory, IReadOnlyDictionary<string, FileAttributes> attributes)
    {
        foreach (var item in attributes)
        {
            var path = Path.Combine(directory, item.Key);
            if (File.Exists(path)) File.SetAttributes(path, item.Value);
        }
    }

    private static string RelativePath(string root, string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("工作区文件路径越界。");
        }

        return fullPath.Substring(root.Length);
    }

    private static string ManifestPath(string directory, ControlledOpenFileDto file)
    {
        var root = Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var relativePath = file.RelativePath ?? file.FileName ?? string.Empty;
        var path = Path.GetFullPath(Path.Combine(root, relativePath));
        if (string.IsNullOrWhiteSpace(relativePath)
            || !path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("PLM打开清单包含越界文件路径。");
        }
        return path;
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(directory.Replace(source, target));
        }
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = file.Replace(source, target);
            File.Copy(file, destination, true);
            File.SetAttributes(destination, FileAttributes.Normal);
        }
    }

    private static void DeleteDirectory(string path)
    {
        foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(path, true);
    }

    private static string SafeSegment(string value)
    {
        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars());
        var chars = (value ?? string.Empty).Select(character => invalid.Contains(character) ? '_' : character).ToArray();
        var result = new string(chars).Trim();
        return string.IsNullOrWhiteSpace(result) ? "PROJECT" : result;
    }

    private static string ComputeSha256(string path)
    {
        return ArchiveFileFingerprint.Read(path);
    }
}
