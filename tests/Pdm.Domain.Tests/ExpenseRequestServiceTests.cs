using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;
using Upton.Pdm.Tests;

namespace Upton.Pdm.Domain.Tests;

public sealed class ExpenseRequestServiceTests
{
    [Fact]
    public async Task CompletionCopyWaitsForAllApprovalsAndDoesNotSendAfterWithdrawal()
    {
        var (_,repository,project)=await Setup();
        var settings=await repository.GetSystemSettingsAsync(default);
        var flow=settings.ApprovalWorkflows.ExpenseProcurement!;
        var copyRule=new ApprovalCopyRule("Submitter","","部门审核已完成") { Recipients=[new("User","buyer"),new("Department",flow.Steps[1].FixedUnitId!.Value.ToString(),"Approver")] };
        await repository.UpdateSystemSettingsAsync(settings with {ApprovalWorkflows=settings.ApprovalWorkflows with {ExpenseProcurement=flow with {Steps=[flow.Steps[0],flow.Steps[1] with {CopyOnApproval=copyRule},flow.Steps[2]]}}},default);
        var store=new InMemoryExpenseRequestRepository(repository);
        var notices=new InMemoryWorkflowNotificationStore();
        var workflow=new WorkflowNotificationService(repository,notices,new InMemoryMaterialRepository(TimeProvider.System,repository),new InMemoryValidationPlanRepository(),TimeProvider.System,store);
        var service=new ExpenseRequestService(repository,store,TimeProvider.System,workflow);
        TenantContext.Set(new(Guid.NewGuid(),project.OrganizationId!.Value,project.OrganizationId.Value,"manager","Administrator",true,new HashSet<string>()));
        try {
            await workflow.SaveFlowAsync("expense-approval",new(true,[new("User",null,Target:"buyer"),new("User",null,Target:"buyer")],0,project.OrganizationId.Value),"manager",UserRole.Administrator,default);
            var saved=await service.SaveAsync(project.Id,Guid.NewGuid(),Input(2500),"manager",UserRole.Administrator,default);
            Assert.Equal(["buyer"],saved.Request.Steps[0].Participants.CopyOnApproval!.Recipients);
            var job=Assert.Single(await notices.ListJobsAsync(project.OrganizationId.Value,"expense-approval",default));
            await workflow.ProcessAsync(job.Id,default);
            saved=await service.DecideAsync(project.Id,saved.Request.Id,new(saved.Request.RowVersion,true,"部门通过"),"buyer",UserRole.Approver,default);
            await workflow.ProcessAsync(job.Id,default);
            Assert.DoesNotContain(await repository.ListUserNotificationsAsync("buyer",100,default),notice=>notice.Category=="WorkflowExpenseCopy");
            await service.DecideAsync(project.Id,saved.Request.Id,new(saved.Request.RowVersion,true,"负责人通过"),"extra",UserRole.Approver,default);
            await workflow.ProcessAsync(job.Id,default);await workflow.ProcessAsync(job.Id,default);
            var copy=Assert.Single(await repository.ListUserNotificationsAsync("buyer",100,default),notice=>notice.Category=="WorkflowExpenseCopy");
            Assert.DoesNotContain("2500",copy.Content);
            var withdrawn=await service.SaveAsync(project.Id,Guid.NewGuid(),Input(100),"manager",UserRole.Administrator,default);
            await service.WithdrawAsync(project.Id,withdrawn.Request.Id,new(withdrawn.Request.RowVersion),"manager",UserRole.Administrator,default);
            var cancelled=(await notices.ListJobsAsync(project.OrganizationId.Value,"expense-approval",default)).Single(item=>item.BusinessId==withdrawn.Request.Id);
            await workflow.ProcessAsync(cancelled.Id,default);Assert.Equal("Cancelled",(await notices.FindJobAsync(cancelled.Id,default))!.State);
        } finally { TenantContext.Clear(); }
    }
    [Fact]
    public async Task TechnicalAdministratorAndFormerManagerCannotReadAmountsOrCreateRequests()
    {
        var (service, repository, project) = await Setup();
        var saved = await service.SaveAsync(project.Id, Guid.NewGuid(), Input(2500), "manager", UserRole.Administrator, default);
        await repository.CreateUserAsync(new(Guid.NewGuid(), "technical", "技术管理员", "unused", UserRole.Administrator, true), default);
        Assert.Empty((await service.GetAsync(project.Id, "technical", UserRole.Administrator, default)).Requests);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SaveAsync(project.Id, Guid.NewGuid(), Input(1), "technical", UserRole.Administrator, default));
        await repository.SetMainProjectStaffingAsync(project.Id, new("extra", [], []), "manager", default);
        var former = await service.GetAsync(project.Id, "manager", UserRole.Administrator, default);
        Assert.False(former.CanCreate);
        var view = Assert.Single(former.Requests);
        Assert.False(view.CanViewAmount); Assert.False(view.CanWithdraw);
        Assert.Null(view.Request.TotalAmount); Assert.All(view.Request.Lines, line => { Assert.Null(line.UnitPrice); Assert.Null(line.Amount); });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.WithdrawAsync(project.Id, saved.Request.Id, new(saved.Request.RowVersion), "manager", UserRole.Administrator, default));
    }

    [Fact]
    public async Task DepartmentAmountsAreIsolatedAndApprovalMessagesContainNoAmounts()
    {
        var (service, repository, project) = await Setup();
        var procurement = await service.SaveAsync(project.Id, Guid.NewGuid(), Input(2500), "manager", UserRole.Administrator, default);
        await service.SaveAsync(project.Id, Guid.NewGuid(), Input(3500) with { Department = "SupplyChain" }, "manager", UserRole.Administrator, default);
        await repository.CreateUserAsync(new(Guid.NewGuid(), "department-reader", "采购人员", "unused", UserRole.Administrator, true, RoleCode: "ProcurementSpecialist", CompanyId: project.OrganizationId), default);
        Assert.NotNull(procurement.Request.DepartmentId);
        await repository.SetOrganizationMembershipsAsync("department-reader", [procurement.Request.DepartmentId!.Value], procurement.Request.DepartmentId.Value, default);
        var owned = Assert.Single((await service.GetAsync(project.Id, "department-reader", UserRole.Administrator, default)).Requests);
        Assert.True(owned.CanViewAmount); Assert.Equal(2500m, owned.Request.TotalAmount);
        var reviewer = (await service.GetAsync(project.Id, "extra", UserRole.Approver, default)).Requests;
        Assert.All(reviewer, view => { Assert.False(view.CanViewAmount); Assert.Null(view.Request.TotalAmount); });
        var notices = await repository.ListUnreadUserNotificationsAsync("buyer", default);
        Assert.Contains(notices, item => item.Category == "ExpenseApprovalPending");
        Assert.All(notices, item => Assert.DoesNotContain("2500", item.Content));
    }

    [Fact]
    public async Task WithdrawClosesPendingWorkRejectsStaleDecisionsAndAllowsResubmission()
    {
        var (service, repository, project) = await Setup();
        var saved = await service.SaveAsync(project.Id, Guid.NewGuid(), Input(2500), "manager", UserRole.Administrator, default);
        Assert.True(saved.CanWithdraw);
        await Assert.ThrowsAsync<PdmConflictException>(() => service.WithdrawAsync(project.Id, saved.Request.Id, new(0), "manager", UserRole.Administrator, default));
        var withdrawn = await service.WithdrawAsync(project.Id, saved.Request.Id, new(saved.Request.RowVersion), "manager", UserRole.Administrator, default);
        Assert.Equal("Withdrawn", withdrawn.Request.Status); Assert.True(withdrawn.CanEdit); Assert.False(withdrawn.CanWithdraw);
        Assert.Null(withdrawn.Request.CurrentStep);
        Assert.DoesNotContain(await repository.ListUnreadUserNotificationsAsync("buyer", default), notice => notice.Category == "ExpenseApprovalPending" && notice.ResolvedAt is null);
        await Assert.ThrowsAsync<PdmConflictException>(() => service.DecideAsync(project.Id, saved.Request.Id, new(saved.Request.RowVersion, true, "旧待办"), "buyer", UserRole.Approver, default));
        var resumed = await service.SaveAsync(project.Id, saved.Request.Id, Input(100) with { ExpectedRowVersion = withdrawn.Request.RowVersion }, "manager", UserRole.Administrator, default);
        Assert.Equal("Pending", resumed.Request.Status); Assert.Single(resumed.Request.Steps); Assert.Equal(100m, resumed.Request.TotalAmount);
        Assert.NotEqual(saved.Request.Steps[0].Id, resumed.Request.Steps[0].Id);
    }

    [Fact]
    public async Task ExtraNodeUsesEachSelectedDepartmentsConfiguredManager()
    {
        var (service, repository, project) = await Setup();
        var settings = await repository.GetSystemSettingsAsync(default);
        async Task<ApprovalWorkflowTemplate> ManagerFlow(ApprovalWorkflowTemplate flow, string manager)
        {
            var unitId = flow.Steps[1].FixedUnitId!.Value;
            await repository.SetOrganizationUnitManagersAsync(unitId, manager, [], default);
            return flow with { Steps = [flow.Steps[0], flow.Steps[1], flow.Steps[2] with {
                AssigneeSource = ApprovalAssigneeSource.FixedUnitManager, FixedAssignee = null, FixedUnitId = unitId }] };
        }
        await repository.UpdateSystemSettingsAsync(settings with { ApprovalWorkflows = settings.ApprovalWorkflows with {
            ExpenseProcurement = await ManagerFlow(settings.ApprovalWorkflows.ExpenseProcurement!, "buyer"),
            ExpenseSupplyChain = await ManagerFlow(settings.ApprovalWorkflows.ExpenseSupplyChain!, "supply")
        } }, default);
        var procurement = await service.SaveAsync(project.Id, Guid.NewGuid(), Input(2001), "manager", UserRole.Administrator, default);
        var supply = await service.SaveAsync(project.Id, Guid.NewGuid(), Input(2001) with { Department = "SupplyChain" }, "manager", UserRole.Administrator, default);
        Assert.Equal("buyer", procurement.Request.Steps[1].Participants.Members.Single());
        Assert.Equal("supply", supply.Request.Steps[1].Participants.Members.Single());
        Assert.Equal(2, procurement.Request.Steps.Count);
    }

    [Theory]
    [InlineData("SitePurchase", "Procurement", 1999.99, 1)]
    [InlineData("RepairSupplement", "Procurement", 2000, 1)]
    [InlineData("SitePurchase", "SupplyChain", 2000.01, 2)]
    [InlineData("RepairSupplement", "SupplyChain", 2500, 2)]
    public async Task DepartmentDeterminesRouteAndExtraApprovalFollowsDepartment(string purpose, string department, decimal amount, int count)
    {
        var (service, repository, project) = await Setup();
        var input = Input(amount) with { Purpose = purpose, Department = department };
        var view = await service.SaveAsync(project.Id, Guid.NewGuid(), input, "manager", UserRole.Administrator, default);
        Assert.Equal(count, view.Request.Steps.Count);
        Assert.Equal(department == "Procurement" ? "buyer" : "supply", Assert.Single(view.Request.CurrentStep!.Participants.Members));
        Assert.Equal(new DateOnly(2026, 12, 1), view.Request.RequiredOn);
        Assert.Equal(amount, view.Request.TotalAmount);
        var buyer = department == "Procurement" ? "buyer" : "supply";
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.DecideAsync(project.Id, view.Request.Id,
            new(view.Request.RowVersion, true, "越级"), "extra", UserRole.Approver, default));
        view = await service.DecideAsync(project.Id, view.Request.Id, new(view.Request.RowVersion, true, "部门同意"), buyer, UserRole.Approver, default);
        if (count == 2)
        {
            Assert.Equal("Pending", view.Request.Status);
            Assert.Equal("extra", Assert.Single(view.Request.CurrentStep!.Participants.Members));
            view = await service.DecideAsync(project.Id, view.Request.Id, new(view.Request.RowVersion, true, "同意"), "extra", UserRole.Approver, default);
        }
        Assert.Equal("Approved", view.Request.Status);
        Assert.Null(view.Request.CurrentStep);
        var notices = await repository.ListUnreadUserNotificationsAsync(buyer, default);
        Assert.DoesNotContain(notices, item => item.Category == "ExpenseApprovalPending" && item.ResolvedAt is null);
    }

    [Fact]
    public async Task RequiredDateAndIntegerUnitsAreValidatedAndDraftCanBeIncomplete()
    {
        var (service, _, project) = await Setup();
        var input = Input(2000) with { RequiredOn = null };
        var error = await Assert.ThrowsAsync<PdmRuleException>(() => service.SaveAsync(project.Id, Guid.NewGuid(), input, "manager", UserRole.Administrator, default));
        Assert.Contains("需求日期", error.Message);
        var draft = await service.SaveAsync(project.Id, Guid.NewGuid(), input with { Submit = false }, "manager", UserRole.Administrator, default);
        Assert.Equal("Draft", draft.Request.Status);
        await Assert.ThrowsAsync<PdmRuleException>(() => service.SaveAsync(project.Id, Guid.NewGuid(), Input(10) with {
            Lines = [Input(10).Lines[0] with { Quantity = 1.5m, Unit = "个" }] }, "manager", UserRole.Administrator, default));
        var fractional = await service.SaveAsync(project.Id, Guid.NewGuid(), Input(10) with {
            Lines = [Input(10).Lines[0] with { Quantity = 1.5m, Unit = "米" }] }, "manager", UserRole.Administrator, default);
        Assert.Equal(15m, fractional.Request.TotalAmount);
    }

    [Fact]
    public async Task SnapshotAndOptimisticLockProtectApprovalsAndResubmitRebuildsRoute()
    {
        var (service, repository, project) = await Setup();
        var view = await service.SaveAsync(project.Id, Guid.NewGuid(), Input(2500), "manager", UserRole.Administrator, default);
        var settings = await repository.GetSystemSettingsAsync(default);
        var changed = settings.ApprovalWorkflows.ExpenseProcurement!;
        await repository.UpdateSystemSettingsAsync(settings with { ApprovalWorkflows = settings.ApprovalWorkflows with {
            ExpenseProcurement = changed with { Steps = changed.Steps.Select((step, index) => index == 2 ? step with { FixedAssignee = "supply" } : step).ToArray() }
        } }, default);
        var first = await service.DecideAsync(project.Id, view.Request.Id, new(view.Request.RowVersion, true, "同意"), "buyer", UserRole.Approver, default);
        Assert.Equal("extra", first.Request.CurrentStep!.Participants.Members.Single());
        await Assert.ThrowsAsync<PdmConflictException>(() => service.DecideAsync(project.Id, first.Request.Id,
            new(view.Request.RowVersion, true, "旧版本"), "extra", UserRole.Approver, default));
        var rejected = await service.DecideAsync(project.Id, first.Request.Id, new(first.Request.RowVersion, false, "请补充原因"), "extra", UserRole.Approver, default);
        Assert.Equal("Rejected", rejected.Request.Status);
        var resubmitted = await service.SaveAsync(project.Id, first.Request.Id, Input(2000) with {
            ExpectedRowVersion = rejected.Request.RowVersion, Department = "SupplyChain", Purpose = "RepairSupplement"
        }, "manager", UserRole.Administrator, default);
        Assert.Single(resubmitted.Request.Steps);
        Assert.Equal("supply", resubmitted.Request.CurrentStep!.Participants.Members.Single());
    }

    [Fact]
    public async Task DepartmentCountersignRequiresEveryMemberBeforeExtraApproval()
    {
        var (service, repository, project) = await Setup();
        var settings = await repository.GetSystemSettingsAsync(default);
        var flow = settings.ApprovalWorkflows.ExpenseProcurement!;
        var departmentId = flow.Steps[1].FixedUnitId!.Value;
        await repository.SetOrganizationMembershipsAsync("supply", [departmentId], departmentId, default);
        await repository.UpdateSystemSettingsAsync(settings with { ApprovalWorkflows = settings.ApprovalWorkflows with {
            ExpenseProcurement = flow with { Steps = flow.Steps.Select((step, index) => index == 1 ? step with { RequireAllApprovers = true } : step).ToArray() }
        } }, default);
        var view = await service.SaveAsync(project.Id, Guid.NewGuid(), Input(2500), "manager", UserRole.Administrator, default);
        view = await service.DecideAsync(project.Id, view.Request.Id, new(view.Request.RowVersion, true, "同意"), "buyer", UserRole.Approver, default);
        Assert.Equal("部门审批", view.Request.CurrentStep!.Name);
        view = await service.DecideAsync(project.Id, view.Request.Id, new(view.Request.RowVersion, true, "同意"), "supply", UserRole.Approver, default);
        Assert.Equal("超额审批", view.Request.CurrentStep!.Name);
    }

    [Fact]
    public async Task CustomExpenseNodesKeepOrderAndEvaluateFormFieldsAtSubmission()
    {
        var (service, repository, project) = await Setup();
        var settings = await repository.GetSystemSettingsAsync(default);
        var flow = settings.ApprovalWorkflows.ExpenseProcurement!;
        var extra = flow.Steps[2] with { AmountGreaterThan = null, Conditions = new("All", [new("Purpose", "Eq", "RepairSupplement"), new("TotalAmount", "Gte", "3000")]) };
        var added = new ApprovalWorkflowStepTemplate(ApprovalStage.HardwareSupervisor, "最终审核", ApprovalAssigneeSource.FixedUser, "supply");
        await repository.UpdateSystemSettingsAsync(settings with { ApprovalWorkflows = settings.ApprovalWorkflows with {
            ExpenseProcurement = flow with { Steps = [flow.Steps[0], extra, flow.Steps[1], added] }
        } }, default);
        var skipped = await service.SaveAsync(project.Id, Guid.NewGuid(), Input(4000), "manager", UserRole.Administrator, default);
        Assert.Equal(new[] { "部门审批", "最终审核" }, skipped.Request.Steps.Select(step => step.Name));
        var selected = await service.SaveAsync(project.Id, Guid.NewGuid(), Input(3000) with { Purpose = "RepairSupplement" }, "manager", UserRole.Administrator, default);
        Assert.Equal(new[] { "超额审批", "部门审批", "最终审核" }, selected.Request.Steps.Select(step => step.Name));
        selected = await service.DecideAsync(project.Id,selected.Request.Id,new(selected.Request.RowVersion,true,"同意"),"extra",UserRole.Approver,default);
        selected = await service.DecideAsync(project.Id,selected.Request.Id,new(selected.Request.RowVersion,true,"同意"),"buyer",UserRole.Approver,default);
        selected = await service.DecideAsync(project.Id,selected.Request.Id,new(selected.Request.RowVersion,true,"同意"),"supply",UserRole.Approver,default);
        Assert.Equal("Approved",selected.Request.Status);
    }

    [Theory]
    [InlineData(2000,"extra")]
    [InlineData(2000.01,"buyer")]
    public async Task IndependentDecisionAssignsOnlySelectedBranchThenCommonApproval(decimal amount,string reviewer)
    {
        var (service,repository,project)=await Setup();var settings=await repository.GetSystemSettingsAsync(default);var flow=settings.ApprovalWorkflows.ExpenseProcurement!;
        var steps=flow.Steps.Select(step=>step with { AmountGreaterThan=null }).ToList();steps.Add(new(ApprovalStage.HardwareSupervisor,"汇合后审核",ApprovalAssigneeSource.FixedUser,"supply"));
        ApprovalFlowNode Node(int i)=>new("node"+i,"Approval",steps[i].Name,steps[i].Stage);
        flow=flow with {Steps=steps,Layout=[Node(0),new("amount","Condition","金额判断",Branches:[new("a","大于2000",new("All",[new("TotalAmount","Gt","2000")]),[Node(1)]),new("b","其他情况",null,[Node(2)])]),Node(3)]};
        await repository.UpdateSystemSettingsAsync(settings with {ApprovalWorkflows=settings.ApprovalWorkflows with {ExpenseProcurement=flow}},default);
        var view=await service.SaveAsync(project.Id,Guid.NewGuid(),Input(amount),"manager",UserRole.Administrator,default);
        Assert.Equal(2,view.Request.Steps.Count);Assert.Equal(reviewer,view.Request.CurrentStep!.Participants.Members.Single());
        var excluded=reviewer=="buyer"?"extra":"buyer";
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>service.DecideAsync(project.Id,view.Request.Id,new(view.Request.RowVersion,true,"不在本次分支"),excluded,UserRole.Approver,default));
        view=await service.DecideAsync(project.Id,view.Request.Id,new(view.Request.RowVersion,true,"同意"),reviewer,UserRole.Approver,default);
        Assert.Equal("汇合后审核",view.Request.CurrentStep!.Name);
        view=await service.DecideAsync(project.Id,view.Request.Id,new(view.Request.RowVersion,true,"同意"),"supply",UserRole.Approver,default);
        Assert.Equal("Approved",view.Request.Status);
    }

    private static SaveExpenseRequest Input(decimal amount) => new("SitePurchase", "Procurement", new(2026, 12, 1), "现场施工需要",
        [new("Material", "", "采购物料", "规格", "", "个", 1, amount, "")], 0, true);

    private static async Task<(ExpenseRequestService, InMemoryPdmRepository, Project)> Setup()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var project = Assert.Single(await repository.ListProjectsAsync(default));
        var company = (await repository.GetOrganizationDirectoryAsync(default)).Organizations.First(item => item.IsActive).Id;
        foreach (var name in new[] { "manager", "buyer", "supply", "extra" })
            await repository.CreateUserAsync(new(Guid.NewGuid(), name, name, "unused", name == "manager" ? UserRole.Administrator : UserRole.Approver, true, CompanyId: company), default);
        await FinancialTestActors.GrantAsync(repository, "Administrator",
            new(PermissionCodes.ProjectExpenseView, ["company"]), new(PermissionCodes.ProjectExpenseSubmit, ["project-manager"]));
        await FinancialTestActors.GrantAsync(repository, "Approver",
            new(PermissionCodes.ProjectExpenseView, ["company"]), new(PermissionCodes.ProjectExpenseApprove, ["company"]));
        await FinancialTestActors.GrantAsync(repository, "ProcurementSpecialist", new AuthorizationGrant(PermissionCodes.ProjectExpenseView, ["company"]));
        var customer = (await repository.ListCustomersAsync(true, default)).First();
        project = await repository.CreateNumberedProjectAsync(new(company, "P", 2, customer.Id, "费用权限验收", null, null, 1, "manager", @"C:\qa-vault", @"C:\qa-release"), default);
        project = await repository.SetMainProjectStaffingAsync(project.Id, new("manager", [], []), "manager", default);
        var procurement = await repository.SaveOrganizationUnitAsync(new(null, company, null, "BUY", "采购部", OrganizationUnitKind.Department, true, 0), default);
        var supply = await repository.SaveOrganizationUnitAsync(new(null, company, null, "SUPPLY", "供应链", OrganizationUnitKind.Department, true, 0), default);
        await repository.SetOrganizationMembershipsAsync("buyer", [procurement.Id], procurement.Id, default);
        await repository.SetOrganizationMembershipsAsync("supply", [supply.Id], supply.Id, default);
        ApprovalWorkflowTemplate Flow(bool chain, Guid unitId)
        {
            var flow = ExpenseWorkflowDefaults.Create(chain);
            return flow with { Steps = [flow.Steps[0], flow.Steps[1] with { FixedUnitId = unitId }, flow.Steps[2] with { AssigneeSource = ApprovalAssigneeSource.FixedUser, FixedAssignee = "extra" }] };
        }
        var settings = await repository.GetSystemSettingsAsync(default);
        await repository.UpdateSystemSettingsAsync(settings with { ApprovalWorkflows = settings.ApprovalWorkflows with {
            ExpenseProcurement = Flow(false, procurement.Id), ExpenseSupplyChain = Flow(true, supply.Id)
        } }, default);
        return (new(repository, new InMemoryExpenseRequestRepository(repository), TimeProvider.System), repository, project);
    }
}
