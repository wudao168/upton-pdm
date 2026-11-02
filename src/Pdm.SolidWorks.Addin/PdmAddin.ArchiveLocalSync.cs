using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Upton.Pdm.LocalSettings;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    private sealed class ArchiveLocalSyncJob
    {
        internal string Path, UploadCopyPath, SourceHash, ProjectCode, WorkspaceRoot, User, Scope;
        internal Guid DocumentId, ProjectId;
        internal DocumentVersionDto Version;
        internal bool SyncIdentity;
        internal bool MemorySnapshot;
        internal EditDraftJournal Journal;
    }

    private sealed class ArchiveSourceStamp
    {
        internal bool Exists;
        internal long Length, Written, Created;
        internal static ArchiveSourceStamp Read(string path)
        {
            var file = new FileInfo(path);
            return file.Exists ? new ArchiveSourceStamp { Exists = true, Length = file.Length,
                Written = file.LastWriteTimeUtc.Ticks, Created = file.CreationTimeUtc.Ticks } : new ArchiveSourceStamp();
        }
        internal bool Matches(string path)
        {
            var other = Read(path);
            return Exists == other.Exists && Length == other.Length && Written == other.Written && Created == other.Created;
        }
    }

    private sealed class ArchiveLocalSyncResult
    {
        internal string Warning;
        internal bool PreserveWarning, LaterDiskChanges;
        internal PdmControlledFileMetadata Recorded;
        internal ArchiveSourceStamp SourceStamp;
        internal Exception JournalError;
    }

    private ArchiveLocalSyncJob CreateArchiveLocalSyncJob(string path, Guid documentId, Guid projectId,
        DocumentVersionDto version, string uploadCopyPath, string sourceHash = null, bool syncIdentity = true,
        EditDraftJournal journal = null)
        => new ArchiveLocalSyncJob { Path = path, DocumentId = documentId, ProjectId = projectId, Version = version,
            UploadCopyPath = uploadCopyPath, SourceHash = sourceHash, SyncIdentity = syncIdentity, Journal = journal,
            ProjectCode = availableProjects.FirstOrDefault(project => project.Id == projectId)?.Code,
            WorkspaceRoot = WorkspaceSettingsStore.GetWorkspaceRoot(), User = authenticatedUsername, Scope = apiClient?.EditJournalScope };

    private async Task<ArchiveLocalSyncResult> RememberControlledVersionIdentityAndManifestAsync(
        string path, Guid documentId, Guid projectId, DocumentVersionDto version, string uploadCopyPath)
    {
        var job = CreateArchiveLocalSyncJob(path, documentId, projectId, version, uploadCopyPath);
        var result = (await RunArchiveLocalSyncJobsAsync(new[] { job }))[0];
        ApplyArchiveLocalSyncResult(job, result);
        return result;
    }

    // Only file facts and the immutable server receipt enter this worker. No CAD
    // objects or tree nodes cross the boundary. One return per bounded batch.
    private static Task<ArchiveLocalSyncResult[]> RunArchiveLocalSyncJobsAsync(ArchiveLocalSyncJob[] jobs)
        => Task.Run(() =>
        {
            var timer = Stopwatch.StartNew();
            var results = jobs.Select(RunArchiveLocalSyncJob).ToArray();
            LogOperation("存档同步批次后台完成；文件数=" + jobs.Length + "；耗时毫秒=" + timer.ElapsedMilliseconds);
            return results;
        });

    private static ArchiveLocalSyncResult RunArchiveLocalSyncJob(ArchiveLocalSyncJob job)
    {
        var result = new ArchiveLocalSyncResult();
        var cacheStarted = false;
        if (job.Journal != null)
        {
            var timer = Stopwatch.StartNew();
            try { FinishLocalEditRecords(job.Journal, job.DocumentId); }
            catch (Exception error) { result.JournalError = error; }
            finally { LogOperation("存档结果同步分段：结束图档编辑记录；文件=" + job.Path + "；耗时毫秒=" + timer.ElapsedMilliseconds); }
        }
        if (job.SyncIdentity)
        {
            try
            {
                ArchiveSession.Current?.ReportFile(0, 0, System.IO.Path.GetFileName(job.Path), "服务器已提交：正在后台核对版本记录及缓存…");
                var timer = Stopwatch.StartNew();
                try
                {
                    if (!job.MemorySnapshot)
                    {
                        RememberControlledVersionIdentity(job.Path, job.DocumentId, job.ProjectId, job.Version);
                        if (!PdmDocumentIdentityStore.TryReadLocalMetadata(job.Path, out var metadata))
                            throw new IOException("无法读取刚写入的本地校验记录。");
                        result.Recorded = metadata;
                    }
                    else if (!PdmDocumentIdentityStore.TryWriteAssociationOnly(job.Path, job.DocumentId, job.ProjectId))
                        throw new IOException("无法记录已存档图档的本机身份关联，源文件未改写。");
                }
                finally { LogOperation("存档结果同步分段：核对源文件并写入版本记录；文件=" + job.Path + "；耗时毫秒=" + timer.ElapsedMilliseconds); }
                if (!string.IsNullOrWhiteSpace(job.ProjectCode))
                {
                    cacheStarted = true;
                    timer.Restart();
                    var cached = CacheCheckedInVersionInProjectViewCore(job.WorkspaceRoot, job.ProjectCode, job.Path,
                        job.DocumentId, job.ProjectId, job.Version, job.UploadCopyPath);
                    LogOperation("存档结果同步分段：核对并同步本地缓存；文件=" + job.Path + "；复用上传副本="
                        + !string.IsNullOrWhiteSpace(job.UploadCopyPath) + "；耗时毫秒=" + timer.ElapsedMilliseconds);
                    if (string.IsNullOrWhiteSpace(cached)) throw new IOException("工作区缓存未同步，已有副本可能包含尚未保存到服务器的内容。");
                    if (!PathsEqual(cached, job.Path)) LogOperation("Checked-in file cached in project workspace source=" + job.Path + " target=" + cached);
                }
            }
            catch (Exception error)
            {
                LogDiagnostic("Cache checked-in file in project workspace." + System.IO.Path.GetFileName(job.Path), error);
                result.PreserveWarning = cacheStarted;
                result.Warning = System.IO.Path.GetFileName(job.Path) + (job.MemorySnapshot
                    ? "：当前状态副本已在服务器存档，源文件未改写。本机缓存暂未同步或已有副本被保留，可从PLM获取已存档版本；不需要解除源文件只读。"
                    : "：服务器存档已完成，但本地文件或校验记录未能同步。请先备份本地文件，再检查磁盘空间及权限并重新获取；若本地有新增修改，请获取编辑权限后重新存档。");
            }
        }
        RefreshArchiveSyncSource(job, result);
        return result;
    }

    private static void RefreshArchiveSyncSource(ArchiveLocalSyncJob job, ArchiveLocalSyncResult result)
    {
        if (job.SourceHash == null) return;
        var timer = Stopwatch.StartNew();
        try
        {
            result.LaterDiskChanges = File.Exists(job.Path) && !string.Equals(job.SourceHash, ComputeFileHash(job.Path), StringComparison.OrdinalIgnoreCase);
            result.SourceStamp = ArchiveSourceStamp.Read(job.Path);
        }
        catch (Exception error)
        {
            result.LaterDiskChanges = true; result.PreserveWarning = true;
            result.Warning = (string.IsNullOrWhiteSpace(result.Warning) ? string.Empty : result.Warning + "\n")
                + System.IO.Path.GetFileName(job.Path) + "：服务器已存档，本地后续修改暂未核对完成，请保留文件并刷新核对。";
            LogDiagnostic("同步后核对源文件", error);
        }
        finally { LogOperation("存档结果同步分段：复核源文件后续修改；文件=" + job.Path + "；耗时毫秒=" + timer.ElapsedMilliseconds); }
    }

    private static bool ArchiveSyncSourceChanged(ArchiveLocalSyncJob job, ArchiveLocalSyncResult result)
    {
        if (result.LaterDiskChanges || result.SourceStamp == null) return false;
        try { return !result.SourceStamp.Matches(job.Path); }
        catch (Exception) { return true; }
    }

    private bool ApplyArchiveLocalSyncResult(ArchiveLocalSyncJob job, ArchiveLocalSyncResult result)
    {
        if (job.User != authenticatedUsername || job.Scope != apiClient?.EditJournalScope)
        {
            result.PreserveWarning = true;
            result.Warning = (string.IsNullOrWhiteSpace(result.Warning) ? string.Empty : result.Warning + "\n")
                + System.IO.Path.GetFileName(job.Path) + "：服务器已存档，账号或公司已切换，请在对应项目刷新本地状态。";
            return false;
        }
        shownEditRequests.Remove(job.DocumentId);
        if (job.Journal != null)
        {
            if (result.JournalError != null) ReportEditMaintenanceError(result.JournalError);
            else { localEdits.Remove(job.DocumentId); lock (checkoutDocumentSync) pendingCloseDocumentIds.Remove(job.DocumentId); }
        }
        var recorded = result.Recorded;
        if (recorded == null) return true;
        var fullPath = System.IO.Path.GetFullPath(job.Path); var version = job.Version; var revision = version.Revision?.Display ?? string.Empty;
        completedWorkingArchives[fullPath] = new CompletedWorkingArchive { ProjectId = job.ProjectId, DocumentId = job.DocumentId,
            VersionId = version.Id, StoredSha256 = version.Sha256, StoredLength = version.FileLength,
            SourceSha256 = recorded.Sha256, SourceLength = recorded.FileLength.Value, Username = job.User, ServerScope = job.Scope };
        foreach (var context in controlledOpenManifests.Where(pair => pair.Value?.ProjectId == job.ProjectId && IsPathWithinDirectory(fullPath, pair.Key)).ToArray())
        {
            foreach (var file in (context.Value.Files ?? new List<ControlledOpenFileDto>()).Where(file => file.DocumentId == job.DocumentId))
            { file.VersionId = version.Id; file.Revision = revision; file.Sha256 = recorded.Sha256; file.FileLength = recorded.FileLength.Value; }
            if (context.Value.RootDocumentId == job.DocumentId) { context.Value.RootVersionId = version.Id; context.Value.RootRevision = revision; }
        }
        return true;
    }
}
