using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Api;

public sealed class CompanyContextMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IPdmRepository repository, CompanySessionService companySessions)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            await next(context);
            return;
        }

        try
        {
            var username = context.User.Identity.Name ?? throw new UnauthorizedAccessException("登录信息无效。");
            var account = await repository.FindUserAsync(username, context.RequestAborted);
            if (account is null || !account.IsActive)
            {
                throw new UnauthorizedAccessException("账号不存在。");
            }
            if (account is not null)
            {
                var company = await companySessions.ResolveAsync(account, context.Request.Headers["X-Company-Id"].FirstOrDefault(), context.RequestAborted);
                TenantContext.Set(new CurrentTenant(account.Id, company.ActiveCompanyId, company.PrimaryCompanyId, account.Username,
                    account.EffectiveRoleCode, company.CrossCompanyView, new HashSet<string>(), account.EffectiveRoleCodes));
                var permissions = await repository.GetUserPermissionsAsync(account.Username, account.Role, context.RequestAborted);
                TenantContext.Set(new CurrentTenant(account.Id, company.ActiveCompanyId, company.PrimaryCompanyId, account.Username,
                    account.EffectiveRoleCode, company.CrossCompanyView, permissions, account.EffectiveRoleCodes)
                    { Authorization = await repository.GetAuthorizationSettingsAsync(context.RequestAborted) });
                if (await AuthorizationResource.ProjectAsync(context, repository) is Guid projectId)
                {
                    var resource = await AuthorizationPolicy.CreateContextAsync(repository, projectId, username, context.RequestAborted);
                    var scoped = resource is null ? [] : AuthorizationPolicy.Decisions(TenantContext.Current!.Authorization!, resource).Where(d => d.Allowed).Select(d => d.Permission).ToHashSet();
                    var projectCodes = RolePermissionCatalog.Permissions.Where(p => p.Scope == "project").Select(p => p.Code).ToHashSet();
                    TenantContext.Set(TenantContext.Current! with { ResourceProjectId = projectId, Permissions = permissions.Where(p => !projectCodes.Contains(p)).Concat(scoped.Where(projectCodes.Contains)).ToHashSet() });
                }
            }
        }
        catch (UnauthorizedAccessException exception)
        {
            TenantContext.Clear();
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { success = false, message = exception.Message }, context.RequestAborted);
            return;
        }

        try
        {
            await next(context);
        }
        finally
        {
            TenantContext.Clear();
        }
    }

}
