using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed partial class InMemoryPdmRepository : IProjectApplicationRepository
{
    private readonly Dictionary<Guid, ProjectApplication> projectApplications = [];
    private readonly Dictionary<Guid, List<ProjectApplicationEvent>> applicationHistory = [];
    public Task SetApplicationOrderDateAsync(Project project, ProjectOrderDateInput input, CancellationToken ct)
    {
        lock (gate)
        {
            var current = projects[project.Id];
            if (current.SignedDate != input.ExpectedOrderDate) throw new PdmConflictException("订单日期已被修改，请刷新后重新填写。");
            foreach (var item in projects.Values.Where(x => x.Id == project.Id || x.RootProjectId == project.Id).ToArray())
                projects[item.Id] = item with { SignedDate = input.OrderDate };
            return Task.CompletedTask;
        }
    }
    public Task<IReadOnlyList<ProjectApplication>> ListApplicationsAsync(Guid companyId, string? applicant, CancellationToken ct)
    {
        lock (gate) return Task.FromResult<IReadOnlyList<ProjectApplication>>(projectApplications.Values.Where(x => x.CompanyId == companyId &&
            (applicant is null || x.Applicant.Equals(applicant, StringComparison.OrdinalIgnoreCase))).OrderByDescending(x => x.CreatedAt).ToArray());
    }
    public Task<ProjectApplication?> FindApplicationAsync(Guid id, Guid companyId, CancellationToken ct)
    {
        lock (gate) return Task.FromResult(projectApplications.GetValueOrDefault(id) is { } value && value.CompanyId == companyId ? value : null);
    }
    public Task<IReadOnlyList<ProjectApplicationEvent>> ListApplicationHistoryAsync(Guid id, CancellationToken ct)
    {
        lock (gate) return Task.FromResult<IReadOnlyList<ProjectApplicationEvent>>(applicationHistory.GetValueOrDefault(id)?.ToArray() ?? []);
    }
    public Task<ProjectApplication> SaveApplicationAsync(ProjectApplication value, ProjectApplicationEvent entry, long expectedRowVersion, CancellationToken ct)
    {
        lock (gate)
        {
            if ((projectApplications.GetValueOrDefault(value.Id)?.RowVersion ?? 0) != expectedRowVersion)
                throw new PdmConflictException("立项申请已被修改或审批，请刷新后重试。");
            projectApplications[value.Id] = value;
            if (!applicationHistory.ContainsKey(value.Id)) applicationHistory[value.Id] = [];
            applicationHistory[value.Id].Add(entry);
            return Task.FromResult(value);
        }
    }
    private ProjectApplication? LockApplicationApproval(CreateNumberedProjectCommand command)
    {
        if (command.ApplicationApproval is not { } approval) return null;
        var value = projectApplications.GetValueOrDefault(approval.ApplicationId);
        if (value is null || value.CompanyId != command.OrganizationId) throw new PdmNotFoundException("立项申请不存在或不属于当前公司。");
        if (value.Status == ProjectApplicationService.Approved) return value;
        if (value.Status != ProjectApplicationService.Pending || value.RowVersion != approval.ExpectedRowVersion)
            throw new PdmConflictException("申请已变化，请刷新后重新审批。");
        return value;
    }
    private void CompleteApplicationApproval(ProjectApplication application, ProjectApplicationApproval approval, Project project)
    {
        projectApplications[application.Id] = application with { Status = ProjectApplicationService.Approved, ProjectId = project.Id,
            ProjectCode = project.Code, RowVersion = application.RowVersion + 1, UpdatedAt = approval.Event.At };
        applicationHistory[application.Id].Add(approval.Event with { ProjectId = project.Id, ProjectCode = project.Code });
        var input = approval.Event.ApprovedData?.Input ?? application.Data.Input;
        if (projectContacts is not null)
            projectContacts.SaveAsync(new(project.Id, UpdatedBy: approval.Event.Actor, UpdatedAt: approval.Event.At,
                SalesPeople: input.SalesUsername, PresalesPeople: string.Join('、', input.PresalesUsernames)), 0, default).GetAwaiter().GetResult();
    }
}
