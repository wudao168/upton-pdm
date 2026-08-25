using System.Text.Json.Serialization;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api;

public sealed record ProgramTemplateParameterRequest(
    [property: JsonConverter(typeof(JsonStringEnumConverter))] ProgramTemplateParameterDirection Direction,
    int SortOrder,
    string Name,
    string DataType,
    string? DefaultValue,
    string? Unit,
    string? Description);

public sealed record CreateProgramTemplateRequest(
    [property: JsonConverter(typeof(JsonStringEnumConverter))] ProgramTemplateAssetType AssetType,
    string Name,
    string Category,
    string Description,
    string Vendor,
    string Platform,
    string SoftwareVersion,
    string ApplicableSeries,
    IReadOnlyList<string>? Tags,
    string ChangeNote,
    IReadOnlyList<ProgramTemplateParameterRequest>? Parameters);

public sealed record UpdateProgramTemplateDraftRequest(
    string Name,
    string Category,
    string Description,
    string Vendor,
    string Platform,
    string SoftwareVersion,
    string ApplicableSeries,
    IReadOnlyList<string>? Tags,
    string ChangeNote,
    IReadOnlyList<ProgramTemplateParameterRequest>? Parameters,
    long ExpectedRowVersion);

public sealed record CreateProgramTemplateRevisionRequest(
    [property: JsonConverter(typeof(JsonStringEnumConverter))] ProgramTemplateVersionBump Bump);

public sealed record StartProgramTemplateUploadRequest(
    [property: JsonConverter(typeof(JsonStringEnumConverter))] ProgramTemplateAttachmentKind Kind,
    string FileName,
    long TotalLength,
    string Sha256);

public sealed record CompleteProgramTemplateUploadRequest(long ExpectedRowVersion);

public sealed record SubmitProgramTemplateRequest(long ExpectedRowVersion);

public sealed record DecideProgramTemplateRequest(
    [property: JsonConverter(typeof(JsonStringEnumConverter))] ProgramTemplateApprovalDecision Decision,
    string? Comment,
    IReadOnlyList<string>? ChecklistItems,
    long ExpectedRowVersion);

public sealed record SetProgramTemplateArchivedRequest(bool Archived, string Reason);

public sealed record ProgramTemplateParameterResponse(
    Guid Id,
    string Direction,
    int SortOrder,
    string Name,
    string DataType,
    string? DefaultValue,
    string? Unit,
    string? Description);

public sealed record ProgramTemplateRevisionResponse(
    Guid Id,
    string Version,
    int AttemptNumber,
    string State,
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
    long? PackageFileLength,
    string? PackageSha256,
    string? EvidenceFileName,
    long? EvidenceFileLength,
    string? EvidenceSha256,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset? SubmittedAt,
    DateTimeOffset? PublishedAt,
    long RowVersion,
    IReadOnlyList<ProgramTemplateParameterResponse> Parameters);

public sealed record ProgramTemplateResponse(
    Guid Id,
    string Code,
    string AssetType,
    Guid? OriginCompanyId,
    string? OriginCompanyName,
    Guid? CurrentPublishedRevisionId,
    bool IsArchived,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    IReadOnlyList<ProgramTemplateRevisionResponse> Revisions);

public sealed record ProgramTemplateTaskResponse(
    Guid Id,
    Guid TemplateId,
    string TemplateCode,
    Guid RevisionId,
    string TemplateName,
    string Version,
    string Stage,
    string State,
    IReadOnlyList<string> RequiredChecklist,
    DateTimeOffset CreatedAt,
    long RowVersion);
