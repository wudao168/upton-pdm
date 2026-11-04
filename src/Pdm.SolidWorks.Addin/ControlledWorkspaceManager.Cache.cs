using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Upton.Pdm.SolidWorks;

internal sealed partial class ControlledWorkspaceManager
{
    internal static string PlannedFilePath(string directory, ControlledOpenFileDto file) => ManifestPath(directory, file);
    internal string UseRecoveryWorkingDirectory(ControlledOpenManifestDto manifest)
    {
        var directory = Path.Combine(ProjectViewDirectory(workspaceRoot, manifest.ProjectCode), "装配工作区",
            "plm-" + manifest.RootDocumentId.ToString("N"));
        serverWorkingDirectories[manifest.RootDocumentId] = directory;
        return directory;
    }
    private readonly HashSet<Guid> inspectedLegacySnapshots = new HashSet<Guid>();
    private void RegisterLegacySnapshotsOnce(ControlledOpenManifestDto manifest)
    {
        if (inspectedLegacySnapshots.Add(manifest.RootDocumentId))
            ControlledCacheMaintenance.RegisterLegacySnapshots(workspaceRoot, manifest);
    }
    private async Task<string> UpdateWorkingCacheAsync(ControlledOpenManifestDto manifest, string target,
        IReadOnlyCollection<Guid> editableIds, CancellationToken token, WorkspaceCacheBackup backup = null)
    {
        var editable = editableIds ?? Array.Empty<Guid>();
        if (manifest.RetainedWorkingHashes.Count > 0)
        {
            using (var retained = new ControlledWorkingCopyScope(Path.Combine(target, manifest.RootRelativePath), manifest))
                if (manifest.Files.Any(file => manifest.RetainedWorkingHashes.ContainsKey(file.DocumentId)
                    && !ControlledReferenceMaterializer.Matches(ManifestPath(target, file), file)))
                    throw new InvalidDataException("上次编辑文件在续接期间发生变化，原文件及恢复记录已保留，未覆盖或继续打开。");
            if (ValidateWorkingWorkspace(target, manifest))
            {
                foreach (var file in manifest.Files) BindWorkspaceFile(target, target, manifest, file);
                SetWorkspaceAttributes(target, manifest, editable);
                LocalCacheNotice = "已恢复本机上次编辑，未提交的修改已保留；尚未生成新的存档版本。";
                ControlledCacheMaintenance.Record(workspaceRoot, target, manifest, "working");
                return ResolveRootPath(target, manifest);
            }
        }
        var localConflicts = Directory.Exists(target)
            ? DescribeCacheConflicts(target, manifest.Files.Where(file => !manifest.RetainedWorkingHashes.ContainsKey(file.DocumentId)), editable, manifest.ProjectId, backup)
            : Array.Empty<WorkspaceCacheConflict>();
        if (localConflicts.Count > 0)
        {
            throw new WorkspaceCacheConflictException(target, localConflicts);
        }
        var changed = manifest.Files.Where(file => !manifest.RetainedWorkingHashes.ContainsKey(file.DocumentId)
            && (!File.Exists(ManifestPath(target, file)) || !ControlledReferenceMaterializer.Matches(ManifestPath(target, file), file))).ToArray();
        var conflict = changed.FirstOrDefault(file => ProtectedPaths.Any(path =>
            string.Equals(Path.GetFullPath(path), ManifestPath(target, file), StringComparison.OrdinalIgnoreCase)));
        if (conflict != null) throw new InvalidDataException("需要更新的图档正在使用或编辑中，已保留窗口及文件，请关闭相关图档后重试：" + conflict.FileName);
        var staging = Path.Combine(workspaceRoot, ".uplm", "Staging", "working-" + Guid.NewGuid().ToString("N"));
        var previousHashes = changed.ToDictionary(file => file.DocumentId, file =>
            File.Exists(ManifestPath(target, file)) ? ComputeSha256(ManifestPath(target, file)) : null);
        try
        {
            var needed = changed.Sum(file => file.NativeIsolation?.Length ?? file.FileLength)
                + changed.Where(file => File.Exists(ManifestPath(target, file))).Sum(file => new FileInfo(ManifestPath(target, file)).Length);
            var drive = new DriveInfo(Path.GetPathRoot(workspaceRoot));
            if (drive.IsReady && drive.AvailableFreeSpace < needed + 64L * 1024 * 1024)
                throw new IOException("工作区所在磁盘空间不足，未更新图档。请先在插件设置中清理缓存或释放磁盘空间。");
            RegisterLegacySnapshotsOnce(manifest);
            await DownloadManifestFilesAsync(manifest, staging, token, changed).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (RefreshProtectedPaths != null) ProtectedPaths = await RefreshProtectedPaths().ConfigureAwait(false);
            foreach (var file in changed)
            {
                var path = ManifestPath(target, file);
                var hash = File.Exists(path) ? ComputeSha256(path) : null;
                if (hash != previousHashes[file.DocumentId] || ProtectedPaths.Any(loaded => string.Equals(loaded, path, StringComparison.OrdinalIgnoreCase)))
                    throw new IOException("准备最新版期间图档被打开或产生新修改，已保留内容，未更新：" + file.FileName);
            }
            Directory.CreateDirectory(target);
            MergeManifestFiles(staging, target, changed, previousHashes);
            if (!ValidateWorkingWorkspace(target, manifest)) throw new InvalidDataException("工作区更新校验失败，未打开图档；原有修改及恢复副本已保留。");
            foreach (var file in manifest.Files) BindWorkspaceFile(target, target, manifest, file);
            SetWorkspaceAttributes(target, manifest, editable);
            ControlledCacheMaintenance.Record(workspaceRoot, target, manifest, "working");
            PreparationProgress?.Invoke("本地文件已就绪：复用" + (manifest.Files.Count - changed.Length) + "个，更新" + changed.Length + "个", manifest.Files.Count, manifest.Files.Count);
            return ResolveRootPath(target, manifest);
        }
        finally { if (Directory.Exists(staging)) DeleteDirectory(staging); }
    }

    private static bool IsWithin(string path, string directory) => !string.IsNullOrWhiteSpace(path)
        && Path.GetFullPath(path).StartsWith(Path.GetFullPath(directory).TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static bool ValidateWorkingWorkspace(string directory, ControlledOpenManifestDto manifest)
    {
        if (manifest.RetainedWorkingHashes.Count == 0) return ValidateWorkspace(directory, manifest.Files);
        if (!HasAllManifestFiles(directory, manifest.Files)) return false;
        using (var retained = new ControlledWorkingCopyScope(ResolveRootPath(directory, manifest), manifest))
            return ValidateWorkspace(directory, manifest.Files);
    }

    private static void BindWorkspaceFile(string source, string target, ControlledOpenManifestDto manifest, ControlledOpenFileDto file)
    {
        var from = ManifestPath(source, file);
        var to = ManifestPath(target, file);
        PdmDocumentIdentityStore.TryReadLocalMetadata(from, out var previous);
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase) && previous != null
            && previous.DocumentId == file.DocumentId && previous.VersionId == file.VersionId
            && (manifest == null || previous.ProjectId == manifest.ProjectId)
            && string.Equals(previous.Revision ?? "", file.Revision ?? "", StringComparison.Ordinal)
            && string.Equals(previous.Sha256, file.Sha256, StringComparison.OrdinalIgnoreCase)
            && previous.FileLength == file.FileLength
            && (file.NativeIsolation != null ? previous.NativeIsolationSnapshotId == file.NativeIsolation.ReferenceSnapshotId
                : !file.IsNativeIsolated || previous.NativeIsolationSnapshotId == file.VersionId)) return;
        // Preserve a verified technical reference projection when source == target.
        if (!PdmDocumentIdentityStore.TryWriteControlledVersion(to, file.DocumentId, manifest?.ProjectId ?? previous?.ProjectId,
            file.VersionId, file.Revision, file.Sha256, file.FileLength))
            throw new IOException("受控副本的版本记录未能保存：" + file.FileName);
        if (previous != null && previous.DocumentId == file.DocumentId && previous.VersionId == file.VersionId)
            ControlledReferenceMaterializer.CopyResolvedBinding(from, to);
    }
}
