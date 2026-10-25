using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api;

public static class LoginSecurityEndpoints
{
    public static void MapLoginSecurity(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();
        api.MapPost("/auth/heartbeat", () => Results.Ok(true));
        api.MapPost("/auth/logout", async (HttpContext context, LoginSecurityService service, CancellationToken ct) =>
        {
            var id = context.User.FindFirst("sid")?.Value;
            if (id is not null) await service.RevokeAsync(context.User.Identity!.Name!, id, ct);
            return Results.Ok(true);
        });
        api.MapGet("/login-security", async (LoginSecurityService service, CancellationToken ct) =>
        {
            if (!CanManage()) return Results.Forbid();
            var tenant = TenantContext.Current!;
            var users = (await service.ListAsync(ct)).Where(s => CanEditPolicy() || s.CompanyId == tenant.PrimaryCompanyId);
            return Results.Ok(new { policy = await service.PolicyAsync(ct), users, canEditPolicy = CanEditPolicy() });
        });
        api.MapPut("/login-security/policy", async (SaveLoginSecurityPolicy input, HttpContext context, LoginSecurityService service, IPdmRepository repository, TimeProvider clock, CancellationToken ct) =>
        {
            if (!CanEditPolicy()) return Results.Forbid();
            var result = await service.SavePolicyAsync(input, ct);
            await repository.AppendAuditAsync(new AuditEntry(Guid.NewGuid(), clock.GetUtcNow(), context.User.Identity!.Name!, "login-security.policy", "LoginSecurity", "policy",
                $"同端登录限制：{(input.SingleSessionEnabled ? "启用" : "关闭")}；电脑绑定：{(input.DeviceBindingEnabled ? "启用" : "关闭")}"), ct);
            return Results.Ok(result);
        });
        api.MapPut("/login-security/users/{username}/devices/{id}", async (string username, string id, DeviceApproval input, HttpContext context, LoginSecurityService service, IPdmRepository repository, TimeProvider clock, CancellationToken ct) =>
        {
            if (!await CanManageUserAsync(username, repository, ct)) return Results.Forbid();
            await service.SetDeviceAsync(username, id, input.Approved, ct);
            await AuditAsync(repository, clock, context, "login-security.device", username, $"{(input.Approved ? "绑定" : "解绑")}电脑：{id}", ct);
            return Results.Ok(true);
        });
        api.MapDelete("/login-security/users/{username}/sessions/{id}", async (string username, string id, HttpContext context, LoginSecurityService service, IPdmRepository repository, TimeProvider clock, CancellationToken ct) =>
        {
            if (!await CanManageUserAsync(username, repository, ct)) return Results.Forbid();
            await service.RevokeAsync(username, id, ct);
            await AuditAsync(repository, clock, context, "login-security.logout", username, "管理员强制下线登录会话：" + id, ct);
            return Results.Ok(true);
        });
    }

    public sealed record DeviceApproval(bool Approved);
    // 与 CompanySessionService 的内置 admin 全局管理规则保持一致。
    private static bool CanEditPolicy() => TenantContext.Current is { } tenant && (tenant.IsPlatformAdministrator || tenant.Username.Equals("admin", StringComparison.OrdinalIgnoreCase));
    private static bool CanManage() => CanEditPolicy() || TenantContext.Current?.HasRole("Administrator") == true;
    private static async Task<bool> CanManageUserAsync(string username, IPdmRepository repository, CancellationToken ct)
    {
        if (!CanManage()) return false;
        var tenant = TenantContext.Current!;
        var user = await repository.FindUserAsync(username, ct);
        if (user is null) throw new PdmNotFoundException("用户不存在。");
        if (CanEditPolicy()) return true;
        var scope = await repository.GetUserCompanyScopeAsync(username, ct);
        return scope?.PrimaryCompanyId == tenant.PrimaryCompanyId;
    }
    private static Task AuditAsync(IPdmRepository repository, TimeProvider clock, HttpContext context, string action, string username, string detail, CancellationToken ct) =>
        repository.AppendAuditAsync(new AuditEntry(Guid.NewGuid(), clock.GetUtcNow(), context.User.Identity!.Name!, action, "LoginSecurity", username, detail), ct);
}
