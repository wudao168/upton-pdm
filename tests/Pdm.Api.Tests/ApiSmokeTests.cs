using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api.Tests;

public sealed class ApiSmokeTests : IClassFixture<PdmApiFactory>
{
    private readonly HttpClient client;
    private readonly PdmApiFactory factory;

    public ApiSmokeTests(PdmApiFactory factory)
    {
        this.factory = factory;
        client = factory.CreateClient();
    }

    [Fact]
    public async Task Health_ReportsIsolatedPdmPorts()
    {
        var health = await client.GetFromJsonAsync<HealthResponse>("/health");

        Assert.NotNull(health);
        Assert.Equal("ok", health.Status);
        Assert.Equal(5080, health.ApiPort);
        Assert.Equal(3308, health.MySqlPort);
    }

    [Fact]
    public async Task ProjectApi_RequiresAuthentication()
    {
        var response = await client.GetAsync("/api/projects");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PersonalSettings_UpdateProfileAndChangePasswordLikeCrm()
    {
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        var passwords = factory.Services.GetRequiredService<IPasswordService>();
        var username = $"profile-{Guid.NewGuid():N}";
        await repository.CreateUserAsync(new UserAccount(Guid.NewGuid(), username, "个人设置测试", passwords.Hash("OldPassword1"), UserRole.Engineer, true), CancellationToken.None);
        var loginResponse = await client.PostAsJsonAsync("/api/auth/login", new { username, password = "OldPassword1" });
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        var login = await loginResponse.Content.ReadFromJsonAsync<LoginSessionResponse>();
        Assert.NotNull(login);
        Assert.False(string.IsNullOrWhiteSpace(login.ResumeToken));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);

        var profileResponse = await client.PutAsJsonAsync("/api/auth/profile", new
        {
            nickname = "设计昵称",
            gender = "female",
            landline = "0512-12345678",
            mobilePhone = "13800000000",
            email = "designer@example.com"
        });
        Assert.Equal(HttpStatusCode.OK, profileResponse.StatusCode);
        var profile = await profileResponse.Content.ReadFromJsonAsync<UserProfileResponse>();
        Assert.NotNull(profile);
        Assert.Equal("设计昵称", profile.Nickname);
        Assert.Equal("female", profile.Gender);
        Assert.Equal("designer@example.com", profile.Email);

        var invalidPassword = await client.PutAsJsonAsync("/api/auth/password", new { currentPassword = "wrong", password = "NewPassword2" });
        Assert.Equal(HttpStatusCode.BadRequest, invalidPassword.StatusCode);
        var changedPassword = await client.PutAsJsonAsync("/api/auth/password", new { currentPassword = "OldPassword1", password = "NewPassword2" });
        Assert.Equal(HttpStatusCode.OK, changedPassword.StatusCode);
        var changedLogin = await changedPassword.Content.ReadFromJsonAsync<LoginSessionResponse>();
        Assert.NotNull(changedLogin);
        Assert.True(passwords.Verify("NewPassword2", (await repository.FindUserAsync(username, CancellationToken.None))!.PasswordHash));

        client.DefaultRequestHeaders.Authorization = null;
        var rejectedResume = await client.PostAsJsonAsync("/api/auth/resume", new { resumeToken = login.ResumeToken });
        Assert.Equal(HttpStatusCode.Unauthorized, rejectedResume.StatusCode);
        var resumed = await client.PostAsJsonAsync("/api/auth/resume", new { resumeToken = changedLogin.ResumeToken });
        Assert.Equal(HttpStatusCode.OK, resumed.StatusCode);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", changedLogin.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task ForgotPassword_HidesAccountExistenceAndCreatesAdminResetTask()
    {
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        var passwords = factory.Services.GetRequiredService<IPasswordService>();
        var username = $"reset-{Guid.NewGuid():N}";
        await repository.CreateUserAsync(new UserAccount(Guid.NewGuid(), username, "重置测试用户", passwords.Hash("OldPassword1"), UserRole.Engineer, true), CancellationToken.None);

        var loginResponse = await client.PostAsJsonAsync("/api/auth/login", new { username, password = "OldPassword1" });
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        var oldLogin = await loginResponse.Content.ReadFromJsonAsync<LoginSessionResponse>();
        Assert.NotNull(oldLogin);

        client.DefaultRequestHeaders.Authorization = null;
        var unknown = await client.PostAsJsonAsync("/api/auth/password-reset-request", new { username = $"unknown-{Guid.NewGuid():N}", displayName = "未知用户" });
        var mismatch = await client.PostAsJsonAsync("/api/auth/password-reset-request", new { username, displayName = "姓名不匹配" });
        var matched = await client.PostAsJsonAsync("/api/auth/password-reset-request", new { username, displayName = "重置测试用户" });
        Assert.Equal(HttpStatusCode.OK, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.OK, mismatch.StatusCode);
        Assert.Equal(HttpStatusCode.OK, matched.StatusCode);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken("admin", "Administrator"));
        var tasks = await client.GetFromJsonAsync<IReadOnlyList<PasswordResetTaskResponse>>("/api/password-reset-requests");
        var task = Assert.Single(tasks!, item => item.Username == username);
        var reset = await client.PutAsJsonAsync($"/api/password-reset-requests/{task.Id}/reset", new { });
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.True(passwords.Verify("11111111", (await repository.FindUserAsync(username, CancellationToken.None))!.PasswordHash));
        Assert.DoesNotContain((await client.GetFromJsonAsync<IReadOnlyList<PasswordResetTaskResponse>>("/api/password-reset-requests"))!, item => item.Username == username);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", oldLogin.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task UserAdministration_CreatesUpdatesAndResetsUser()
    {
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        var passwords = factory.Services.GetRequiredService<IPasswordService>();
        var username = $"managed-{Guid.NewGuid():N}";
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken("admin", "Administrator"));

        var created = await client.PostAsJsonAsync("/api/users", new { username, displayName = "受管用户", password = "Initial123", role = nameof(UserRole.Engineer), isActive = true });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Contains((await client.GetFromJsonAsync<IReadOnlyList<ManagedUserResponse>>("/api/users"))!, user => user.Username == username && user.Role == "Engineer");

        var updated = await client.PutAsJsonAsync($"/api/users/{username}", new { displayName = "计划用户", role = nameof(UserRole.PlanningManager), isActive = true });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var account = await repository.FindUserAsync(username, CancellationToken.None);
        Assert.NotNull(account);
        Assert.Equal("计划用户", account.DisplayName);
        Assert.Equal(UserRole.PlanningManager, account.Role);

        var reset = await client.PutAsJsonAsync($"/api/users/{username}/reset-password", new { });
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.True(passwords.Verify("11111111", (await repository.FindUserAsync(username, CancellationToken.None))!.PasswordHash));
    }

    [Fact]
    public async Task EditSessionApi_RequiresMatchingSessionForHeartbeatAndRelease()
    {
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        var project = await repository.CreateProjectAsync(
            new CreateProjectCommand($"LOCK-{Guid.NewGuid():N}", "编辑会话接口验收", "admin", @"D:\PDM\Lock", @"D:\Release\Lock"),
            "admin",
            CancellationToken.None);
        var document = await repository.RegisterDocumentAsync(
            new RegisterDocumentCommand(project.Id, "LOCK-API", "编辑会话图档", "LOCK-API.SLDPRT", DocumentKind.Part),
            "admin",
            CancellationToken.None);
        var sessionId = Guid.NewGuid();
        var staleSessionId = Guid.NewGuid();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken("admin", "Administrator"));

        var checkout = await client.PostAsJsonAsync($"/api/documents/{document.Id}/checkout", new { sessionId, machineName = "API-TEST" });
        Assert.Equal(HttpStatusCode.OK, checkout.StatusCode);
        var heartbeat = await client.PostAsJsonAsync($"/api/edit-sessions/{sessionId}/heartbeat", new { machineName = "API-TEST", documentIds = new[] { document.Id } });
        Assert.Equal(HttpStatusCode.OK, heartbeat.StatusCode);
        using (var heartbeatJson = JsonDocument.Parse(await heartbeat.Content.ReadAsStringAsync()))
            Assert.Contains(document.Id, heartbeatJson.RootElement.GetProperty("activeDocumentIds").EnumerateArray().Select(item => item.GetGuid()));

        var secondCheckout = await client.PostAsJsonAsync($"/api/documents/{document.Id}/checkout", new { sessionId = staleSessionId, machineName = "API-TEST-2" });
        Assert.Equal(HttpStatusCode.Conflict, secondCheckout.StatusCode);
        var staleDiscard = await client.PostAsJsonAsync($"/api/documents/{document.Id}/discard-checkout", new { checkoutSessionId = staleSessionId });
        Assert.Equal(HttpStatusCode.Conflict, staleDiscard.StatusCode);
        var discard = await client.PostAsJsonAsync($"/api/documents/{document.Id}/discard-checkout", new { checkoutSessionId = sessionId });
        Assert.Equal(HttpStatusCode.OK, discard.StatusCode);
    }

    [Fact]
    public async Task EditSessionApi_ReclaimsLegacySessionWithoutMachine()
    {
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        var project = await repository.CreateProjectAsync(
            new CreateProjectCommand($"RECLAIM-{Guid.NewGuid():N}", "本机过期会话验收", "admin", @"D:\PDM\Reclaim", @"D:\Release\Reclaim"),
            "admin",
            CancellationToken.None);
        var document = await repository.RegisterDocumentAsync(
            new RegisterDocumentCommand(project.Id, "RECLAIM-API", "本机恢复图档", "RECLAIM-API.SLDPRT", DocumentKind.Part),
            "admin",
            CancellationToken.None);
        var expiredSessionId = Guid.NewGuid();
        var replacementSessionId = Guid.NewGuid();
        var otherMachineSessionId = Guid.NewGuid();
        await repository.CheckoutAsync(document.Id, "admin", expiredSessionId, string.Empty, DateTimeOffset.UtcNow.AddMinutes(15), CancellationToken.None);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken("admin", "Administrator"));

        var reclaim = await client.PostAsJsonAsync($"/api/documents/{document.Id}/checkout", new { sessionId = replacementSessionId, machineName = "api-test" });
        Assert.Equal(HttpStatusCode.OK, reclaim.StatusCode);
        using var reclaimJson = JsonDocument.Parse(await reclaim.Content.ReadAsStringAsync());
        Assert.Equal(replacementSessionId, reclaimJson.RootElement.GetProperty("checkoutSessionId").GetGuid());

        var otherMachine = await client.PostAsJsonAsync($"/api/documents/{document.Id}/checkout", new { sessionId = otherMachineSessionId, machineName = "API-TEST-2" });
        Assert.Equal(HttpStatusCode.Conflict, otherMachine.StatusCode);

        var oldHeartbeat = await client.PostAsJsonAsync($"/api/edit-sessions/{expiredSessionId}/heartbeat", new { machineName = "API-TEST", documentIds = new[] { document.Id } });
        Assert.Equal(HttpStatusCode.OK, oldHeartbeat.StatusCode);
        using var heartbeatJson = JsonDocument.Parse(await oldHeartbeat.Content.ReadAsStringAsync());
        Assert.DoesNotContain(document.Id, heartbeatJson.RootElement.GetProperty("activeDocumentIds").EnumerateArray().Select(item => item.GetGuid()));
    }

    [Fact]
    public async Task BomResolve_AcceptsStringTargetKindFromWebClient()
    {
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        var project = await repository.CreateProjectAsync(
            new CreateProjectCommand($"BOM-{Guid.NewGuid():N}", "BOM分类接口验收", "admin", @"D:\PDM\Bom", @"D:\Release\Bom"),
            "admin",
            CancellationToken.None);
        var item = new BomItem(Guid.NewGuid(), project.Id, BomKind.NonStandard, 1, "BOM-001", "待分类零件", 1, "件", "6061", null, "W1", false)
        {
            IsPendingClassification = true
        };
        await repository.ReplaceBomAsync(project.Id, BomKind.NonStandard, [item], CancellationToken.None);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken("admin", "Administrator"));

        using var content = new StringContent("""{"action":"classify","targetKind":"Standard"}""", Encoding.UTF8, "application/json");
        var response = await client.PostAsync($"/api/projects/{project.Id}/boms/items/{item.Id}/resolve", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var standard = await repository.GetBomAsync(project.Id, BomKind.Standard, CancellationToken.None);
        var resolved = Assert.Single(standard, candidate => candidate.Id == item.Id);
        Assert.False(resolved.IsPendingClassification);
        Assert.True(resolved.IsManuallyOverridden);
        Assert.Equal(BomKind.Standard, resolved.Kind);
    }

    [Fact]
    public async Task BomBatchUpdate_ValidatesAllRowsAndUpdatesSelectedPropertiesTogether()
    {
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        var project = await repository.CreateProjectAsync(
            new CreateProjectCommand($"BATCH-{Guid.NewGuid():N}", "BOM批量编辑验收", "admin", @"D:\PDM\Batch", @"D:\Release\Batch"),
            "admin",
            CancellationToken.None);
        var first = new BomItem(Guid.NewGuid(), project.Id, BomKind.NonStandard, 1, "BATCH-001", "批量零件一", 1, "件", "6061", null, "W1", false) { IsPendingClassification = true };
        var second = new BomItem(Guid.NewGuid(), project.Id, BomKind.NonStandard, 2, "BATCH-002", "批量零件二", 1, "件", "6061", null, "W1", false) { IsPendingClassification = true };
        await repository.ReplaceBomAsync(project.Id, BomKind.NonStandard, [first, second], CancellationToken.None);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken("admin", "Administrator"));

        using var content = new StringContent($$"""{"itemIds":["{{first.Id}}","{{second.Id}}"],"fields":["kind","brand"],"targetKind":"Standard","brand":"UPTON"}""", Encoding.UTF8, "application/json");
        var response = await client.PatchAsync($"/api/projects/{project.Id}/boms/items/batch", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var standard = await repository.GetBomAsync(project.Id, BomKind.Standard, CancellationToken.None);
        Assert.Equal(2, standard.Count);
        Assert.All(standard, item =>
        {
            Assert.Equal("UPTON", item.Brand);
            Assert.False(item.IsPendingClassification);
            Assert.Equal(BomKind.Standard, item.Kind);
        });

        using var invalidContent = new StringContent($$"""{"itemIds":["{{first.Id}}","{{Guid.NewGuid()}}"],"fields":["brand"],"brand":"不应保存"}""", Encoding.UTF8, "application/json");
        var invalid = await client.PatchAsync($"/api/projects/{project.Id}/boms/items/batch", invalidContent);
        Assert.Equal(HttpStatusCode.NotFound, invalid.StatusCode);
        var unchanged = await repository.GetBomAsync(project.Id, BomKind.Standard, CancellationToken.None);
        Assert.All(unchanged, item => Assert.Equal("UPTON", item.Brand));
    }

    [Fact]
    public async Task BomBatchDelete_PersistentlyExcludesDrawingRowsAndClassificationRestoresThem()
    {
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        var project = await repository.CreateProjectAsync(
            new CreateProjectCommand($"DELETE-{Guid.NewGuid():N}", "BOM批量删除验收", "admin", @"D:\PDM\Delete", @"D:\Release\Delete"),
            "admin",
            CancellationToken.None);
        var source = new BomItem(Guid.NewGuid(), project.Id, BomKind.NonStandard, 1, "DELETE-001", "图纸来源物料", 1, "件", "6061", null, "W1", true)
        {
            Source = "Auto",
            SourceDocumentId = Guid.NewGuid()
        };
        await repository.ReplaceBomAsync(project.Id, BomKind.NonStandard, [source], CancellationToken.None);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken("admin", "Administrator"));

        var deleted = await client.PostAsJsonAsync($"/api/projects/{project.Id}/boms/items/batch-delete", new { itemIds = new[] { source.Id }, reason = "接口回收站测试" });

        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.True(Assert.Single(await repository.GetBomAsync(project.Id, BomKind.NonStandard, CancellationToken.None)).IsManuallyExcluded);

        var restored = await client.PostAsJsonAsync($"/api/projects/{project.Id}/boms/items/batch-restore", new { itemIds = new[] { source.Id }, mode = "AsManual" });

        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        var restoredItem = Assert.Single(await repository.GetBomAsync(project.Id, BomKind.NonStandard, CancellationToken.None));
        Assert.False(restoredItem.IsManuallyExcluded);
        Assert.Null(restoredItem.SourceDocumentId);
    }

    [Fact]
    public async Task Administrator_CreatesNumberedHierarchyAndEngineerCannotDelete()
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken("admin", "Administrator"));

        var response = await client.PostAsJsonAsync("/api/projects", new
        {
            organizationId = "70000000-0000-0000-0000-000000000001",
            projectTypeCode = "P",
            equipmentTypeCode = 2,
            customerId = "c0046500-0000-0000-0000-000000000001",
            name = "项目创建接口验收",
            projectAlias = "验收别名",
            signedDate = "2026-08-13",
            quantity = 2
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<ProjectResponse>();
        Assert.NotNull(created);
        Assert.Matches("^P7[0-9]{5}$", created.Code);
        Assert.Matches("^AK-2-C00465-[0-9]{3}-00$", created.DeviceModel);
        Assert.Equal(2, created.SerialNumbers.Count);
        Assert.Equal(int.Parse(created.SerialNumbers[0]) + 1, int.Parse(created.SerialNumbers[1]));
        Assert.Equal($@"D:\PDM\Vault\{created.Code}", created.VaultLocation);

        var childResponse = await client.PostAsJsonAsync($"/api/projects/{created.Id}/children", new
        {
            name = "子项目一",
            projectAlias = "子项目别名",
            quantity = 2
        });
        Assert.Equal(HttpStatusCode.Created, childResponse.StatusCode);
        var child = await childResponse.Content.ReadFromJsonAsync<ProjectResponse>();
        Assert.NotNull(child);
        Assert.Equal($"{created.Code}-1", child.Code);
        Assert.Matches("^AK-2-C00465-[0-9]{3}-01$", child.DeviceModel);
        Assert.Equal(2, child.SerialNumbers.Count);

        var secondChildResponse = await client.PostAsJsonAsync($"/api/projects/{created.Id}/children", new { name = "子项目二", quantity = 1 });
        var secondChild = await secondChildResponse.Content.ReadFromJsonAsync<ProjectResponse>();
        Assert.Equal($"{created.Code}-2", secondChild!.Code);
        Assert.Matches("^AK-2-C00465-[0-9]{3}-02$", secondChild.DeviceModel);
        Assert.Single(secondChild.SerialNumbers);

        var registerOnParent = await client.PostAsJsonAsync($"/api/projects/{created.Id}/documents/register", new
        {
            drawingNumber = "MAIN-WITH-CHILD",
            name = "主项目图档",
            fileName = "MAIN-WITH-CHILD.SLDASM",
            kind = DocumentKind.Assembly,
            sourceSha256 = new string('A', 64)
        });
        Assert.Equal(HttpStatusCode.OK, registerOnParent.StatusCode);
        Guid modelDocumentId;
        using (var registered = JsonDocument.Parse(await registerOnParent.Content.ReadAsStringAsync()))
        {
            Assert.NotEqual(Guid.Empty, registered.RootElement.GetProperty("folderId").GetGuid());
            modelDocumentId = registered.RootElement.GetProperty("id").GetGuid();
        }

        var duplicatePreflight = await client.PostAsJsonAsync($"/api/projects/{created.Id}/documents/registration-preflight", new
        {
            candidates = new[]
            {
                new
                {
                    candidateKey = "same-main",
                    fileName = "MAIN-WITH-CHILD.SLDASM",
                    kind = DocumentKind.Assembly,
                    sourceSha256 = new string('A', 64)
                }
            }
        });
        Assert.Equal(HttpStatusCode.OK, duplicatePreflight.StatusCode);
        using (var preflight = JsonDocument.Parse(await duplicatePreflight.Content.ReadAsStringAsync()))
        {
            var match = Assert.Single(preflight.RootElement.EnumerateArray());
            Assert.Equal((int)DocumentRegistrationMatchKind.SameNameSameContent, match.GetProperty("matchKind").GetInt32());
            Assert.Equal(modelDocumentId, match.GetProperty("existingDocumentId").GetGuid());
        }

        var registerDrawing = await client.PostAsJsonAsync($"/api/projects/{created.Id}/documents/register", new
        {
            drawingNumber = "MAIN-WITH-CHILD",
            name = "主项目工程图",
            fileName = "MAIN-WITH-CHILD.SLDDRW",
            kind = DocumentKind.Drawing,
            relatedModelDocumentId = modelDocumentId,
            sourceSha256 = new string('B', 64)
        });
        Assert.Equal(HttpStatusCode.OK, registerDrawing.StatusCode);
        using var registeredDrawing = JsonDocument.Parse(await registerDrawing.Content.ReadAsStringAsync());
        var drawingDocumentId = registeredDrawing.RootElement.GetProperty("id").GetGuid();

        var relationResponse = await client.GetAsync($"/api/projects/{created.Id}/document-relations");
        Assert.Equal(HttpStatusCode.OK, relationResponse.StatusCode);
        using var relations = JsonDocument.Parse(await relationResponse.Content.ReadAsStringAsync());
        var relation = Assert.Single(relations.RootElement.EnumerateArray());
        Assert.Equal(modelDocumentId, relation.GetProperty("modelDocumentId").GetGuid());
        Assert.Equal(drawingDocumentId, relation.GetProperty("drawingDocumentId").GetGuid());

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken("engineer", "Engineer"));
        var engineerDelete = await client.DeleteAsync($"/api/projects/{created.Id}");
        Assert.Equal(HttpStatusCode.Forbidden, engineerDelete.StatusCode);
    }

    [Fact]
    public async Task Administrator_SynchronizesU9CustomersAndMaintainsRolePermissionsWhileManualCustomerEndpointIsRemoved()
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken("admin", "Administrator"));

        var manualCustomerResponse = await client.PostAsJsonAsync("/api/customers", new { code = "C00999", name = "手工客户", isActive = true });
        Assert.Equal(HttpStatusCode.MethodNotAllowed, manualCustomerResponse.StatusCode);

        var saveU9Settings = await client.PutAsJsonAsync("/api/u9-material-integration", new
        {
            baseUrl = "http://u9.example.test/U9",
            enterpriseCode = "01",
            organizationCode = "7",
            userCode = "pdm",
            clientId = "PDM",
            clientSecret = "test-secret",
            itemCreatePath = U9MaterialContract.CreatePath,
            itemQueryPath = U9MaterialContract.QueryPath,
            itemModifyPath = U9MaterialContract.ModifyPath,
            itemDeletePath = U9MaterialContract.DeletePath,
            writeEnabled = false
        });
        Assert.True(saveU9Settings.IsSuccessStatusCode, await saveU9Settings.Content.ReadAsStringAsync());

        var saveCrmSettings = await client.PutAsJsonAsync("/api/crm-integration", new
        {
            baseUrl = "",
            username = "",
            password = (string?)null,
            autoSyncEnabled = true,
            autoSyncIntervalMinutes = 30
        });
        Assert.True(saveCrmSettings.IsSuccessStatusCode, await saveCrmSettings.Content.ReadAsStringAsync());
        var crmSettings = await saveCrmSettings.Content.ReadFromJsonAsync<CrmIntegrationSettingsResponse>();
        Assert.NotNull(crmSettings);
        Assert.True(crmSettings.PasswordConfigured);
        Assert.Equal("http://u9.example.test/U9", crmSettings.BaseUrl);
        Assert.Equal("pdm", crmSettings.Username);
        Assert.True(crmSettings.AutoSyncEnabled);
        Assert.Equal(30, crmSettings.AutoSyncIntervalMinutes);
        using (var settingsJson = JsonDocument.Parse(await saveCrmSettings.Content.ReadAsStringAsync()))
            Assert.False(settingsJson.RootElement.TryGetProperty("password", out _));
        var disableAutomaticSync = await client.PutAsJsonAsync("/api/crm-integration", new
        {
            baseUrl = "",
            username = "",
            password = (string?)null,
            autoSyncEnabled = false,
            autoSyncIntervalMinutes = 30
        });
        Assert.True(disableAutomaticSync.IsSuccessStatusCode, await disableAutomaticSync.Content.ReadAsStringAsync());

        var testConnection = await client.PostAsync("/api/crm-integration/test", null);
        Assert.Equal(HttpStatusCode.OK, testConnection.StatusCode);
        var testResult = await testConnection.Content.ReadFromJsonAsync<CrmConnectionTestResponse>();
        Assert.Equal(3, testResult!.CustomerCount);
        Assert.Equal(0, testResult.SkippedCount);

        var syncResponse = await client.PostAsync("/api/crm-integration/sync", null);
        Assert.Equal(HttpStatusCode.OK, syncResponse.StatusCode);
        var syncResult = await syncResponse.Content.ReadFromJsonAsync<CrmCustomerSyncResponse>();
        Assert.Equal(3, syncResult!.CustomerCount);
        Assert.Equal(0, syncResult.SkippedCount);
        var customers = await client.GetFromJsonAsync<List<CustomerResponse>>("/api/customers");
        Assert.Contains(customers!, item => item.Code == "C00999" && item.Name == "U9C接口客户");
        Assert.NotNull(factory.Services.GetRequiredService<TestU9OpenApiClient>().LastRequest);

        var settings = await client.GetFromJsonAsync<SystemSettingsResponse>("/api/system-settings");
        Assert.Equal(@"D:\PDM\Vault", settings!.VaultRoot);
        var equipmentTypes = await client.GetFromJsonAsync<List<EquipmentTypeResponse>>("/api/system-settings/equipment-types");
        Assert.Equal(100, equipmentTypes!.Count);
        var equipmentResponse = await client.PutAsJsonAsync("/api/system-settings/equipment-types/99", new { name = "停用验收类型", isActive = false });
        Assert.Equal(HttpStatusCode.OK, equipmentResponse.StatusCode);
        var numberingOptions = await client.GetFromJsonAsync<NumberingOptionsResponse>("/api/project-numbering/options");
        Assert.DoesNotContain(numberingOptions!.EquipmentTypes, item => item.Code == 99);

        var directory = await client.GetFromJsonAsync<RolePermissionDirectoryResponse>("/api/role-permissions");
        Assert.Contains(directory!.Permissions, permission => permission.Code == PermissionCodes.ProjectDesignerAssign);
        var engineer = Assert.Single(directory.Roles, role => role.Role == nameof(UserRole.Engineer));
        var originalPermissions = engineer.Permissions;
        try
        {
            var response = await client.PutAsJsonAsync("/api/role-permissions/Engineer", new { permissions = new[] { PermissionCodes.DocumentEdit } });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var updated = await response.Content.ReadFromJsonAsync<RolePermissionDirectoryResponse>();
            var updatedEngineer = Assert.Single(updated!.Roles, role => role.Role == nameof(UserRole.Engineer));
            Assert.Contains(PermissionCodes.DocumentEdit, updatedEngineer.Permissions);
            Assert.Contains(PermissionCodes.ProjectContentView, updatedEngineer.Permissions);
            Assert.Contains(PermissionCodes.ProjectView, updatedEngineer.Permissions);
        }
        finally
        {
            await client.PutAsJsonAsync("/api/role-permissions/Engineer", new { permissions = originalPermissions });
        }

        var removedEndpoint = await client.PutAsJsonAsync("/api/projects/11111111-1111-1111-1111-111111111111/responsibles", new { usernames = new[] { "admin" } });
        Assert.False(removedEndpoint.IsSuccessStatusCode);
    }

    [Fact]
    public async Task Administrator_CreatesAndDeletesOnlyCustomRoles()
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken("admin", "Administrator"));
        var roleName = $"自定义角色-{Guid.NewGuid():N}";

        var createdResponse = await client.PostAsJsonAsync("/api/role-permissions", new
        {
            name = roleName,
            description = "复制工程师角色用于验证",
            sourceRoleCode = nameof(UserRole.Engineer)
        });

        Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
        var createdDirectory = await createdResponse.Content.ReadFromJsonAsync<RolePermissionDirectoryResponse>();
        var created = Assert.Single(createdDirectory!.Roles, role => role.Name == roleName);
        Assert.False(created.IsSystem);
        Assert.Equal(nameof(UserRole.Engineer), created.BaseRole);
        Assert.Contains(PermissionCodes.ProjectView, created.Permissions);

        var deletedResponse = await client.DeleteAsync($"/api/role-permissions/{Uri.EscapeDataString(created.Role)}");
        Assert.Equal(HttpStatusCode.OK, deletedResponse.StatusCode);
        var deletedDirectory = await deletedResponse.Content.ReadFromJsonAsync<RolePermissionDirectoryResponse>();
        Assert.DoesNotContain(deletedDirectory!.Roles, role => role.Role == created.Role);

        var protectedResponse = await client.DeleteAsync("/api/role-permissions/Administrator");
        Assert.False(protectedResponse.IsSuccessStatusCode);
        Assert.Contains((await client.GetFromJsonAsync<RolePermissionDirectoryResponse>("/api/role-permissions"))!.Roles,
            role => role.Role == nameof(UserRole.Administrator) && role.IsSystemAdministrator);
    }

    [Fact]
    public async Task Administrator_DeletesOnlyProjectsWithoutBusinessData()
    {
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        var project = await repository.CreateProjectAsync(
            new CreateProjectCommand($"DELETE-{Guid.NewGuid():N}", "管理员删除验收", "admin", @"D:\PDM\Delete", @"D:\Release\Delete"),
            "admin",
            CancellationToken.None);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken("admin", "Administrator"));

        var deleteResponse = await client.DeleteAsync($"/api/projects/{project.Id}");

        Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);
        Assert.Null(await repository.FindProjectAsync(project.Id, CancellationToken.None));
        var audit = await repository.ListAuditAsync("admin", UserRole.Administrator, 100, CancellationToken.None);
        Assert.Contains(audit, entry => entry.Action == "project.delete" && entry.EntityId == project.Id.ToString());

        var protectedResponse = await client.DeleteAsync("/api/projects/11111111-1111-1111-1111-111111111111");
        Assert.Equal(HttpStatusCode.Conflict, protectedResponse.StatusCode);
        Assert.Contains("受控图档", await protectedResponse.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Engineer_CannotReadAProjectOutsideOwnScope()
    {
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        var seedProjectId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        Assert.False(await repository.HasProjectReadAccessAsync(seedProjectId, "engineer", UserRole.Engineer, CancellationToken.None));
        Assert.False(await repository.HasProjectContentReadAccessAsync(seedProjectId, "engineer", UserRole.Engineer, CancellationToken.None));

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken("unassigned", "Engineer"));

        var response = await client.GetAsync("/api/projects/11111111-1111-1111-1111-111111111111");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var documentResponse = await client.GetAsync("/api/documents/22222222-2222-2222-2222-222222222222/versions");
        Assert.Equal(HttpStatusCode.Unauthorized, documentResponse.StatusCode);

        var versionsResponse = await client.GetAsync("/api/projects/11111111-1111-1111-1111-111111111111/versions");
        Assert.Equal(HttpStatusCode.Forbidden, versionsResponse.StatusCode);

        var auditResponse = await client.GetAsync("/api/projects/11111111-1111-1111-1111-111111111111/audit?take=20");
        Assert.Equal(HttpStatusCode.Forbidden, auditResponse.StatusCode);

        var counterResponse = await client.PutAsJsonAsync("/api/project-numbering/organizations/70000000-0000-0000-0000-000000000001/counters", new { currentProjectSequence = 2130, currentSerialSequence = 6071 });
        Assert.Equal(HttpStatusCode.Forbidden, counterResponse.StatusCode);
    }

    [Fact]
    public async Task ProjectWorkspaceFeeds_ReturnOnlyAccessibleProjectDataAndAssignedTasks()
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken("赵经理", "Administrator"));

        var taskResponse = await client.GetAsync("/api/approval-tasks/mine");
        Assert.Equal(HttpStatusCode.OK, taskResponse.StatusCode);
        var tasks = await taskResponse.Content.ReadFromJsonAsync<List<MyApprovalTaskResponse>>();
        var task = Assert.Single(tasks!);
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), task.ProjectId);
        Assert.Equal("PRJ-2026-018", task.ProjectCode);
        Assert.Equal("RP-2026-018-003", task.ReleasePackageNumber);
        Assert.Equal(ApprovalStage.Approval, task.Stage);

        var versionsResponse = await client.GetAsync("/api/projects/11111111-1111-1111-1111-111111111111/versions");
        Assert.Equal(HttpStatusCode.OK, versionsResponse.StatusCode);
        Assert.NotNull(await versionsResponse.Content.ReadFromJsonAsync<List<ProjectVersionResponse>>());

        var auditResponse = await client.GetAsync("/api/projects/11111111-1111-1111-1111-111111111111/audit?take=20");
        Assert.Equal(HttpStatusCode.OK, auditResponse.StatusCode);
        Assert.NotNull(await auditResponse.Content.ReadFromJsonAsync<List<AuditResponse>>());
    }

    [Fact]
    public async Task OrganizationAndStaffing_EnforcesDivisionAssignmentAndCrossDivisionChildScope()
    {
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        foreach (var user in new[]
        {
            new UserAccount(Guid.NewGuid(), "plan-user", "计划管理", "unused", UserRole.PlanningManager, true),
            new UserAccount(Guid.NewGuid(), "division-manager", "事业部负责人", "unused", UserRole.Engineer, true),
            new UserAccount(Guid.NewGuid(), "project-manager", "项目经理", "unused", UserRole.Engineer, true),
            new UserAccount(Guid.NewGuid(), "design-lead", "设计负责人", "unused", UserRole.Engineer, true),
            new UserAccount(Guid.NewGuid(), "designer-own", "本事业部设计", "unused", UserRole.Engineer, true),
            new UserAccount(Guid.NewGuid(), "designer-other", "跨事业部设计", "unused", UserRole.Engineer, true)
        })
        {
            if (await repository.FindUserAsync(user.Username, CancellationToken.None) is null)
                await repository.CreateUserAsync(user, CancellationToken.None);
        }

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken("admin", "Administrator"));
        if (await repository.FindUserAsync("admin", CancellationToken.None) is null)
            await repository.CreateUserAsync(new UserAccount(Guid.NewGuid(), "admin", "系统管理员", "unused", UserRole.Administrator, true), CancellationToken.None);

        var division = await CreateUnitAsync("DIV-A-" + Guid.NewGuid().ToString("N")[..6], "自动化事业部");
        var otherDivision = await CreateUnitAsync("DIV-B-" + Guid.NewGuid().ToString("N")[..6], "机器人事业部");
        foreach (var username in new[] { "plan-user", "division-manager", "project-manager", "design-lead", "designer-own" })
            await PutMembershipAsync(username, division.Id);
        await PutMembershipAsync("designer-other", otherDivision.Id);

        var managersResponse = await client.PutAsJsonAsync($"/api/organization-units/{division.Id}/managers", new { primaryManager = "division-manager", collaborativeManagers = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.OK, managersResponse.StatusCode);

        var projectResponse = await client.PostAsJsonAsync("/api/projects", new
        {
            organizationId = "70000000-0000-0000-0000-000000000001",
            projectTypeCode = "P", equipmentTypeCode = 2,
            customerId = "c0046500-0000-0000-0000-000000000001",
            name = "组织权限验收项目", signedDate = "2026-08-14", quantity = 1
        });
        var project = await projectResponse.Content.ReadFromJsonAsync<Project>();
        Assert.Equal(HttpStatusCode.Created, projectResponse.StatusCode);
        Assert.NotNull(project);
        var childResponse = await client.PostAsJsonAsync($"/api/projects/{project.Id}/children", new { name = "分配子项目", quantity = 1 });
        var child = await childResponse.Content.ReadFromJsonAsync<Project>();
        var siblingResponse = await client.PostAsJsonAsync($"/api/projects/{project.Id}/children", new { name = "未分配兄弟子项目", quantity = 1 });
        var sibling = await siblingResponse.Content.ReadFromJsonAsync<Project>();
        Assert.NotNull(child);
        Assert.NotNull(sibling);

        var administratorProjects = await client.GetFromJsonAsync<List<ProjectResponse>>("/api/projects");
        var administratorMain = Assert.Single(administratorProjects!, item => item.Id == project.Id);
        Assert.True(administratorMain.CanAssignExecutionUnit);
        Assert.False(administratorMain.CanManageMainStaffing);

        var administratorExecutionResponse = await client.PutAsJsonAsync($"/api/projects/{project.Id}/execution-unit", new { executionUnitId = division.Id });
        Assert.Equal(HttpStatusCode.OK, administratorExecutionResponse.StatusCode);
        administratorProjects = await client.GetFromJsonAsync<List<ProjectResponse>>("/api/projects");
        administratorMain = Assert.Single(administratorProjects!, item => item.Id == project.Id);
        Assert.True(administratorMain.CanManageMainStaffing);

        var administratorStaffingResponse = await client.PutAsJsonAsync($"/api/projects/{project.Id}/staffing", new
        {
            primaryProjectManager = "project-manager", collaborativeProjectManagers = Array.Empty<string>(), designLead = "design-lead"
        });
        Assert.Equal(HttpStatusCode.OK, administratorStaffingResponse.StatusCode);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken("plan-user", "PlanningManager"));
        var executionResponse = await client.PutAsJsonAsync($"/api/projects/{project.Id}/execution-unit", new { executionUnitId = division.Id });
        Assert.Equal(HttpStatusCode.OK, executionResponse.StatusCode);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken("division-manager", "Engineer"));
        var staffingResponse = await client.PutAsJsonAsync($"/api/projects/{project.Id}/staffing", new
        {
            primaryProjectManager = "project-manager", collaborativeProjectManagers = Array.Empty<string>(), designLead = "design-lead"
        });
        Assert.Equal(HttpStatusCode.OK, staffingResponse.StatusCode);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken("design-lead", "Engineer"));
        var designersResponse = await client.PutAsJsonAsync($"/api/projects/{child.Id}/designers", new { designers = new[] { "designer-own", "designer-other" } });
        Assert.Equal(HttpStatusCode.OK, designersResponse.StatusCode);
        Assert.False(await repository.HasProjectContentReadAccessAsync(project.Id, "designer-other", UserRole.Engineer, CancellationToken.None));
        Assert.True(await repository.HasProjectContentReadAccessAsync(child.Id, "designer-other", UserRole.Engineer, CancellationToken.None));
        Assert.False(await repository.HasProjectContentReadAccessAsync(sibling.Id, "designer-other", UserRole.Engineer, CancellationToken.None));
        Assert.True(await repository.HasProjectReadAccessAsync(project.Id, "project-manager", UserRole.Engineer, CancellationToken.None));
        Assert.False(await repository.HasProjectContentReadAccessAsync(project.Id, "project-manager", UserRole.Engineer, CancellationToken.None));

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken("designer-other", "Engineer"));
        var visible = await client.GetFromJsonAsync<List<ProjectResponse>>("/api/projects");
        Assert.NotNull(visible);
        Assert.Contains(visible, item => item.Id == project.Id);
        Assert.Contains(visible, item => item.Id == child.Id);
        Assert.DoesNotContain(visible, item => item.Id == sibling.Id);
        var visibleMain = Assert.Single(visible, item => item.Id == project.Id);
        Assert.False(visibleMain.CanReadContent);
        Assert.Null(visibleMain.DocumentCount);
        Assert.Null(visibleMain.BusinessStatus);
        var visibleChild = Assert.Single(visible, item => item.Id == child.Id);
        Assert.True(visibleChild.CanReadContent);
        Assert.Equal(0, visibleChild.DocumentCount);
        Assert.Equal("正常", visibleChild.BusinessStatus);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken("design-lead", "Engineer"));
        var directory = await client.GetFromJsonAsync<OrganizationDirectoryResponse>("/api/organization-directory");
        Assert.Contains(directory!.Units, item => item.Id == division.Id);

        async Task<OrganizationUnit> CreateUnitAsync(string code, string name)
        {
            var response = await client.PostAsJsonAsync("/api/organization-units", new
            {
                organizationId = "70000000-0000-0000-0000-000000000001", parentUnitId = (Guid?)null,
                code, name, kind = OrganizationUnitKind.BusinessDivision, isActive = true, sortOrder = 0
            });
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<OrganizationUnit>())!;
        }

        async Task PutMembershipAsync(string username, Guid unitId)
        {
            var response = await client.PutAsJsonAsync($"/api/organization-users/{username}/memberships", new { unitIds = new[] { unitId }, primaryUnitId = unitId });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    private static string CreateToken(string username, string role)
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")),
            SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            "upton-pdm",
            "upton-pdm-clients",
            [new Claim(ClaimTypes.Name, username), new Claim(ClaimTypes.Role, role)],
            expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private sealed record HealthResponse(string Status, string Service, string Database, int ApiPort, int MySqlPort);
    private sealed record ProjectResponse(Guid Id, string Code, string Name, string Owner, string VaultLocation, string ReleaseLocation, bool IsActive,
        string? DeviceModel, IReadOnlyList<string> SerialNumbers, IReadOnlyList<string> ResponsibleUsers, bool CanReadContent = false,
        int? DocumentCount = null, string? BusinessStatus = null, bool CanAssignExecutionUnit = false, bool CanManageMainStaffing = false);
    private sealed record CustomerResponse(Guid Id, string Code, string Name, bool IsActive);
    private sealed record CrmIntegrationSettingsResponse(string BaseUrl, string Username, bool PasswordConfigured, bool AutoSyncEnabled, int AutoSyncIntervalMinutes, DateTimeOffset? LastSyncAt, int LastSyncCount, DateTimeOffset? LastAutoSyncAttemptAt, string? LastAutoSyncError);
    private sealed record CrmConnectionTestResponse(int CustomerCount, int SkippedCount, DateTimeOffset TestedAt);
    private sealed record CrmCustomerSyncResponse(int CustomerCount, int SkippedCount, DateTimeOffset SyncedAt, CrmIntegrationSettingsResponse Settings, IReadOnlyList<CustomerResponse> Customers);
    private sealed record SystemSettingsResponse(string VaultRoot, string ReleaseRoot);
    private sealed record EquipmentTypeResponse(int Code, string Name, bool IsActive);
    private sealed record NumberingOptionsResponse(IReadOnlyList<EquipmentTypeResponse> EquipmentTypes);
    private sealed record OrganizationDirectoryResponse(IReadOnlyList<OrganizationUnitResponse> Units);
    private sealed record UserProfileResponse(string Username, string DisplayName, string? Nickname, string Gender, string? Landline, string? MobilePhone, string? Email);
    private sealed record PasswordResetTaskResponse(Guid Id, string Username, string DisplayName, DateTimeOffset RequestedAt);
    private sealed record ManagedUserResponse(string Username, string DisplayName, string Role, bool IsActive);
    private sealed record LoginSessionResponse(string AccessToken, DateTimeOffset ExpiresAt, string ResumeToken, string Username, string DisplayName, string Role, IReadOnlyList<string> Permissions);
    private sealed record OrganizationUnitResponse(Guid Id, Guid OrganizationId, Guid? ParentUnitId, string Code, string Name, string Kind, bool IsActive, int SortOrder);
    private sealed record RolePermissionDirectoryResponse(IReadOnlyList<PermissionDefinitionResponse> Permissions, IReadOnlyList<RolePermissionSettingsResponse> Roles);
    private sealed record PermissionDefinitionResponse(string Code, string Name, string Module, string? Description, bool Sensitive);
    private sealed record RolePermissionSettingsResponse(string Role, string Name, string Description, string BaseRole, bool IsSystem, bool IsSystemAdministrator, IReadOnlyList<string> Permissions, int UserCount);
    private sealed record MyApprovalTaskResponse(Guid Id, Guid ProjectId, string ProjectCode, string ProjectName, Guid ReleasePackageId, string ReleasePackageNumber, ApprovalStage Stage, ReleasePackageState PackageState, DateTimeOffset CreatedAt);
    private sealed record ProjectVersionResponse(Guid Id, Guid DocumentId, string DrawingNumber, string DocumentName, string FileName, RevisionLabel Revision, DocumentVersionStatus Status, string CreatedBy, DateTimeOffset CreatedAt, string ChangeNote);
    private sealed record AuditResponse(Guid Id, DateTimeOffset OccurredAt, string Actor, string Action, string EntityType, Guid EntityId, string Detail);
}

public sealed class PdmApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<ICrmCustomerClient>();
            services.RemoveAll<ICrmCredentialProtector>();
            services.RemoveAll<IU9OpenApiClient>();
            services.RemoveAll<IU9SecretProtector>();
            services.RemoveAll<IPersistentSessionTokenService>();
            services.AddSingleton<TestCrmCustomerClient>();
            services.AddSingleton<ICrmCustomerClient>(provider => provider.GetRequiredService<TestCrmCustomerClient>());
            services.AddSingleton<ICrmCredentialProtector, TestCrmCredentialProtector>();
            services.AddSingleton<TestU9OpenApiClient>();
            services.AddSingleton<IU9OpenApiClient>(provider => provider.GetRequiredService<TestU9OpenApiClient>());
            services.AddSingleton<IU9SecretProtector, TestU9SecretProtector>();
            services.AddSingleton<IPersistentSessionTokenService, TestPersistentSessionTokenService>();
        });
    }
}

public sealed class TestPersistentSessionTokenService : IPersistentSessionTokenService
{
    private readonly Dictionary<string, PersistentSessionTicket> tickets = [];

    public string Issue(UserAccount account)
    {
        var token = Guid.NewGuid().ToString("N");
        tickets[token] = new PersistentSessionTicket(account.Username, account.TokenVersion);
        return token;
    }

    public bool TryRead(string token, out PersistentSessionTicket ticket) => tickets.TryGetValue(token, out ticket!);
}

public sealed class TestCrmCustomerClient : ICrmCustomerClient
{
    public string LastUsername { get; private set; } = string.Empty;
    public string LastPassword { get; private set; } = string.Empty;

    public Task<CrmCustomerBatch> ListCustomersAsync(string baseUrl, string username, string password, CancellationToken cancellationToken)
    {
        LastUsername = username;
        LastPassword = password;
        return Task.FromResult(new CrmCustomerBatch([
            new("C00465", "中山比亚迪电子有限公司"),
            new("C00999", "CRM接口客户"),
            new("C01000", "CRM范围客户")
        ], 0));
    }
}

public sealed class TestCrmCredentialProtector : ICrmCredentialProtector
{
    public string Protect(string password) => "protected:" + password;

    public string Unprotect(string ciphertext) => ciphertext["protected:".Length..];
}

public sealed class TestU9OpenApiClient : IU9OpenApiClient
{
    public U9AuthenticationRequest? LastRequest { get; private set; }
    public U9ItemQueryResult QueryResult { get; set; } = new(0, null, []);
    public Queue<U9ItemQueryResult> QueryResults { get; } = new();
    public U9UomQueryResult UomResult { get; set; } = new(0, null, [new("uom-001", "001")]);
    public U9BusinessBatchResult BusinessResult { get; set; } = new(0, null, []);
    public U9CustomerQueryResult CustomerResult { get; set; } = new(0, null, [
        new("C00465", "中山比亚迪电子有限公司"),
        new("C00999", "U9C接口客户"),
        new("C01000", "U9C范围客户")
    ], 3);
    public int QueryCallCount { get; private set; }
    public int PostCallCount { get; private set; }
    public string LastPostPath { get; private set; } = string.Empty;
    public string LastPostPayload { get; private set; } = string.Empty;

    public Task<U9AuthenticationResult> AuthenticateAsync(U9AuthenticationRequest request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        return Task.FromResult(new U9AuthenticationResult("test-token"));
    }

    public Task<U9BusinessBatchResult> PostBatchAsync(string baseUrl, string path, string token, string payloadJson, CancellationToken cancellationToken)
    {
        PostCallCount++;
        LastPostPath = path;
        LastPostPayload = payloadJson;
        return Task.FromResult(BusinessResult);
    }

    public Task<U9ItemQueryResult> QueryItemsAsync(string baseUrl, string path, string token, string payloadJson, CancellationToken cancellationToken)
    {
        QueryCallCount++;
        return Task.FromResult(QueryResults.Count > 0 ? QueryResults.Dequeue() : QueryResult);
    }

    public Task<U9UomQueryResult> QueryUomsAsync(string baseUrl, string token, string payloadJson, CancellationToken cancellationToken) =>
        Task.FromResult(UomResult);

    public Task<U9CustomerQueryResult> QueryCustomerReferencesAsync(
        string baseUrl, string token, string payloadJson, CancellationToken cancellationToken) =>
        Task.FromResult(CustomerResult);
}

public sealed class TestU9SecretProtector : IU9SecretProtector
{
    public string Protect(string secret) => "protected:" + secret;
    public string Unprotect(string ciphertext) => ciphertext["protected:".Length..];
}
