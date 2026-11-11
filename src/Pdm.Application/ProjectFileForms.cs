using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed record ProjectFormAttachment(Guid Id, string FileName, byte[]? Content);
public sealed record ProjectSampleItem(string? MaterialName, decimal? Quantity, string? Unit, DateOnly? RequiredOn,
    string? Phase, string? Note, ProjectFormAttachment? Image);
public sealed record ProjectAcceptanceDetails(string? ContractNumber = null, string? Location = null, string? Attendees = null,
    string? Standard = null, string? Explanation = null, string? Corrections = null, bool? Passed = null, string? Comments = null,
    string? Terms = null);

public sealed record ProjectFileFormData(
    string? Recipient = null, string? Contact = null, string? Address = null, IReadOnlyList<ProjectSampleItem>? Items = null,
    string? Supplier = null, string? Customer = null, DateOnly? ChangedOn = null, string? Reason = null,
    string? Before = null, string? After = null, bool? Paid = null, string? Note = null,
    IReadOnlyList<ProjectFormAttachment>? Evidence = null, ProjectAcceptanceDetails? Acceptance = null);

public sealed record ProjectFileFormSummary(Guid Id, string Number, long RowVersion, long ArchivedRevision,
    Guid? FileId, int FileVersion, Guid? EvidenceFileId, DateTimeOffset UpdatedAt, int ArchivedLayoutVersion = 1);
public sealed record ProjectFileForm(ProjectFileFormSummary Summary, ProjectFileFormData Data);
public sealed record ProjectFileFormIndex(Guid FolderId, string ProjectCode, string ProjectName, string Company, string Customer,
    bool CanEdit, bool CanArchive, IReadOnlyList<ProjectFileFormSummary> Forms, CompanyReceivingDetails? Receiving = null, bool CanExport = false);
public sealed record SaveProjectFileForm(long ExpectedRowVersion, ProjectFileFormData Data);
public sealed record ProjectFileFormArchiveResult(ProjectFileFormSummary Form, bool Added);

public interface IProjectFileFormRepository
{
    Task<IReadOnlyList<ProjectFileFormSummary>> ListAsync(Guid projectId, string kind, CancellationToken ct);
    Task<ProjectFileForm?> FindAsync(Guid projectId, string kind, Guid formId, CancellationToken ct);
    Task<ProjectFileForm> SaveAsync(Project project, string kind, Guid formId, long expectedRevision,
        ProjectFileFormData data, string actor, CancellationToken ct);
    Task<ProjectFileFormArchiveResult> ArchiveAsync(Project project, string kind, Guid formId, long expectedRevision,
        int expectedFileVersion, int layoutVersion, StoredProjectFileUpload workbook, StoredProjectFileUpload? evidence, string actor, CancellationToken ct);
}

public interface IProjectFileFormWorkbook
{
    byte[] Build(string kind, ProjectFileForm form, Project project, Project root, OrganizationDirectory directory,
        string actor, DateTimeOffset now, string? evidenceFileName);
}

public static class ProjectFileFormIdentity
{
    public static string Number(Project project, string kind, int sequence) => kind switch
    {
        ProjectFileFormPermissions.Sample when sequence == 1 => $"{project.Code}-YPXQ",
        ProjectFileFormPermissions.Sample => $"{project.Code}-YPXQ-{sequence:D3}",
        ProjectFileFormPermissions.PreAcceptance => $"{project.Code}-PAT",
        ProjectFileFormPermissions.FinalAcceptance => $"{project.Code}-FAT",
        _ => $"AK-XQBG-{project.Code}-{sequence:D3}",
    };

    public static bool IsSinglePerProject(string kind) => ProjectFileFormPermissions.IsAcceptance(kind);
    public static int LayoutVersion(string kind) => kind == ProjectFileFormPermissions.Change ? 3 : 2;
}
