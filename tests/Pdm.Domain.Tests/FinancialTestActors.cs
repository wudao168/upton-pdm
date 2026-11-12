using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Tests;

// Calculation/persistence tests use explicitly appointed financial readers.
// Scope denial is covered separately in ProjectFinancialAccessTests.
internal static class FinancialTestActors
{
    public static async Task GrantAsync(InMemoryPdmRepository repository, string roleCode, params AuthorizationGrant[] grants)
    {
        var settings = await repository.GetAuthorizationSettingsAsync(default);
        await repository.SaveAuthorizationSettingsAsync(settings with
        {
            Templates = settings.Templates.Select(role => role.RoleCode == roleCode
                ? role with { Grants = [.. role.Grants.Where(existing => grants.All(grant => grant.Permission != existing.Permission)), .. grants] }
                : role).ToArray()
        }, settings.Revision, default);
    }

    public static async Task ConfigureAsync(InMemoryPdmRepository repository, Guid projectId,
        params (string Name, UserRole Role)[] actors)
    {
        foreach (var actor in actors)
            if (await repository.FindUserAsync(actor.Name, default) is null)
                await repository.CreateUserAsync(new(Guid.NewGuid(), actor.Name, actor.Name, "unused", actor.Role, true), default);
        var project = (await repository.FindProjectAsync(projectId, default))!;
        await repository.SetMainProjectStaffingAsync(projectId, new(project.PrimaryProjectManager ?? actors[0].Name,
            project.CollaborativeProjectManagers.Concat(actors.Select(actor => actor.Name)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            project.DesignLeads.Concat(actors.Select(actor => actor.Name)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()), "admin", default);
    }
}
