namespace Upton.Pdm.Domain;

public enum ProgramTemplateAssetType
{
    PlcFunctionBlock,
    PlcProgram,
    HmiTemplate
}

public enum ProgramTemplateRevisionState
{
    Draft,
    PendingReview,
    PendingApproval,
    Rejected,
    Published,
    Superseded,
    Archived
}

public enum ProgramTemplateParameterDirection
{
    Input,
    Output,
    InOut
}

public enum ProgramTemplateAttachmentKind
{
    Package,
    TestEvidence
}

public enum ProgramTemplateApprovalStage
{
    Review,
    Approval
}

public enum ProgramTemplateApprovalDecision
{
    Approved,
    Rejected
}

public sealed record ProgramTemplateParameter(
    Guid Id,
    Guid RevisionId,
    ProgramTemplateParameterDirection Direction,
    int SortOrder,
    string Name,
    string DataType,
    string? DefaultValue,
    string? Unit,
    string? Description);

public sealed record ProgramTemplateRevision(
    Guid Id,
    Guid TemplateId,
    int VersionMajor,
    int VersionMinor,
    int VersionPatch,
    int AttemptNumber,
    ProgramTemplateRevisionState State,
    string Name,
    string Category,
    string Description,
    string Vendor,
    string Platform,
    string SoftwareVersion,
    string ApplicableSeries,
    IReadOnlyList<string> Tags,
    string ChangeNote,
    string? PackageFileName,
    string? PackageStoragePath,
    long? PackageFileLength,
    string? PackageSha256,
    string? EvidenceFileName,
    string? EvidenceStoragePath,
    long? EvidenceFileLength,
    string? EvidenceSha256,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset? SubmittedAt,
    DateTimeOffset? PublishedAt,
    long RowVersion,
    IReadOnlyList<ProgramTemplateParameter> Parameters)
{
    public string VersionLabel => $"v{VersionMajor}.{VersionMinor}.{VersionPatch}";
}

public sealed record ProgramTemplate(
    Guid Id,
    string Code,
    ProgramTemplateAssetType AssetType,
    Guid? OriginCompanyId,
    string? OriginCompanyName,
    Guid? CurrentPublishedRevisionId,
    bool IsArchived,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    IReadOnlyList<ProgramTemplateRevision> Revisions);

public sealed record ProgramTemplateApprovalTask(
    Guid Id,
    Guid RevisionId,
    ProgramTemplateApprovalStage Stage,
    string? Assignee,
    string? AssigneeRoleCode,
    ProgramTemplateApprovalDecision? Decision,
    string? DecisionBy,
    string? Comment,
    IReadOnlyList<string> ChecklistItems,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DecidedAt,
    long RowVersion);

public static class ProgramTemplateChecklist
{
    public static IReadOnlyList<string> For(ProgramTemplateAssetType type) => type switch
    {
        ProgramTemplateAssetType.PlcFunctionBlock =>
        [
            "命名、注释与版本信息符合规范",
            "接口定义与程序包内容一致",
            "异常、复位与边界处理已验证",
            "离线测试记录完整且与当前哈希一致"
        ],
        ProgramTemplateAssetType.PlcProgram =>
        [
            "程序结构、命名与注释符合规范",
            "诊断、报警与恢复策略已验证",
            "平台和控制器兼容信息完整",
            "离线测试记录完整且与当前哈希一致"
        ],
        _ =>
        [
            "画面、变量与命名符合规范",
            "报警、权限与导航逻辑已验证",
            "分辨率和运行平台兼容信息完整",
            "离线测试记录完整且与当前哈希一致"
        ]
    };
}
