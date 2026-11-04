using System;
using System.Collections.Generic;
using System.IO;

namespace Upton.Pdm.SolidWorks;

internal enum WorkspaceConflictAction { Cancel, RestoreEditing, BackupAndGetLatest }

internal sealed class WorkspaceCacheConflict
{
    public string Path { get; set; }
    public string FileName { get; set; }
    public Guid DocumentId { get; set; }
    public string LocalRevision { get; set; }
    public string RequiredRevision { get; set; }
    public Guid RequiredVersionId { get; set; }
    public string RequiredSha256 { get; set; }
    public string ActualSha256 { get; set; }
    public string Reason { get; set; }
    public FileAttributes Attributes { get; set; }
    public PdmControlledFileMetadata Binding { get; set; }
}

internal sealed class WorkspaceCacheConflictException : IOException
{
    internal WorkspaceCacheConflictException(string directory, IReadOnlyList<WorkspaceCacheConflict> files)
        : base("工作区有" + files.Count + "份图档需要核对，请在工作区选择恢复编辑或保留备份后打开PLM最新版。")
    { Directory = directory; Files = files; }
    internal string Directory { get; }
    internal IReadOnlyList<WorkspaceCacheConflict> Files { get; }
}

internal sealed class WorkspaceCacheBackup
{
    internal IReadOnlyList<LocalEditDraft> PreservedEdits { get; set; } = Array.Empty<LocalEditDraft>();
    public string WorkingDirectory { get; set; }
    public string RecoveryDirectory { get; set; }
    public string ManifestKey { get; set; }
    public List<WorkspaceCacheConflict> Files { get; set; }
}
