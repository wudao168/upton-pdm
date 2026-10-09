using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public static class CommercialProjectAccess
{
    public static bool IsCommercialRole(string roleCode) => roleCode is "PresalesEngineer" or "SalesManager";

    public static async Task<bool> CanReadAsync(Project project, UserAccount? user, string actor,
        UserRole role, IProjectContactInformationRepository? contacts, CancellationToken ct)
    {
        var roles = user?.EffectiveRoleCodes ?? TenantContext.Current?.EffectiveRoleCodes ?? [role.ToString()];
        if (!roles.Any(IsCommercialRole) || roles.Any(code => !IsCommercialRole(code))) return true;
        if (contacts is null) return false;
        var information = await contacts.FindAsync(project.RootProjectId ?? project.Id, ct);
        if (information is null) return false;
        return roles.Contains("SalesManager") && Includes(information.SalesPeople, actor, user?.DisplayName)
            || roles.Contains("PresalesEngineer") && Includes(information.PresalesPeople, actor, user?.DisplayName);
    }

    private static bool Includes(string names, string actor, string? displayName) =>
        names.Split(['、', ',', '，', ';', '；'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Any(name => string.Equals(name, actor, StringComparison.OrdinalIgnoreCase)
                || !string.IsNullOrWhiteSpace(displayName) && string.Equals(name, displayName, StringComparison.OrdinalIgnoreCase));
}
