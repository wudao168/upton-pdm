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

    public ControlledWorkspaceManager(PdmApiClient apiClient)
    {
        this.apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
    }

    public async Task<string> PrepareReadOnlyAsync(
        ControlledOpenManifestDto manifest,
        bool historicalPreview,
        CancellationToken cancellationToken)
    {
        if (manifest?.Files == null || manifest.Files.Count == 0)
        {
            throw new InvalidDataException("PDM返回的打开清单为空。");
        }

        var target = ReadOnlyDirectory(manifest, historicalPreview);
        if (Directory.Exists(target) && ValidateWorkspace(target, manifest.Files))
        {
            SetWorkspaceAttributes(target, manifest, Array.Empty<Guid>());
            return ResolveRootPath(target, manifest);
        }

        var parent = Path.GetDirectoryName(target) ?? throw new InvalidDataException("受控工作区路径无效。");
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, string.Concat(".stage-", manifest.Id.ToString("N")));
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

        var parent = Path.GetDirectoryName(target) ?? throw new InvalidDataException("编辑工作区路径无效。");
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, string.Concat(".working-stage-", Guid.NewGuid().ToString("N")));
        CopyDirectory(source, staging);
        try
        {
            if (!ValidateWorkspace(staging, manifest.Files))
            {
                throw new InvalidDataException("最新受控文件复制校验失败，未更新本地工作区。");
            }

            ReplaceDirectory(staging, target);
            SetWorkspaceAttributes(target, manifest, editableIds);
            return ResolveRootPath(target, manifest);
        }
        finally
        {
            if (Directory.Exists(staging)) DeleteDirectory(staging);
        }
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

        var parent = Path.GetDirectoryName(target) ?? throw new InvalidDataException("编辑工作区路径无效。");
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, string.Concat(".edit-stage-", Guid.NewGuid().ToString("N")));
        CopyDirectory(source, staging);
        try
        {
            if (!ValidateWorkspace(staging, manifest.Files))
            {
                throw new InvalidDataException("编辑工作区复制校验失败。");
            }
            ReplaceDirectory(staging, target);
            SetWorkspaceAttributes(target, manifest, new[] { manifest.RootDocumentId });
            return ResolveRootPath(target, manifest);
        }
        finally
        {
            if (Directory.Exists(staging)) DeleteDirectory(staging);
        }
    }

    public string GetWorkingRootPath(ControlledOpenManifestDto manifest) =>
        Path.Combine(WorkingDirectory(manifest), manifest.RootRelativePath ?? string.Empty);

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
        return true;
    }

    public IReadOnlyList<string> GetWorkingFilePaths(ControlledOpenManifestDto manifest) =>
        manifest.Files.Select(file => Path.Combine(WorkingDirectory(manifest), file.RelativePath ?? file.FileName ?? string.Empty)).ToArray();

    public string GetWorkingFilePath(ControlledOpenManifestDto manifest, Guid documentId)
    {
        var file = manifest.Files.FirstOrDefault(item => item.DocumentId == documentId)
            ?? throw new FileNotFoundException("编辑工作区中未找到所选图档。");
        return Path.Combine(WorkingDirectory(manifest), file.RelativePath ?? file.FileName ?? string.Empty);
    }

    public string GetReadOnlyDirectory(ControlledOpenManifestDto manifest, bool historicalPreview = false) =>
        ReadOnlyDirectory(manifest, historicalPreview);

    private static string WorkspaceRoot(ControlledOpenManifestDto manifest)
    {
        return Path.Combine(
            WorkspaceSettingsStore.GetWorkspaceRoot(),
            SafeSegment(manifest.ProjectCode),
            manifest.RootDocumentId.ToString("N"));
    }

    private static string ReadOnlyDirectory(ControlledOpenManifestDto manifest, bool historicalPreview) =>
        Path.Combine(
            WorkspaceRoot(manifest),
            historicalPreview ? "ReadOnly" : "Latest",
            manifest.RootVersionId.ToString("N"));

    private static string WorkingDirectory(ControlledOpenManifestDto manifest) =>
        Path.Combine(WorkspaceRoot(manifest), "Working");

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
            var path = Path.Combine(directory, file.RelativePath ?? file.FileName ?? string.Empty);
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
        files.All(file => File.Exists(Path.Combine(directory, file.RelativePath ?? file.FileName ?? string.Empty)));

    private static bool HasPotentialLocalChanges(
        string directory,
        IReadOnlyCollection<ControlledOpenFileDto> files,
        IReadOnlyCollection<Guid> editableDocumentIds)
    {
        if (editableDocumentIds.Count > 0)
        {
            return true;
        }

        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var expected = new HashSet<string>(files
            .Select(file => file.RelativePath ?? file.FileName ?? string.Empty)
            .Where(path => !string.IsNullOrWhiteSpace(path)), StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
        {
            if (!expected.Contains(RelativePath(root, path))
                || (File.GetAttributes(path) & FileAttributes.ReadOnly) == 0)
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
            var path = Path.Combine(directory, file.RelativePath ?? file.FileName ?? string.Empty);
            var attributes = File.GetAttributes(path) | FileAttributes.ReadOnly;
            if (editableIds.Contains(file.DocumentId)) attributes &= ~FileAttributes.ReadOnly;
            File.SetAttributes(path, attributes);
        }
    }

    private static void ReplaceDirectory(string source, string target)
    {
        if (!Directory.Exists(target))
        {
            Directory.Move(source, target);
            return;
        }

        var backup = string.Concat(target, ".backup-", Guid.NewGuid().ToString("N"));
        var originalAttributes = SnapshotFileAttributes(target);
        var backupCompleted = false;
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
                throw new AggregateException("工作区更新失败，并且未能完整回滚原工作区。", updateException, rollbackException);
            }

            throw;
        }
        finally
        {
            if (Directory.Exists(backup)) DeleteDirectory(backup);
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
                File.SetAttributes(target, FileAttributes.Normal);
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
