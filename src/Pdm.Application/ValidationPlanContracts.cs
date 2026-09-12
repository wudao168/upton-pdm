using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed record SaveValidationCheckCategoryCommand(
    string Name,
    int SortOrder,
    bool IsActive,
    string? Note,
    long? ExpectedRowVersion = null);

public sealed record SaveValidationCheckItemCommand(
    Guid CategoryId,
    string Content,
    string? DefaultInformationSource,
    int SortOrder,
    bool IsActive,
    string? Note,
    long? ExpectedRowVersion = null);

public sealed record SaveProjectValidationPlanItemCommand(
    Guid? CatalogItemId,
    string? ValidationContent,
    string? InformationSource,
    DateOnly? ValidationDate,
    string? Result,
    string? ResponsiblePerson,
    string? Remark,
    int SortOrder);

public sealed record SaveProjectValidationPlanCommand(
    string? PreparedBy,
    DateOnly? ValidationDate,
    IReadOnlyList<SaveProjectValidationPlanItemCommand> Items,
    long? ExpectedRowVersion = null);

public sealed record ValidationPlanExportData(Project Project, ProjectValidationPlan Plan, DateTimeOffset ExportedAt);

public sealed record ValidationPlanAttachmentDownload(ValidationPlanAttachment Attachment, Stream Content);

public sealed record ConfirmValidationPlanExecutionItemCommand(
    Guid PlanItemId,
    decimal MatchConfidence,
    string SourceText,
    string? RecognizedResult,
    DateOnly? RecognizedValidationDate,
    string? RecognizedResponsiblePerson,
    string? RecognizedRemark,
    string? Result,
    DateOnly? ValidationDate,
    string? ResponsiblePerson,
    string? Remark);

public sealed record ConfirmValidationPlanExecutionCommand(
    Guid SourceAttachmentId,
    string OcrText,
    IReadOnlyList<ConfirmValidationPlanExecutionItemCommand> Items);

public interface IValidationPlanTextRecognitionService
{
    Task<string> RecognizeAsync(string absolutePath, CancellationToken cancellationToken);
}

public interface IValidationPlanRepository
{
    Task<ValidationCheckCatalog> ListCatalogAsync(bool includeInactive, CancellationToken cancellationToken);
    Task<ValidationCheckCategory> CreateCategoryAsync(ValidationCheckCategory category, CancellationToken cancellationToken);
    Task<ValidationCheckCategory> UpdateCategoryAsync(ValidationCheckCategory category, long expectedRowVersion, CancellationToken cancellationToken);
    Task DeleteCategoryAsync(Guid categoryId, long expectedRowVersion, CancellationToken cancellationToken);
    Task<ValidationCheckItem> CreateItemAsync(ValidationCheckItem item, CancellationToken cancellationToken);
    Task<ValidationCheckItem> UpdateItemAsync(ValidationCheckItem item, long expectedRowVersion, CancellationToken cancellationToken);
    Task DeleteItemAsync(Guid itemId, long expectedRowVersion, CancellationToken cancellationToken);
    Task<ProjectValidationPlan?> FindPlanAsync(Guid projectId, CancellationToken cancellationToken);
    Task<ProjectValidationPlan?> FindPlanByIdAsync(Guid planId, CancellationToken cancellationToken);
    Task<ProjectValidationPlan> SavePlanAsync(ProjectValidationPlan plan, long? expectedRowVersion, CancellationToken cancellationToken);
    Task<ProjectValidationPlan> CreateRevisionAsync(ProjectValidationPlan plan, CancellationToken cancellationToken);
    Task<ProjectValidationPlan> SubmitAsync(Guid planId, long expectedRowVersion, string workflowCode, int workflowVersion, IReadOnlyList<ValidationPlanApprovalTask> tasks, string actor, DateTimeOffset submittedAt, CancellationToken cancellationToken);
    Task<ProjectValidationPlan> DecideAsync(Guid taskId, string actor, ApprovalDecision decision, string? comment, DateTimeOffset decidedAt, CancellationToken cancellationToken);
    Task<IReadOnlyList<PendingValidationPlanApprovalTask>> ListPendingApprovalTasksAsync(string actor, bool includeAll, CancellationToken cancellationToken);
    Task<ValidationPlanAttachment> AddAttachmentAsync(ValidationPlanAttachment attachment, CancellationToken cancellationToken);
    Task<IReadOnlyList<ValidationPlanExecutionRecord>> ListExecutionRecordsAsync(Guid planId, CancellationToken cancellationToken);
    Task<ValidationPlanExecutionRecord> AddExecutionRecordAsync(ValidationPlanExecutionRecord record, CancellationToken cancellationToken);
}
