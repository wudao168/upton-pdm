using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Upton.Pdm.LocalSettings;

namespace Upton.Pdm.SolidWorks;

internal sealed class ControlledWorkspaceManager
{
    private readonly PdmApiClient apiClient;
    private readonly string workspaceRoot;

    public ControlledWorkspaceManager(PdmApiClient apiClient)
        : this(apiClient, WorkspaceSettingsStore.GetWorkspaceRoot())
    {
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
        if (manifest?.Files == null || manifest.Files.Count == 0)
        {
            throw new InvalidDataException("PLM返回的打开清单为空。");
        }

        var target = ReadOnlyDirectory(manifest, historicalPreview);
        if (Directory.Exists(target) && ValidateWorkspace(target, manifest.Files))
        {
            SetWorkspaceAttributes(target, manifest, Array.Empty<Guid>());
            return ResolveRootPath(target, manifest);
        }

        var parent = Path.GetDirectoryName(target) ?? throw new InvalidDataException("受控工作区路径无效。");
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(workspaceRoot, ".uplm", "Staging", manifest.Id.ToString("N"));
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var file in manifest.Files)
            {
                await apiClient.DownloadControlledOpenFileAsync(file, staging, cancellationToken).ConfigureAwait(false);
            }
            if (!ValidateWorkspace(staging, manifest.Files))
            {
                throw new InvalidDataException("受控工作区整体校验失败，未打开图档。");
            }

            ReplaceDirectory(staging, target);
            SetWorkspaceAttributes(target, manifest, Array.Empty<Guid>());
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
        var readOnlyRootPath = await PrepareReadOnlyAsync(manifest, false, cancellationToken).ConfigureAwait(false);
        var source = Path.GetDirectoryName(readOnlyRootPath) ?? throw new InvalidDataException("只读工作区路径无效。");
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

    public async Task<string> PrepareHistoricalDerivedCopyAsync(
        ControlledOpenManifestDto manifest,
        CancellationToken cancellationToken)
    {
        var readOnlyRootPath = await PrepareReadOnlyAsync(manifest, true, cancellationToken).ConfigureAwait(false);
        var source = Path.GetDirectoryName(readOnlyRootPath) ?? throw new InvalidDataException("历史只读工作区路径无效。");
        var target = HistoricalDerivedDirectory(manifest);
        if (Directory.Exists(target))
        {
            if (!ValidateWorkspace(target, manifest.Files))
            {
                throw new InvalidDataException(
                    "该历史版本的派生编辑工作区已存在变化或不完整，已停止覆盖。请先提交、备份或清理该派生工作区后重试。");
            }

            SetWorkspaceAttributes(target, manifest, Array.Empty<Guid>());
            return ResolveRootPath(target, manifest);
        }

        try
        {
            CopyDirectory(source, target);
            if (!ValidateWorkspace(target, manifest.Files))
            {
                throw new InvalidDataException("历史版本派生工作区复制校验失败。");
            }
            SetWorkspaceAttributes(target, manifest, Array.Empty<Guid>());
            return ResolveRootPath(target, manifest);
        }
        catch
        {
            if (Directory.Exists(target)) DeleteDirectory(target);
            throw;
        }
    }

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

    private string SnapshotRoot(ControlledOpenManifestDto manifest)
    {
        return Path.Combine(
            workspaceRoot,
            ".uplm",
            "Snapshots",
            SafeSegment(manifest.ProjectCode),
            manifest.RootDocumentId.ToString("N"));
    }

    private string ReadOnlyDirectory(ControlledOpenManifestDto manifest, bool historicalPreview) =>
        Path.Combine(
            SnapshotRoot(manifest),
            historicalPreview ? "ReadOnly" : "Latest",
            manifest.RootVersionId.ToString("N"));

    private string WorkingDirectory(ControlledOpenManifestDto manifest) =>
        ProjectViewDirectory(workspaceRoot, manifest.ProjectCode);

    private string HistoricalDerivedDirectory(ControlledOpenManifestDto manifest) =>
        Path.Combine(
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
            var info = new FileInfo(path);
            if (info.Length != file.FileLength || !string.Equals(ComputeSha256(path), file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        return true;
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
            var info = new FileInfo(path);
            var matches = info.Length == file.FileLength
                && string.Equals(ComputeSha256(path), file.Sha256, StringComparison.OrdinalIgnoreCase);
            if (!matches
                && (editableIds.Contains(file.DocumentId)
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
            File.SetAttributes(path, attributes);
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
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath));
                CopyFileAtomically(sourcePath, targetPath);
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

        var temporary = string.Concat(target, ".pdm-update-", Guid.NewGuid().ToString("N"));
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
        using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var hash = SHA256.Create())
        {
            return BitConverter.ToString(hash.ComputeHash(input)).Replace("-", string.Empty);
        }
    }
}
