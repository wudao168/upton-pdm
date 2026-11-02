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

    private static string AdminToken() => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("upton-pdm", "upton-pdm-clients",
        [new(ClaimTypes.Name, "admin"), new(ClaimTypes.Role, "Administrator")], expires: DateTime.UtcNow.AddMinutes(10),
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
        Assert.Equal(HttpStatusCode.Forbidden, unbound.StatusCode); Assert.Contains("unbound-id", await unbound.Content.ReadAsStringAsync());
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
}
