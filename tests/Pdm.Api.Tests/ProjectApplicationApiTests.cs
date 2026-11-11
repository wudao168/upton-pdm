using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.DependencyInjection;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api.Tests;

public sealed class ProjectApplicationApiTests
{
    private static void Authenticate(HttpClient client, string username = "admin", string role = "Administrator")
    {
        var jwt = new JwtSecurityToken("upton-pdm", "upton-pdm-clients", [new(ClaimTypes.Name, username), new(ClaimTypes.Role, role)],
            expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256));
        client.DefaultRequestHeaders.Authorization = new("Bearer", new JwtSecurityTokenHandler().WriteToken(jwt));
    }

    [Fact]
    public async Task HttpWorkflowAcceptsNullOrderDateAndPreservesSubmissionAndApproval()
    {
        using var factory = new PdmApiFactory();
        using var client = factory.CreateClient();
        const string path = "/api/project-applications";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
        Authenticate(client);
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        var company = (await repository.GetProjectNumberingOptionsAsync(default)).Organizations.First().Id;
        await repository.CreateUserAsync(new(Guid.NewGuid(), "admin", "兼任事业部负责人的管理员", "unused", UserRole.Administrator, true, CompanyId: company), default);
        var division = await repository.SaveOrganizationUnitAsync(new(null, company, null, "QA-BUDGET", "测试事业部", OrganizationUnitKind.BusinessDivision, true, 0), default);
        await repository.SetOrganizationUnitManagersAsync(division.Id, "admin", [], default);
        var view = (await client.GetFromJsonAsync<ProjectApplicationView>(path))!;
        Assert.True(view.CanApply); Assert.True(view.CanApprove);
        var person = view.People.First();
        var input = new ProjectApplicationInput(null, "提前启动新客户", "HTTP特殊立项", null, new(2026, 12, 30), person.Username, [person.Username], "合同未签", true)
        {
            BudgetAmounts = ProjectBudgetCategories.All.ToDictionary(category => category, category => (decimal?)(category == "Standard" ? 123.45m : 0m)),
            SettlementLines = [new("申请设备款", "台", 2, 123.45m), new("免费配件", "套", 1, 0)]
        };
        var request = JsonSerializer.SerializeToNode(new SaveProjectApplicationInput(input, 0, true), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        request["data"]!["settlementAmount"] = 999999m;
        request["data"]!["settlementLines"]![0]!["subtotal"] = 999999m;
        var createdResponse = await client.PostAsJsonAsync(path, request);
        Assert.Equal(HttpStatusCode.OK, createdResponse.StatusCode);
        var created = (await createdResponse.Content.ReadFromJsonAsync<ProjectApplication>())!;
        Assert.Matches("^LX[0-9]{8}-[0-9]{4}$", created.Number);
        Assert.Null(created.Data.Input.OrderDate);
        Assert.Equal(246.90m, created.Data.Input.SettlementAmount);
        using (var responseJson = JsonDocument.Parse(await createdResponse.Content.ReadAsStringAsync()))
            Assert.Equal(246.90m, responseJson.RootElement.GetProperty("data").GetProperty("input").GetProperty("settlementAmount").GetDecimal());
        Assert.True((await client.GetFromJsonAsync<ProjectApplicationDetail>($"{path}/{created.Id}"))!.CanEdit);
        var revisedInput = input with { ProjectName = "HTTP申请人修改后", DeliveryDate = new(2027, 1, 20) };
        var revisedResponse = await client.PutAsJsonAsync($"{path}/{created.Id}", new SaveProjectApplicationInput(revisedInput, created.RowVersion));
        Assert.Equal(HttpStatusCode.OK, revisedResponse.StatusCode);
        var revised = (await revisedResponse.Content.ReadFromJsonAsync<ProjectApplication>())!;
        Assert.Equal(created.Number, revised.Number);
        Assert.Equal("待审批", revised.Status);
        Assert.Equal(2, revised.Revision);
        var finalInput = revisedInput with { ProjectName = "HTTP审批后最终项目", OrderDate = new(2026, 11, 2), Remarks = "审批修改的备注", IsSpecial = false,
            Contract = new("HT-2026-01", "约定交付设备一套"),
            SettlementLines = [new("批准设备款", "台", 3, 120.25m, "批准结算"), new("免费配件", "套", 1, 0)] };
        var approval = new ProjectApplicationApprovalInput(revised.RowVersion, view.Customers.First().Id, "P", 2, 1, "0301", null, "批准提前立项", finalInput);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"{path}/{created.Id}/approve", approval with { ExpectedRowVersion = created.RowVersion })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"{path}/{created.Id}/return", new ProjectApplicationActionInput(revised.RowVersion, ""))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"{path}/{created.Id}/approve", approval with { ProjectData = finalInput with { Contract = null } })).StatusCode);
        var approveResponse = await client.PostAsJsonAsync($"{path}/{created.Id}/approve", approval);
        Assert.Equal(HttpStatusCode.OK, approveResponse.StatusCode);
        var approved = (await approveResponse.Content.ReadFromJsonAsync<ProjectApplication>())!;
        Assert.NotNull(approved.ProjectId);
        Assert.Equal(ProjectApplicationService.Approved, approved.Status);
        Assert.Equal(finalInput.Contract, approved.Contract);
        var legacyContract = await client.GetFromJsonAsync<ProjectApplicationContractLookup>($"{path}/projects/{approved.ProjectId}/contract");
        Assert.Equal(finalInput.Contract, legacyContract!.Contract);
        var budget = (await factory.Services.GetRequiredService<IProjectBudgetRepository>().FindAsync(approved.ProjectId.Value, default))!;
        Assert.Equal(123.45m, budget.Lines.Sum(line => line.BudgetAmount));
        Assert.Equal(360.75m, ProjectApplicationSettlements.Total(budget.SettlementLines));
        var budgetView = (await client.GetFromJsonAsync<ProjectBudgetView>($"/api/projects/{approved.ProjectId}/budget"))!;
        Assert.Equal(360.75m, budgetView.SettlementAmount);
        Assert.Equal("批准结算", budgetView.SettlementLines![0].Note);
        var project = Assert.Single((await client.GetFromJsonAsync<List<Project>>("/api/projects"))!, x => x.Id == approved.ProjectId);
        Assert.Equal(finalInput.ProjectName, project.Name);
        Assert.Equal(finalInput.OrderDate, project.SignedDate);
        Assert.Equal(finalInput.DeliveryDate, project.DeliveryDate);
        Assert.False(project.IsSpecialInitiation);
        var repeated = (await (await client.PostAsJsonAsync($"{path}/{created.Id}/approve", approval)).Content.ReadFromJsonAsync<ProjectApplication>())!;
        Assert.Equal(approved.ProjectId, repeated.ProjectId);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"{path}/{created.Id}", new SaveProjectApplicationInput(input with { ProjectName = "非法覆盖" }, approved.RowVersion))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"{path}/projects/{approved.ProjectId}/order-date", new ProjectOrderDateInput(new(2026, 11, 3), finalInput.OrderDate))).StatusCode);
        var detail = (await client.GetFromJsonAsync<ProjectApplicationDetail>($"{path}/{created.Id}"))!;
        Assert.Equal(3, detail.History.Count);
        Assert.All(detail.History, x => Assert.Null(x.Data.Input.OrderDate));
        Assert.Equal("HTTP特殊立项", detail.History[0].Data.Input.ProjectName);
        Assert.Equal("HTTP申请人修改后", detail.Application.Data.Input.ProjectName);
        Assert.Equal(finalInput.ProjectName, detail.History.Last().ApprovedData!.Input.ProjectName);
        Assert.Equal(finalInput.OrderDate, detail.History.Last().ApprovedData!.Input.OrderDate);
        Assert.All(detail.History, entry => Assert.Equal(246.90m, entry.Data.Input.SettlementAmount));
        Assert.Equal(360.75m, detail.History.Last().ApprovedData!.Input.SettlementAmount);
        Assert.Equal("批准提前立项", detail.History.Last().Comment);
        Assert.False(detail.CanEdit);
    }

    [Fact]
    public async Task SettlementDraftRoundTripPreservesIncompleteRowsAndSubmissionReturnsChineseValidation()
    {
        using var factory = new PdmApiFactory();
        using var client = factory.CreateClient();
        Authenticate(client);
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        await repository.CreateUserAsync(new(Guid.NewGuid(), "admin", "系统管理员", "unused", UserRole.Administrator, true), default);
        const string path = "/api/project-applications";
        var view = (await client.GetFromJsonAsync<ProjectApplicationView>(path))!;
        var person = view.People.First().Username;
        var input = new ProjectApplicationInput(null, "客户", "结算草稿", null, new(2026, 12, 30), person, [person], "", false)
        {
            BudgetAmounts = null,
            SettlementLines = [new("设备款", Quantity: 2), new("")]
        };
        var forbidden = await client.PostAsJsonAsync(path, new SaveProjectApplicationInput(input with { BudgetAmounts = new Dictionary<string, decimal?> { ["Standard"] = 999m } }, 0));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        var response = await client.PostAsJsonAsync(path, new SaveProjectApplicationInput(input, 0));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var draft = (await response.Content.ReadFromJsonAsync<ProjectApplication>())!;
        Assert.False(draft.Data.CanViewBudget);
        Assert.Null(draft.Data.Input.BudgetAmounts);
        var detail = (await client.GetFromJsonAsync<ProjectApplicationDetail>($"{path}/{draft.Id}"))!;
        Assert.Equal(2, detail.Application.Data.Input.SettlementLines!.Count);
        Assert.Null(detail.Application.Data.Input.SettlementAmount);
        var invalid = await client.PutAsJsonAsync($"{path}/{draft.Id}", new SaveProjectApplicationInput(input, draft.RowVersion, true));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var invalidJson = JsonDocument.Parse(await invalid.Content.ReadAsStringAsync());
        Assert.Contains("请补齐项目结算第1行", invalidJson.RootElement.GetProperty("detail").GetString());
        var fraction = await client.PutAsJsonAsync($"{path}/{draft.Id}", new SaveProjectApplicationInput(input with { SettlementLines = [new("设备款", Quantity: 1.5m, UnitPrice: 1)] }, draft.RowVersion));
        Assert.Equal(HttpStatusCode.BadRequest, fraction.StatusCode);
        using var fractionJson = JsonDocument.Parse(await fraction.Content.ReadAsStringAsync());
        Assert.Contains("整数", fractionJson.RootElement.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task OnlyAuthorizedApplicantCanCompleteContractAfterSpecialInitiation()
    {
        using var factory = new PdmApiFactory();
        using var client = factory.CreateClient();
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        var company = (await repository.GetProjectNumberingOptionsAsync(default)).Organizations.First().Id;
        await repository.CreateUserAsync(new(Guid.NewGuid(), "admin", "计划管理", "unused", UserRole.Administrator, true, CompanyId: company), default);
        foreach (var name in new[] { "contract-owner", "contract-other" })
            await repository.CreateUserAsync(new(Guid.NewGuid(), name, "销售助理", "unused", UserRole.ProductionViewer, true, RoleCode: "contract-test", CompanyId: company), default);
        var settings = await repository.GetAuthorizationSettingsAsync(default);
        var template = new AuthorizationTemplate("contract-test", "立项补合同测试", "", [new(PermissionCodes.ProjectApplicationSubmit, ["company"])]) { RoleCode = "contract-test" };
        settings = await repository.SaveAuthorizationSettingsAsync(settings with { Templates = settings.Templates.Append(template).ToArray() }, settings.Revision, default);
        var customer = (await repository.ListCustomersAsync(true, default)).First(x => x.IsActive && x.SourceSystem == "u9c");
        const string path = "/api/project-applications";
        Authenticate(client, "contract-owner", "ProductionViewer");
        var input = new ProjectApplicationInput(customer.Id, customer.Name, "特殊立项合同测试", null, new(2026, 12, 30), "contract-owner", ["contract-owner"], "提前准备", false);
        var normal = await client.PostAsJsonAsync(path, new SaveProjectApplicationInput(input, 0, true));
        Assert.Equal(HttpStatusCode.BadRequest, normal.StatusCode);
        Assert.Contains("请填写合同编号和合同明细", (await normal.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());
        var incomplete = new ProjectApplicationContract(" HT-01 ", "")
        {
            Groups = [new(new(" 第一组设备 ", "A", 1, "先准备"), [new("第一组配件", "A-1", 2, "")]),
                new(new("第二组设备", "B", 3, "合同附件B"), [new("第二组配件", "B-1", null, "待确认")])]
        };
        var draft = await client.PostAsJsonAsync(path, new SaveProjectApplicationInput(input with { Contract = incomplete }, 0));
        Assert.Equal(HttpStatusCode.OK, draft.StatusCode);
        var draftValue = (await draft.Content.ReadFromJsonAsync<ProjectApplication>())!;
        Assert.Equal(input.ProjectName, draftValue.Data.Input.ProjectName);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{path}/{draftValue.Id}", new SaveProjectApplicationInput(input with { Contract = incomplete }, draftValue.RowVersion, true))).StatusCode);
        var missingProjectName = await client.PutAsJsonAsync($"{path}/{draftValue.Id}", new SaveProjectApplicationInput(input with { IsSpecial = true, ProjectName = "", Contract = incomplete }, draftValue.RowVersion, true));
        Assert.Equal(HttpStatusCode.BadRequest, missingProjectName.StatusCode);
        Assert.Contains("项目名称", (await missingProjectName.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());
        var submitted = await client.PutAsJsonAsync($"{path}/{draftValue.Id}", new SaveProjectApplicationInput(input with { IsSpecial = true, Contract = incomplete }, draftValue.RowVersion, true));
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        var application = (await submitted.Content.ReadFromJsonAsync<ProjectApplication>())!;
        Assert.Equal(input.ProjectName, application.Data.Input.ProjectName);
        Assert.Equal("第一组设备", application.Data.Input.Contract!.Groups![0].Main.Name);
        var contractPath = $"{path}/{application.Id}/contract";
        var completedGroup = incomplete.Groups![1] with { Children = [new("第二组配件", "B-1", 4, "交付设备及技术文件")] };
        var completedContract = incomplete with { Number = "HT-01", Groups = [application.Data.Input.Contract.Groups![0], completedGroup] };
        var supplement = new SupplementProjectApplicationContractInput(application.RowVersion, completedContract);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync(contractPath, supplement)).StatusCode);

        Authenticate(client);
        var approval = new ProjectApplicationApprovalInput(application.RowVersion, customer.Id, "P", 2, 1, "0301", null, "同意提前启动",
            application.Data.Input with { ProjectName = "审批确认的立项名称" });
        var response = await client.PostAsJsonAsync($"{path}/{application.Id}/approve", approval);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var approved = (await response.Content.ReadFromJsonAsync<ProjectApplication>())!;
        Assert.Equal(ProjectApplicationService.PendingContract, approved.Status);
        Assert.Equal("HT-01", approved.Contract!.Number);
        Assert.NotNull(approved.ProjectId);
        var project = (await repository.FindProjectAsync(approved.ProjectId.Value, default))!;
        Assert.Equal("审批确认的立项名称", project.Name);
        Assert.Equal(1, project.Quantity);
        Assert.Single(await repository.ListProjectsAsync(default), item => item.SourceApplicationId == application.Id || item.RootProjectId == project.Id);
        var queryPath = $"{path}/projects/{approved.ProjectId}/contract";
        var pendingContract = (await client.GetFromJsonAsync<ProjectApplicationContractLookup>(queryPath))!;
        Assert.Equal(ProjectApplicationService.PendingContract, pendingContract.Status);
        Assert.Null(pendingContract.Contract!.Groups![1].Children[0].Quantity);
        var repeated = (await (await client.PostAsJsonAsync($"{path}/{application.Id}/approve", approval)).Content.ReadFromJsonAsync<ProjectApplication>())!;
        Assert.Equal(approved.ProjectId, repeated.ProjectId);
        supplement = supplement with { ExpectedRowVersion = approved.RowVersion };
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync(contractPath, supplement)).StatusCode);
        Authenticate(client, "contract-other", "ProductionViewer");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync(contractPath, supplement)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(queryPath)).StatusCode);

        Authenticate(client, "contract-owner", "ProductionViewer");
        var detailPath = $"{path}/{application.Id}";
        Assert.True((await client.GetFromJsonAsync<ProjectApplicationDetail>(detailPath))!.CanSupplementContract);
        var fractionalGroup = completedGroup with { Main = completedGroup.Main with { Quantity = 1.5m } };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(contractPath, supplement with { Contract = completedContract with { Groups = [completedContract.Groups![0], fractionalGroup] } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(contractPath, supplement with { Contract = incomplete })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync(contractPath, supplement with { ExpectedRowVersion = application.RowVersion })).StatusCode);
        var revoked = template with { Grants = [] };
        settings = await repository.SaveAuthorizationSettingsAsync(settings with { Templates = settings.Templates.Select(item => item.Key == template.Key ? revoked : item).ToArray() }, settings.Revision, default);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync(contractPath, supplement)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(queryPath)).StatusCode);
        await repository.SaveAuthorizationSettingsAsync(settings with { Templates = settings.Templates.Select(item => item.Key == template.Key ? template : item).ToArray() }, settings.Revision, default);

        var legacySupplement = JsonSerializer.SerializeToNode(supplement, JsonSerializerOptions.Web)!;
        legacySupplement["contract"]!["primaryGroupIndex"] = 9;
        var saved = await client.PutAsJsonAsync(contractPath, legacySupplement);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var detail = (await client.GetFromJsonAsync<ProjectApplicationDetail>(detailPath))!;
        Assert.Equal(ProjectApplicationService.Approved, detail.Application.Status);
        Assert.Equal(approved.ProjectId, detail.Application.ProjectId);
        Assert.Equal(project.Name, (await repository.FindProjectAsync(project.Id, default))!.Name);
        Assert.Equal("HT-01", detail.Application.Contract!.Number);
        Assert.Equal(completedGroup.Children[0], detail.Application.Contract.Groups![1].Children[0]);
        Assert.False(detail.CanSupplementContract);
        Assert.Equal("", detail.History[0].Data.Input.Contract!.Details);
        Assert.Equal(completedGroup.Children[0], detail.History.Last().SupplementedContract!.Groups![1].Children[0]);
        var lookup = (await client.GetFromJsonAsync<ProjectApplicationContractLookup>(queryPath))!;
        Assert.Equal(approved.ProjectId, lookup.ProjectId);
        Assert.Equal(application.Id, lookup.ApplicationId);
        Assert.Equal(ProjectApplicationService.Approved, lookup.Status);
        Assert.Equal(2, lookup.Contract!.Groups!.Count);
        Assert.Equal(completedGroup.Children[0], lookup.Contract.Groups[1].Children[0]);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync(contractPath, supplement)).StatusCode);
    }
}
