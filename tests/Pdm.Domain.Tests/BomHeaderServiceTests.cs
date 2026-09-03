using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class BomHeaderServiceTests
{
    private static readonly Guid ProjectId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task List_ReturnsFourIndependentHeaderSlots()
    {
        var service = CreateService(out _, out _);
        var result = await service.ListAsync(ProjectId, "admin", UserRole.Administrator, default);

        Assert.Equal([ProjectBomHeaderKind.Master, ProjectBomHeaderKind.Standard, ProjectBomHeaderKind.NonStandard, ProjectBomHeaderKind.Electrical], result.Select(item => item.Kind));
        Assert.All(result, item => Assert.Null(item.MaterialId));
        Assert.Null(result[0].ParentKind);
        Assert.All(result.Skip(1), item => Assert.Equal(ProjectBomHeaderKind.Master, item.ParentKind));
    }

    [Fact]
    public void ProjectBomHeader_SerializesEnumFieldsWithFrontendContractNames()
    {
        var header = new ProjectBomHeader(
            ProjectId, ProjectBomHeaderKind.Standard, ProjectBomHeaderKind.Master,
            Guid.NewGuid(), null, "标准件BOM", "0201", MaterialApprovalStatus.Draft, 1);

        var json = JsonSerializer.Serialize(header, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("\"kind\":\"Standard\"", json);
        Assert.Contains("\"parentKind\":\"Master\"", json);
        Assert.Contains("\"approvalStatus\":\"Draft\"", json);
    }

    [Fact]
    public async Task Bind_RequiresApprovedProductInProjectCategoryAndDistinctCodes()
    {
        var service = CreateService(out var materials, out _);
        var draft = await AddMaterial(materials, MaterialKind.Product, MaterialApprovalStatus.Draft, "0302", "03020000001");
        var wrongKind = await AddMaterial(materials, MaterialKind.Standard, MaterialApprovalStatus.Approved, "0302", "03020000002");
        var wrongCategory = await AddMaterial(materials, MaterialKind.Product, MaterialApprovalStatus.Approved, "0301", "03010000001");
        var approved = await AddMaterial(materials, MaterialKind.Product, MaterialApprovalStatus.Approved, "0302", "PDM-0302-3", "03020000003", true);
        var childBom = await AddMaterial(materials, MaterialKind.Product, MaterialApprovalStatus.Approved, "0201", "PDM-0201-1", "02010000001", true);

        await Assert.ThrowsAsync<PdmRuleException>(() => service.BindMaterialAsync(ProjectId, ProjectBomHeaderKind.Master, draft.Id, 0, "admin", UserRole.Administrator, default));
        await Assert.ThrowsAsync<PdmRuleException>(() => service.BindMaterialAsync(ProjectId, ProjectBomHeaderKind.Master, wrongKind.Id, 0, "admin", UserRole.Administrator, default));
        await Assert.ThrowsAsync<PdmRuleException>(() => service.BindMaterialAsync(ProjectId, ProjectBomHeaderKind.Master, wrongCategory.Id, 0, "admin", UserRole.Administrator, default));

        var master = await service.BindMaterialAsync(ProjectId, ProjectBomHeaderKind.Master, approved.Id, 0, "admin", UserRole.Administrator, default);
        Assert.Equal("03020000003", master.MaterialCode);
        await Assert.ThrowsAsync<PdmRuleException>(() => service.BindMaterialAsync(ProjectId, ProjectBomHeaderKind.Standard, approved.Id, 0, "admin", UserRole.Administrator, default));
        var standard = await service.BindMaterialAsync(ProjectId, ProjectBomHeaderKind.Standard, childBom.Id, 0, "admin", UserRole.Administrator, default);
        Assert.Equal("0201", standard.CategoryCode);
        await Assert.ThrowsAsync<PdmConflictException>(() => service.BindMaterialAsync(ProjectId, ProjectBomHeaderKind.NonStandard, childBom.Id, 0, "admin", UserRole.Administrator, default));
    }

    [Fact]
    public async Task GenerateHierarchy_WithChild_CreatesOnlyRootMasterAndChildHeaders()
    {
        var service = CreateService(out var materials, out var repository, out var u9Client);
        var root = await repository.CreateNumberedProjectAsync(new(
            Guid.Parse("70000000-0000-0000-0000-000000000001"),
            "P",
            2,
            Guid.Parse("c0046500-0000-0000-0000-000000000001"),
            "测试主项目",
            null,
            new DateOnly(2026, 8, 22),
            1,
            "admin",
            @"D:\PDM\Vault",
            @"D:\PDM\Release"), default);
        await repository.CreateSubprojectAsync(new(root.Id, "测试子项目", null, 1), default);

        var first = await service.GenerateHierarchyMaterialsAsync(root.Id, "admin", UserRole.Administrator, default);

        Assert.Equal(5, first.ExpectedCount);
        Assert.Equal(5, first.GeneratedCount);
        Assert.Equal(0, first.ExistingCount);
        Assert.Equal(0, u9Client.AuthenticationCount);
        Assert.Equal(0, u9Client.ItemQueryCount);
        Assert.Equal(0, u9Client.UomQueryCount);
        Assert.Equal(0, u9Client.ReferenceQueryCount);
        Assert.Equal(8, first.Headers.Count);
        Assert.Equal(2, first.Headers.Count(header => header.Kind == ProjectBomHeaderKind.Master));
        Assert.All(first.Headers.Where(header => header.Kind == ProjectBomHeaderKind.Master), header => Assert.Equal("0302", header.CategoryCode));
        Assert.All(first.Headers.Where(header => header.Kind != ProjectBomHeaderKind.Master && header.MaterialId is not null), header => Assert.Equal("0201", header.CategoryCode));
        var rootHeaders = first.Headers.Where(header => header.ProjectId == root.Id).ToArray();
        Assert.NotNull(rootHeaders.Single(header => header.Kind == ProjectBomHeaderKind.Master).MaterialId);
        Assert.All(rootHeaders.Where(header => header.Kind != ProjectBomHeaderKind.Master), header => Assert.Null(header.MaterialId));
        Assert.All(first.Headers.Where(header => header.MaterialId is not null), header => Assert.Null(header.MaterialCode));
        Assert.All(first.Headers.Where(header => header.MaterialId is not null), header => Assert.Equal(MaterialApprovalStatus.Draft, header.ApprovalStatus));
        Assert.All(first.Headers.Where(header => header.MaterialId is not null), header => Assert.Equal(MaterialCodeApplicationStatus.Pending, header.ApplicationStatus));
        Assert.All(first.Headers.Where(header => header.MaterialId is not null), header => Assert.Equal("admin", header.RequestedBy));
        var applications = await materials.ListMaterialCodeApplicationsAsync(null, MaterialCodeApplicationStatus.Pending, default);
        Assert.Equal(5, applications.Count);
        Assert.All(applications, application => Assert.NotNull(application.BomHeaderKind));
        Assert.All(applications, application => Assert.Null(application.BomItemId));
        Assert.All(applications, application => Assert.Null(application.RequestedMaterialCode));
        Assert.DoesNotContain(applications, application => application.ProjectId == root.Id
            && application.BomHeaderKind != ProjectBomHeaderKind.Master);
        var pendingMaterials = await materials.ListMaterialsAsync(null, null, false, 100, default);
        Assert.Equal(5, pendingMaterials.Count);
        Assert.All(pendingMaterials, material => Assert.StartsWith("PDM-PENDING-", material.MaterialCode));
        Assert.Equal(5, pendingMaterials.Select(material => material.MaterialCode).Distinct().Count());

        var second = await service.GenerateHierarchyMaterialsAsync(root.Id, "admin", UserRole.Administrator, default);
        Assert.Equal(0, second.GeneratedCount);
        Assert.Equal(5, second.ExistingCount);
        Assert.Equal(first.Headers.Select(header => header.MaterialId), second.Headers.Select(header => header.MaterialId));
    }

    [Fact]
    public async Task BomApproval_AutomaticallyCreatesMasterAndApprovedCategoryApplicationsIdempotently()
    {
        var service = CreateService(out var materials, out _, out var u9Client);

        var standard = await service.EnsureApplicationsAfterBomApprovalAsync(
            ProjectId, ProjectBomHeaderKind.Standard, "reviewer", default);

        Assert.Equal(2, standard.ExpectedCount);
        Assert.Equal(2, standard.GeneratedCount);
        Assert.Equal(0, standard.ExistingCount);
        var firstApplications = await materials.ListMaterialCodeApplicationsAsync(
            ProjectId, MaterialCodeApplicationStatus.Pending, default);
        Assert.Equal(
            [ProjectBomHeaderKind.Master, ProjectBomHeaderKind.Standard],
            firstApplications.Select(application => application.BomHeaderKind).OrderBy(kind => kind).ToArray());
        Assert.All(firstApplications, application => Assert.Null(application.RequestedMaterialCode));

        var repeated = await service.EnsureApplicationsAfterBomApprovalAsync(
            ProjectId, ProjectBomHeaderKind.Standard, "reviewer", default);
        Assert.Equal(0, repeated.GeneratedCount);
        Assert.Equal(2, repeated.ExistingCount);

        var electrical = await service.EnsureApplicationsAfterBomApprovalAsync(
            ProjectId, ProjectBomHeaderKind.Electrical, "reviewer", default);
        Assert.Equal(1, electrical.GeneratedCount);
        Assert.Equal(1, electrical.ExistingCount);
        var allApplications = await materials.ListMaterialCodeApplicationsAsync(
            ProjectId, MaterialCodeApplicationStatus.Pending, default);
        Assert.Equal(3, allApplications.Count);
        Assert.Equal(0, u9Client.AuthenticationCount);
        Assert.Equal(0, u9Client.ItemQueryCount);
    }

    [Fact]
    public async Task BomApproval_OnChild_CreatesChildCategoryAndMasterPlusAncestorMasters()
    {
        var service = CreateService(out var materials, out var repository);
        var root = await repository.CreateNumberedProjectAsync(new(
            Guid.Parse("70000000-0000-0000-0000-000000000001"),
            "P", 2, Guid.Parse("c0046500-0000-0000-0000-000000000001"), "自动申请主项目", null,
            new DateOnly(2026, 9, 3), 1, "admin", @"D:\PDM\Vault", @"D:\PDM\Release"), default);
        var child = await repository.CreateSubprojectAsync(new(root.Id, "自动申请子项目", null, 1), default);

        var result = await service.EnsureApplicationsAfterBomApprovalAsync(
            child.Id, ProjectBomHeaderKind.Standard, "reviewer", default);

        Assert.Equal(root.Id, result.RootProjectId);
        Assert.Equal(3, result.ExpectedCount);
        Assert.Equal(3, result.GeneratedCount);
        var applications = await materials.ListMaterialCodeApplicationsAsync(
            null, MaterialCodeApplicationStatus.Pending, default);
        Assert.Contains(applications, application => application.ProjectId == child.Id && application.BomHeaderKind == ProjectBomHeaderKind.Standard);
        Assert.Contains(applications, application => application.ProjectId == child.Id && application.BomHeaderKind == ProjectBomHeaderKind.Master);
        Assert.Contains(applications, application => application.ProjectId == root.Id && application.BomHeaderKind == ProjectBomHeaderKind.Master);
        Assert.DoesNotContain(applications, application => application.ProjectId == root.Id && application.BomHeaderKind == ProjectBomHeaderKind.Standard);
    }

    [Fact]
    public async Task GenerateHierarchy_AfterChildAdded_AppliesOnlyForNewChildHeaders()
    {
        var service = CreateService(out var materials, out var repository);
        var root = await repository.CreateNumberedProjectAsync(new(
            Guid.Parse("70000000-0000-0000-0000-000000000001"),
            "P", 2, Guid.Parse("c0046500-0000-0000-0000-000000000001"), "增量主项目", null,
            new DateOnly(2026, 8, 22), 1, "admin", @"D:\PDM\Vault", @"D:\PDM\Release"), default);

        var initial = await service.GenerateHierarchyMaterialsAsync(root.Id, "admin", UserRole.Administrator, default);
        Assert.Equal(4, initial.GeneratedCount);
        var child = await repository.CreateSubprojectAsync(new(root.Id, "后增子项目", null, 1), default);

        var incremental = await service.GenerateHierarchyMaterialsAsync(root.Id, "admin", UserRole.Administrator, default);

        Assert.Equal(5, incremental.ExpectedCount);
        Assert.Equal(4, incremental.GeneratedCount);
        Assert.Equal(1, incremental.ExistingCount);
        var applications = await materials.ListMaterialCodeApplicationsAsync(null, MaterialCodeApplicationStatus.Pending, default);
        Assert.Equal(8, applications.Count);
        Assert.Equal(4, applications.Count(application => application.ProjectId == child.Id));
        Assert.Equal(4, applications.Count(application => application.ProjectId == root.Id));
    }

    [Fact]
    public async Task GenerateHierarchy_ReplacesBindingWhoseMaterialNoLongerExists()
    {
        var service = CreateService(out _, out var repository);
        var missingMaterialId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        await repository.SaveProjectBomHeaderBindingAsync(
            ProjectId, ProjectBomHeaderKind.Master, missingMaterialId, 0, "admin", default);

        var result = await service.GenerateHierarchyMaterialsAsync(ProjectId, "admin", UserRole.Administrator, default);

        Assert.Equal(4, result.ExpectedCount);
        Assert.Equal(4, result.GeneratedCount);
        Assert.Equal(0, result.ExistingCount);
        Assert.All(result.Headers, header => Assert.NotNull(header.MaterialId));
        Assert.All(result.Headers, header => Assert.Null(header.MaterialCode));
        Assert.NotEqual(missingMaterialId, result.Headers.Single(header => header.Kind == ProjectBomHeaderKind.Master).MaterialId);
    }

    [Fact]
    public async Task ScopedRelease_RequiresMasterAndOnlyTheTargetBomHeader()
    {
        var service = CreateService(out var materials, out _);
        var master = await AddMaterial(materials, MaterialKind.Product, MaterialApprovalStatus.Approved, "0302", "PDM-0302-10", "03020000010", true);
        var standard = await AddMaterial(materials, MaterialKind.Product, MaterialApprovalStatus.Approved, "0201", "PDM-0201-11", "02010000011", true);
        await service.BindMaterialAsync(ProjectId, ProjectBomHeaderKind.Master, master.Id, 0, "admin", UserRole.Administrator, default);
        await service.BindMaterialAsync(ProjectId, ProjectBomHeaderKind.Standard, standard.Id, 0, "admin", UserRole.Administrator, default);

        await service.EnsureReleaseReadyAsync(ProjectId, ReleaseScope.StandardFormal, default);
        var exception = await Assert.ThrowsAsync<PdmRuleException>(() => service.EnsureReleaseReadyAsync(ProjectId, ReleaseScope.ElectricalFormal, default));
        Assert.Contains("电气BOM", exception.Message);
    }

    [Fact]
    public async Task ScopedRelease_RejectsApprovedHeaderUntilU9ReturnsOfficialCode()
    {
        var service = CreateService(out var materials, out _);
        var master = await AddMaterial(materials, MaterialKind.Product, MaterialApprovalStatus.Approved, "0302", "PDM-PREDICTED-MASTER");
        var standard = await AddMaterial(materials, MaterialKind.Product, MaterialApprovalStatus.Approved, "0201", "PDM-PREDICTED-STANDARD");
        await service.BindMaterialAsync(ProjectId, ProjectBomHeaderKind.Master, master.Id, 0, "admin", UserRole.Administrator, default);
        await service.BindMaterialAsync(ProjectId, ProjectBomHeaderKind.Standard, standard.Id, 0, "admin", UserRole.Administrator, default);

        var exception = await Assert.ThrowsAsync<PdmRuleException>(() =>
            service.EnsureReleaseReadyAsync(ProjectId, ReleaseScope.StandardFormal, default));

        Assert.Contains("料号仍在申请中", exception.Message);
        Assert.Contains("U9C正式料号", exception.Message);
    }

    private static BomHeaderService CreateService(out InMemoryMaterialRepository materials, out InMemoryPdmRepository repository)
        => CreateService(out materials, out repository, out _);

    private static BomHeaderService CreateService(
        out InMemoryMaterialRepository materials,
        out InMemoryPdmRepository repository,
        out AvailableCodeClient u9Client)
    {
        var time = TimeProvider.System;
        repository = new InMemoryPdmRepository(time);
        materials = new InMemoryMaterialRepository(time);
        materials.SaveIntegrationConfigurationAsync(new(
            "http://u9.example.test/U9", "01", "7", "pdm", "PDM", "protected:test-secret",
            U9MaterialContract.CreatePath, U9MaterialContract.QueryPath, false, "admin", time.GetUtcNow(),
            UnitCodeMappings: new Dictionary<string, string>()), default).GetAwaiter().GetResult();
        u9Client = new AvailableCodeClient();
        var materialService = new MaterialService(materials, repository, new TestProtector(), u9Client, time);
        return new BomHeaderService(repository, materials, materialService, time);
    }

    [Fact]
    public async Task List_UsesOnlyU9ConfirmedCodeAsOfficialBomNumber()
    {
        var service = CreateService(out var materials, out _);
        var pending = await AddMaterial(materials, MaterialKind.Product, MaterialApprovalStatus.Approved, "0302", "PDM-PREDICTED-001");
        await service.BindMaterialAsync(ProjectId, ProjectBomHeaderKind.Master, pending.Id, 0, "admin", UserRole.Administrator, default);

        var pendingHeader = (await service.ListAsync(ProjectId, "admin", UserRole.Administrator, default)).Single(header => header.Kind == ProjectBomHeaderKind.Master);

        Assert.NotNull(pendingHeader.MaterialId);
        Assert.Null(pendingHeader.MaterialCode);
    }

    [Fact]
    public async Task List_HidesConfirmedU9CodeWhileLatestHeaderApplicationIsPending()
    {
        var service = CreateService(out var materials, out var repository);
        var material = await AddMaterial(materials, MaterialKind.Product, MaterialApprovalStatus.Approved,
            "0302", "PDM-0302-13", "03020000013", true);
        await repository.SaveProjectBomHeaderBindingAsync(
            ProjectId, ProjectBomHeaderKind.Master, material.Id, 0, "admin", default);
        var application = await materials.CreateMaterialCodeApplicationAsync(new(
            Guid.NewGuid(), ProjectId, null, MaterialCodeApplicationStatus.Pending, "developer", DateTimeOffset.UtcNow,
            null, null, null, material.Id, null, 1, ProjectBomHeaderKind.Master), default);

        var header = (await service.ListAsync(ProjectId, "admin", UserRole.Administrator, default))
            .Single(item => item.Kind == ProjectBomHeaderKind.Master);

        Assert.Equal(application.Id, header.ApplicationId);
        Assert.Equal(MaterialCodeApplicationStatus.Pending, header.ApplicationStatus);
        Assert.Equal(material.Id, header.MaterialId);
        Assert.Null(header.MaterialCode);
    }

    [Fact]
    public async Task ProjectBomU9Sync_CreatesEmptyBomBeforeApprovalAndSyncsReleasedComponentsAfterApproval()
    {
        var time = TimeProvider.System;
        var repository = new InMemoryPdmRepository(time);
        var materials = new InMemoryMaterialRepository(time);
        await materials.SaveIntegrationConfigurationAsync(new(
            "http://u9.example.test/U9", "01", "7", "pdm", "PDM", "protected:test-secret",
            U9MaterialContract.CreatePath, U9MaterialContract.QueryPath, true, "admin", time.GetUtcNow()), default);
        var header = await AddMaterial(materials, MaterialKind.Product, MaterialApprovalStatus.Approved, "0201", "02010000101", "02010000101", true);
        var component = await AddMaterial(materials, MaterialKind.Standard, MaterialApprovalStatus.Approved, "0102", "01020000057", "01020000057", true);
        await repository.SaveProjectBomHeaderBindingAsync(
            ProjectId, ProjectBomHeaderKind.Standard, header.Id, 0, "admin", default);
        var workingItem = new BomItem(
            Guid.NewGuid(), ProjectId, BomKind.Standard, 1, component.MaterialCode, "阀岛", 2, "001", null, "10P", "A", true);
        await repository.ReplaceBomAsync(ProjectId, BomKind.Standard, [workingItem], default);

        var client = new ApprovalAutomationClient();
        var u9 = new U9BomWriteService(materials, repository, client, client, new TestProtector(), time);
        var service = new ProjectBomU9SyncService(repository, materials, u9, time);

        var preview = await service.PreviewAsync(ProjectId, ProjectBomHeaderKind.Standard, "admin", UserRole.Administrator, default);
        Assert.Equal(ProjectBomU9SyncState.CreateRequired, preview.State);
        Assert.Equal("02010000101", preview.ItemCode);
        Assert.Equal(0, preview.ComponentCount);
        Assert.NotNull(preview.WritePreview);
        using (var payload = JsonDocument.Parse(preview.WritePreview.RequestPreview))
            Assert.Empty(payload.RootElement[0].GetProperty("BOMComponents").EnumerateArray());

        var execution = await service.ExecuteAsync(
            ProjectId, ProjectBomHeaderKind.Standard,
            preview.WritePreview!.RequestSha256, preview.WritePreview.RequiredConfirmation,
            "admin", UserRole.Administrator, default);
        Assert.Equal("02010000101", execution.Execution.Verification.Boms.Single().ItemCode);
        Assert.Empty(execution.Execution.Verification.Boms.Single().Components);

        var awaitingApproval = await service.PreviewAsync(ProjectId, ProjectBomHeaderKind.Standard, "admin", UserRole.Administrator, default);
        Assert.Equal(ProjectBomU9SyncState.AwaitingApproval, awaitingApproval.State);
        Assert.Null(awaitingApproval.WritePreview);

        var released = await repository.SaveBomDraftAsync(ProjectId, BomKind.Standard, [workingItem], "reviewer", default);
        await repository.SetBomVersionStateAsync([released.Id], BomVersionState.Released, "reviewer", time.GetUtcNow(), default);
        var approvedPreview = await service.PreviewAsync(ProjectId, ProjectBomHeaderKind.Standard, "admin", UserRole.Administrator, default);
        Assert.Equal(ProjectBomU9SyncState.ModifyRequired, approvedPreview.State);
        Assert.Equal(1, approvedPreview.ComponentCount);
        Assert.Equal(1, approvedPreview.WritePreview!.AddedComponentCount);

        await service.ExecuteAsync(
            ProjectId, ProjectBomHeaderKind.Standard,
            approvedPreview.WritePreview.RequestSha256, approvedPreview.WritePreview.RequiredConfirmation,
            "admin", UserRole.Administrator, default);
        var verified = await service.PreviewAsync(ProjectId, ProjectBomHeaderKind.Standard, "admin", UserRole.Administrator, default);
        Assert.Equal(ProjectBomU9SyncState.UpToDate, verified.State);
        Assert.Equal(0, verified.WritePreview!.AddedComponentCount);
    }

    [Fact]
    public async Task ProjectBomU9Sync_UsesPublishedLongLeadTotalsUntilFormalReleaseBecomesAuthoritative()
    {
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-03T08:00:00Z"));
        var repository = new InMemoryPdmRepository(time);
        var materials = new InMemoryMaterialRepository(time);
        await materials.SaveIntegrationConfigurationAsync(new(
            "http://u9.example.test/U9", "01", "7", "pdm", "PDM", "protected:test-secret",
            U9MaterialContract.CreatePath, U9MaterialContract.QueryPath, true, "admin", time.GetUtcNow()), default);
        var header = await AddMaterial(materials, MaterialKind.Product, MaterialApprovalStatus.Approved,
            "0201", "02010000101", "02010000101", true);
        var component = await AddMaterial(materials, MaterialKind.Standard, MaterialApprovalStatus.Approved,
            "0102", "01020000057", "01020000057", true);
        await repository.SaveProjectBomHeaderBindingAsync(
            ProjectId, ProjectBomHeaderKind.Standard, header.Id, 0, "admin", default);
        var item = new BomItem(
            Guid.NewGuid(), ProjectId, BomKind.Standard, 1, component.MaterialCode, "阀岛", 4, "001",
            null, "10P", "W1", true);
        await repository.CreateReleasePackageAsync(new ReleasePackage(
            Guid.NewGuid(), ProjectId, "RP-LL-1", ReleasePackageState.Published, Guid.NewGuid(), "LL1", "LL1",
            [], time.GetUtcNow().AddHours(-2), time.GetUtcNow().AddHours(-2), "C:\\PDM\\Release\\LL1")
        {
            Scope = ReleaseScope.StandardLongLead,
            StandardBomSnapshot = [item with { Quantity = 1 }]
        }, default);
        await repository.CreateReleasePackageAsync(new ReleasePackage(
            Guid.NewGuid(), ProjectId, "RP-LL-2", ReleasePackageState.Published, Guid.NewGuid(), "LL2", "LL2",
            [], time.GetUtcNow().AddHours(-1), time.GetUtcNow().AddHours(-1), "C:\\PDM\\Release\\LL2")
        {
            Scope = ReleaseScope.StandardLongLead,
            StandardBomSnapshot = [item with { Quantity = 2 }]
        }, default);
        var client = new ApprovalAutomationClient();
        var service = new ProjectBomU9SyncService(repository, materials,
            new U9BomWriteService(materials, repository, client, client, new TestProtector(), time), time);

        var longLead = await service.PreviewAsync(ProjectId, ProjectBomHeaderKind.Standard, "admin", UserRole.Administrator, default);

        Assert.Equal(1, longLead.ComponentCount);
        using (var payload = JsonDocument.Parse(longLead.WritePreview!.RequestPreview))
            Assert.Equal(3, payload.RootElement[0].GetProperty("BOMComponents")[0].GetProperty("UsageQty").GetDecimal());
        await service.ExecuteAsync(
            ProjectId, ProjectBomHeaderKind.Standard,
            longLead.WritePreview.RequestSha256, longLead.WritePreview.RequiredConfirmation,
            "admin", UserRole.Administrator, default);

        await repository.CreateReleasePackageAsync(new ReleasePackage(
            Guid.NewGuid(), ProjectId, "RP-LL-3", ReleasePackageState.Published, Guid.NewGuid(), "LL3", "LL3",
            [], time.GetUtcNow(), time.GetUtcNow(), "C:\\PDM\\Release\\LL3")
        {
            Scope = ReleaseScope.StandardLongLead,
            StandardBomSnapshot = [item with { Quantity = 1 }]
        }, default);
        var incremental = await service.PreviewAsync(ProjectId, ProjectBomHeaderKind.Standard, "admin", UserRole.Administrator, default);
        Assert.Equal(ProjectBomU9SyncState.ModifyRequired, incremental.State);
        Assert.Equal(1, incremental.WritePreview!.AddedComponentCount);
        using (var incrementalPayload = JsonDocument.Parse(incremental.WritePreview.RequestPreview))
            Assert.Equal(1, incrementalPayload.RootElement[0].GetProperty("BOMComponents")[0].GetProperty("UsageQty").GetDecimal());
        await service.ExecuteAsync(
            ProjectId, ProjectBomHeaderKind.Standard,
            incremental.WritePreview.RequestSha256, incremental.WritePreview.RequiredConfirmation,
            "admin", UserRole.Administrator, default);
        Assert.Equal(ProjectBomU9SyncState.UpToDate,
            (await service.PreviewAsync(ProjectId, ProjectBomHeaderKind.Standard, "admin", UserRole.Administrator, default)).State);

        var formal = await repository.SaveBomDraftAsync(ProjectId, BomKind.Standard, [item], "reviewer", default);
        await repository.SetBomVersionStateAsync([formal.Id], BomVersionState.Released, "reviewer", time.GetUtcNow(), default);
        var authoritative = await service.PreviewAsync(ProjectId, ProjectBomHeaderKind.Standard, "admin", UserRole.Administrator, default);
        Assert.Equal(ProjectBomU9SyncState.UpToDate, authoritative.State);
    }

    [Fact]
    public async Task ProjectBomU9Sync_MasterDependsOnCategoryHeaderInsteadOfCategoryItems()
    {
        var time = TimeProvider.System;
        var repository = new InMemoryPdmRepository(time);
        var materials = new InMemoryMaterialRepository(time);
        await materials.SaveIntegrationConfigurationAsync(new(
            "http://u9.example.test/U9", "01", "7", "pdm", "PDM", "protected:test-secret",
            U9MaterialContract.CreatePath, U9MaterialContract.QueryPath, true, "admin", time.GetUtcNow()), default);
        var master = await AddMaterial(materials, MaterialKind.Product, MaterialApprovalStatus.Approved,
            "0302", "03020000009", "03020000009", true);
        var standard = await AddMaterial(materials, MaterialKind.Product, MaterialApprovalStatus.Approved,
            "0201", "02010000101", "02010000101", true);
        await repository.SaveProjectBomHeaderBindingAsync(
            ProjectId, ProjectBomHeaderKind.Master, master.Id, 0, "admin", default);
        await repository.SaveProjectBomHeaderBindingAsync(
            ProjectId, ProjectBomHeaderKind.Standard, standard.Id, 0, "admin", default);
        await repository.ReplaceBomAsync(ProjectId, BomKind.NonStandard, [], default);
        await repository.ReplaceBomAsync(ProjectId, BomKind.Electrical, [], default);
        var releasedStandardItem = new BomItem(
            Guid.NewGuid(), ProjectId, BomKind.Standard, 1, "PDM-PENDING-ITEM", "待审批子件", 1, "001",
            null, null, "A1", false) { IsPendingClassification = true };
        await repository.ReplaceBomAsync(ProjectId, BomKind.Standard, [releasedStandardItem], default);
        var releasedStandard = await repository.SaveBomDraftAsync(ProjectId, BomKind.Standard, [releasedStandardItem], "reviewer", default);
        await repository.SetBomVersionStateAsync([releasedStandard.Id], BomVersionState.Released, "reviewer", time.GetUtcNow(), default);
        var client = new ApprovalAutomationClient();
        var service = new ProjectBomU9SyncService(repository, materials,
            new U9BomWriteService(materials, repository, client, client, new TestProtector(), time), time);

        var preview = await service.PreviewAsync(
            ProjectId, ProjectBomHeaderKind.Master, "admin", UserRole.Administrator, default);

        Assert.Equal(ProjectBomU9SyncState.CreateRequired, preview.State);
        Assert.Equal(1, preview.ComponentCount);
        using var payload = JsonDocument.Parse(preview.WritePreview!.RequestPreview);
        var row = payload.RootElement[0];
        Assert.Equal("02010000101", row.GetProperty("BOMComponents")[0].GetProperty("ItemMaster").GetProperty("Code").GetString());
        var expectedEffectiveDate = DateOnly.FromDateTime(time.GetLocalNow().DateTime).ToString("yyyy-MM-dd");
        Assert.Equal(expectedEffectiveDate, row.GetProperty("EffectiveDate").GetString());
        Assert.Equal(expectedEffectiveDate, row.GetProperty("BOMComponents")[0].GetProperty("EffectiveDate").GetString());
        Assert.Equal(0, client.BomWriteCount);
    }

    [Fact]
    public async Task ProjectBomU9Sync_RootWithChildrenUsesChildRootsAndAppendsOnlyNewChild()
    {
        var time = TimeProvider.System;
        var repository = new InMemoryPdmRepository(time);
        var materials = new InMemoryMaterialRepository(time);
        var root = await repository.CreateNumberedProjectAsync(new(
            Guid.Parse("70000000-0000-0000-0000-000000000001"),
            "P", 2, Guid.Parse("c0046500-0000-0000-0000-000000000001"), "U9层级主项目", null,
            new DateOnly(2026, 8, 22), 1, "admin", @"D:\PDM\Vault", @"D:\PDM\Release"), default);
        await materials.SaveIntegrationConfigurationAsync(new(
            "http://u9.example.test/U9", "01", "7", "pdm", "PDM", "protected:test-secret",
            U9MaterialContract.CreatePath, U9MaterialContract.QueryPath, true, "admin", time.GetUtcNow()), default);
        var rootMaster = await AddMaterial(materials, MaterialKind.Product, MaterialApprovalStatus.Approved,
            "0302", "03020000009", "03020000009", true);
        var rootStandard = await AddMaterial(materials, MaterialKind.Product, MaterialApprovalStatus.Approved,
            "0201", "02010000100", "02010000100", true);
        await repository.SaveProjectBomHeaderBindingAsync(root.Id, ProjectBomHeaderKind.Master, rootMaster.Id, 0, "admin", default);
        await repository.SaveProjectBomHeaderBindingAsync(root.Id, ProjectBomHeaderKind.Standard, rootStandard.Id, 0, "admin", default);
        await ReleaseCategoryAsync(repository, root.Id, BomKind.Standard, "ROOT-ITEM", time);

        var firstChild = await repository.CreateSubprojectAsync(new(root.Id, "泵组单元", null, 1), default);
        var firstChildMaster = await AddMaterial(materials, MaterialKind.Product, MaterialApprovalStatus.Approved,
            "0302", "03020000010", "03020000010", true);
        var firstChildStandard = await AddMaterial(materials, MaterialKind.Product, MaterialApprovalStatus.Approved,
            "0201", "02010000101", "02010000101", true);
        await repository.SaveProjectBomHeaderBindingAsync(firstChild.Id, ProjectBomHeaderKind.Master, firstChildMaster.Id, 0, "admin", default);
        await repository.SaveProjectBomHeaderBindingAsync(firstChild.Id, ProjectBomHeaderKind.Standard, firstChildStandard.Id, 0, "admin", default);
        await ReleaseCategoryAsync(repository, firstChild.Id, BomKind.Standard, "CHILD-1-ITEM", time);

        var client = new ApprovalAutomationClient();
        var service = new ProjectBomU9SyncService(repository, materials,
            new U9BomWriteService(materials, repository, client, client, new TestProtector(), time), time);

        var childPreview = await service.PreviewAsync(firstChild.Id, ProjectBomHeaderKind.Master, "admin", UserRole.Administrator, default);
        Assert.Equal(1, childPreview.ComponentCount);
        using (var childPayload = JsonDocument.Parse(childPreview.WritePreview!.RequestPreview))
            Assert.Equal("02010000101", childPayload.RootElement[0].GetProperty("BOMComponents")[0].GetProperty("ItemMaster").GetProperty("Code").GetString());

        var initial = await service.PreviewAsync(root.Id, ProjectBomHeaderKind.Master, "admin", UserRole.Administrator, default);
        Assert.Equal(ProjectBomU9SyncState.CreateRequired, initial.State);
        Assert.Equal(1, initial.ComponentCount);
        using (var initialPayload = JsonDocument.Parse(initial.WritePreview!.RequestPreview))
        {
            var componentCodes = initialPayload.RootElement[0].GetProperty("BOMComponents").EnumerateArray()
                .Select(component => component.GetProperty("ItemMaster").GetProperty("Code").GetString())
                .ToArray();
            Assert.Equal("03020000010", Assert.Single(componentCodes));
            Assert.DoesNotContain("02010000100", componentCodes);
        }
        await service.ExecuteAsync(root.Id, ProjectBomHeaderKind.Master,
            initial.WritePreview.RequestSha256, initial.WritePreview.RequiredConfirmation,
            "admin", UserRole.Administrator, default);

        var secondChild = await repository.CreateSubprojectAsync(new(root.Id, "回收单元", null, 1), default);
        var secondChildMaster = await AddMaterial(materials, MaterialKind.Product, MaterialApprovalStatus.Approved,
            "0302", "03020000011", "03020000011", true);
        await repository.SaveProjectBomHeaderBindingAsync(secondChild.Id, ProjectBomHeaderKind.Master, secondChildMaster.Id, 0, "admin", default);
        await ReleaseCategoryAsync(repository, secondChild.Id, BomKind.Standard, "CHILD-2-ITEM", time);

        var incremental = await service.PreviewAsync(root.Id, ProjectBomHeaderKind.Master, "admin", UserRole.Administrator, default);

        Assert.Equal(ProjectBomU9SyncState.ModifyRequired, incremental.State);
        Assert.Equal(2, incremental.ComponentCount);
        Assert.Equal(1, incremental.WritePreview!.AddedComponentCount);
    }

    [Fact]
    public async Task ApprovalAutomation_WaitsForFirstReleasedComponentThenCreatesA1Bom()
    {
        var time = TimeProvider.System;
        var repository = new InMemoryPdmRepository(time);
        var materials = new InMemoryMaterialRepository(time);
        await materials.SaveIntegrationConfigurationAsync(new(
            "http://u9.example.test/U9", "01", "7", "pdm", "PDM", "protected:test-secret",
            U9MaterialContract.CreatePath, U9MaterialContract.QueryPath, true, "admin", time.GetUtcNow()), default);
        var client = new ApprovalAutomationClient();
        var materialService = new MaterialService(materials, repository, new TestProtector(), client, time);
        var header = await materialService.CreateAsync(new(
            null, "P700001 标准件BOM", MaterialKind.Product, MaterialSupplyMode.Manufacture, "001",
            null, null, "气密设备 · 标准件BOM", null, null, null, null, CategoryCode: "0201"),
            "engineer", UserRole.Administrator, default);
        var component = await AddMaterial(materials, MaterialKind.Standard, MaterialApprovalStatus.Approved,
            "0102", "01020000057", "01020000057", true);
        await repository.SaveProjectBomHeaderBindingAsync(
            ProjectId, ProjectBomHeaderKind.Standard, header.Id, 0, "engineer", default);
        var workingItem = new BomItem(
            Guid.NewGuid(), ProjectId, BomKind.Standard, 1, component.MaterialCode, "阀岛", 2, "001",
            null, "10P", "A", true);
        await repository.ReplaceBomAsync(ProjectId, BomKind.Standard, [workingItem], default);
        var application = await materials.CreateMaterialCodeApplicationAsync(new(
            Guid.NewGuid(), ProjectId, null, MaterialCodeApplicationStatus.Pending, "engineer", time.GetUtcNow(),
            null, null, null, header.Id, null, 1, ProjectBomHeaderKind.Standard), default);

        var decision = await materialService.DecideMaterialCodeApplicationAsync(
            application.Id, application.RowVersion, true, "同意", "standardizer", UserRole.ProcessReviewer, default);
        var itemIntegration = new U9MaterialIntegrationService(materials, repository, new TestProtector(), client, time);
        var bomWrite = new U9BomWriteService(materials, repository, client, client, new TestProtector(), time);
        var projectBomSync = new ProjectBomU9SyncService(repository, materials, bomWrite, time);
        var automation = new ApprovalU9AutomationService(materials, repository, itemIntegration, projectBomSync, time);

        var result = await automation.RunAfterApprovalAsync(
            decision.Application, decision.Material, decision.Task, "standardizer", default);

        Assert.Equal(ApprovalU9AutomationStage.WaitingForDependencies, result.Stage);
        Assert.Equal(MaterialSyncStatus.Succeeded, result.ItemSync?.Task.Status);
        Assert.True(result.ItemSync?.Material.U9SyncConfirmed);
        Assert.Equal(ProjectBomU9AutomaticState.WaitingForDependencies, Assert.Single(result.Boms).State);
        Assert.Equal(1, client.ItemWriteCount);
        Assert.Equal(0, client.BomWriteCount);

        var repeated = await automation.ContinueAfterMaterialSyncAsync(ProjectId, "standardizer", default);
        Assert.Equal(ApprovalU9AutomationStage.WaitingForDependencies, repeated.Stage);
        Assert.Equal(ProjectBomU9AutomaticState.WaitingForDependencies, Assert.Single(repeated.Boms).State);
        Assert.Equal(0, client.BomWriteCount);

        var released = await repository.SaveBomDraftAsync(ProjectId, BomKind.Standard, [workingItem], "reviewer", default);
        await repository.SetBomVersionStateAsync([released.Id], BomVersionState.Released, "reviewer", time.GetUtcNow(), default);
        var afterBomApproval = await automation.ContinueAfterMaterialSyncAsync(ProjectId, "reviewer", default);
        Assert.Equal(ApprovalU9AutomationStage.Completed, afterBomApproval.Stage);
        Assert.Equal(ProjectBomU9AutomaticState.Created, Assert.Single(afterBomApproval.Boms).State);
        Assert.Equal(1, client.BomWriteCount);
        var expectedEffectiveDate = DateOnly.FromDateTime(time.GetLocalNow().DateTime).ToString("yyyy-MM-dd");
        Assert.Equal(expectedEffectiveDate, client.LastBomEffectiveDate);
        Assert.All(client.LastComponentEffectiveDates, value => Assert.Equal(expectedEffectiveDate, value));
    }

    [Fact]
    public async Task ApprovalAutomation_WaitsWithoutWritingWhenAComponentHasNoOfficialU9Code()
    {
        var time = TimeProvider.System;
        var repository = new InMemoryPdmRepository(time);
        var materials = new InMemoryMaterialRepository(time);
        await materials.SaveIntegrationConfigurationAsync(new(
            "http://u9.example.test/U9", "01", "7", "pdm", "PDM", "protected:test-secret",
            U9MaterialContract.CreatePath, U9MaterialContract.QueryPath, true, "admin", time.GetUtcNow()), default);
        var header = await AddMaterial(materials, MaterialKind.Product, MaterialApprovalStatus.Approved,
            "0201", "02010000101", "02010000101", true);
        var pendingComponent = await AddMaterial(materials, MaterialKind.Standard, MaterialApprovalStatus.Approved,
            "0102", "01020000057");
        await repository.SaveProjectBomHeaderBindingAsync(
            ProjectId, ProjectBomHeaderKind.Standard, header.Id, 0, "engineer", default);
        var pendingBomItem = new BomItem(
            Guid.NewGuid(), ProjectId, BomKind.Standard, 1, pendingComponent.MaterialCode, "阀岛", 2, "001",
            null, "10P", "A", true);
        await repository.ReplaceBomAsync(ProjectId, BomKind.Standard, [pendingBomItem], default);
        var released = await repository.SaveBomDraftAsync(
            ProjectId, BomKind.Standard, [pendingBomItem], "reviewer", default);
        await repository.SetBomVersionStateAsync(
            [released.Id], BomVersionState.Released, "reviewer", time.GetUtcNow(), default);
        var client = new ApprovalAutomationClient();
        var service = new ProjectBomU9SyncService(repository, materials,
            new U9BomWriteService(materials, repository, client, client, new TestProtector(), time), time);

        var result = await service.SynchronizeApprovedAsync(
            ProjectId, ProjectBomHeaderKind.Standard, "standardizer", default);

        Assert.Equal(ProjectBomU9AutomaticState.WaitingForDependencies, result.State);
        Assert.Contains("尚未取得U9C正式料号", result.Message);
        Assert.Equal(0, client.BomWriteCount);
    }

    private static async Task ReleaseCategoryAsync(
        InMemoryPdmRepository repository,
        Guid projectId,
        BomKind kind,
        string drawingNumber,
        TimeProvider time)
    {
        var item = new BomItem(
            Guid.NewGuid(), projectId, kind, 1, drawingNumber, drawingNumber, 1, "001",
            null, null, "A1", true);
        await repository.ReplaceBomAsync(projectId, kind, [item], default);
        var version = await repository.SaveBomDraftAsync(projectId, kind, [item], "reviewer", default);
        await repository.SetBomVersionStateAsync([version.Id], BomVersionState.Released, "reviewer", time.GetUtcNow(), default);
    }

    private static async Task<PdmMaterial> AddMaterial(InMemoryMaterialRepository repository, MaterialKind kind, MaterialApprovalStatus approval, string categoryCode, string code, string? u9ItemCode = null, bool u9SyncConfirmed = false)
    {
        var category = await repository.FindCategoryAsync(categoryCode, default) ?? throw new InvalidOperationException();
        var now = DateTimeOffset.UtcNow;
        var material = new PdmMaterial(
            Guid.NewGuid(), code, $"料品{code}", kind, MaterialSupplyMode.Manufacture, "001", null, null, null, null, null,
            null, null, null, approval, approval == MaterialApprovalStatus.Approved ? "admin" : null,
            approval == MaterialApprovalStatus.Approved ? now : null, categoryCode, null, u9ItemCode, MaterialSyncStatus.NotQueued,
            "admin", now, "admin", now, 1, categoryCode, U9SyncConfirmed: u9SyncConfirmed);
        return await repository.CreateMaterialAsync(material, category, default);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TestProtector : IU9SecretProtector
    {
        public string Protect(string secret) => $"protected:{secret}";
        public string Unprotect(string ciphertext) => ciphertext[10..];
    }

    private sealed class AvailableCodeClient : IU9OpenApiClient
    {
        public int AuthenticationCount { get; private set; }
        public int ItemQueryCount { get; private set; }
        public int UomQueryCount { get; private set; }
        public int ReferenceQueryCount { get; private set; }

        public Task<U9AuthenticationResult> AuthenticateAsync(U9AuthenticationRequest request, CancellationToken cancellationToken)
        {
            AuthenticationCount++;
            return Task.FromResult(new U9AuthenticationResult("token"));
        }

        public Task<U9ItemQueryResult> QueryItemsAsync(string baseUrl, string path, string token, string payloadJson, CancellationToken cancellationToken)
        {
            ItemQueryCount++;
            return Task.FromResult(new U9ItemQueryResult(0, null, []));
        }

        public Task<U9UomQueryResult> QueryUomsAsync(string baseUrl, string token, string payloadJson, CancellationToken cancellationToken)
        {
            UomQueryCount++;
            return Task.FromResult(new U9UomQueryResult(0, null, [new U9UomReference("uom-001", "001")]));
        }

        public Task<U9BusinessBatchResult> PostBatchAsync(string baseUrl, string path, string token, string payloadJson, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<U9CustomerQueryResult> QueryCustomerReferencesAsync(string baseUrl, string path, string token, string payloadJson, CancellationToken cancellationToken)
        {
            ReferenceQueryCount++;
            return Task.FromResult(new U9CustomerQueryResult(0, null, [], 0));
        }
    }

    private sealed class BomWriteState
    {
        public bool Exists { get; set; }

        public U9BomReference Bom => new(
            null, "02010000101", "P700001 标准件BOM", "A1", "7", "昆山工厂", 0, 1,
            "001", "个", null, null, 0, 0, 0, "P700001", OwnershipExplain("02010000101", "A1"),
            null, false, 0, 0,
            [new(10, null, "01020000057", "阀岛", null, 2, "001", "个", 1, 0, true, null, null, null, null, 0, 0, false, false)]);

        private static string OwnershipExplain(string itemCode, string versionCode)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{itemCode}|{versionCode}"));
            var marker = $"pdm-bom-{Convert.ToHexString(bytes)[..24].ToLowerInvariant()}";
            return $"[PDM:{marker}]";
        }
    }

    private sealed class BomQueryClient(BomWriteState state) : IU9BomQueryClient
    {
        public Task<U9AuthenticationResult> AuthenticateAsync(U9AuthenticationRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new U9AuthenticationResult("token"));

        public Task<U9BomQueryResult> QueryBomsAsync(string baseUrl, string path, string token, string payloadJson, CancellationToken cancellationToken) =>
            Task.FromResult(new U9BomQueryResult(0, null, state.Exists ? [state.Bom] : []));

        public Task<U9BomOperationReference?> QueryBomOperationAsync(string baseUrl, string path, string token, string organizationCode, string itemCode, string bomVersionCode, string otherId, CancellationToken cancellationToken) =>
            Task.FromResult<U9BomOperationReference?>(null);
    }

    private sealed class BomWriteClient(BomWriteState state) : IU9OpenApiClient
    {
        public Task<U9AuthenticationResult> AuthenticateAsync(U9AuthenticationRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new U9AuthenticationResult("token"));

        public Task<U9BusinessBatchResult> PostBatchAsync(string baseUrl, string path, string token, string payloadJson, CancellationToken cancellationToken)
        {
            state.Exists = true;
            return Task.FromResult(new U9BusinessBatchResult(0, null, [new(true, null, null, "02010000101")]));
        }

        public Task<U9ItemQueryResult> QueryItemsAsync(string baseUrl, string path, string token, string payloadJson, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<U9UomQueryResult> QueryUomsAsync(string baseUrl, string token, string payloadJson, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<U9CustomerQueryResult> QueryCustomerReferencesAsync(string baseUrl, string path, string token, string payloadJson, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class ApprovalAutomationClient : IU9OpenApiClient, IU9BomQueryClient
    {
        private U9BomReference? bom;
        private U9ItemReference? createdItem;

        public int ItemWriteCount { get; private set; }
        public int BomWriteCount { get; private set; }
        public string? LastBomEffectiveDate { get; private set; }
        public IReadOnlyList<string?> LastComponentEffectiveDates { get; private set; } = [];

        public Task<U9AuthenticationResult> AuthenticateAsync(U9AuthenticationRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new U9AuthenticationResult("token"));

        public Task<U9ItemQueryResult> QueryItemsAsync(string baseUrl, string path, string token, string payloadJson, CancellationToken cancellationToken) =>
            Task.FromResult(new U9ItemQueryResult(0, null, createdItem is null ? [] : [createdItem]));

        public Task<U9UomQueryResult> QueryUomsAsync(string baseUrl, string token, string payloadJson, CancellationToken cancellationToken) =>
            Task.FromResult(new U9UomQueryResult(0, null, [new U9UomReference("uom-001", "001")]));

        public Task<U9BusinessBatchResult> PostBatchAsync(string baseUrl, string path, string token, string payloadJson, CancellationToken cancellationToken)
        {
            using var document = JsonDocument.Parse(payloadJson);
            var row = document.RootElement[0];
            if (string.Equals(path, U9MaterialContract.CreatePath, StringComparison.OrdinalIgnoreCase))
            {
                ItemWriteCount++;
                var itemCode = row.GetProperty("Code").GetString();
                createdItem = new U9ItemReference(
                    "item-1",
                    itemCode,
                    row.GetProperty("Name").GetString(),
                    row.TryGetProperty("SPECS", out var specification) ? specification.GetString() : null,
                    row.GetProperty("MainItemCategory").GetProperty("Code").GetString(),
                    null,
                    row.GetProperty("InventoryUOM").GetProperty("Code").GetString(),
                    null,
                    null,
                    row.TryGetProperty("Description", out var description) ? description.GetString() : null);
                return Task.FromResult(new U9BusinessBatchResult(0, null, [new(true, null, "item-1", itemCode)]));
            }

            BomWriteCount++;
            LastBomEffectiveDate = row.GetProperty("EffectiveDate").GetString();
            LastComponentEffectiveDates = row.GetProperty("BOMComponents").EnumerateArray()
                .Select(component => component.GetProperty("EffectiveDate").GetString())
                .ToArray();
            var code = row.GetProperty("ItemMaster").GetProperty("Code").GetString();
            var uom = row.GetProperty("ProductUOM").GetProperty("Code").GetString();
            var writtenComponents = row.GetProperty("BOMComponents").EnumerateArray().Select(component => new U9BomComponentReference(
                component.GetProperty("Sequence").GetInt32(),
                null,
                component.GetProperty("ItemMaster").GetProperty("Code").GetString(),
                null,
                null,
                component.GetProperty("UsageQty").GetDecimal(),
                component.GetProperty("IssueUOM").GetProperty("Code").GetString(),
                null,
                component.GetProperty("ParentQty").GetDecimal(),
                0,
                true,
                null,
                null,
                component.GetProperty("Remark").GetString(),
                null,
                0,
                0,
                false,
                false)).ToArray();
            var components = bom is null ? writtenComponents : bom.Components.Concat(writtenComponents).ToArray();
            bom = new U9BomReference(
                "bom-1", code, null, "A1", "7", null, 0, 1, uom, null,
                null, null, 0, 0, 0, row.GetProperty("ProjectMapNum").GetString(),
                row.GetProperty("Explain").GetString(), null, false, 0, 0, components,
                row.GetProperty("OtherID").GetString());
            return Task.FromResult(new U9BusinessBatchResult(0, null, [new(true, null, "bom-1", code)]));
        }

        public Task<U9BomQueryResult> QueryBomsAsync(string baseUrl, string path, string token, string payloadJson, CancellationToken cancellationToken) =>
            Task.FromResult(new U9BomQueryResult(0, null, bom is null ? [] : [bom]));

        public Task<U9BomOperationReference?> QueryBomOperationAsync(string baseUrl, string path, string token, string organizationCode, string itemCode, string bomVersionCode, string otherId, CancellationToken cancellationToken) =>
            Task.FromResult<U9BomOperationReference?>(null);

        public Task<U9CustomerQueryResult> QueryCustomerReferencesAsync(string baseUrl, string path, string token, string payloadJson, CancellationToken cancellationToken) =>
            Task.FromResult(new U9CustomerQueryResult(0, null, createdItem is null
                ? []
                : [new U9CustomerReference(createdItem.U9ItemCode!, createdItem.U9ItemName ?? createdItem.U9ItemCode!)], createdItem is null ? 0 : 1));
    }
}
