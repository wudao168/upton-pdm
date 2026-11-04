using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Upton.Pdm.SolidWorks;

internal sealed partial class ControlledWorkspaceManager
{
    internal WorkspaceCacheConflictException FindWorkingConflicts(ControlledOpenManifestDto manifest, IReadOnlyCollection<Guid> editableIds)
    {
        var directory = WorkingDirectory(manifest);
        var files = DescribeCacheConflicts(directory, manifest.Files, editableIds, manifest.ProjectId);
        return files.Count == 0 ? null : new WorkspaceCacheConflictException(directory, files);
    }

    private static string CacheManifestKey(ControlledOpenManifestDto manifest)
    {
        using var sha = SHA256.Create();
        var value = manifest.ProjectId + "|" + manifest.RootDocumentId + "|" + string.Join("|", manifest.Files
            .OrderBy(file => file.DocumentId).Select(file => file.DocumentId + ":" + file.VersionId + ":" + file.Sha256 + ":" + file.FileLength
                + ":" + file.RelativePath + ":" + file.Configuration + ":" + file.IsNativeIsolated + ":" + file.NativeIsolation?.ReferenceSnapshotId
                + ":" + file.NativeIsolation?.Sha256 + ":" + file.NativeIsolation?.Length));
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "");
    }

    internal WorkspaceCacheBackup BackupWorkingConflicts(ControlledOpenManifestDto manifest, WorkspaceCacheConflictException conflict,
        CancellationToken token)
    {
        var directory = WorkingDirectory(manifest);
        if (!string.Equals(directory, conflict.Directory, StringComparison.OrdinalIgnoreCase)
            || !IsWithin(directory, workspaceRoot)) throw new IOException("备份目录与当前工作区不一致，未更新文件。");
        var backup = new WorkspaceCacheBackup { WorkingDirectory = directory, ManifestKey = CacheManifestKey(manifest),
            RecoveryDirectory = RecoveryDirectory("open-conflict"), Files = conflict.Files.ToList() };
        foreach (var file in backup.Files)
        {
            token.ThrowIfCancellationRequested();
            var required = manifest.Files.Single(item => item.DocumentId == file.DocumentId);
            if (!string.Equals(ManifestPath(directory, required), file.Path, StringComparison.OrdinalIgnoreCase)
                || required.VersionId != file.RequiredVersionId || required.Sha256 != file.RequiredSha256
                || ProtectedPaths.Any(path => string.Equals(path, file.Path, StringComparison.OrdinalIgnoreCase))
                || ComputeSha256(file.Path) != file.ActualSha256)
                throw new IOException("图档状态在确认后发生变化或仍在打开，原文件已保留，未开始更新：" + file.FileName);
            var target = ManifestPath(backup.RecoveryDirectory, required);
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            CopyFileAtomically(file.Path, target);
            if (ComputeSha256(target) != file.ActualSha256 || ComputeSha256(file.Path) != file.ActualSha256)
                throw new IOException("工作区备份未通过校验，原文件已保留：" + file.FileName);
        }
        var json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
        File.WriteAllText(Path.Combine(backup.RecoveryDirectory, "workspace-recovery.json"), json.Serialize(backup), Encoding.UTF8);
        return backup;
    }

    internal Task<string> PrepareBackedUpWorkingCopyAsync(ControlledOpenManifestDto manifest, IReadOnlyCollection<Guid> editableIds,
        WorkspaceCacheBackup backup, CancellationToken token)
        => Task.Run(async () =>
    {
        if (backup.ManifestKey != CacheManifestKey(manifest)
            || !string.Equals(backup.WorkingDirectory, WorkingDirectory(manifest), StringComparison.OrdinalIgnoreCase))
            throw new IOException("服务器版本清单在确认后发生变化，备份和原文件已保留，请重新打开。");
        foreach (var file in backup.Files)
        {
            var required = manifest.Files.Single(item => item.DocumentId == file.DocumentId);
            if (ComputeSha256(ManifestPath(backup.RecoveryDirectory, required)) != file.ActualSha256)
                throw new IOException("恢复备份已变化，原工作文件未更新，请核对后重试。");
        }
        var path = await UpdateWorkingCacheAsync(manifest, backup.WorkingDirectory, editableIds, token, backup).ConfigureAwait(false);
        LocalCacheNotice = "已保留本机差异文件及恢复记录备份，并取得PLM版本。备份位置：" + backup.RecoveryDirectory;
        return path;
    }, token);

    internal static bool SynchronizeConfirmedCache(string workspaceRoot, string projectCode, string source,
        Guid documentId, Guid projectId, DocumentVersionDto version, string uploadCopy, string sourceHash,
        IReadOnlyCollection<string> protectedPaths)
    {
        if (protectedPaths == null || version == null || string.IsNullOrWhiteSpace(uploadCopy) || !File.Exists(uploadCopy)
            || new FileInfo(uploadCopy).Length != version.FileLength || ComputeSha256(uploadCopy) != version.Sha256) return false;
        var project = ProjectViewDirectory(workspaceRoot, projectCode);
        var target = IsWithin(source, project) ? Path.GetFullPath(source)
            : Path.Combine(project, "图档缓存", documentId.ToString("N"), Path.GetFileName(source));
        if (!File.Exists(target) || protectedPaths.Any(path => string.Equals(path, target, StringComparison.OrdinalIgnoreCase))) return false;
        if (!PdmDocumentIdentityStore.TryReadLocalMetadata(target, out var binding)
            || binding.DocumentId != documentId || binding.ProjectId != projectId) return false;
        using var original = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var before = ComputeSha256(target);
        var sameSource = string.Equals(target, Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase);
        if (sameSource && before != sourceHash) return false;
        var confirmedSource = sameSource && before == sourceHash;
        if (!confirmedSource && before != binding.Sha256 && before != binding.ResolvedReferenceSha256) return false;
        var backup = Path.Combine(workspaceRoot, ".uplm", "Recovery", "cache-sync-" + Guid.NewGuid().ToString("N"), Path.GetFileName(source));
        var attributes = File.GetAttributes(target);
        Directory.CreateDirectory(Path.GetDirectoryName(backup));
        CopyFileAtomically(target, backup);
        if (ComputeSha256(backup) != before || ComputeSha256(target) != before)
            throw new IOException("存档后缓存备份未通过校验，工作文件保持原状。");
        try
        {
            CopyFileAtomically(uploadCopy, target);
            if (ComputeSha256(target) != version.Sha256
                || !PdmDocumentIdentityStore.TryWriteControlledVersion(target, documentId, projectId,
                    version.Id, version.Revision?.Display, version.Sha256, version.FileLength))
                throw new IOException("存档后的缓存版本记录尚未同步。");
            File.SetAttributes(target, File.GetAttributes(target) | FileAttributes.ReadOnly);
        }
        catch
        {
            CopyFileAtomically(backup, target);
            File.SetAttributes(target, attributes);
            PdmDocumentIdentityStore.RestoreLocalMetadata(target, binding);
            throw;
        }
        try { File.Delete(backup); Directory.Delete(Path.GetDirectoryName(backup), false); }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
        { PluginDiagnosticLog.Record("缓存同步", "新版缓存已核对，旧备份暂保留：" + error); }
        return true;
    }
}
