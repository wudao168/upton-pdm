using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api.Tests;

internal static class ApiTestAuthorization
{
    public static async Task CreateUserAsync(IPdmRepository repository, UserAccount user, CancellationToken cancellationToken)
    {
        // Shared endpoint fixtures may contain projects from several companies; never choose a
        // new actor's company from ConcurrentDictionary enumeration order.
        var company = user.CompanyId ?? TenantContext.CompanyId ?? (await repository.FindUserAsync("admin", cancellationToken))?.CompanyId;
        await repository.CreateUserAsync(user with { CompanyId = company }, cancellationToken);
    }

    public static async Task GrantRoleAsync(IPdmRepository repository, string role, params string[] permissions)
    {
        var settings = await repository.GetAuthorizationSettingsAsync(default);
        var codes = new HashSet<string>(permissions);
        var pending = new Queue<string>(codes);
        while (pending.TryDequeue(out var code))
            foreach (var dependency in RolePermissionCatalog.Permissions.Single(item => item.Code == code).Dependencies)
                if (codes.Add(dependency)) pending.Enqueue(dependency);
        await repository.SaveAuthorizationSettingsAsync(settings with
        {
            Templates = settings.Templates.Select(template => template.RoleCode == role
                ? template with { Grants = [.. template.Grants.Where(grant => !codes.Contains(grant.Permission)), .. codes.Select(code => new AuthorizationGrant(code, ["company"]))] }
                : template).ToArray()
        }, settings.Revision, default);
    }

    public static async Task EnsureUserAsync(IPdmRepository repository, UserAccount user, CancellationToken cancellationToken)
    {
        if (await repository.FindUserAsync(user.Username, cancellationToken) is null)
        {
            await repository.CreateUserAsync(user, cancellationToken);
            return;
        }
        await repository.UpdateUserAsync(user.Username, user.DisplayName, user.Role, user.EffectiveRoleCode,
            user.EffectiveRoleCodes, user.IsActive, cancellationToken);
        if (user.CompanyId is Guid company)
            await repository.SetUserCompanyScopeAsync(user.Username, company, false, [], "fixture", cancellationToken);
    }

    public static async Task<Project> CreateProjectAsync(IPdmRepository repository, CreateProjectCommand command,
        string actor, CancellationToken cancellationToken)
    {
        var previous = TenantContext.Current;
        var company = TenantContext.CompanyId
            ?? (await repository.FindUserAsync(actor, cancellationToken))?.CompanyId
            ?? Guid.Parse("70000000-0000-0000-0000-000000000001");
        try
        {
            if (previous is null)
                TenantContext.Set(new(Guid.Empty, company, company, actor, "fixture", false, new HashSet<string>()));
            return await repository.CreateProjectAsync(command, actor, cancellationToken);
        }
        finally
        {
            if (previous is null) TenantContext.Clear(); else TenantContext.Set(previous);
        }
    }

    // 夹具与实际权限中心一样保存模板，不能继续写入已退出业务流程的旧角色表。
    public static async Task<RolePermissionDirectory> CreateRoleAsync(IPdmRepository repository,
        string name, string description, string sourceRoleCode, CancellationToken cancellationToken)
    {
        var settings = await repository.GetAuthorizationSettingsAsync(cancellationToken);
        var source = settings.Templates.Single(template => template.RoleCode == sourceRoleCode);
        var code = "api-fixture-" + Guid.NewGuid().ToString("N");
        var template = source with { Key = code, RoleCode = code, Name = name, Description = description };
        await repository.SaveAuthorizationSettingsAsync(settings with
        {
            Templates = settings.Templates.Append(template).ToArray()
        }, settings.Revision, cancellationToken, updateRoles: true);
        return await repository.GetRolePermissionDirectoryAsync(cancellationToken);
    }
}
