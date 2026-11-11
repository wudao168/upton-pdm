using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Tests;

// Calculation/persistence tests use explicitly appointed financial readers.
// Scope denial is covered separately in ProjectFinancialAccessTests.
internal static class FinancialTestActors
{
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
