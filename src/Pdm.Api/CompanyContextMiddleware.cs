using Upton.Pdm.Application;
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
            if (account is null && repository is InMemoryPdmRepository)
            {
                await next(context);
                return;
            }
            if (account is null) throw new UnauthorizedAccessException("账号不存在。");
            var company = await companySessions.ResolveAsync(account, context.Request.Headers["X-Company-Id"].FirstOrDefault(), context.RequestAborted);
            var permissions = await repository.GetUserPermissionsAsync(account.Username, account.Role, context.RequestAborted);
            TenantContext.Set(new CurrentTenant(account.Id, company.ActiveCompanyId, company.PrimaryCompanyId, account.Username,
                account.EffectiveRoleCode, company.CrossCompanyView, permissions, account.EffectiveRoleCodes));
            await next(context);
        }
        catch (UnauthorizedAccessException exception)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { success = false, message = exception.Message }, context.RequestAborted);
        }
        finally
        {
            TenantContext.Clear();
        }
    }

}
