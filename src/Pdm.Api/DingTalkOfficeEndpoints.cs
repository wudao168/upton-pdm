using Microsoft.Extensions.Options;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api;

public sealed record DingTalkEmployeeBinding(string CorpId, string UserId, string Username);
public sealed record SaveDingTalkEmployeeBindings(long ExpectedRowVersion, IReadOnlyList<DingTalkEmployeeBinding> Bindings);
public sealed record DingTalkOfficeLogin(Guid CompanyId, string Code, string? ClientInstanceId = null);

public static class DingTalkOfficeEndpoints
{
    public static void MapDingTalkOfficeEndpoints(this WebApplication app)
    {
        var self = app.MapGroup("/api/me/dingtalk-binding").RequireAuthorization();
        self.MapGet("", async (bool? qr, DingTalkConfigurationService service, DingTalkEmployeePairing pairing, CancellationToken ct) =>
            Results.Ok(await service.SelfBindingAsync(pairing, false, qr == true, ct)));
        self.MapPost("", async (bool? qr, DingTalkConfigurationService service, DingTalkEmployeePairing pairing, CancellationToken ct) =>
            Results.Ok(await service.SelfBindingAsync(pairing, true, qr == true, ct)));
        self.MapPost("/confirm", async (DingTalkPairingCode input, DingTalkConfigurationService service, DingTalkEmployeePairing pairing, CancellationToken ct) =>
        {
            await service.ConfirmSelfBindingAsync(pairing, input.Code, ct);
            return Results.NoContent();
        });
        app.MapPost("/api/integrations/dingtalk/office/binding-scan", async (DingTalkPairingScan input,
            DingTalkEmployeePairing pairing, DingTalkOplIntegration integration, HttpContext context, CancellationToken ct) =>
        {
            var ticket = pairing.FindPending(input.CompanyId, input.Code, qr: true);
            await integration.LoadAsync(ticket.CompanyId, ct);
            if (!DingTalkEmployeePairing.Matches(ticket, integration.Settings))
                throw new PdmConflictException("钉钉应用配置已更新，请在客户端重新生成二维码。");
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new { ticket.EmployeeName, ticket.CorpId, ticket.AppKey });
        }).AllowAnonymous().RequireRateLimiting("dingtalk-pairing");
        app.MapPost("/api/integrations/dingtalk/office/binding-proof", async (DingTalkPairingProof input,
            DingTalkEmployeePairing pairing, DingTalkOplIntegration integration, CancellationToken ct) =>
        {
            var ticket = pairing.FindPending(input.CompanyId, input.Code, input.Qr);
            await integration.LoadAsync(ticket.CompanyId, ct);
            if (!DingTalkEmployeePairing.Matches(ticket, integration.Settings))
                throw new PdmConflictException("钉钉应用配置已更新，请在客户端重新生成绑定请求。");
            var identity = await integration.ResolveVisitorAsync(null, input.AuthCode, ct);
            pairing.Verify(ticket, identity);
            return Results.NoContent();
        }).AllowAnonymous().RequireRateLimiting("dingtalk-pairing");

        app.MapGet("/api/integrations/dingtalk/office/bootstrap", async (Guid? companyId, DingTalkOplIntegration integration, CancellationToken ct) =>
        {
            await integration.LoadAsync(companyId, ct);
            return Results.Ok(new { enabled = integration.ApplicationEnabled, integration.Settings.CompanyId,
                corpId = integration.ApplicationEnabled ? integration.Settings.CorpId : "",
                appKey = integration.ApplicationEnabled ? integration.Settings.AppKey : "" });
        }).AllowAnonymous();

        app.MapPost("/api/integrations/dingtalk/office/login", async (DingTalkOfficeLogin input, DingTalkOplIntegration integration,
            IPdmRepository repository, CompanySessionService companies, LoginSecurityService security, ITokenIssuer tokens,
            IPersistentSessionTokenService persistent, IOptions<AuthenticationOptions> authentication, HttpContext context,
            TimeProvider clock, CancellationToken ct) =>
        {
            if (input.CompanyId == Guid.Empty) throw new PdmRuleException("企业入口无效，请从钉钉工作台重新打开。");
            await integration.LoadAsync(input.CompanyId, ct);
            if (!integration.ApplicationEnabled || integration.Settings.CompanyId != input.CompanyId)
                throw new PdmRuleException("钉钉移动办公尚未启用，请联系管理员配置当前企业应用。");
            var identity = await integration.ResolveVisitorAsync(null, input.Code, ct);
            var binding = integration.Settings.EmployeeBindings.SingleOrDefault(item => item.CorpId == integration.Settings.CorpId && item.UserId == identity.UserId);
            if (binding is null) throw new UnauthorizedAccessException("当前钉钉账号尚未绑定 UPLM。请在客户端打开「个人设置 → 绑定钉钉」扫码，或联系管理员协助绑定。");
            var account = await repository.FindUserAsync(binding.Username, ct);
            if (account is null || !account.IsActive) throw new UnauthorizedAccessException("绑定的UPLM账号不存在或已停用，请联系管理员。");
            var company = await companies.ResolveAsync(account, input.CompanyId.ToString(), ct);
            var peer = await security.ResolvePeerAsync(context.Request, input.ClientInstanceId, "web", ct);
            var login = await security.OpenAsync(account with { CompanyId = company.PrimaryCompanyId }, peer, null, false, ct);
            if (login.Error is not null) return Results.Problem(login.Error, statusCode: 409, title: "登录受限");
            var lifetime = TimeSpan.FromHours(authentication.Value.TokenLifetimeHours);
            await repository.AppendAuditAsync(new(Guid.NewGuid(), clock.GetUtcNow(), account.Username, "auth.dingtalk.login", "User", account.Username, "通过已绑定的钉钉企业账号登录移动办公。"), ct);
            return Results.Ok(new LoginResponse(tokens.Issue(account, lifetime, login.SessionId), clock.GetUtcNow().Add(lifetime),
                persistent.Issue(account, login.SessionId), account.Username, account.DisplayName, account.EffectiveRoleCode,
                (await repository.GetUserPermissionsAsync(account.Username, account.Role, ct)).Order().ToArray(), company.PrimaryCompanyId,
                company.ActiveCompanyId, company.ActiveCompanyName, company.CrossCompanyView, company.AccessibleCompanies, account.EffectiveRoleCodes));
        }).AllowAnonymous();

        app.MapGet("/api/settings/dingtalk/employees", async (HttpContext context, DingTalkConfigurationService service, CancellationToken ct) =>
            Results.Ok(await service.GetEmployeeBindingsAsync(context.User.Identity!.Name!, Role(context), ct))).RequireAuthorization();
        app.MapPut("/api/settings/dingtalk/employees", async (SaveDingTalkEmployeeBindings input, HttpContext context, DingTalkConfigurationService service, CancellationToken ct) =>
            Results.Ok(await service.SaveEmployeeBindingsAsync(input, context.User.Identity!.Name!, Role(context), ct))).RequireAuthorization();
    }

    private static UserRole Role(HttpContext context) => Enum.Parse<UserRole>(context.User.FindFirst(System.Security.Claims.ClaimTypes.Role)!.Value);
}
