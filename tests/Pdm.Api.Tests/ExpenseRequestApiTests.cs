using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api.Tests;

public sealed class ExpenseRequestApiTests
{
    [Fact]
    public async Task UnifiedSettingsPersistAmountBranchAndApiEnforcesDateAndApprovalOrder()
    {
        await using var factory = new PdmApiFactory();
        var client = factory.CreateClient();
        Login(client, "admin", "Administrator");
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        var project = (await repository.ListProjectsAsync(default)).First();
        if (await repository.FindUserAsync("admin", default) is null)
            await repository.CreateUserAsync(new(Guid.NewGuid(), "admin", "项目经理", "unused", UserRole.Administrator, true, CompanyId: project.OrganizationId), default);
        project = await repository.SetMainProjectStaffingAsync(project.Id, new("admin", [], []), "admin", default);
        foreach (var name in new[] { "expense-buyer", "expense-extra" })
            await repository.CreateUserAsync(new(Guid.NewGuid(), name, name, "unused", UserRole.Approver, true), default);
        var company = (await repository.GetOrganizationDirectoryAsync(default)).Organizations.First().Id;
        var department = await repository.SaveOrganizationUnitAsync(new(null, company, null, "EXPENSE", "采购部", OrganizationUnitKind.Department, true, 0), default);
        await repository.SetOrganizationMembershipsAsync("expense-buyer", [department.Id], department.Id, default);
        var settings = (await client.GetFromJsonAsync<PdmSystemSettings>("/api/system-settings"))!;
        var flow = ExpenseWorkflowDefaults.Create(false);
        var saveSettings = await client.PutAsJsonAsync("/api/system-settings", settings with { ApprovalWorkflows = settings.ApprovalWorkflows with {
            ExpenseProcurement = flow with { Steps = [flow.Steps[0], flow.Steps[1] with { FixedUnitId = department.Id },
                flow.Steps[2] with { AssigneeSource = ApprovalAssigneeSource.FixedUser, FixedAssignee = "expense-extra", AmountGreaterThan = null,
                    Conditions = new("All", [new("TotalAmount", "Gt", "3000")]) }] }
        } });
        Assert.True(saveSettings.IsSuccessStatusCode, await saveSettings.Content.ReadAsStringAsync());
        settings = (await client.GetFromJsonAsync<PdmSystemSettings>("/api/system-settings"))!;
        Assert.Null(settings.ApprovalWorkflows.ExpenseProcurement!.Steps[2].AmountGreaterThan);
        Assert.Equal("3000", settings.ApprovalWorkflows.ExpenseProcurement.Steps[2].Conditions!.Rules.Single().Value);
        Assert.Null(settings.ApprovalWorkflows.ExpenseSupplyChain);
        var configured = settings.ApprovalWorkflows.ExpenseProcurement;
        var expanded = configured with { Steps = [configured.Steps[0], configured.Steps[2], configured.Steps[1],
            new(ApprovalStage.HardwareSupervisor, "最终审核", ApprovalAssigneeSource.FixedUser, "expense-extra")] };
        var expandedResponse = await client.PutAsJsonAsync("/api/system-settings", settings with { ApprovalWorkflows = settings.ApprovalWorkflows with { ExpenseProcurement = expanded } });
        Assert.True(expandedResponse.IsSuccessStatusCode, await expandedResponse.Content.ReadAsStringAsync());
        var reread = (await client.GetFromJsonAsync<PdmSystemSettings>("/api/system-settings"))!;
        Assert.Equal(new[] { "提交费用申请", "超额审批", "部门审批", "最终审核" }, reread.ApprovalWorkflows.ExpenseProcurement!.Steps.Select(step => step.Name));
        Assert.Equal(department.Id, reread.ApprovalWorkflows.ExpenseProcurement.ExpenseDepartmentId);
        var invalidFlow = expanded with { Steps = [expanded.Steps[0], expanded.Steps[1] with { Conditions = new("All", [new("UnknownField", "Eq", "value")]) }] };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/system-settings", reread with { ApprovalWorkflows = reread.ApprovalWorkflows with { ExpenseProcurement = invalidFlow } })).StatusCode);
        var restore = await client.PutAsJsonAsync("/api/system-settings", reread with { ApprovalWorkflows = reread.ApprovalWorkflows with { ExpenseProcurement = configured } });
        Assert.True(restore.IsSuccessStatusCode, await restore.Content.ReadAsStringAsync());
        var graphSteps=configured.Steps.Select(step=>step with {Conditions=null}).ToArray();
        ApprovalFlowNode Node(int i)=>new("step"+i,"Approval",graphSteps[i].Name,graphSteps[i].Stage);
        var graphFlow=configured with {Steps=graphSteps,Layout=[Node(0),Node(1),new("amount","Condition","独立金额判断",Branches:[
            new("yes","大于3000",new("All",[new("TotalAmount","Gt","3000")]),[Node(2)]),new("no","其他情况",null,[])])]};
        var graphSave=await client.PutAsJsonAsync("/api/system-settings",reread with {ApprovalWorkflows=reread.ApprovalWorkflows with {ExpenseProcurement=graphFlow}});
        Assert.True(graphSave.IsSuccessStatusCode,await graphSave.Content.ReadAsStringAsync());
        var graphRead=(await client.GetFromJsonAsync<PdmSystemSettings>("/api/system-settings"))!.ApprovalWorkflows.ExpenseProcurement!;
        Assert.Equal("Condition",graphRead.Layout![2].Kind);Assert.Equal(2,graphRead.Layout[2].Branches!.Count);
        var mergeSettings = (await client.GetFromJsonAsync<PdmSystemSettings>("/api/system-settings"))!;
        var combined = ExpenseWorkflowDefaults.Unified(mergeSettings.ApprovalWorkflows with { ExpenseSupplyChain = graphRead })!;
        var additional = new[] { ApprovalStage.ExpenseReview5, ApprovalStage.ExpenseReview6, ApprovalStage.ExpenseReview7 }
            .Select(stage => new ApprovalWorkflowStepTemplate(stage,"供应链复核",ApprovalAssigneeSource.FixedUser,"expense-extra")).ToArray();
        var departmentNode = combined.Layout![1]; var supplyBranch = departmentNode.Branches![1];
        combined = combined with { Steps = combined.Steps.Concat(additional).ToArray(), Layout = [combined.Layout[0], departmentNode with {
            Branches = [departmentNode.Branches[0], supplyBranch with { Nodes = supplyBranch.Nodes.Concat(additional.Select(step => new ApprovalFlowNode(step.Stage.ToString(),"Approval",step.Name,step.Stage))).ToArray() }] }] };
        var mergedSave = await client.PutAsJsonAsync("/api/system-settings",mergeSettings with { ApprovalWorkflows = mergeSettings.ApprovalWorkflows with { Expense = combined } });
        Assert.True(mergedSave.IsSuccessStatusCode,await mergedSave.Content.ReadAsStringAsync());
        var mergedRead = (await client.GetFromJsonAsync<PdmSystemSettings>("/api/system-settings"))!.ApprovalWorkflows.Expense!;
        Assert.Equal("费用审批",mergedRead.Name); Assert.Equal(8,mergedRead.Steps.Count);
        Assert.Equal("Department",mergedRead.Layout![1].Branches![0].Conditions!.Rules[0].Field);
        var path = $"/api/projects/{project.Id}/expense-requests";
        var id = Guid.NewGuid();
        var input = new SaveExpenseRequest("RepairSupplement", "Procurement", null, "设备维修",
            [new("Service", "", "维修服务", "更换密封", "", "次", 1, 3000.01m, "")], 0, true);
        var invalid = await client.PutAsJsonAsync($"{path}/{id}", input);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Contains("需求日期", await invalid.Content.ReadAsStringAsync());
        var response = await client.PutAsJsonAsync($"{path}/{id}", input with { RequiredOn = new(2026, 12, 1) });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var saved = (await response.Content.ReadFromJsonAsync<ExpenseRequestView>())!;
        Assert.Equal(2, saved.Request.Steps.Count);
        Assert.Equal(ExpenseWorkflowDefaults.UnifiedCode,saved.Request.Workflow!.Code);
        Assert.True(saved.CanWithdraw);
        Login(client, "expense-buyer", "Approver");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"{path}/{id}/withdraw", new WithdrawExpenseRequest(saved.Request.RowVersion))).StatusCode);
        Login(client, "admin", "Administrator");
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"{path}/{id}/withdraw", new WithdrawExpenseRequest(0))).StatusCode);
        var withdrawnResponse = await client.PostAsJsonAsync($"{path}/{id}/withdraw", new WithdrawExpenseRequest(saved.Request.RowVersion));
        Assert.True(withdrawnResponse.IsSuccessStatusCode, await withdrawnResponse.Content.ReadAsStringAsync());
        var withdrawn = (await withdrawnResponse.Content.ReadFromJsonAsync<ExpenseRequestView>())!;
        Assert.Equal("Withdrawn", withdrawn.Request.Status);
        Assert.Null(withdrawn.Request.CurrentStep);
        Login(client, "expense-buyer", "Approver");
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"{path}/{id}/decision", new DecideExpenseRequest(saved.Request.RowVersion, true, "旧待办"))).StatusCode);
        Login(client, "admin", "Administrator");
        var resumed = await client.PutAsJsonAsync($"{path}/{id}", input with { RequiredOn = new(2026, 12, 1), ExpectedRowVersion = withdrawn.Request.RowVersion });
        Assert.True(resumed.IsSuccessStatusCode, await resumed.Content.ReadAsStringAsync());
        saved = (await resumed.Content.ReadFromJsonAsync<ExpenseRequestView>())!;
        Login(client, "expense-extra", "Approver");
        var premature = await client.PostAsJsonAsync($"{path}/{id}/decision", new DecideExpenseRequest(saved.Request.RowVersion, true, "同意"));
        Assert.Equal(HttpStatusCode.Forbidden, premature.StatusCode);
        Login(client, "expense-buyer", "Approver");
        var decision = await client.PostAsJsonAsync($"{path}/{id}/decision", new DecideExpenseRequest(saved.Request.RowVersion, true, "部门同意"));
        Assert.True(decision.IsSuccessStatusCode, await decision.Content.ReadAsStringAsync());
        saved = (await decision.Content.ReadFromJsonAsync<ExpenseRequestView>())!;
        Assert.False(saved.CanViewAmount);
        Assert.Null(saved.Request.TotalAmount);
        Assert.All(saved.Request.Lines, line => Assert.Null(line.UnitPrice));
        Assert.Equal("expense-extra", saved.Request.CurrentStep!.Participants.Members.Single());
        Login(client, "expense-extra", "Approver");
        var final = await client.PostAsJsonAsync($"{path}/{id}/decision", new DecideExpenseRequest(saved.Request.RowVersion, true, "同意"));
        Assert.True(final.IsSuccessStatusCode, await final.Content.ReadAsStringAsync());
        Assert.Equal("Approved", (await final.Content.ReadFromJsonAsync<ExpenseRequestView>())!.Request.Status);
    }

    private static void Login(HttpClient client, string username, string role)
    {
        var token = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("upton-pdm", "upton-pdm-clients",
            [new Claim(ClaimTypes.Name, username), new Claim(ClaimTypes.Role, role)], expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256)));
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
    }
}
