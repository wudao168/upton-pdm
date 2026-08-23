using Upton.Pdm.Application;

namespace Upton.Pdm.Api;

public sealed record ResolvedCompanySession(
    Guid PrimaryCompanyId,
    Guid ActiveCompanyId,
    string ActiveCompanyName,
    bool CrossCompanyView,
    IReadOnlyList<CompanyOption> AccessibleCompanies);

public sealed class CompanySessionService(IPdmRepository repository)
{
    public async Task<ResolvedCompanySession> ResolveAsync(UserAccount account, string? requestedCompanyId, CancellationToken cancellationToken)
    {
        var scope = await repository.GetUserCompanyScopeAsync(account.Username, cancellationToken)
            ?? throw new UnauthorizedAccessException("账号尚未配置所属公司。");
        var primaryCompanyId = scope.PrimaryCompanyId ?? throw new UnauthorizedAccessException("账号尚未配置主公司。");
        var organizations = (await repository.GetProjectNumberingOptionsAsync(cancellationToken)).Organizations
            .Where(item => item.IsActive)
            .ToArray();
        var primary = organizations.SingleOrDefault(item => item.Id == primaryCompanyId)
            ?? throw new UnauthorizedAccessException("所属公司已停用。");
        var platformAdministrator = string.Equals(account.EffectiveRoleCode, "platform_admin", StringComparison.OrdinalIgnoreCase)
            || string.Equals(account.EffectiveRoleCode, "developer", StringComparison.OrdinalIgnoreCase);
        var accessibleIds = new HashSet<Guid> { primaryCompanyId };
        if (platformAdministrator)
        {
            foreach (var organization in organizations) accessibleIds.Add(organization.Id);
        }
        foreach (var companyId in scope.AccessibleCompanyIds) accessibleIds.Add(companyId);
        var accessibleCompanies = organizations.Where(item => accessibleIds.Contains(item.Id))
            .Select(item => new CompanyOption(item.Id, item.Name, item.ProjectCompanyCode))
            .OrderBy(item => item.Name, StringComparer.Ordinal)
            .ToArray();
        var activeCompanyId = string.IsNullOrWhiteSpace(requestedCompanyId)
            ? primaryCompanyId
            : Guid.TryParse(requestedCompanyId, out var parsed)
                ? parsed
                : throw new UnauthorizedAccessException("当前公司无效。");
        if (activeCompanyId != primaryCompanyId
            && !platformAdministrator
            && (!scope.CrossCompanyView || !scope.AccessibleCompanyIds.Contains(activeCompanyId)))
            throw new UnauthorizedAccessException("无权访问所选公司。");
        var active = organizations.SingleOrDefault(item => item.Id == activeCompanyId)
            ?? throw new UnauthorizedAccessException("当前公司不可用。");
        return new ResolvedCompanySession(primaryCompanyId, activeCompanyId, active.Name, scope.CrossCompanyView, accessibleCompanies);
    }
}
