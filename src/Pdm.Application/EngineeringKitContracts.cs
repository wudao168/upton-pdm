using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed record SaveEngineeringKitComponentCommand(
    Guid MaterialId,
    decimal Quantity,
    bool IsOptional,
    int SortOrder);

public sealed record SaveEngineeringKitDraftCommand(
    string Name,
    string Brand,
    string? Description,
    string? ChangeNote,
    IReadOnlyList<SaveEngineeringKitComponentCommand> Components,
    long? ExpectedRowVersion = null,
    EngineeringKitModelMode ModelMode = EngineeringKitModelMode.Auto,
    string? Model = null,
    string? StandardName = null,
    string? StandardCode = null,
    string? CategoryName = null,
    string? CategoryCode = null,
    Guid? StandardLibraryCategoryId = null);

public sealed record ExpandEngineeringKitCommand(
    Guid? RevisionId,
    decimal Quantity,
    IReadOnlyList<Guid> SelectedOptionalComponentIds);

public interface IEngineeringKitRepository
{
    Task<EngineeringKit> UpdateLibraryMetadataAsync(EngineeringKit kit, long expectedRowVersion, CancellationToken cancellationToken);
    Task<IReadOnlyList<EngineeringKit>> ListAsync(bool releasedOnly, CancellationToken cancellationToken);
    Task<EngineeringKit?> FindAsync(Guid kitId, CancellationToken cancellationToken);
    Task<EngineeringKit> SetActiveAsync(Guid kitId, bool isActive, long expectedRowVersion, string actor, DateTimeOffset updatedAt, CancellationToken cancellationToken);
    // 套件附件沿用通用附件结构，其中 MaterialId 表示所属套件 ID，不写入料品主档。
    Task<IReadOnlyList<MaterialAttachment>> ListLibraryAttachmentsAsync(Guid kitId, MaterialAttachmentKind? kind, CancellationToken cancellationToken);
    Task<MaterialAttachment?> FindLibraryAttachmentAsync(Guid attachmentId, CancellationToken cancellationToken);
    Task<MaterialAttachment> CreateLibraryAttachmentAsync(MaterialAttachment attachment, CancellationToken cancellationToken);
    Task<bool> DeleteLibraryAttachmentAsync(Guid kitId, Guid attachmentId, CancellationToken cancellationToken);
    Task<EngineeringKit> SaveDraftAsync(EngineeringKit kit, EngineeringKitRevision revision, long? expectedRowVersion, CancellationToken cancellationToken);
    Task<EngineeringKit> PublishAsync(Guid kitId, long expectedRowVersion, string actor, DateTimeOffset publishedAt, string? modelCategoryCode, CancellationToken cancellationToken);
    Task<EngineeringKit> MoveCategoryAsync(Guid kitId, Guid categoryId, long expectedRowVersion, string actor, DateTimeOffset updatedAt, CancellationToken cancellationToken);
}

public sealed record SaveEngineeringKitLibraryMetadataCommand(string? Remark, string? SelectionAdvice, decimal? ReferencePrice,
    bool IsRecommended, Guid? CoverImageAttachmentId, long ExpectedRowVersion);
