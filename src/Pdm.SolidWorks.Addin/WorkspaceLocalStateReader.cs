#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace Upton.Pdm.SolidWorks;

internal sealed class WorkspaceDocumentStateRequest
{
    public Guid DocumentId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string LatestRevision { get; set; } = string.Empty;
    public string CheckedOutBy { get; set; } = string.Empty;
}

internal sealed class WorkspaceLocalFileState
{
    public Guid DocumentId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public string LocalState { get; set; } = "NotDownloaded";
    public string LocalStateLabel { get; set; } = "未下载";
    public string LocalRevision { get; set; } = string.Empty;
    public string LatestRevision { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public bool IsReadOnly { get; set; }
    public DateTime? LastWriteTimeUtc { get; set; }
    public Guid? BoundVersionId { get; set; }
    public string VerificationKey { get; set; } = string.Empty;
    public string VerificationDetails { get; set; } = string.Empty;
    public string CheckedOutBy { get; set; } = string.Empty;
    public string ContentSha256 { get; set; } = string.Empty;
}

internal sealed class WorkspaceLocalStateSnapshot
{
    public Guid ProjectId { get; set; }
    public string ProjectCode { get; set; } = string.Empty;
    public string ProjectDirectory { get; set; } = string.Empty;
    public bool ProjectDirectoryExists { get; set; }
    public List<WorkspaceLocalFileState> Items { get; set; } = new List<WorkspaceLocalFileState>();
}

internal static class WorkspaceLocalStateReader
{
    public static WorkspaceLocalStateSnapshot Read(
        string workspaceRoot,
        Guid projectId,
        string projectCode,
        string currentUsername,
        IReadOnlyCollection<WorkspaceDocumentStateRequest> documents)
    {
        var projectDirectory = ProjectDirectory(workspaceRoot, projectCode);
        var result = new WorkspaceLocalStateSnapshot
        {
            ProjectId = projectId,
            ProjectCode = projectCode ?? string.Empty,
            ProjectDirectory = projectDirectory,
            ProjectDirectoryExists = Directory.Exists(projectDirectory)
        };
        var uniqueDocuments = (documents ?? Array.Empty<WorkspaceDocumentStateRequest>())
            .Where(item => item != null && item.DocumentId != Guid.Empty)
            .GroupBy(item => item.DocumentId)
            .Select(group => group.First())
            .ToArray();
        if (!result.ProjectDirectoryExists)
        {
            result.Items.AddRange(uniqueDocuments.Select(NotDownloaded));
            return result;
        }

        var pathsByDocument = new Dictionary<Guid, List<Tuple<string, PdmControlledFileMetadata>>>();
        foreach (var path in Directory.EnumerateFiles(projectDirectory, "*", SearchOption.AllDirectories))
        {
            if (!PdmDocumentIdentityStore.TryReadControlledMetadata(path, out var metadata)
                || metadata.DocumentId == Guid.Empty
                || (metadata.ProjectId.HasValue && metadata.ProjectId.Value != projectId))
            {
                continue;
            }
            if (!pathsByDocument.TryGetValue(metadata.DocumentId, out var paths))
                pathsByDocument[metadata.DocumentId] = paths = new List<Tuple<string, PdmControlledFileMetadata>>();
            paths.Add(Tuple.Create(path, metadata));
        }

        foreach (var document in uniqueDocuments)
        {
            if (!pathsByDocument.TryGetValue(document.DocumentId, out var paths))
            {
                result.Items.Add(NotDownloaded(document));
                continue;
            }
            var states = paths.Select(local => Evaluate(document, local.Item1, local.Item2, currentUsername)).ToArray();
            var edits = states.Where(item => item.LocalState == "Modified").ToArray();
            var valid = states.Where(item => item.LocalState != "IntegrityMismatch"
                && paths.Any(local => local.Item1 == item.FullPath && !string.IsNullOrWhiteSpace(local.Item2.Sha256))).ToArray();
            // Preserve real writable edits. Damaged read-only caches must not displace a valid working copy.
            var candidates = edits.Length > 0 ? edits : valid.Length > 0 ? valid : states;
            var changed = candidates.Where(item => item.LocalState == "Modified" || item.LocalState == "IntegrityMismatch").ToArray();
            if (changed.Length > 1 && changed.Select(item => item.ContentSha256).Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any())
            {
                result.Items.Add(State(document, string.Empty, null, "IdentityConflict", "身份冲突", false,
                    "同一图档存在内容不同的多个修改副本，请先备份并确认需要保留的文件：\r\n" + string.Join("\r\n", changed.Select(item => item.FullPath))));
                continue;
            }
            var selected = candidates
                .OrderByDescending(item => string.Equals(item.LocalRevision, document.LatestRevision, StringComparison.OrdinalIgnoreCase))
                .ThenBy(item => item.IsReadOnly)
                .ThenBy(item => item.FullPath.IndexOf("图档缓存", StringComparison.OrdinalIgnoreCase) >= 0)
                .ThenBy(item => item.FullPath, StringComparer.OrdinalIgnoreCase).First();
            var damaged = states.Where(item => item != selected && item.LocalState == "IntegrityMismatch").ToArray();
            if (damaged.Length > 0)
                selected.Message += " 另有只读副本校验不一致，未自动覆盖或删除，请核对：" + string.Join("；", damaged.Select(item => item.FullPath));
            result.Items.Add(selected);
        }
        return result;
    }

    public static string ProjectDirectory(string workspaceRoot, string projectCode)
    {
        var root = Path.GetFullPath(workspaceRoot ?? throw new ArgumentNullException(nameof(workspaceRoot)));
        return Path.Combine(root, "View", SafeSegment(projectCode));
    }

    internal static bool RecoverArchivedBinding(WorkspaceLocalFileState state, Guid projectId, Guid versionId,
        string revision, string storedSha256, string sourceSha256)
    {
        if (state == null || state.LocalState != "IntegrityMismatch" || versionId == Guid.Empty
            || (!string.Equals(revision, state.LatestRevision, StringComparison.OrdinalIgnoreCase)
                && versionId != state.BoundVersionId)
            || !PdmDocumentIdentityStore.TryReadControlledMetadata(state.FullPath, out var metadata)
            || metadata.DocumentId != state.DocumentId || metadata.ProjectId != projectId
            || (File.GetAttributes(state.FullPath) & FileAttributes.ReadOnly) == 0) return false;
        // Keep the verified bytes fixed until the conditional binding write completes.
        using (var source = new FileStream(state.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var sha256 = SHA256.Create())
        {
            var hash = BitConverter.ToString(sha256.ComputeHash(source)).Replace("-", string.Empty);
            state.VerificationDetails = "本地SHA256=" + hash + "；绑定SHA256=" + metadata.Sha256
                + "；绑定版本=" + metadata.VersionId + "；服务器版本=" + versionId
                + "；服务器SHA256=" + storedSha256 + "；源文件SHA256=" + sourceSha256;
            if (!string.Equals(hash, storedSha256, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(hash, sourceSha256, StringComparison.OrdinalIgnoreCase)) return false;
            // The add-in can finish writing this exact binding while the desktop awaits HTTP.
            // Accept an already verified binding, but never overwrite a different concurrent edit.
            if (metadata.VersionId != state.BoundVersionId)
                return metadata.VersionId == versionId && metadata.Revision == revision
                    && metadata.FileLength == source.Length
                    && string.Equals(hash, metadata.Sha256, StringComparison.OrdinalIgnoreCase);
            return PdmDocumentIdentityStore.TryRecoverControlledVersion(state.FullPath, metadata, projectId,
                versionId, revision, hash, source.Length);
        }
    }

    internal static WorkspaceLocalFileState Recheck(WorkspaceLocalFileState state, Guid projectId, string currentUsername)
    {
        if (!PdmDocumentIdentityStore.TryReadControlledMetadata(state.FullPath, out var metadata)
            || metadata.DocumentId != state.DocumentId || metadata.ProjectId != projectId) return state;
        return Evaluate(new WorkspaceDocumentStateRequest { DocumentId = state.DocumentId, FileName = state.FileName,
            LatestRevision = state.LatestRevision, CheckedOutBy = state.CheckedOutBy }, state.FullPath, metadata, currentUsername);
    }

    private static WorkspaceLocalFileState Evaluate(
        WorkspaceDocumentStateRequest document,
        string path,
        PdmControlledFileMetadata metadata,
        string currentUsername)
    {
        string hash;
        try { hash = ComputeHash(path); }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
        {
            return State(document, path, metadata, "NeedsUpdate", "等待校验", true,
                "文件正在保存、替换或暂时无法读取，请等待操作完成后刷新；本地文件已保留。");
        }
        // Enumeration may have captured metadata before the archive finished writing it.
        if (PdmDocumentIdentityStore.TryReadControlledMetadata(path, out var current))
        {
            if (current.DocumentId != metadata.DocumentId || current.ProjectId != metadata.ProjectId)
                return State(document, path, metadata, "NeedsUpdate", "等待校验", true,
                    "文件关联记录正在更新，请等待存档完成后刷新。");
            metadata = current;
        }
        else return State(document, path, metadata, "NeedsUpdate", "等待校验", true,
            "当前无法确认文件关联记录，请等待存档完成后刷新。");
        var attributes = File.GetAttributes(path);
        var readOnly = (attributes & FileAttributes.ReadOnly) != 0;
        var info = new FileInfo(path);
        // ADS/attribute writes can change timestamps without changing CAD content.
        var verificationKey = string.Join("|", path, metadata?.VersionId, metadata?.Revision, metadata?.Sha256,
            metadata?.FileLength, info.Length, hash, document.LatestRevision);
        var changed = !string.IsNullOrWhiteSpace(metadata?.Sha256)
            && (metadata.FileLength.HasValue && info.Length != metadata.FileLength.Value
                || !string.Equals(hash, metadata.Sha256, StringComparison.OrdinalIgnoreCase));
        var checkedOutByMe = !string.IsNullOrWhiteSpace(currentUsername)
            && string.Equals(document.CheckedOutBy, currentUsername, StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(metadata?.Sha256) || !metadata.VersionId.HasValue
            || metadata.VersionId == Guid.Empty || string.IsNullOrWhiteSpace(metadata.Revision) || !metadata.FileLength.HasValue)
            return State(document, path, metadata, "NeedsUpdate", "需要校验", readOnly,
                "本地校验记录不完整，无法确认文件是否已存档。请先备份本地文件，再核对服务器版本。");
        if (changed)
        {
            var state = State(document, path, metadata, readOnly ? "IntegrityMismatch" : "Modified",
                readOnly ? "文件异常" : "待签入", readOnly,
                readOnly ? "只读缓存内容与获取时不一致，请重新获取最新版。" : "本地内容已修改，确认保存后请提交存档。" );
            state.VerificationKey = verificationKey;
            state.ContentSha256 = hash;
            state.VerificationDetails = "本地SHA256=" + hash + "；绑定SHA256=" + metadata.Sha256
                + "；本地长度=" + info.Length + "；绑定长度=" + metadata.FileLength + "；绑定版本=" + metadata.VersionId;
            return state;
        }
        if (checkedOutByMe)
        {
            return readOnly
                ? State(document, path, metadata, "PermissionMismatch", "权限异常", true, "你已检出，但本地文件仍为只读，请重新获取编辑权限。")
                : State(document, path, metadata, "Editable", "可编辑", false, "已由你检出，可以继续编辑。" );
        }
        if (!readOnly)
        {
            return State(document, path, metadata, "UnexpectedWritable", "异常可写", false, "文件未由你检出但可以写入，请勿修改并重新获取最新版。" );
        }
        if (!string.IsNullOrWhiteSpace(metadata?.Revision)
            && !string.IsNullOrWhiteSpace(document.LatestRevision)
            && !string.Equals(metadata.Revision, document.LatestRevision, StringComparison.OrdinalIgnoreCase))
        {
            return State(document, path, metadata, "NeedsUpdate", "需要更新", true,
                string.Concat("本地 ", metadata.Revision, "，PLM最新 ", document.LatestRevision, "。"));
        }
        return State(document, path, metadata, "ReadOnlyCache", "只读缓存", true, "本地文件与已记录受控版本一致。" );
    }

    private static WorkspaceLocalFileState NotDownloaded(WorkspaceDocumentStateRequest document) =>
        State(document, string.Empty, null, "NotDownloaded", "未下载", true, "首次打开时将自动获取到项目工作区。" );

    private static WorkspaceLocalFileState State(
        WorkspaceDocumentStateRequest document,
        string path,
        PdmControlledFileMetadata metadata,
        string state,
        string label,
        bool readOnly,
        string message)
    {
        return new WorkspaceLocalFileState
        {
            DocumentId = document.DocumentId,
            FileName = document.FileName ?? string.Empty,
            FullPath = path ?? string.Empty,
            LocalState = state,
            LocalStateLabel = label,
            LocalRevision = metadata?.Revision ?? string.Empty,
            LatestRevision = document.LatestRevision ?? string.Empty,
            BoundVersionId = metadata?.VersionId,
            CheckedOutBy = document.CheckedOutBy,
            Message = message ?? string.Empty,
            IsReadOnly = readOnly,
            LastWriteTimeUtc = !string.IsNullOrWhiteSpace(path) && File.Exists(path)
                ? File.GetLastWriteTimeUtc(path)
                : (DateTime?)null
        };
    }

    private static string ComputeHash(string path)
    {
        try
        {
            // Most cache files have no writer: one protected read is enough.
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(input)).Replace("-", string.Empty);
        }
        catch (IOException error) when ((error.HResult & 0xffff) == 32 || (error.HResult & 0xffff) == 33)
        {
            // Loaded CAD documents may retain writable handles; verify two independent reads.
            return ArchiveFileFingerprint.Read(path);
        }
    }

    private static string SafeSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var characters = (value ?? string.Empty).Select(character => invalid.Contains(character) ? '_' : character).ToArray();
        var result = new string(characters).Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(result) ? "project" : result;
    }
}
