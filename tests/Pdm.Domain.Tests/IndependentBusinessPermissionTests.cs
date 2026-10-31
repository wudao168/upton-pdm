using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class IndependentBusinessPermissionTests
{
    [Fact]
    public async Task PlanEditingRequiresBothRoleGrantAndConfiguredProjectScope()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var project = Assert.Single(await repository.ListProjectsAsync(default));
        await repository.SetMainProjectStaffingAsync(project.Id, new("engineer", [], []), "admin", default);
        Assert.True(await ProjectPermissionPolicy.CanAsync(repository, project.Id, "engineer", UserRole.Engineer, PermissionCodes.ProjectPlanEdit, default));
        Assert.False(await ProjectPermissionPolicy.CanAsync(repository, project.Id, "other", UserRole.Engineer, PermissionCodes.ProjectPlanEdit, default));
        var settings = await repository.GetProjectPermissionSettingsAsync(default);
        await repository.UpdateProjectPermissionSettingsAsync(new(settings.Rules.ToDictionary(pair => pair.Key,
            pair => (IReadOnlyList<string>)(pair.Key == ProjectPermissionPositions.MainManager
                ? pair.Value.Where(code => code != PermissionCodes.ProjectPlanEdit && !RolePermissionCatalog.Permissions.Any(item => item.Code == code && item.LegacyPermission == PermissionCodes.ProjectPlanEdit)).ToArray() : pair.Value))), default);
        Assert.False(await ProjectPermissionPolicy.CanAsync(repository, project.Id, "engineer", UserRole.Engineer, PermissionCodes.ProjectPlanEdit, default));
    }

    [Fact]
    public async Task QualityUploadKindsCanBeGrantedSeparately()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var project = Assert.Single(await repository.ListProjectsAsync(default));
        await repository.SetRolePermissionsAsync("Engineer", [PermissionCodes.ProjectContentView, PermissionCodes.QualityIncomingUpload], default);
        Assert.True(await QualityUploadPolicy.CanAsync(repository, project.Id, "incoming", "engineer", UserRole.Engineer, default));
        Assert.False(await QualityUploadPolicy.CanAsync(repository, project.Id, "assembly", "engineer", UserRole.Engineer, default));
        await repository.SetRolePermissionsAsync("Engineer", [PermissionCodes.ProjectContentView, PermissionCodes.QualityAssemblyUpload], default);
        Assert.False(await QualityUploadPolicy.CanAsync(repository, project.Id, "incoming", "engineer", UserRole.Engineer, default));
        Assert.True(await QualityUploadPolicy.CanAsync(repository, project.Id, "assembly", "engineer", UserRole.Engineer, default));
    }

    [Fact]
    public async Task ContactAndNameplateEditingCanBeGrantedSeparately()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var project = Assert.Single(await repository.ListProjectsAsync(default));
        var contacts = new ProjectContactInformationService(repository, new InMemoryProjectContactInformationRepository(), TimeProvider.System);
        var nameplates = new ProjectNameplateService(repository, new InMemoryProjectNameplateRepository(repository), TimeProvider.System);
        await repository.SetChildProjectDesignersAsync(project.Id, ["engineer"], "admin", default);
        await repository.SetRolePermissionsAsync("Engineer", [PermissionCodes.ProjectContentView, PermissionCodes.ProjectContactEdit], default);
        Assert.True((await contacts.GetAsync(project.Id, "engineer", UserRole.Engineer, default)).CanEdit);
        Assert.False((await nameplates.GetAsync(project.Id, "engineer", UserRole.Engineer, default)).CanEdit);
        await repository.SetRolePermissionsAsync("Engineer", [PermissionCodes.ProjectContentView, PermissionCodes.ProjectNameplateEdit], default);
        Assert.False((await contacts.GetAsync(project.Id, "engineer", UserRole.Engineer, default)).CanEdit);
        Assert.True((await nameplates.GetAsync(project.Id, "engineer", UserRole.Engineer, default)).CanEdit);
    }

    [Fact]
    public async Task BudgetReserveAndSettlementCanBeGrantedSeparately()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var project = Assert.Single(await repository.ListProjectsAsync(default));
        var service = new ProjectBudgetService(repository, new InMemoryProjectBudgetRepository(), new InMemoryU9ProcurementRepository(), TimeProvider.System, new InMemoryMaterialRepository(TimeProvider.System));
        await repository.SetRolePermissionsAsync("Engineer", [PermissionCodes.ProjectBudgetView, PermissionCodes.ProjectBudgetReserve], default);
        var reserve = await service.GetAsync(project.Id, "engineer", UserRole.Engineer, default);
        Assert.True(reserve.CanViewReserve);
        Assert.False(reserve.CanViewSettlement);
        await repository.SetRolePermissionsAsync("Engineer", [PermissionCodes.ProjectBudgetView, PermissionCodes.ProjectBudgetSettlementView], default);
        var settlement = await service.GetAsync(project.Id, "engineer", UserRole.Engineer, default);
        Assert.False(settlement.CanViewReserve);
        Assert.True(settlement.CanViewSettlement);
    }
}
