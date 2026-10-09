using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Tests;

public sealed class CommercialRolePermissionTests
{
    [Theory]
    [InlineData("SalesManager")]
    [InlineData("PresalesEngineer")]
    public async Task CommercialRolesReadAssignedProjectsAndUploadOnlyCommercialFolders(string roleCode)
    {
        var contacts = new InMemoryProjectContactInformationRepository();
        var repository = new InMemoryPdmRepository(TimeProvider.System, contacts);
        await repository.CreateUserAsync(new(Guid.NewGuid(), "commercial", "商务测试", "unused", UserRole.ProductionViewer, true, RoleCode: roleCode), default);
        var project = (await repository.ListProjectsAsync(default)).First();
        Assert.False(await repository.HasProjectReadAccessAsync(project.Id, "commercial", UserRole.ProductionViewer, default));
        Assert.Empty(await repository.ListProjectsForUserAsync("commercial", UserRole.ProductionViewer, default));
        await contacts.SaveAsync(new(project.Id, SalesPeople: roleCode == "SalesManager" ? "commercial" : "other",
            PresalesPeople: roleCode == "PresalesEngineer" ? "commercial" : "other"), 0, default);
        Assert.True(await repository.HasProjectReadAccessAsync(project.Id, "commercial", UserRole.ProductionViewer, default));
        Assert.True(await repository.HasProjectContentReadAccessAsync(project.Id, "commercial", UserRole.ProductionViewer, default));
        var user = await repository.FindUserAsync("commercial", default);
        Assert.True(await CommercialProjectAccess.CanReadAsync(project with { Id = Guid.NewGuid(), RootProjectId = project.Id, ParentProjectId = project.Id },
            user, "commercial", UserRole.ProductionViewer, contacts, default));
        Assert.False(await CommercialProjectAccess.CanReadAsync(project with { Id = Guid.NewGuid(), RootProjectId = Guid.NewGuid() },
            user, "commercial", UserRole.ProductionViewer, contacts, default));
        Assert.Single(await repository.ListProjectsForUserAsync("commercial", UserRole.ProductionViewer, default));
        var folders = await repository.ListProjectFoldersAsync(project.Id, "commercial", UserRole.ProductionViewer, default);
        foreach (var key in new[] { "presales", "customer-files" })
        {
            var folder = Assert.Single(folders, folder => folder.TemplateKey == key);
            Assert.Equal(FolderAccess.View | FolderAccess.Download | FolderAccess.Upload, folder.EffectiveAccess);
        }
        Assert.All(folders.Where(folder => folder.TemplateKey is not "presales" and not "customer-files"),
            folder => Assert.Equal(FolderAccess.None, folder.EffectiveAccess & (FolderAccess.Upload | FolderAccess.Edit | FolderAccess.Delete)));
        var permissions = await repository.GetUserPermissionsAsync("commercial", UserRole.ProductionViewer, default);
        Assert.DoesNotContain(PermissionCodes.DocumentEdit, permissions);
        Assert.DoesNotContain(PermissionCodes.DrawingReviewDecide, permissions);
        await contacts.SaveAsync(new(project.Id, SalesPeople: "other", PresalesPeople: "other"), 1, default);
        Assert.False(await repository.HasProjectContentReadAccessAsync(project.Id, "commercial", UserRole.ProductionViewer, default));
    }
}
