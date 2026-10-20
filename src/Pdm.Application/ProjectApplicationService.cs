using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed record ProjectApplicationInput(Guid? CustomerId, string CustomerName, string ProjectName,
    DateOnly? OrderDate, DateOnly? DeliveryDate, string SalesUsername, IReadOnlyList<string> PresalesUsernames,
    string Remarks, bool IsSpecial);
public sealed record ProjectApplicationPerson(string Username, string DisplayName);
public sealed record ProjectApplicationData(ProjectApplicationInput Input, string SalesName, IReadOnlyList<ProjectApplicationPerson> PresalesPeople);
public sealed record ProjectApplication(Guid Id, string Number, Guid CompanyId, string Applicant, string ApplicantName,
    string Status, int Revision, ProjectApplicationData Data, long RowVersion, DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt, Guid? ProjectId = null, string? ProjectCode = null);
public sealed record ProjectApplicationApprovalInput(long ExpectedRowVersion, Guid CustomerId, string ProjectTypeCode,
    int EquipmentTypeCode, int Quantity, string BomItemCategoryCode, string? ProjectAlias, string? Comment,
    ProjectApplicationInput? ProjectData = null);
public sealed record ProjectApplicationEvent(Guid Id, int Revision, string Action, string Actor, string ActorName,
    DateTimeOffset At, ProjectApplicationData Data, string? Comment = null, ProjectApplicationApprovalInput? Approval = null,
    Guid? ProjectId = null, string? ProjectCode = null, string? ApprovedCustomerName = null, ProjectApplicationData? ApprovedData = null);
public sealed record ProjectApplicationApproval(Guid ApplicationId, long ExpectedRowVersion, ProjectApplicationEvent Event);
public sealed record SaveProjectApplicationInput(ProjectApplicationInput Data, long ExpectedRowVersion, bool Submit = false);
public sealed record ProjectApplicationActionInput(long ExpectedRowVersion, string? Comment = null);
public sealed record ProjectOrderDateInput(DateOnly? OrderDate, DateOnly? ExpectedOrderDate);
public sealed record ProjectApplicationDetail(ProjectApplication Application, IReadOnlyList<ProjectApplicationEvent> History,
    bool CanEdit, bool CanWithdraw, bool CanApprove);
public sealed record ProjectApplicationView(IReadOnlyList<ProjectApplication> Applications, bool CanApply, bool CanApprove,
    IReadOnlyList<ProjectApplicationPerson> People, IReadOnlyList<PdmCustomer> Customers, ProjectNumberingOptions NumberingOptions);

public interface IProjectApplicationRepository
{
    Task<IReadOnlyList<ProjectApplication>> ListApplicationsAsync(Guid companyId, string? applicant, CancellationToken ct);
    Task<ProjectApplication?> FindApplicationAsync(Guid id, Guid companyId, CancellationToken ct);
    Task<IReadOnlyList<ProjectApplicationEvent>> ListApplicationHistoryAsync(Guid id, CancellationToken ct);
    Task<ProjectApplication> SaveApplicationAsync(ProjectApplication value, ProjectApplicationEvent entry, long expectedRowVersion, CancellationToken ct);
    Task SetApplicationOrderDateAsync(Project project, ProjectOrderDateInput input, CancellationToken ct);
}

public sealed class ProjectApplicationService(IPdmRepository repository, IProjectApplicationRepository applications,
    PdmWorkflowService workflow, TimeProvider timeProvider)
{
    public const string Draft = "草稿", Pending = "待审批", Returned = "已退回", Withdrawn = "已撤回", Approved = "已批准";

    private Task<bool> CanApply(string actor, UserRole role, CancellationToken ct) => repository.HasUserPermissionAsync(actor, role, PermissionCodes.ProjectApplicationSubmit, ct);
    private Task<bool> CanApprove(string actor, UserRole role, CancellationToken ct) => repository.HasUserPermissionAsync(actor, role, PermissionCodes.ProjectApplicationApprove, ct);
    private async Task<Guid> Company(CancellationToken ct) => TenantContext.CompanyId
        ?? (await repository.GetProjectNumberingOptionsAsync(ct)).Organizations.First().Id;
    private async Task<string> Name(string actor, CancellationToken ct) => (await repository.FindUserAsync(actor, ct))?.DisplayName ?? actor;

    public async Task<ProjectApplicationView> GetAsync(string actor, UserRole role, CancellationToken ct)
    {
        var apply = await CanApply(actor, role, ct);
        var approve = await CanApprove(actor, role, ct);
        if (!apply && !approve) throw new UnauthorizedAccessException("当前账号没有立项申请权限。");
        var companyId = await Company(ct);
        var directory = await repository.GetOrganizationDirectoryAsync(ct);
        var options = await repository.GetProjectNumberingOptionsAsync(ct);
        return new(await applications.ListApplicationsAsync(companyId, approve ? null : actor, ct), apply, approve,
            directory.Users.Where(x => x.IsActive && (x.CompanyId == companyId || x.CompanyId is null || x.CrossCompanyView || x.AccessibleCompanyIds?.Contains(companyId) == true))
                .Select(x => new ProjectApplicationPerson(x.Username, x.DisplayName)).ToArray(),
            (await repository.ListCustomersAsync(true, ct)).Where(x => x.IsActive && x.SourceSystem.Equals("u9c", StringComparison.OrdinalIgnoreCase)).ToArray(),
            options with { Organizations = options.Organizations.Where(x => x.Id == companyId).ToArray() });
    }

    private async Task<ProjectApplication> Read(Guid id, string actor, UserRole role, CancellationToken ct)
    {
        var approve = await CanApprove(actor, role, ct);
        if (!approve && !await CanApply(actor, role, ct)) throw new UnauthorizedAccessException("当前账号没有立项申请权限。");
        var value = await applications.FindApplicationAsync(id, await Company(ct), ct) ?? throw new PdmNotFoundException("立项申请不存在或不属于当前公司。");
        if (!approve && !value.Applicant.Equals(actor, StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("只能查看本人发起的立项申请。");
        return value;
    }

    public async Task<ProjectApplicationDetail> DetailAsync(Guid id, string actor, UserRole role, CancellationToken ct)
    {
        var value = await Read(id, actor, role, ct);
        var own = value.Applicant.Equals(actor, StringComparison.OrdinalIgnoreCase) && await CanApply(actor, role, ct);
        return new(value, await applications.ListApplicationHistoryAsync(id, ct), own && value.Status is Draft or Pending or Returned or Withdrawn,
            own && value.Status == Pending, value.Status == Pending && await CanApprove(actor, role, ct));
    }

    public async Task<ProjectApplication> SaveAsync(Guid? id, SaveProjectApplicationInput command, string actor, UserRole role, CancellationToken ct)
    {
        if (!await CanApply(actor, role, ct)) throw new UnauthorizedAccessException("当前账号没有发起立项申请权限。");
        var current = id.HasValue ? await Read(id.Value, actor, role, ct) : null;
        if (current is not null && (!current.Applicant.Equals(actor, StringComparison.OrdinalIgnoreCase) || current.Status is not (Draft or Pending or Returned or Withdrawn)))
            throw new PdmConflictException("只能修改本人尚未批准的申请；已批准申请请在主项目中维护业务信息。");
        var submit = command.Submit || current?.Status == Pending;
        var data = await Normalize(command.Data, submit, await Company(ct), ct);
        var now = timeProvider.GetUtcNow();
        var actorName = await Name(actor, ct);
        var applicationId = id ?? Guid.NewGuid();
        var revision = (current?.Revision ?? 0) + (submit ? 1 : 0);
        var number = current?.Number ?? await repository.ReserveBusinessNumberAsync($"LX{now.ToOffset(TimeSpan.FromHours(8)):yyyyMMdd}-", ct);
        var value = new ProjectApplication(applicationId, number,
            await Company(ct), actor, current?.ApplicantName ?? actorName, submit ? Pending : Draft, revision, data,
            command.ExpectedRowVersion + 1, current?.CreatedAt ?? now, now);
        var action = current?.Status == Pending ? "修改并重新提交" : submit ? "提交申请" : "保存草稿";
        var saved = await applications.SaveApplicationAsync(value, new(Guid.NewGuid(), revision, action, actor, actorName, now, data), command.ExpectedRowVersion, ct);
        if (submit) await NotifyApprovers(saved, ct);
        return saved;
    }

    private async Task<ProjectApplicationData> Normalize(ProjectApplicationInput input, bool submit, Guid companyId, CancellationToken ct)
    {
        if (input is null || input.PresalesUsernames is null) throw new PdmRuleException("申请资料不完整，请重新填写。");
        var data = input with { CustomerName = input.CustomerName?.Trim() ?? "", ProjectName = input.ProjectName?.Trim() ?? "",
            SalesUsername = input.SalesUsername?.Trim() ?? "", PresalesUsernames = input.PresalesUsernames.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), Remarks = input.Remarks?.Trim() ?? "" };
        if (data.CustomerName.Length > 200 || data.ProjectName.Length > 200 || data.Remarks.Length > 2000 || data.SalesUsername.Length > 100 || data.PresalesUsernames.Count > 50)
            throw new PdmRuleException("客户和项目名称最多200字，备注最多2000字，售前工程师最多50人。");
        if (data.CustomerId.HasValue)
        {
            var customer = await repository.FindCustomerAsync(data.CustomerId.Value, ct);
            if (customer is null || !customer.IsActive || !customer.SourceSystem.Equals("u9c", StringComparison.OrdinalIgnoreCase)) throw new PdmRuleException("所选客户不存在、已停用或尚未从U9C同步。");
            data = data with { CustomerName = customer.Name };
        }
        if (data.OrderDate == DateOnly.MinValue || data.DeliveryDate == DateOnly.MinValue) throw new PdmRuleException("日期无效，请重新选择或清空订单日期。");
        if (data.OrderDate.HasValue && data.DeliveryDate < data.OrderDate) throw new PdmRuleException("交货日期不能早于订单日期。");
        var users = (await repository.GetOrganizationDirectoryAsync(ct)).Users.Where(x => x.IsActive &&
            (x.CompanyId == companyId || x.CompanyId is null || x.CrossCompanyView || x.AccessibleCompanyIds?.Contains(companyId) == true)).ToArray();
        if (submit && (data.CustomerName.Length == 0 || data.ProjectName.Length == 0 || !data.DeliveryDate.HasValue || data.SalesUsername.Length == 0 || data.PresalesUsernames.Count == 0))
            throw new PdmRuleException("请填写客户、项目名称、交货日期、销售人员和售前工程师；订单日期可不填。");
        var selected = data.PresalesUsernames.Prepend(data.SalesUsername).Where(x => x.Length > 0).ToArray();
        if (selected.Any(username => !users.Any(x => x.Username.Equals(username, StringComparison.OrdinalIgnoreCase)))) throw new PdmRuleException("所选人员已停用或不属于当前公司，请重新选择。");
        return new(data, users.FirstOrDefault(x => x.Username.Equals(data.SalesUsername, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? "",
            data.PresalesUsernames.Select(username => new ProjectApplicationPerson(username, users.First(x => x.Username.Equals(username, StringComparison.OrdinalIgnoreCase)).DisplayName)).ToArray());
    }

    public async Task<ProjectApplication> WithdrawAsync(Guid id, ProjectApplicationActionInput command, string actor, UserRole role, CancellationToken ct)
    {
        var value = await Read(id, actor, role, ct);
        if (!await CanApply(actor, role, ct) || !value.Applicant.Equals(actor, StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("仅申请人可以撤回本人申请。");
        return await Change(value, command, Withdrawn, "撤回申请", actor, ct);
    }

    public async Task<ProjectApplication> ReturnAsync(Guid id, ProjectApplicationActionInput command, string actor, UserRole role, CancellationToken ct)
    {
        if (!await CanApprove(actor, role, ct)) throw new UnauthorizedAccessException("当前账号没有立项审批权限。");
        if (string.IsNullOrWhiteSpace(command.Comment)) throw new PdmRuleException("请填写退回原因。");
        var saved = await Change(await Read(id, actor, role, ct), command, Returned, "退回修改", actor, ct);
        await Notify(saved, saved.Applicant, "立项申请已退回", command.Comment!, ct);
        return saved;
    }

    private async Task<ProjectApplication> Change(ProjectApplication value, ProjectApplicationActionInput command, string status, string action, string actor, CancellationToken ct)
    {
        if (value.Status != Pending) throw new PdmConflictException("申请已不在待审批状态，请刷新后重试。");
        if (command.Comment?.Length > 2000) throw new PdmRuleException("审批意见最多2000字。");
        var now = timeProvider.GetUtcNow();
        return await applications.SaveApplicationAsync(value with { Status = status, UpdatedAt = now, RowVersion = command.ExpectedRowVersion + 1 },
            new(Guid.NewGuid(), value.Revision, action, actor, await Name(actor, ct), now, value.Data, command.Comment?.Trim()), command.ExpectedRowVersion, ct);
    }

    public async Task<ProjectApplication> ApproveAsync(Guid id, ProjectApplicationApprovalInput command, string actor, UserRole role, CancellationToken ct)
    {
        if (!await CanApprove(actor, role, ct)) throw new UnauthorizedAccessException("当前账号没有立项审批权限。");
        var value = await Read(id, actor, role, ct);
        if (value.Status == Approved) return value;
        if (value.Status != Pending || value.RowVersion != command.ExpectedRowVersion) throw new PdmConflictException("申请已变化，请刷新后重新审批。");
        if (command.Comment?.Length > 2000) throw new PdmRuleException("审批意见最多2000字。");
        var customer = await repository.FindCustomerAsync(command.CustomerId, ct) ?? throw new PdmRuleException("所选客户不存在，请关联正式客户档案。");
        var approvedData = await Normalize((command.ProjectData ?? value.Data.Input) with { CustomerId = command.CustomerId }, true, value.CompanyId, ct);
        var input = approvedData.Input;
        var approval = new ProjectApplicationApproval(id, command.ExpectedRowVersion, new(Guid.NewGuid(), value.Revision, "批准并创建主项目", actor, await Name(actor, ct), timeProvider.GetUtcNow(), value.Data, command.Comment?.Trim(), command with { ProjectData = input }, ApprovedCustomerName: customer.Name, ApprovedData: approvedData));
        await workflow.CreateApprovedApplicationProjectAsync(new(value.CompanyId, command.ProjectTypeCode, command.EquipmentTypeCode, command.CustomerId,
            input.ProjectName, command.ProjectAlias, input.OrderDate, command.Quantity, actor, "", "", command.BomItemCategoryCode) { ApplicationApproval = approval }, actor, role, ct);
        var saved = (await applications.FindApplicationAsync(id, value.CompanyId, ct))!;
        await Notify(saved, saved.Applicant, "立项申请已批准", $"已创建主项目 {saved.ProjectCode}", ct);
        return saved;
    }

    private async Task NotifyApprovers(ProjectApplication value, CancellationToken ct)
    {
        foreach (var user in (await repository.GetOrganizationDirectoryAsync(ct)).Users.Where(x => x.IsActive && (x.CompanyId == value.CompanyId || x.CompanyId is null)))
            if (await repository.HasUserPermissionAsync(user.Username, user.Role, PermissionCodes.ProjectApplicationApprove, ct))
                await Notify(value, user.Username, "立项申请待审批", "请进入立项申请审阅并批准或退回。", ct);
    }
    private Task Notify(ProjectApplication value, string recipient, string title, string content, CancellationToken ct) =>
        repository.CreateUserNotificationsAsync([new(Guid.NewGuid(), recipient, "project-application", title,
            $"{value.Number} · {value.Data.Input.ProjectName}；{content}", value.ProjectId, null, $"project-application:{value.Id:N}:{value.RowVersion}:{recipient}", timeProvider.GetUtcNow(), null)], ct);

    public async Task<Project> UpdateOrderDateAsync(Guid projectId, ProjectOrderDateInput input, string actor, UserRole role, CancellationToken ct)
    {
        var project = await repository.FindProjectAsync(projectId, ct) ?? throw new PdmNotFoundException("项目不存在。");
        if (project.OrganizationId != await Company(ct) || project.SourceApplicationId is null || project.ParentProjectId is not null)
            throw new UnauthorizedAccessException("只能维护当前公司由立项申请生成的主项目订单日期。");
        if (!await CanApprove(actor, role, ct))
        {
            if (!await repository.HasUserPermissionAsync(actor, role, PermissionCodes.ProjectEdit, ct)) throw new UnauthorizedAccessException("当前账号没有订单日期维护权限。");
            await ProjectPermissionPolicy.RequireAsync(repository, projectId, actor, role, PermissionCodes.ProjectEdit, ct);
        }
        if (input.OrderDate == DateOnly.MinValue || input.OrderDate > project.DeliveryDate)
            throw new PdmRuleException("订单日期无效或晚于约定交货日期，请重新选择。");
        await applications.SetApplicationOrderDateAsync(project, input, ct);
        await repository.AppendAuditAsync(new(Guid.NewGuid(), timeProvider.GetUtcNow(), actor, "project.order-date.update", nameof(Project), project.Id.ToString(),
            $"{project.Code}；订单日期 {project.SignedDate?.ToString("yyyy-MM-dd") ?? "未填写"} → {input.OrderDate?.ToString("yyyy-MM-dd") ?? "未填写"}；原立项申请及审批记录保留。"), ct);
        return (await repository.FindProjectAsync(projectId, ct))!;
    }
}
