using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed record AssemblyPreviewFile(string Path, Guid VersionId, long FileLength, string Sha256);

public interface IAssemblyPreviewConverter
{
    Task<AssemblyPreviewFile> GenerateAssemblyAsync(ControlledOpenManifest manifest, Project project, CancellationToken cancellationToken, string? initiator = null);
}
