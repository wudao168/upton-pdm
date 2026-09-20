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
    string AssetType,
    Guid? OriginCompanyId,
    string? OriginCompanyName,
    Guid? CurrentPublishedRevisionId,
    bool IsArchived,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    IReadOnlyList<ProgramTemplateRevision> Revisions);

/// <summary>
/// 程序模板类型：管理员在“选项维护”里新增/改名/排序，Key 为图档存储值，编号前缀决定模板编号，检查清单组决定审核检查项。
/// </summary>
public sealed record ProgramTemplateTypeOption(
    string Key,
    string Name,
    string CodePrefix,
    ProgramTemplateAssetType ChecklistKind);

public static class ProgramTemplateTypeCatalog
{
    public static IReadOnlyList<ProgramTemplateTypeOption> Default { get; } =
    [
        new("PlcFunctionBlock", "PLC功能块", "PT-FB", ProgramTemplateAssetType.PlcFunctionBlock),
        new("PlcProgram", "PLC整包模板", "PT-PLC", ProgramTemplateAssetType.PlcProgram),
        new("HmiTemplate", "HMI模板", "PT-HMI", ProgramTemplateAssetType.HmiTemplate)
    ];

    public static ProgramTemplateTypeOption? Find(IReadOnlyList<ProgramTemplateTypeOption>? types, string? key)
    {
        var normalized = key?.Trim();
        if (string.IsNullOrEmpty(normalized)) return null;
        return (types is { Count: > 0 } ? types : Default)
            .FirstOrDefault(item => string.Equals(item.Key?.Trim(), normalized, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>审核检查清单组：未知类型（历史数据或已删除类型）按 PLC 整包模板处理。</summary>
    public static ProgramTemplateAssetType ChecklistKind(IReadOnlyList<ProgramTemplateTypeOption>? types, string? key) =>
        Find(types, key)?.ChecklistKind ?? ProgramTemplateAssetType.PlcProgram;
}

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

/// <summary>
/// 程序模板维护选项：分类、厂商、平台由管理员在程序模板页面统一维护，上传/编辑时只能从维护好的值中选择。
/// </summary>
public sealed record ProgramTemplateOptionCatalog(
    IReadOnlyList<string> Categories,
    IReadOnlyList<string> Vendors,
    IReadOnlyList<string> Platforms,
    IReadOnlyList<ProgramTemplateTypeOption>? Types = null)
{
    public static ProgramTemplateOptionCatalog Empty { get; } = new([], [], [], ProgramTemplateTypeCatalog.Default);
}

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
