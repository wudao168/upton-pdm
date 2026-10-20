using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public static class BrowserCadArchivePolicy
{
    public static bool CanConfigure(UserRole role) => TenantContext.Current is { } tenant
        ? tenant.IsPlatformAdministrator || tenant.HasRole(nameof(UserRole.Administrator)) || tenant.HasRole(nameof(UserRole.PlatformAdministrator))
        : role is UserRole.Administrator or UserRole.PlatformAdministrator;
}

public sealed record BrowserCadArchiveTarget(Guid? ProjectId = null, Guid? StructureId = null,
    Guid? ExpectedStructureRevisionId = null, int DrawingType = 9, string Name = "", Guid? SelectedDocumentId = null);
public sealed record BrowserCadArchiveFile(string RelativePath, long Length);
public sealed record BrowserCadArchiveView(Guid Id, string State, string Stage, IReadOnlyList<BrowserCadArchiveFile> Files,
    string? RootPath = null, string? Error = null, int ArchivedFiles = 0, int NewVersions = 0,
    string? ResultCode = null, IReadOnlyList<string>? Warnings = null);
public sealed record BrowserCadArchiveCapability(bool Enabled, bool CanArchive, bool CanConfigure, string? Reason = null);
public sealed record BrowserCadNativeFile(Guid Key, string RelativePath, string FileName, long Length, string Sha256,
    DocumentReferenceNode Root, IReadOnlyDictionary<string, string?> Properties, string? PreviewPath = null, string? InputRelativePath = null);
public sealed record BrowserCadNativeResult(bool Success, Guid RootKey, IReadOnlyList<BrowserCadNativeFile> Files,
    string? Error = null, IReadOnlyList<string>? Warnings = null);
public sealed record BrowserCadNativeRequest(string Directory, string RootPath, bool Previews);
public interface IBrowserCadReader
{
    Task<BrowserCadNativeResult> ReadAsync(string directory, BrowserCadNativeRequest input, CancellationToken ct);
}
