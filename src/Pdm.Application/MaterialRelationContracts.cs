using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed record SaveMaterialRelationOptionCommand(
    Guid MaterialId,
    MaterialRelationQuantityMode QuantityMode,
    decimal QuantityPerSet,
    bool IsDefault,
    int SortOrder,
    string? SelectionAdvice = null);

public sealed record SaveMaterialRelationGroupCommand(
    string Name,
    bool IsRequired,
    MaterialRelationSelectionMode SelectionMode,
    int MinSelection,
    int? MaxSelection,
    bool AutoSelectUnique,
    int SortOrder,
    IReadOnlyList<SaveMaterialRelationOptionCommand> Options);

public sealed record SaveMaterialRelationTemplateCommand(
    Guid MainMaterialId,
    string Name,
    string? ChangeNote,
    long? ExpectedRevisionRowVersion,
    IReadOnlyList<SaveMaterialRelationGroupCommand> Groups,
    IReadOnlyList<Guid>? MainMaterialIds = null);

public sealed record MaterialRelationChoice(
    Guid GroupId,
    IReadOnlyList<Guid> OptionIds,
    bool ConfirmNoAccessory = false,
    string? NoAccessoryReason = null);

public sealed record MaterialRelationAllocationCommand(Guid Id, string Name, decimal Quantity, IReadOnlyList<MaterialRelationChoice> Choices);

public sealed record ApplyMaterialRelationsCommand(Guid MainBomItemId, IReadOnlyList<MaterialRelationChoice> Choices,
    IReadOnlyList<MaterialRelationAllocationCommand>? Allocations = null);

public sealed record MaterialRelationGroupCheck(
    Guid GroupId,
    string GroupName,
    bool IsRequired,
    MaterialRelationSelectionMode SelectionMode,
    int? MaxSelection,
    bool IsComplete,
    string Status,
    decimal ExpectedQuantity,
    decimal ActualQuantity,
    IReadOnlyList<Guid> SelectedOptionIds,
    MaterialRelationReviewDecision? ReviewDecision,
    string? ReviewReason,
    string? ReviewedBy,
    DateTimeOffset? ReviewedAt,
    IReadOnlyList<MaterialRelationOption> Options)
{
    public IReadOnlyDictionary<Guid,Guid> AccessoryBomItemIds { get; init; } = new Dictionary<Guid,Guid>();
}

public sealed record MaterialRelationMainCheck(
    Guid MainBomItemId,
    string MainMaterialCode,
    string MainMaterialName,
    decimal MainQuantity,
    Guid TemplateId,
    Guid RevisionId,
    int RevisionVersion,
    bool IsComplete,
    IReadOnlyList<MaterialRelationGroupCheck> Groups)
{
    public int BomSequence { get; init; }
    public IReadOnlyList<MaterialRelationAllocation> Allocations { get; init; } = [];
}

public sealed record MaterialRelationCompleteness(
    Guid ProjectId,
    bool IsComplete,
    int MainMaterialCount,
    int IncompleteGroupCount,
    IReadOnlyList<MaterialRelationMainCheck> MainMaterials);

public interface IMaterialRelationRepository
{
    Task<IReadOnlyList<MaterialRelationTemplate>> ListTemplatesAsync(bool includeDraft, CancellationToken cancellationToken);
    Task<MaterialRelationTemplate> SaveDraftAsync(Guid? templateId, SaveMaterialRelationTemplateCommand command, string mainMaterialCode, string mainMaterialName, IReadOnlyList<MaterialRelationGroup> groups, string actor, DateTimeOffset now, CancellationToken cancellationToken, IReadOnlyList<MaterialRelationMainMaterial>? mainMaterials = null);
    Task<MaterialRelationTemplate> PublishAsync(Guid templateId, Guid revisionId, long expectedRowVersion, string actor, DateTimeOffset now, CancellationToken cancellationToken);
    Task<IReadOnlyList<MaterialRelationSelection>> ListSelectionsAsync(Guid projectId, CancellationToken cancellationToken);
    async Task<IReadOnlyList<Guid>> ListRelatedMainBomItemIdsAsync(Guid projectId, IReadOnlyCollection<Guid> accessoryIds, CancellationToken ct) =>
        (await ListSelectionsAsync(projectId, ct)).Where(item => accessoryIds.Contains(item.AccessoryBomItemId)).Select(item => item.MainBomItemId).Distinct().ToArray();
    Task ReplaceSelectionsAsync(Guid projectId, Guid mainBomItemId, IReadOnlyList<MaterialRelationSelection> selections, CancellationToken cancellationToken);
    Task<IReadOnlyList<MaterialRelationReview>> ListReviewsAsync(Guid projectId, CancellationToken cancellationToken);
    Task ReplaceReviewsAsync(Guid projectId, Guid mainBomItemId, IReadOnlyList<MaterialRelationReview> reviews, CancellationToken cancellationToken);
}

public interface IMaterialRelationReleaseGuard
{
    Task EnsureCompleteAsync(Guid projectId, CancellationToken cancellationToken);
    Task EnsureCompleteAsync(Guid projectId, IReadOnlyCollection<Guid> selectedBomItemIds, CancellationToken cancellationToken) =>
        EnsureCompleteAsync(projectId, cancellationToken);
}
