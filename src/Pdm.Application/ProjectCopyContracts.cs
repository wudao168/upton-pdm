using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed record ProjectCopyOptions(
    bool CopyModels = true,
    bool CopyDrawings = true,
    bool CopyBom = true,
    bool CopyValidationItems = true,
    IReadOnlyList<Guid>? FolderIds = null);

public sealed record ProjectCopyFolderOption(
    Guid Id,
    string Name,
    string Path,
    string TemplateKey,
    int FileCount,
    long TotalBytes,
    bool DefaultSelected);

public sealed record ProjectCopyPreview(
    Guid SourceProjectId,
    Guid TargetProjectId,
    int ModelCount,
    int DrawingCount,
    int BomItemCount,
    int ValidationItemCount,
    int ProjectFileCount,
    long TotalBytes,
    IReadOnlyList<ProjectCopyFolderOption> Folders,
    IReadOnlyList<string> BlockingReasons,
    IReadOnlyList<string> Warnings)
{
    public bool CanExecute => BlockingReasons.Count == 0;
}

public sealed record ProjectCopyResult(
    Guid SourceProjectId,
    Guid TargetProjectId,
    int DocumentCount,
    int BomItemCount,
    int ValidationItemCount,
    int ProjectFileCount,
    long TotalBytes);
