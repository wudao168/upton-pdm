namespace Upton.Pdm.Domain;

public enum ProjectValidationPlanState
{
    Draft,
    PendingApproval,
    Effective,
    Rejected,
    Superseded
}

public enum ValidationPlanAttachmentKind
{
    PlanDocument,
    Evidence
}

public sealed record ValidationCheckCategory(
    Guid Id,
    string Name,
    int SortOrder,
    bool IsActive,
    string? Note,
    int ItemCount,
    int ReferenceCount,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    string UpdatedBy,
    DateTimeOffset UpdatedAt,
    long RowVersion);

public sealed record ValidationCheckItem(
    Guid Id,
    Guid CategoryId,
    string Content,
    string DefaultInformationSource,
    int SortOrder,
    bool IsActive,
    string? Note,
    int ReferenceCount,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    string UpdatedBy,
    DateTimeOffset UpdatedAt,
    long RowVersion);

public sealed record ValidationCheckCatalog(
    IReadOnlyList<ValidationCheckCategory> Categories,
    IReadOnlyList<ValidationCheckItem> Items);

public sealed record ProjectValidationPlanItem(
    Guid Id,
    Guid? CatalogCategoryId,
    Guid? CatalogItemId,
    string CategoryName,
    string ValidationContent,
    string? InformationSource,
    DateOnly? ValidationDate,
    string? Result,
    string? ResponsiblePerson,
    string? Remark,
    int SortOrder);

public sealed record ProjectValidationPlan(
    Guid Id,
    Guid ProjectId,
    int RevisionNumber,
    ProjectValidationPlanState State,
    string? PreparedBy,
    DateOnly? ValidationDate,
    IReadOnlyList<ProjectValidationPlanItem> Items,
    IReadOnlyList<ValidationPlanApprovalTask> ApprovalTasks,
    IReadOnlyList<ValidationPlanAttachment> Attachments,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    string UpdatedBy,
    DateTimeOffset UpdatedAt,
    long RowVersion)
{
    public string? WorkflowCode { get; init; }
    public int? WorkflowVersion { get; init; }
    public string? SubmittedBy { get; init; }
    public DateTimeOffset? SubmittedAt { get; init; }
    public string? EffectiveBy { get; init; }
    public DateTimeOffset? EffectiveAt { get; init; }
}

public sealed record ValidationPlanApprovalTask(
    Guid Id,
    Guid PlanId,
    int StepOrder,
    ApprovalStage Stage,
    string StepName,
    string Assignee,
    ApprovalDecision? Decision,
    string? DecisionBy,
    string? DecisionComment,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DecidedAt);

public sealed record ValidationPlanAttachment(
    Guid Id,
    Guid PlanId,
    ValidationPlanAttachmentKind Kind,
    string OriginalFileName,
    int FileVersion,
    string StorageRelativePath,
    long FileLength,
    string Sha256,
    string UploadedBy,
    DateTimeOffset UploadedAt);

public sealed record ValidationPlanRecognitionCandidate(
    Guid PlanItemId,
    string CategoryName,
    string ValidationContent,
    decimal MatchConfidence,
    string MatchStatus,
    string? RecognizedResult,
    DateOnly? RecognizedValidationDate,
    string? RecognizedResponsiblePerson,
    string? RecognizedRemark,
    string SourceText);

public sealed record ValidationPlanRecognitionDraft(
    Guid PlanId,
    Guid AttachmentId,
    string OriginalFileName,
    DateTimeOffset RecognizedAt,
    string OcrText,
    IReadOnlyList<ValidationPlanRecognitionCandidate> Candidates);

public sealed record ValidationPlanExecutionItem(
    Guid Id,
    Guid ExecutionRecordId,
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

public sealed record ValidationPlanExecutionRecord(
    Guid Id,
    Guid PlanId,
    Guid SourceAttachmentId,
    string SourceFileName,
    string OcrText,
    IReadOnlyList<ValidationPlanExecutionItem> Items,
    string ConfirmedBy,
    DateTimeOffset ConfirmedAt);

public sealed record PendingValidationPlanApprovalTask(
    Guid Id,
    Guid PlanId,
    Guid ProjectId,
    int RevisionNumber,
    ApprovalStage Stage,
    string StepName,
    string Assignee,
    DateTimeOffset CreatedAt);
