using Upton.Pdm.Application;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class ProjectFolderTreeTests
{
    private static readonly Guid KunshanId = Guid.Parse("70000000-0000-0000-0000-000000000001");
    private static readonly Guid CustomerId = Guid.Parse("c0046500-0000-0000-0000-000000000001");

    [Fact]
    public async Task MainAndChildProjectsHaveMatchingMechanicalAndElectricalContainers()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var main = await repository.CreateNumberedProjectAsync(Command("主项目"), CancellationToken.None);
        var child = await repository.CreateSubprojectAsync(new(main.Id, "子项目一", null, 1), CancellationToken.None);

        var folders = await repository.ListProjectFoldersAsync(main.Id, "engineer", UserRole.Administrator, CancellationToken.None);
        var containers = folders.Where(item => item.Purpose == ProjectFolderPurpose.ProjectContainer).ToArray();

        Assert.Contains(containers, item => item.TemplateKey == "mechanical.project" && item.TargetProjectId == main.Id && item.Name == $"{main.Code}-0");
        Assert.Contains(containers, item => item.TemplateKey == "electrical.project" && item.TargetProjectId == main.Id && item.Name == $"{main.Code}-0");
        Assert.Contains(containers, item => item.TemplateKey == "mechanical.project" && item.TargetProjectId == child.Id && item.Name == child.Code);
        Assert.Contains(containers, item => item.TemplateKey == "electrical.project" && item.TargetProjectId == child.Id && item.Name == child.Code);
    }

    [Fact]
    public async Task RegistrationDefaultsToMechanicalAndRejectsAnotherProjectsFolder()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var main = await repository.CreateNumberedProjectAsync(Command("主项目"), CancellationToken.None);
        var child = await repository.CreateSubprojectAsync(new(main.Id, "子项目一", null, 1), CancellationToken.None);
        var folders = await repository.ListProjectFoldersAsync(main.Id, "engineer", UserRole.Administrator, CancellationToken.None);
        var mainMechanical = folders.Single(item => item.TemplateKey == "mechanical.project" && item.TargetProjectId == main.Id);
        var childElectrical = folders.Single(item => item.TemplateKey == "electrical.project" && item.TargetProjectId == child.Id);

        var mainDocument = await repository.RegisterDocumentAsync(new(main.Id, "M-001", "主项目零件", "M-001.SLDPRT", DocumentKind.Part), "engineer", CancellationToken.None);
        var childDocument = await repository.RegisterDocumentAsync(new(child.Id, "E-001", "子项目电气图", "E-001.SLDDRW", DocumentKind.Drawing, childElectrical.Id), "engineer", CancellationToken.None);

        Assert.Equal(mainMechanical.Id, mainDocument.FolderId);
        Assert.Equal(childElectrical.Id, childDocument.FolderId);
        await Assert.ThrowsAsync<PdmRuleException>(() => repository.RegisterDocumentAsync(
            new(child.Id, "BAD-001", "错误归属", "BAD-001.SLDPRT", DocumentKind.Part, mainMechanical.Id), "engineer", CancellationToken.None));
    }

    [Fact]
    public async Task ProjectFolderCanOverrideRolePermissionsIndependently()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var main = await repository.CreateNumberedProjectAsync(Command("权限项目"), CancellationToken.None);
        var folders = await repository.ListProjectFoldersAsync(main.Id, "engineer", UserRole.Administrator, CancellationToken.None);
        var release = folders.Single(item => item.TemplateKey == "mechanical.release");

        await repository.SetProjectFolderPermissionsAsync(main.Id, release.Id,
            [new(FolderPrincipalType.Role, nameof(UserRole.Engineer), FolderAccess.View)], "admin", UserRole.Administrator, CancellationToken.None);
        var engineerFolders = await repository.ListProjectFoldersAsync(main.Id, "engineer", UserRole.Engineer, CancellationToken.None);

        Assert.Equal(FolderAccess.View, engineerFolders.Single(item => item.Id == release.Id).EffectiveAccess);
    }

    [Fact]
    public async Task ProjectFolderPermissionsGateDocumentReadAndEditAccess()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var main = await repository.CreateNumberedProjectAsync(Command("目录权限项目"), CancellationToken.None);
        await repository.SetMainProjectStaffingAsync(main.Id, new("project-manager", [], "engineer"), "admin", CancellationToken.None);
        var folders = await repository.ListProjectFoldersAsync(main.Id, "admin", UserRole.Administrator, CancellationToken.None);
        var mechanical = folders.Single(item => item.TemplateKey == "mechanical.project" && item.TargetProjectId == main.Id);
        var document = await repository.RegisterDocumentAsync(
            new(main.Id, "ACL-001", "目录权限零件", "ACL-001.SLDPRT", DocumentKind.Part, mechanical.Id), "engineer", CancellationToken.None);

        await repository.SetProjectFolderPermissionsAsync(main.Id, mechanical.Id,
            [new(FolderPrincipalType.Role, nameof(UserRole.Engineer), FolderAccess.View)], "admin", UserRole.Administrator, CancellationToken.None);
        Assert.True(await repository.HasDocumentReadAccessAsync(document.Id, "engineer", UserRole.Engineer, CancellationToken.None));
        Assert.False(await repository.HasDocumentAccessAsync(document.Id, "engineer", UserRole.Engineer, FolderAccess.Edit, CancellationToken.None));

        await repository.SetProjectFolderPermissionsAsync(main.Id, mechanical.Id,
            [new(FolderPrincipalType.Role, nameof(UserRole.Engineer), FolderAccess.None)], "admin", UserRole.Administrator, CancellationToken.None);
        Assert.False(await repository.HasDocumentReadAccessAsync(document.Id, "engineer", UserRole.Engineer, CancellationToken.None));
    }

    [Fact]
    public async Task ProjectListReportsDocumentAndBusinessStatusForAuthorizedUsers()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);

        var project = Assert.Single(await repository.ListProjectsForUserAsync("admin", UserRole.Administrator, CancellationToken.None));

        Assert.Equal(7, project.DocumentCount);
        Assert.Contains("编辑中", project.BusinessStatus);
        Assert.Contains("待审批", project.BusinessStatus);
        Assert.Equal("王工", project.RootDocumentCheckedOutBy);
    }

    [Fact]
    public async Task ProjectListIgnoresCheckoutOnNonRootDocumentForProjectStatus()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var project = Assert.Single(await repository.ListProjectsForUserAsync("admin", UserRole.Administrator, CancellationToken.None));
        var snapshot = await repository.GetLatestReferenceSnapshotAsync(project.Id, CancellationToken.None);
        Assert.NotNull(snapshot);
        await repository.ForceReleaseCheckoutAsync(snapshot.RootDocumentId, "admin", "test", CancellationToken.None);
        var child = (await repository.ListDocumentsAsync(project.Id, CancellationToken.None)).First(item => item.Id != snapshot.RootDocumentId);
        await repository.CheckoutAsync(child.Id, "engineer", CancellationToken.None);

        var refreshed = Assert.Single(await repository.ListProjectsForUserAsync("admin", UserRole.Administrator, CancellationToken.None));

        Assert.DoesNotContain("编辑中", refreshed.BusinessStatus ?? string.Empty);
        Assert.Null(refreshed.RootDocumentCheckedOutBy);
    }

    private static CreateNumberedProjectCommand Command(string name) => new(
        KunshanId, "P", 2, CustomerId, name, null, new DateOnly(2026, 8, 14), 1,
        "engineer", @"D:\PDM\Vault", @"D:\PDM\Release");
}
