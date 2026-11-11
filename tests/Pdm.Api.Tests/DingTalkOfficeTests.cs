using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api.Tests;

public sealed class DingTalkOfficeTests
{
    private sealed class Handler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath == "/v1.0/oauth2/accessToken")
                return new(HttpStatusCode.OK) { Content = new StringContent("{\"accessToken\":\"private-token\"}") };
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var code = body.RootElement.GetProperty("code").GetString();
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { errcode = code == "invalid" ? 40029 : 0,
                result = new { userid = code == "unbound" ? "unbound-id" : "employee-id", name = "与UPLM姓名不同的钉钉姓名" } }) };
        }
    }

    private sealed class PairingClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static string AdminToken(string username = "admin", string role = "Administrator") => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("upton-pdm", "upton-pdm-clients",
        [new(ClaimTypes.Name, username), new(ClaimTypes.Role, role)], expires: DateTime.UtcNow.AddMinutes(10),
        signingCredentials: new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256)));

    [Fact]
    public async Task EmployeeLoginUsesVerifiedBindingAndNormalCompanyPermissionsAndSessions()
    {
        using var baseFactory = new PdmApiFactory();
        using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddHttpClient<DingTalkOplIntegration>().ConfigurePrimaryHttpMessageHandler(() => new Handler())));
        using var admin = factory.CreateClient();
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        var companies = (await repository.GetProjectNumberingOptionsAsync(default)).Organizations.Where(item => item.IsActive).ToArray();
        var company = companies[0].Id;
        await repository.CreateUserAsync(new(Guid.NewGuid(), "admin", "管理员", "unused", UserRole.Administrator, true), default);
        await repository.SetUserCompanyScopeAsync("admin", company, false, [], "admin", default);
        await repository.CreateUserAsync(new(Guid.NewGuid(), "mobile-employee", "员工张工", "unused", UserRole.Engineer, true), default);
        await repository.SetUserCompanyScopeAsync("mobile-employee", company, false, [], "admin", default);
        admin.DefaultRequestHeaders.Authorization = new("Bearer", AdminToken());
        admin.DefaultRequestHeaders.Add("X-Company-Id", company.ToString());
        var config = await admin.PutAsJsonAsync("/api/settings/dingtalk", new { enabled = true, corpId = "office-corp", appKey = "office-key", appSecret = "private-secret", publicUrl = "http://192.168.2.8:5080", expectedRowVersion = 0 });
        Assert.Equal(HttpStatusCode.OK, config.StatusCode);
        var settings = await config.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal($"http://192.168.2.8:5080/?dingtalkOffice=1&companyId={company}", settings.GetProperty("officeEntryUrl").GetString());
        var input = new SaveDingTalkEmployeeBindings(1, [new("office-corp", "employee-id", "mobile-employee")]);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/settings/dingtalk/employees", input)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync("/api/settings/dingtalk/employees", input)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/settings/dingtalk/employees", new SaveDingTalkEmployeeBindings(2, [new("other-corp", "employee-id", "mobile-employee")]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/settings/dingtalk/employees", new SaveDingTalkEmployeeBindings(2, [new("office-corp", "employee-id", "mobile-employee"), new("office-corp", "second-id", "mobile-employee")]))).StatusCode);

        using var employee = factory.CreateClient();
        var bootstrap = await employee.GetStringAsync($"/api/integrations/dingtalk/office/bootstrap?companyId={company}");
        Assert.DoesNotContain("private-secret", bootstrap); Assert.DoesNotContain("employee-id", bootstrap);
        Assert.Equal(HttpStatusCode.Unauthorized, (await employee.GetAsync("/api/work-hours?from=2026-10-30&to=2026-10-30&report=false")).StatusCode);
        var unbound = await employee.PostAsJsonAsync("/api/integrations/dingtalk/office/login", new DingTalkOfficeLogin(company, "unbound"));
        Assert.Equal(HttpStatusCode.Forbidden, unbound.StatusCode); Assert.Contains("扫码", await unbound.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await employee.PostAsJsonAsync("/api/integrations/dingtalk/office/login", new DingTalkOfficeLogin(company, "invalid"))).StatusCode);
        var response = await employee.PostAsJsonAsync("/api/integrations/dingtalk/office/login", new DingTalkOfficeLogin(company, "valid", Guid.NewGuid().ToString()));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var session = (await response.Content.ReadFromJsonAsync<LoginResponse>())!;
        Assert.Equal("mobile-employee", session.Username); Assert.Equal("员工张工", session.DisplayName); Assert.Equal(company, session.ActiveCompanyId);
        Assert.Equal((await repository.GetUserPermissionsAsync(session.Username, UserRole.Engineer, default)).Order(), session.Permissions.Order());
        employee.DefaultRequestHeaders.Authorization = new("Bearer", session.AccessToken);
        employee.DefaultRequestHeaders.Add("X-Company-Id", company.ToString());
        Assert.Equal(HttpStatusCode.OK, (await employee.GetAsync("/api/projects")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await employee.GetAsync("/api/work-hours?from=2026-10-30&to=2026-10-30&report=false")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await employee.GetAsync("/api/settings/dingtalk/employees")).StatusCode);
        if (companies.Length > 1)
        {
            employee.DefaultRequestHeaders.Remove("X-Company-Id");
            employee.DefaultRequestHeaders.Add("X-Company-Id", companies[1].Id.ToString());
            Assert.Equal(HttpStatusCode.Unauthorized, (await employee.GetAsync("/api/projects")).StatusCode);
            employee.DefaultRequestHeaders.Remove("X-Company-Id");
            employee.DefaultRequestHeaders.Add("X-Company-Id", company.ToString());
        }
        Assert.Equal(HttpStatusCode.OK, (await employee.PostAsync("/api/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await employee.GetAsync("/api/projects")).StatusCode);

        var security = factory.Services.GetRequiredService<LoginSecurityService>();
        await security.SavePolicyAsync(new(true, false), default);
        using var first = factory.CreateClient(); using var second = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await first.PostAsJsonAsync("/api/integrations/dingtalk/office/login", new DingTalkOfficeLogin(company, "valid", Guid.NewGuid().ToString()))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await second.PostAsJsonAsync("/api/integrations/dingtalk/office/login", new DingTalkOfficeLogin(company, "valid", Guid.NewGuid().ToString()))).StatusCode);
        await security.SavePolicyAsync(new(false, false), default);

        // 更换企业后旧企业相同用户ID不能继承账号绑定。
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/settings/dingtalk", new { enabled = true, corpId = "new-corp", appKey = "new-key", publicUrl = "http://192.168.2.8:5080", expectedRowVersion = 2 })).StatusCode);
        using var changedCorp = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Forbidden, (await changedCorp.PostAsJsonAsync("/api/integrations/dingtalk/office/login", new DingTalkOfficeLogin(company, "valid"))).StatusCode);
    }

    [Fact]
    public async Task SelfBindingNeedsScopedPermissionDingTalkProofAndOwnerConfirmation()
    {
        using var baseFactory = new PdmApiFactory();
        using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddHttpClient<DingTalkOplIntegration>().ConfigurePrimaryHttpMessageHandler(() => new Handler())));
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        var companies = (await repository.GetProjectNumberingOptionsAsync(default)).Organizations.Where(item => item.IsActive).ToArray();
        var company = companies[0].Id;
        foreach (var (username, role) in new[] { ("admin", UserRole.Administrator), ("employee", UserRole.Engineer), ("other", UserRole.Engineer) })
        {
            await repository.CreateUserAsync(new(Guid.NewGuid(), username, "测试姓名", "unused", role, true), default);
            await repository.SetUserCompanyScopeAsync(username, company, false, [], "admin", default);
        }
        HttpClient Client(string username, UserRole role)
        {
            var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new("Bearer", AdminToken(username, role.ToString()));
            client.DefaultRequestHeaders.Add("X-Company-Id", company.ToString());
            return client;
        }
        using var admin = Client("admin", UserRole.Administrator);
        using var employee = Client("employee", UserRole.Engineer);
        using var other = Client("other", UserRole.Engineer);
        using var mobile = factory.CreateClient();
        const string path = "/api/me/dingtalk-binding";
        const string proof = "/api/integrations/dingtalk/office/binding-proof";
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/settings/dingtalk", new { enabled = true,
            corpId = "office-corp", appKey = "office-key", appSecret = "private-secret", publicUrl = "http://192.168.2.8:5173", expectedRowVersion = 0 })).StatusCode);
        Assert.Contains((await repository.GetRolePermissionDirectoryAsync(default)).Permissions, p => p.Code == PermissionCodes.DingTalkSelfBind && p.Sensitive);
        Assert.Equal(HttpStatusCode.Unauthorized, (await mobile.PostAsync(path, null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await employee.GetAsync(path)).StatusCode);
        var oldGrants = (await repository.GetRolePermissionsAsync(UserRole.Engineer, default)).Where(code => code != PermissionCodes.DingTalkSelfBind).ToArray();
        Task Grant(bool enabled) => repository.SetRolePermissionsAsync("Engineer", enabled ? [.. oldGrants, PermissionCodes.DingTalkSelfBind] : oldGrants, default);
        async Task<string> Issue(HttpClient client)
        {
            var result = await client.PostAsync(path, null);
            Assert.Equal(HttpStatusCode.OK, result.StatusCode);
            return (await result.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;
        }
        var code = await Issue(employee);
        Assert.Equal(HttpStatusCode.BadRequest, (await employee.PostAsJsonAsync(path + "/confirm", new { code })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await mobile.PostAsJsonAsync(proof, new DingTalkPairingProof(Guid.NewGuid(), code, "valid"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await mobile.PostAsJsonAsync(proof, new DingTalkPairingProof(company, code, "invalid"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await mobile.PostAsJsonAsync(proof, new DingTalkPairingProof(company, code, "valid"))).StatusCode);
        Assert.Contains("钉钉姓名", await employee.GetStringAsync(path));
        Assert.Equal(HttpStatusCode.BadRequest, (await other.PostAsJsonAsync(path + "/confirm", new { code })).StatusCode);
        await Grant(false);
        Assert.Equal(HttpStatusCode.Forbidden, (await employee.PostAsync(path, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await employee.PostAsJsonAsync(path + "/confirm", new { code })).StatusCode);
        await Grant(true);
        Assert.Equal(HttpStatusCode.NoContent, (await employee.PostAsJsonAsync(path + "/confirm", new { code })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await employee.PostAsJsonAsync(path + "/confirm", new { code })).StatusCode);
        var conflicting = await Issue(other);
        Assert.Equal(HttpStatusCode.NoContent, (await mobile.PostAsJsonAsync(proof, new DingTalkPairingProof(company, conflicting, "valid"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await other.PostAsJsonAsync(path + "/confirm", new { code = conflicting })).StatusCode);
        var session = await mobile.PostAsJsonAsync("/api/integrations/dingtalk/office/login", new DingTalkOfficeLogin(company, "valid"));
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        Assert.Equal("employee", (await session.Content.ReadFromJsonAsync<LoginResponse>())!.Username);
    }

    [Fact]
    public async Task QrBindingVerifiesTheScannedCompanyAndRequiresAnAuthorizedOwnerToFinish()
    {
        var clock = new PairingClock();
        using var baseFactory = new PdmApiFactory();
        using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.AddSingleton(new DingTalkEmployeePairing(clock));
            services.AddHttpClient<DingTalkOplIntegration>().ConfigurePrimaryHttpMessageHandler(() => new Handler());
        }));
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        var company = (await repository.GetProjectNumberingOptionsAsync(default)).Organizations.First(item => item.IsActive).Id;
        HttpClient Client(string username, string role)
        {
            var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new("Bearer", AdminToken(username, role));
            client.DefaultRequestHeaders.Add("X-Company-Id", company.ToString());
            return client;
        }
        foreach (var (username, name, role) in new[] { ("admin", "管理员", UserRole.Administrator), ("qr-owner", "扫码员工", UserRole.Engineer), ("qr-other", "其他员工", UserRole.Engineer) })
        {
            await repository.CreateUserAsync(new(Guid.NewGuid(), username, name, "unused", role, true), default);
            await repository.SetUserCompanyScopeAsync(username, company, false, [], "admin", default);
        }
        using var admin = Client("admin", "Administrator");
        using var owner = Client("qr-owner", "Engineer");
        using var other = Client("qr-other", "Engineer");
        using var phone = factory.CreateClient();
        const string self = "/api/me/dingtalk-binding";
        const string scan = "/api/integrations/dingtalk/office/binding-scan";
        const string proof = "/api/integrations/dingtalk/office/binding-proof";
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/settings/dingtalk", new { enabled = true,
            corpId = "office-corp", appKey = "office-key", appSecret = "private-secret", publicUrl = "http://192.168.2.8:5173", expectedRowVersion = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.PostAsync(self + "?qr=true", null)).StatusCode);

        async Task<string> Issue(HttpClient client)
        {
            var response = await client.PostAsync(self + "?qr=true", null);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var ticket = await response.Content.ReadFromJsonAsync<JsonElement>();
            var code = ticket.GetProperty("code").GetString()!;
            Assert.Matches("^[0-9A-F]{48}$", code);
            Assert.Equal($"http://192.168.2.8:5173/?dingtalkBinding=1&companyId={company}#ticket={code}", ticket.GetProperty("scanUrl").GetString());
            return code;
        }
        var expired = await Issue(owner);
        clock.Now = clock.Now.AddMinutes(5);
        Assert.Equal(HttpStatusCode.BadRequest, (await phone.PostAsJsonAsync(scan, new DingTalkPairingScan(company, expired))).StatusCode);
        var superseded = await Issue(owner);
        var code = await Issue(owner);
        var otherCode = await Issue(other);
        Assert.Equal(HttpStatusCode.BadRequest, (await phone.PostAsJsonAsync(scan, new DingTalkPairingScan(company, superseded))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await phone.PostAsJsonAsync(scan, new DingTalkPairingScan(Guid.NewGuid(), code))).StatusCode);
        var context = await phone.PostAsJsonAsync(scan, new DingTalkPairingScan(company, code));
        Assert.Equal(HttpStatusCode.OK, context.StatusCode);
        Assert.True(context.Headers.CacheControl!.NoStore);
        var data = await context.Content.ReadAsStringAsync();
        Assert.Equal("扫码员工", JsonDocument.Parse(data).RootElement.GetProperty("employeeName").GetString());
        Assert.DoesNotContain("qr-owner", data);
        Assert.DoesNotContain("private-secret", data);
        Assert.Equal(HttpStatusCode.BadRequest, (await phone.PostAsJsonAsync(proof, new DingTalkPairingProof(company, code, "valid"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await phone.PostAsJsonAsync(proof, new DingTalkPairingProof(company, code, "invalid", true))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await phone.PostAsJsonAsync(proof, new DingTalkPairingProof(company, code, "valid", true))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await phone.PostAsJsonAsync(proof, new DingTalkPairingProof(company, code, "valid", true))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await other.PostAsJsonAsync(self + "/confirm", new { code })).StatusCode);
        var permissions = (await repository.GetRolePermissionsAsync(UserRole.Engineer, default)).ToArray();
        await repository.SetRolePermissionsAsync("Engineer", permissions.Where(p => p != PermissionCodes.DingTalkSelfBind).ToArray(), default);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync(self + "/confirm", new { code })).StatusCode);
        await repository.SetRolePermissionsAsync("Engineer", permissions, default);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsJsonAsync(self + "/confirm", new { code })).StatusCode);
        Assert.True(JsonDocument.Parse(await owner.GetStringAsync(self + "?qr=true")).RootElement.GetProperty("bound").GetBoolean());
        Assert.Equal(HttpStatusCode.NoContent, (await phone.PostAsJsonAsync(proof, new DingTalkPairingProof(company, otherCode, "valid", true))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await other.PostAsJsonAsync(self + "/confirm", new { code = otherCode })).StatusCode);
    }
}
