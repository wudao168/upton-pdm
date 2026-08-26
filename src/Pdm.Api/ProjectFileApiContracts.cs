namespace Upton.Pdm.Api;

public sealed record StartProjectFileUploadRequest(string FileName, long TotalLength, string Sha256);
public sealed record CompleteProjectFileUploadRequest(string? Comment);
public sealed record CreateProjectFolderRequest(string Name);
public sealed record RenameProjectEntryRequest(string Name);
public sealed record MoveProjectEntryRequest(Guid FolderId);
