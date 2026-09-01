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

        var pathsByDocument = new Dictionary<Guid, Tuple<string, PdmControlledFileMetadata>>();
        var ambiguousDocuments = new HashSet<Guid>();
        foreach (var path in Directory.EnumerateFiles(projectDirectory, "*", SearchOption.AllDirectories))
        {
            if (!PdmDocumentIdentityStore.TryReadControlledMetadata(path, out var metadata)
                || metadata.DocumentId == Guid.Empty
                || (metadata.ProjectId.HasValue && metadata.ProjectId.Value != projectId))
            {
                continue;
            }
            if (pathsByDocument.TryGetValue(metadata.DocumentId, out var existing)
                && !string.Equals(existing.Item1, path, StringComparison.OrdinalIgnoreCase))
            {
                ambiguousDocuments.Add(metadata.DocumentId);
                continue;
            }
            pathsByDocument[metadata.DocumentId] = Tuple.Create(path, metadata);
        }

        foreach (var document in uniqueDocuments)
        {
            if (ambiguousDocuments.Contains(document.DocumentId))
            {
                result.Items.Add(State(document, string.Empty, null, "IdentityConflict", "身份冲突", false,
                    "同一图档在项目工作区中对应多个文件，已停止自动关联。"));
                continue;
            }
            if (!pathsByDocument.TryGetValue(document.DocumentId, out var local))
            {
                result.Items.Add(NotDownloaded(document));
                continue;
            }
            result.Items.Add(Evaluate(document, local.Item1, local.Item2, currentUsername));
        }
        return result;
    }

    public static string ProjectDirectory(string workspaceRoot, string projectCode)
    {
        var root = Path.GetFullPath(workspaceRoot ?? throw new ArgumentNullException(nameof(workspaceRoot)));
        return Path.Combine(root, "View", SafeSegment(projectCode));
    }

    private static WorkspaceLocalFileState Evaluate(
        WorkspaceDocumentStateRequest document,
        string path,
        PdmControlledFileMetadata metadata,
        string currentUsername)
    {
        var attributes = File.GetAttributes(path);
        var readOnly = (attributes & FileAttributes.ReadOnly) != 0;
        var changed = HasChanged(path, metadata);
        var checkedOutByMe = !string.IsNullOrWhiteSpace(currentUsername)
            && string.Equals(document.CheckedOutBy, currentUsername, StringComparison.OrdinalIgnoreCase);
        if (changed)
        {
            return State(document, path, metadata, readOnly ? "IntegrityMismatch" : "Modified",
                readOnly ? "文件异常" : "待签入", readOnly,
                readOnly ? "只读缓存内容与获取时不一致，请重新获取最新版。" : "本地内容已修改，确认保存后请提交存档。" );
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
            Message = message ?? string.Empty,
            IsReadOnly = readOnly,
            LastWriteTimeUtc = !string.IsNullOrWhiteSpace(path) && File.Exists(path)
                ? File.GetLastWriteTimeUtc(path)
                : (DateTime?)null
        };
    }

    private static bool HasChanged(string path, PdmControlledFileMetadata metadata)
    {
        if (metadata == null || string.IsNullOrWhiteSpace(metadata.Sha256)) return false;
        var info = new FileInfo(path);
        if (metadata.FileLength.HasValue && info.Length != metadata.FileLength.Value) return true;
        using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var hash = SHA256.Create())
        {
            return !string.Equals(
                BitConverter.ToString(hash.ComputeHash(input)).Replace("-", string.Empty),
                metadata.Sha256,
                StringComparison.OrdinalIgnoreCase);
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
