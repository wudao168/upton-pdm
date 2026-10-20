using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class ProjectApplicationServiceTests
{
    private static readonly Guid Customer = Guid.Parse("c0046500-0000-0000-0000-000000000001");
    private static ProjectApplicationInput Input(bool special = true) => new(null, "未签合同客户", "提前立项设备", null,
        new(2026, 12, 30), "sales", ["pre1", "pre2"], "合同未签，先启动设计", special);
    private static ProjectApplicationApprovalInput Approval(ProjectApplication value) => new(value.RowVersion, Customer, "P", 2, 1, "0301", "审批补充别名", "同意提前启动");

    private static async Task<(InMemoryPdmRepository Repository, ProjectApplicationService Service, InMemoryProjectContactInformationRepository Contacts)> Setup(TimeProvider? clock = null)
    {
        clock ??= TimeProvider.System;
        var contacts = new InMemoryProjectContactInformationRepository();
        var repository = new InMemoryPdmRepository(clock, contacts);
        var companyId = (await repository.GetProjectNumberingOptionsAsync(default)).Organizations.First().Id;
        foreach (var (username, role, name) in new[] { ("assistant", "SalesAssistant", "销售助理甲"), ("assistant2", "SalesAssistant", "销售助理乙"),
            ("planner", "PlanningManager", "计划管理甲"), ("sales", "SalesManager", "销售甲"), ("sales2", "SalesManager", "销售乙"),
            ("pre1", "PresalesEngineer", "售前甲"), ("pre2", "PresalesEngineer", "售前乙"), ("pre3", "PresalesEngineer", "售前丙") })
            await repository.CreateUserAsync(new(Guid.NewGuid(), username, name, "unused", role == "PlanningManager" ? UserRole.PlanningManager : UserRole.ProductionViewer,
                true, RoleCode: role, CompanyId: companyId), default);
        var workflow = new PdmWorkflowService(repository, null!, null!, clock);
        return (repository, new(repository, repository, workflow, clock), contacts);
    }

    private sealed class NumberClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 19, 15, 59, 59, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task ApplicationNumbersUseFourDigitsResetAtChinaMidnightAndNeverChangeWhenEditing()
    {
        var clock = new NumberClock();
        var (repository, service, _) = await Setup(clock);
        await Assert.ThrowsAsync<PdmRuleException>(() => service.SaveAsync(null, new(Input() with { ProjectName = "" }, 0, true), "assistant", UserRole.ProductionViewer, default));
        var first = await service.SaveAsync(null, new(Input(), 0, true), "assistant", UserRole.ProductionViewer, default);
        var second = await service.SaveAsync(null, new(Input(), 0, true), "assistant", UserRole.ProductionViewer, default);
        Assert.Equal("LX20261019-0001", first.Number);
        Assert.Equal("LX20261019-0002", second.Number);
        var edited = await service.SaveAsync(first.Id, new(Input() with { Remarks = "修改后重新提交" }, first.RowVersion), "assistant", UserRole.ProductionViewer, default);
        Assert.Equal(first.Number, edited.Number);
        Assert.Equal(2, (await service.DetailAsync(first.Id, "assistant", UserRole.ProductionViewer, default)).History.Count);
        var legacy = first with { Id = Guid.NewGuid(), Number = "LX20261019-AFCFEA92BA69", Status = ProjectApplicationService.Draft };
        await repository.SaveApplicationAsync(legacy, new(Guid.NewGuid(), legacy.Revision, "保存草稿", "assistant", "销售助理甲", clock.Now, legacy.Data), 0, default);
        var oldEdited = await service.SaveAsync(legacy.Id, new(Input(), legacy.RowVersion), "assistant", UserRole.ProductionViewer, default);
        Assert.Equal(legacy.Number, oldEdited.Number);
        clock.Now = clock.Now.AddSeconds(2);
        var nextDay = await service.SaveAsync(null, new(Input(), 0, true), "assistant", UserRole.ProductionViewer, default);
        Assert.Equal("LX20261020-0001", nextDay.Number);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OptionalOrderDateCreatesProjectAndChildrenWithoutInventingDate(bool special)
    {
        var (repository, service, contacts) = await Setup();
        var app = await service.SaveAsync(null, new(Input(special), 0, true), "assistant", UserRole.ProductionViewer, default);
        Assert.Equal("待审批", app.Status);
        Assert.Null(app.Data.Input.OrderDate);
        var approved = await service.ApproveAsync(app.Id, Approval(app), "planner", UserRole.PlanningManager, default);
        var project = (await repository.FindProjectAsync(approved.ProjectId!.Value, default))!;
        Assert.Equal(special, project.IsSpecialInitiation);
        Assert.Null(project.SignedDate);
        Assert.Equal(app.Id, project.SourceApplicationId);
        Assert.Equal(Input().DeliveryDate, project.DeliveryDate);
        Assert.Equal("提前立项设备", project.Name);
        Assert.Equal("审批补充别名", project.ProjectAlias);
        var child = await repository.CreateSubprojectAsync(new(project.Id, "设备子项目", null, 1), default);
        Assert.Null(child.SignedDate);
        Assert.Equal(special, child.IsSpecialInitiation);
        Assert.Equal("sales", (await contacts.FindAsync(project.Id, default))!.SalesPeople);
        Assert.Equal("pre1、pre2", (await contacts.FindAsync(project.Id, default))!.PresalesPeople);
        var history = (await service.DetailAsync(app.Id, "assistant", UserRole.ProductionViewer, default)).History;
        Assert.Equal(["提交申请", "批准并创建主项目"], history.Select(x => x.Action));
        Assert.Equal("未签合同客户", history[0].Data.Input.CustomerName);
        Assert.Equal(approved.ProjectId, history[1].ProjectId);
        Assert.Contains(await repository.ListUserNotificationsAsync("planner", 20, default), x => x.Category == "project-application");
        Assert.Contains(await repository.ListUserNotificationsAsync("assistant", 20, default), x => x.Title == "立项申请已批准");
    }

    [Fact]
    public async Task ReturnAndResubmitKeepEverySubmittedSnapshotAndDecision()
    {
        var (_, service, _) = await Setup();
        var first = await service.SaveAsync(null, new(Input(), 0, true), "assistant", UserRole.ProductionViewer, default);
        await Assert.ThrowsAsync<PdmRuleException>(() => service.ReturnAsync(first.Id, new(first.RowVersion, ""), "planner", UserRole.PlanningManager, default));
        var returned = await service.ReturnAsync(first.Id, new(first.RowVersion, "交货日期需延后"), "planner", UserRole.PlanningManager, default);
        var second = await service.SaveAsync(first.Id, new(Input() with { ProjectName = "调整后的设备", DeliveryDate = new(2027, 1, 20) }, returned.RowVersion, true), "assistant", UserRole.ProductionViewer, default);
        var approved = await service.ApproveAsync(first.Id, Approval(second), "planner", UserRole.PlanningManager, default);
        var detail = await service.DetailAsync(first.Id, "assistant", UserRole.ProductionViewer, default);
        Assert.Equal(2, approved.Revision);
        Assert.Equal(["提交申请", "退回修改", "提交申请", "批准并创建主项目"], detail.History.Select(x => x.Action));
        Assert.Equal("提前立项设备", detail.History[0].Data.Input.ProjectName);
        Assert.Equal(new DateOnly(2026, 12, 30), detail.History[0].Data.Input.DeliveryDate);
        Assert.Equal("交货日期需延后", detail.History[1].Comment);
        Assert.Equal("调整后的设备", detail.History[2].Data.Input.ProjectName);
        Assert.False(detail.CanEdit);
        await Assert.ThrowsAsync<PdmConflictException>(() => service.SaveAsync(first.Id, new(Input(), approved.RowVersion), "assistant", UserRole.ProductionViewer, default));
    }

    [Fact]
    public async Task RepeatedAndConcurrentApprovalsCreateExactlyOneProjectAndApprovalEvent()
    {
        var (repository, service, _) = await Setup();
        var app = await service.SaveAsync(null, new(Input(), 0, true), "assistant", UserRole.ProductionViewer, default);
        var command = Approval(app);
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => service.ApproveAsync(app.Id, command, "planner", UserRole.PlanningManager, default))));
        Assert.Single(results.Select(x => x.ProjectId).Distinct());
        Assert.Single(await repository.ListProjectsAsync(default), x => x.SourceApplicationId == app.Id);
        Assert.Single(await repository.ListApplicationHistoryAsync(app.Id, default), x => x.Action == "批准并创建主项目");
    }

    [Fact]
    public async Task InvalidApprovalDoesNotChangeApplicationOrAllocateProject()
    {
        var (repository, service, _) = await Setup();
        var app = await service.SaveAsync(null, new(Input(), 0, true), "assistant", UserRole.ProductionViewer, default);
        var before = (await repository.ListProjectsAsync(default)).Count;
        await Assert.ThrowsAsync<PdmRuleException>(() => service.ApproveAsync(app.Id, Approval(app) with { CustomerId = Guid.NewGuid() }, "planner", UserRole.PlanningManager, default));
        var detail = await service.DetailAsync(app.Id, "assistant", UserRole.ProductionViewer, default);
        Assert.Equal("待审批", detail.Application.Status);
        Assert.Single(detail.History);
        Assert.Equal(before, (await repository.ListProjectsAsync(default)).Count);
    }

    [Fact]
    public async Task FillingOrderDateAndEditingProjectDoNotAlterOriginalApplication()
    {
        var (repository, service, _) = await Setup();
        var app = await service.SaveAsync(null, new(Input(), 0, true), "assistant", UserRole.ProductionViewer, default);
        var approved = await service.ApproveAsync(app.Id, Approval(app), "planner", UserRole.PlanningManager, default);
        var project = await service.UpdateOrderDateAsync(approved.ProjectId!.Value, new(new(2026, 11, 1), null), "planner", UserRole.PlanningManager, default);
        Assert.Equal(new DateOnly(2026, 11, 1), project.SignedDate);
        await Assert.ThrowsAsync<PdmConflictException>(() => service.UpdateOrderDateAsync(project.Id, new(new(2026, 11, 2), null), "planner", UserRole.PlanningManager, default));
        await repository.UpdateProjectDetailsAsync(project.Id, new(project.OrganizationId, project.ProjectTypeCode, project.EquipmentTypeCode, null, "执行期改名", null, project.SignedDate, 1), default);
        var detail = await service.DetailAsync(app.Id, "assistant", UserRole.ProductionViewer, default);
        Assert.Null(detail.Application.Data.Input.OrderDate);
        Assert.Equal("提前立项设备", detail.Application.Data.Input.ProjectName);
        Assert.All(detail.History, entry => Assert.Null(entry.Data.Input.OrderDate));
        Assert.Equal(2, detail.History.Count);
    }

    [Fact]
    public async Task ApplicantOwnershipRolesTenantIsolationAndStaleWritesAreEnforced()
    {
        var (repository, service, _) = await Setup();
        var app = await service.SaveAsync(null, new(Input(), 0, true), "assistant", UserRole.ProductionViewer, default);
        Assert.Empty((await service.GetAsync("assistant2", UserRole.ProductionViewer, default)).Applications);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.DetailAsync(app.Id, "assistant2", UserRole.ProductionViewer, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ApproveAsync(app.Id, Approval(app), "assistant", UserRole.ProductionViewer, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SaveAsync(null, new(Input(), 0, true), "planner", UserRole.PlanningManager, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SaveAsync(app.Id, new(Input(), app.RowVersion), "assistant2", UserRole.ProductionViewer, default));
        var withdrawn = await service.WithdrawAsync(app.Id, new(app.RowVersion), "assistant", UserRole.ProductionViewer, default);
        Assert.Equal("已撤回", withdrawn.Status);
        await Assert.ThrowsAsync<PdmConflictException>(() => service.ApproveAsync(app.Id, Approval(app), "planner", UserRole.PlanningManager, default));
        await Assert.ThrowsAsync<PdmConflictException>(() => service.SaveAsync(app.Id, new(Input(), app.RowVersion, true), "assistant", UserRole.ProductionViewer, default));
        var second = await service.SaveAsync(app.Id, new(Input(), withdrawn.RowVersion, true), "assistant", UserRole.ProductionViewer, default);
        Assert.Equal(2, second.Revision);
        Assert.Null(await repository.FindApplicationAsync(app.Id, Guid.NewGuid(), default));
    }

    [Fact]
    public async Task DraftMayBeIncompleteButSubmittedDatesAndPeopleAreValidated()
    {
        var (_, service, _) = await Setup();
        var draft = await service.SaveAsync(null, new(Input() with { ProjectName = "", CustomerName = "", DeliveryDate = null, SalesUsername = "", PresalesUsernames = [] }, 0), "assistant", UserRole.ProductionViewer, default);
        Assert.Equal("草稿", draft.Status);
        Assert.Equal(0, draft.Revision);
        await Assert.ThrowsAsync<PdmRuleException>(() => service.SaveAsync(draft.Id, new(draft.Data.Input, draft.RowVersion, true), "assistant", UserRole.ProductionViewer, default));
        await Assert.ThrowsAsync<PdmRuleException>(() => service.SaveAsync(null, new(Input() with { OrderDate = new(2027, 1, 1) }, 0, true), "assistant", UserRole.ProductionViewer, default));
        await Assert.ThrowsAsync<PdmRuleException>(() => service.SaveAsync(null, new(Input() with { PresalesUsernames = ["missing-person"] }, 0, true), "assistant", UserRole.ProductionViewer, default));
    }

    [Fact]
    public async Task ApplicantCanRevisePendingApplicationWithoutLosingEarlierSubmissionOrApprovingStaleVersion()
    {
        var (_, service, _) = await Setup();
        var first = await service.SaveAsync(null, new(Input(), 0, true), "assistant", UserRole.ProductionViewer, default);
        Assert.True((await service.DetailAsync(first.Id, "assistant", UserRole.ProductionViewer, default)).CanEdit);
        var changed = Input() with { ProjectName = "申请人修改的项目", OrderDate = new(2026, 11, 1), SalesUsername = "sales2", PresalesUsernames = ["pre3"], Remarks = "更新交期及人员" };
        var revised = await service.SaveAsync(first.Id, new(changed, first.RowVersion), "assistant", UserRole.ProductionViewer, default);
        Assert.Equal("待审批", revised.Status);
        Assert.Equal(2, revised.Revision);
        var history = (await service.DetailAsync(first.Id, "assistant", UserRole.ProductionViewer, default)).History;
        Assert.Equal("提前立项设备", history[0].Data.Input.ProjectName);
        Assert.Null(history[0].Data.Input.OrderDate);
        Assert.Equal("修改并重新提交", history[1].Action);
        Assert.Equal("申请人修改的项目", history[1].Data.Input.ProjectName);
        Assert.Equal("销售乙", history[1].Data.SalesName);
        await Assert.ThrowsAsync<PdmConflictException>(() => service.ApproveAsync(first.Id, Approval(first), "planner", UserRole.PlanningManager, default));
        await Assert.ThrowsAsync<PdmConflictException>(() => service.SaveAsync(first.Id, new(changed, first.RowVersion, true), "assistant", UserRole.ProductionViewer, default));
        var approved = await service.ApproveAsync(first.Id, Approval(revised), "planner", UserRole.PlanningManager, default);
        Assert.Equal("已批准", approved.Status);
        await Assert.ThrowsAsync<PdmConflictException>(() => service.SaveAsync(first.Id, new(changed, approved.RowVersion, true), "assistant", UserRole.ProductionViewer, default));
    }

    [Fact]
    public async Task ApproverCanEditProjectFieldsAndKeepsOriginalAndFinalSnapshotsSeparately()
    {
        var (repository, service, contacts) = await Setup();
        var app = await service.SaveAsync(null, new(Input(), 0, true), "assistant", UserRole.ProductionViewer, default);
        var finalInput = Input() with { ProjectName = "审批后确定的项目", OrderDate = new(2026, 11, 2), DeliveryDate = new(2027, 2, 1),
            SalesUsername = "sales2", PresalesUsernames = ["pre3", "pre1"], Remarks = "审批调整客户交期及人员", IsSpecial = false };
        var approved = await service.ApproveAsync(app.Id, Approval(app) with { ProjectData = finalInput, Quantity = 2 }, "planner", UserRole.PlanningManager, default);
        var project = (await repository.FindProjectAsync(approved.ProjectId!.Value, default))!;
        Assert.Equal(finalInput.ProjectName, project.Name);
        Assert.Equal(finalInput.OrderDate, project.SignedDate);
        Assert.Equal(finalInput.DeliveryDate, project.DeliveryDate);
        Assert.False(project.IsSpecialInitiation);
        Assert.Equal(2, project.Quantity);
        var staff = (await contacts.FindAsync(project.Id, default))!;
        Assert.Equal("sales2", staff.SalesPeople);
        Assert.Equal("pre3、pre1", staff.PresalesPeople);
        var detail = await service.DetailAsync(app.Id, "assistant", UserRole.ProductionViewer, default);
        Assert.Equal("提前立项设备", detail.Application.Data.Input.ProjectName);
        Assert.Null(detail.Application.Data.Input.OrderDate);
        Assert.True(detail.Application.Data.Input.IsSpecial);
        var approvalEvent = detail.History.Last();
        Assert.Equal("提前立项设备", approvalEvent.Data.Input.ProjectName);
        Assert.Equal(finalInput.ProjectName, approvalEvent.ApprovedData!.Input.ProjectName);
        Assert.Equal("销售乙", approvalEvent.ApprovedData.SalesName);
        Assert.Equal(["售前丙", "售前甲"], approvalEvent.ApprovedData.PresalesPeople.Select(x => x.DisplayName));
        Assert.Equal(project.CustomerName, approvalEvent.ApprovedData.Input.CustomerName);
        Assert.Equal(finalInput.Remarks, approvalEvent.Approval!.ProjectData!.Remarks);
    }

    [Fact]
    public async Task EditedApprovalFieldsAreValidatedBeforeProjectCreation()
    {
        var (repository, service, _) = await Setup();
        var app = await service.SaveAsync(null, new(Input(), 0, true), "assistant", UserRole.ProductionViewer, default);
        foreach (var invalid in new[] { Input() with { ProjectName = "" }, Input() with { DeliveryDate = null }, Input() with { PresalesUsernames = [] },
            Input() with { SalesUsername = "missing-person" }, Input() with { OrderDate = new(2027, 1, 1) } })
            await Assert.ThrowsAsync<PdmRuleException>(() => service.ApproveAsync(app.Id, Approval(app) with { ProjectData = invalid }, "planner", UserRole.PlanningManager, default));
        Assert.Equal("待审批", (await service.DetailAsync(app.Id, "assistant", UserRole.ProductionViewer, default)).Application.Status);
        Assert.Single(await repository.ListApplicationHistoryAsync(app.Id, default));
        Assert.DoesNotContain(await repository.ListProjectsAsync(default), x => x.SourceApplicationId == app.Id);
    }
}
