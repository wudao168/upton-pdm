using Upton.Pdm.Application;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class ProjectFolderTreeTests
{
    private static readonly Guid KunshanId = Guid.Parse("70000000-0000-0000-0000-000000000001");
    private static readonly Guid CustomerId = Guid.Parse("c0046500-0000-0000-0000-000000000001");

    [Fact]
    public async Task NonStandardReminderTracksActualSupplyReceiptsAcrossAllEligiblePackages()
    {
        var store = new InMemoryProductionTrackingStore(DateTimeOffset.UtcNow.AddDays(-7));
        var repository = new InMemoryPdmRepository(TimeProvider.System, productionTracking: store);
        var project = Assert.Single(await repository.ListProjectsForUserAsync("admin", UserRole.Administrator, default));
        ReleasePackage Package(string number, DateTimeOffset published) => new(Guid.NewGuid(), project.Id, number,
            ReleasePackageState.Published, null, "", "", [], published, published, "release")
            { Scope = ReleaseScope.NonStandardSupplement, PreviewState = ReleasePreviewState.Succeeded };
        var first = Package("RP-SUPPLY-1", DateTimeOffset.UtcNow.AddDays(-3));
        var second = Package("RP-SUPPLY-2", DateTimeOffset.UtcNow.AddDays(-2));
        await repository.CreateReleasePackageAsync(first, default);
        await repository.CreateReleasePackageAsync(second, default);
        var service = new ProductionTrackingService(repository, store, TimeProvider.System, new ProductionDrawingService(repository));
        async Task<Project> Summary() => (await repository.ListProjectsForUserAsync("admin", UserRole.Administrator, default)).Single();
        Assert.True((await Summary()).HasUnreceivedNonStandardRelease);
        Assert.Null((await Summary()).LatestPurchasedBomPublishedAt);
        await service.ReceiveAsync(first.Id, Guid.NewGuid(), "admin", UserRole.Administrator, default);
        Assert.True((await Summary()).HasUnreceivedNonStandardRelease);
        await service.ReceiveAsync(second.Id, Guid.NewGuid(), "admin", UserRole.Administrator, default);
        Assert.False((await Summary()).HasUnreceivedNonStandardRelease);
        var historical = Package("RP-SUPPLY-LEGACY", DateTimeOffset.UtcNow.AddDays(-10));
        await repository.CreateReleasePackageAsync(historical, default);
        Assert.False((await Summary()).HasUnreceivedNonStandardRelease);
        await store.CreateAsync(new(historical.Id, project.Id, project.OrganizationId ?? Guid.Empty, project.Code, project.Name,
            historical.Number, historical.Scope, historical.PublishedAt!.Value, [], [], []), default);
        Assert.True((await Summary()).HasUnreceivedNonStandardRelease);
        await service.ReceiveAsync(historical.Id, Guid.NewGuid(), "admin", UserRole.Administrator, default);
        Assert.False((await Summary()).HasUnreceivedNonStandardRelease);
        var standard = Package("RP-SUPPLY-STANDARD", DateTimeOffset.UtcNow) with { Scope = ReleaseScope.StandardFormal };
        await repository.CreateReleasePackageAsync(standard, default);
        Assert.False((await Summary()).HasUnreceivedNonStandardRelease);
        Assert.Equal(standard.PublishedAt, (await Summary()).LatestPurchasedBomPublishedAt);
    }

    [Theory]
    [InlineData(ReleaseScope.NonStandardWithDrawing, ReleasePackageState.Published, ReleasePreviewState.Pending, true)]
    [InlineData(ReleaseScope.NonStandardSupplement, ReleasePackageState.Published, ReleasePreviewState.WaitingForServer, true)]
    [InlineData(ReleaseScope.NonStandardSupplement, ReleasePackageState.Published, ReleasePreviewState.Running, true)]
    [InlineData(ReleaseScope.NonStandardSupplement, ReleasePackageState.Published, ReleasePreviewState.Failed, true)]
    [InlineData(ReleaseScope.NonStandardSupplement, ReleasePackageState.Published, ReleasePreviewState.Succeeded, false)]
    [InlineData(ReleaseScope.StandardFormal, ReleasePackageState.Published, ReleasePreviewState.Failed, false)]
    [InlineData(ReleaseScope.ElectricalSupplement, ReleasePackageState.Published, ReleasePreviewState.Pending, false)]
    [InlineData(ReleaseScope.StandardFormal, ReleasePackageState.PublishFailed, ReleasePreviewState.None, true)]
    [InlineData(ReleaseScope.NonStandardSupplement, ReleasePackageState.Draft, ReleasePreviewState.None, false)]
    public async Task ProjectListContainsReleaseAttentionAndFormalPublicationTime(ReleaseScope scope, ReleasePackageState state, string previewState, bool attention)
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var project = Assert.Single(await repository.ListProjectsForUserAsync("admin", UserRole.Administrator, default));
        var published = DateTimeOffset.Parse("2026-10-27T12:00:00Z");
        var package = new ReleasePackage(Guid.NewGuid(), project.Id, "RP-ATTENTION", state, null, "", "", [], published.AddDays(-1),
            state == ReleasePackageState.Published ? published : null, null) { Scope = scope, LocksDocuments = true, PreviewState = previewState };
        await repository.CreateReleasePackageAsync(package, default);
        var summary = Assert.Single(await repository.ListProjectsForUserAsync("admin", UserRole.Administrator, default));
        Assert.Equal(attention, summary.HasReleaseAttention);
        Assert.Equal(package.PublishedAt, summary.LatestPublishedAt);
        if (state == ReleasePackageState.Published)
        {
            await repository.MarkReleasePreviewStateAsync(package.Id, ReleasePreviewState.Succeeded, null, 3, published.AddHours(3), default);
            summary = Assert.Single(await repository.ListProjectsForUserAsync("admin", UserRole.Administrator, default));
            Assert.False(summary.HasReleaseAttention);
            Assert.Equal(published, summary.LatestPublishedAt);
        }
    }

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
        var acceptance = folders.Single(item => item.TemplateKey == "acceptance");
        var validationPlan = folders.Single(item => item.TemplateKey == "acceptance.validation-plan");
        Assert.Equal(acceptance.Id, validationPlan.ParentFolderId);
        Assert.Equal("验证计划", validationPlan.Name);
    }

    [Fact]
    public async Task DefaultFolderTemplateDoesNotContainNameplateFolder()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var main = await repository.CreateNumberedProjectAsync(Command("默认目录模板"), CancellationToken.None);

        var template = await repository.ListFolderTemplateAsync(CancellationToken.None);
        var folders = await repository.ListProjectFoldersAsync(main.Id, "engineer", UserRole.Administrator, CancellationToken.None);

        Assert.DoesNotContain(template, item => item.FolderKey == "mechanical.nameplate");
        Assert.DoesNotContain(folders, item => item.TemplateKey == "mechanical.nameplate" || item.Name == "铭牌");
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
    public async Task FolderPermissionsMatchTheUsersActualRoleCode()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        await repository.CreateUserAsync(new(Guid.NewGuid(), "electrical", "电气工程师", "unused", UserRole.Engineer, true, RoleCode: "ElectricalEngineer"), CancellationToken.None);
        var main = await repository.CreateNumberedProjectAsync(Command("实际角色目录权限"), CancellationToken.None);
        var template = await repository.ListFolderTemplateAsync(CancellationToken.None);
        await repository.SaveFolderTemplateAsync(template.Select(node => new SaveFolderTemplateNodeCommand(
            node.FolderKey, node.Name, node.SortOrder, node.InheritPermissions,
            node.FolderKey == "electrical"
                ? [new SaveFolderPermissionCommand(FolderPrincipalType.Role, "ElectricalEngineer", FolderAccess.Publish)]
                : [])).ToArray(), CancellationToken.None);

        var folders = await repository.ListProjectFoldersAsync(main.Id, "electrical", UserRole.Engineer, CancellationToken.None);

        Assert.Equal(FolderAccess.Publish, folders.Single(item => item.TemplateKey == "electrical").EffectiveAccess);
    }

    [Fact]
    public async Task ProjectFolderPermissionsGateDocumentReadAndEditAccess()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var main = await repository.CreateNumberedProjectAsync(Command("目录权限项目"), CancellationToken.None);
        await repository.SetMainProjectStaffingAsync(main.Id, new("project-manager", [], ["engineer"]), "admin", CancellationToken.None);
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
    public async Task ProjectListReportsReferenceAndStandaloneDocumentCountsForAuthorizedUsers()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);

        var project = Assert.Single(await repository.ListProjectsForUserAsync("admin", UserRole.Administrator, CancellationToken.None));

        // Include the unregistered model reference as the workspace tree does.
        Assert.Equal(8, project.DocumentCount);
        Assert.Equal(7, project.ModelDocumentCount);
        Assert.Equal(1, project.DrawingDocumentCount);
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
