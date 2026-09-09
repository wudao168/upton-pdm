using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

[Collection("BomHeader automatic queue")]
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
        u9Client.ReferenceCodes.AddRange(["03025000000", "02015000000"]);

        var first = await service.GenerateHierarchyMaterialsAsync(root.Id, "admin", UserRole.Administrator, default);

        Assert.Equal(5, first.ExpectedCount);
        Assert.Equal(5, first.GeneratedCount);
        Assert.Equal(0, first.ExistingCount);
        Assert.Equal(5, first.QueuedApprovalCount);
        Assert.Equal(0, first.AutoApprovedCount);
        Assert.Equal(0, u9Client.AuthenticationCount);
        Assert.True(await service.ProcessAutomaticQueueAsync(default));
        Assert.Equal(5, u9Client.AuthenticationCount);
        Assert.Equal(0, u9Client.ItemQueryCount);
        Assert.Equal(0, u9Client.UomQueryCount);
        Assert.Equal(5, u9Client.ReferenceQueryCount);
        Assert.Equal(8, first.Headers.Count);
        Assert.Equal(2, first.Headers.Count(header => header.Kind == ProjectBomHeaderKind.Master));
        Assert.All(first.Headers.Where(header => header.Kind == ProjectBomHeaderKind.Master), header => Assert.Equal("0302", header.CategoryCode));
        Assert.All(first.Headers.Where(header => header.Kind != ProjectBomHeaderKind.Master && header.MaterialId is not null), header => Assert.Equal("0201", header.CategoryCode));
        var rootHeaders = first.Headers.Where(header => header.ProjectId == root.Id).ToArray();
        Assert.NotNull(rootHeaders.Single(header => header.Kind == ProjectBomHeaderKind.Master).MaterialId);
        Assert.All(rootHeaders.Where(header => header.Kind != ProjectBomHeaderKind.Master), header => Assert.Null(header.MaterialId));
        Assert.All(first.Headers.Where(header => header.MaterialId is not null), header => Assert.Null(header.MaterialCode));
        Assert.All(first.Headers.Where(header => header.MaterialId is not null), header => Assert.Equal("ApprovalQueued", header.AutomaticStatus));
        Assert.All(first.Headers.Where(header => header.MaterialId is not null), header => Assert.Equal("admin", header.RequestedBy));
        Assert.Empty(await materials.ListMaterialCodeApplicationsAsync(null, MaterialCodeApplicationStatus.Pending, default));
        var applications = await materials.ListMaterialCodeApplicationsAsync(null, MaterialCodeApplicationStatus.Approved, default);
        Assert.Equal(5, applications.Count);
        Assert.All(applications, application => Assert.NotNull(application.BomHeaderKind));
        Assert.All(applications, application => Assert.Null(application.BomItemId));
        Assert.All(applications, application => Assert.NotNull(application.MaterialCode));
        Assert.DoesNotContain(applications, application => application.ProjectId == root.Id
            && application.BomHeaderKind != ProjectBomHeaderKind.Master);
        var pendingMaterials = await materials.ListMaterialsAsync(null, null, false, 100, default);
        Assert.Equal(5, pendingMaterials.Count);
        Assert.DoesNotContain(pendingMaterials, material => material.MaterialCode.StartsWith("PDM-PENDING-", StringComparison.Ordinal));
        Assert.Contains(pendingMaterials, material => material.MaterialCode == "03025000001");
        Assert.Equal(
            ["02015000001", "02015000002", "02015000003"],
            pendingMaterials.Where(material => material.CategoryCode == "0201").Select(material => material.MaterialCode).Order().ToArray());
        Assert.Equal(5, pendingMaterials.Select(material => material.MaterialCode).Distinct().Count());
        var batches = await materials.ListRecentSyncBatchesAsync("admin", 10, default);
        Assert.Equal(5, batches.Sum(batch => batch.TotalCount));
        Assert.All(batches, batch => Assert.Equal(MaterialSyncBatchStatus.Queued, batch.Status));

        var second = await service.GenerateHierarchyMaterialsAsync(root.Id, "admin", UserRole.Administrator, default);
        Assert.Equal(0, second.GeneratedCount);
        Assert.Equal(5, second.ExistingCount);
        Assert.Equal(0, second.AutoApprovedCount);
        Assert.Equal(0, second.QueuedSyncCount);
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
        Assert.Equal(2, standard.QueuedApprovalCount);
        Assert.True(await service.ProcessAutomaticQueueAsync(default));
        Assert.Empty(await materials.ListMaterialCodeApplicationsAsync(
            ProjectId, MaterialCodeApplicationStatus.Pending, default));
        var firstApplications = await materials.ListMaterialCodeApplicationsAsync(
            ProjectId, MaterialCodeApplicationStatus.Approved, default);
        Assert.Equal(
            [ProjectBomHeaderKind.Master, ProjectBomHeaderKind.Standard],
            firstApplications.Select(application => application.BomHeaderKind).OrderBy(kind => kind).ToArray());
        Assert.All(firstApplications, application => Assert.NotNull(application.MaterialCode));

        var repeated = await service.EnsureApplicationsAfterBomApprovalAsync(
            ProjectId, ProjectBomHeaderKind.Standard, "reviewer", default);
        Assert.Equal(0, repeated.GeneratedCount);
        Assert.Equal(2, repeated.ExistingCount);
        Assert.Equal(0, repeated.AutoApprovedCount);
        Assert.Equal(0, repeated.QueuedSyncCount);

        var electrical = await service.EnsureApplicationsAfterBomApprovalAsync(
            ProjectId, ProjectBomHeaderKind.Electrical, "reviewer", default);
        Assert.Equal(1, electrical.GeneratedCount);
        Assert.Equal(1, electrical.ExistingCount);
        Assert.Equal(1, electrical.QueuedApprovalCount);
        Assert.True(await service.ProcessAutomaticQueueAsync(default));
        var allApplications = await materials.ListMaterialCodeApplicationsAsync(
            ProjectId, MaterialCodeApplicationStatus.Approved, default);
        Assert.Equal(3, allApplications.Count);
        Assert.Equal(3, u9Client.AuthenticationCount);
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
        Assert.Equal(3, result.QueuedApprovalCount);
        Assert.True(await service.ProcessAutomaticQueueAsync(default));
        Assert.Empty(await materials.ListMaterialCodeApplicationsAsync(
            null, MaterialCodeApplicationStatus.Pending, default));
        var applications = await materials.ListMaterialCodeApplicationsAsync(
            null, MaterialCodeApplicationStatus.Approved, default);
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
        await service.ProcessAutomaticQueueAsync(default);
        var child = await repository.CreateSubprojectAsync(new(root.Id, "后增子项目", null, 1), default);

        var incremental = await service.GenerateHierarchyMaterialsAsync(root.Id, "admin", UserRole.Administrator, default);

        Assert.Equal(5, incremental.ExpectedCount);
        Assert.Equal(4, incremental.GeneratedCount);
        Assert.Equal(1, incremental.ExistingCount);
        Assert.Equal(4, incremental.QueuedApprovalCount);
        await service.ProcessAutomaticQueueAsync(default);
        Assert.Empty(await materials.ListMaterialCodeApplicationsAsync(null, MaterialCodeApplicationStatus.Pending, default));
        var applications = await materials.ListMaterialCodeApplicationsAsync(null, MaterialCodeApplicationStatus.Approved, default);
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

    [Fact]
    public async Task AutomaticTimeout_ReportsFailureAndRetriesOriginalApplicationWithoutDuplicates()
    {
        var service = CreateService(out var materials, out _, out var client);
        client.ReferenceFailure = new TimeoutException("U9C客户参照查询请求超时。");
        await service.EnsureApplicationsAfterBomApprovalAsync(ProjectId, ProjectBomHeaderKind.Standard, "admin", default);
        await service.ProcessAutomaticQueueAsync(default);
        var before = await service.ListAsync(ProjectId, "admin", UserRole.Administrator, default);
        var master = before.Single(item => item.Kind == ProjectBomHeaderKind.Master);
        Assert.Equal("Failed", master.AutomaticStatus);
        Assert.Contains("客户参照查询请求超时", master.AutomaticMessage);
        Assert.True(master.CanRetryAutomatic);
        Assert.Empty(await materials.ListSyncTasksAsync(default));
        var originalApplications = await materials.ListMaterialCodeApplicationsAsync(ProjectId, null, default);

        client.ReferenceFailure = null;
        var retried = await service.RetryAutomaticAsync(ProjectId, master.Kind, master.ApplicationId!.Value,
            master.ApplicationRowVersion, "确认重试料号自动处理", "admin", UserRole.Administrator, default);
        Assert.Equal(master.ApplicationId, retried.ApplicationId);
        Assert.Equal(master.MaterialId, retried.MaterialId);
        Assert.Equal("ApprovalQueued", retried.AutomaticStatus);
        Assert.False(retried.CanRetryAutomatic);
        Assert.Null(retried.MaterialCode);
        Assert.Empty(await materials.ListSyncTasksAsync(default));
        await service.ProcessAutomaticQueueAsync(default);
        Assert.Single(await materials.ListSyncTasksAsync(default));
        Assert.Equal(originalApplications.Count, (await materials.ListMaterialCodeApplicationsAsync(ProjectId, null, default)).Count);
        await Assert.ThrowsAsync<PdmConflictException>(() => service.RetryAutomaticAsync(ProjectId, master.Kind,
            master.ApplicationId.Value, master.ApplicationRowVersion, "确认重试料号自动处理", "admin", UserRole.Administrator, default));
        Assert.Single(await materials.ListSyncTasksAsync(default));
    }

    [Fact]
    public async Task AutomaticRetry_RequiresConfirmationAndMatchingApplication()
    {
        var service = CreateService(out var materials, out _, out var client);
        client.ReferenceFailure = new TimeoutException("超时");
        await service.EnsureApplicationsAfterBomApprovalAsync(ProjectId, ProjectBomHeaderKind.Standard, "admin", default);
        await service.ProcessAutomaticQueueAsync(default);
        var header = (await service.ListAsync(ProjectId, "admin", UserRole.Administrator, default)).First();
        await Assert.ThrowsAsync<PdmRuleException>(() => service.RetryAutomaticAsync(ProjectId, header.Kind,
            header.ApplicationId!.Value, header.ApplicationRowVersion, "", "admin", UserRole.Administrator, default));
        await Assert.ThrowsAsync<PdmConflictException>(() => service.RetryAutomaticAsync(ProjectId, header.Kind,
            Guid.NewGuid(), header.ApplicationRowVersion, "确认重试料号自动处理", "admin", UserRole.Administrator, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.RetryAutomaticAsync(ProjectId, header.Kind,
            header.ApplicationId!.Value, header.ApplicationRowVersion, "确认重试料号自动处理", "viewer", UserRole.ProductionViewer, default));
        Assert.Empty(await materials.ListSyncTasksAsync(default));
    }

    [Fact]
    public async Task LegacyPendingApplication_UsesReleaseAuditFailureWithoutChangingApproval()
    {
        var service = CreateService(out var materials, out var repository);
        var draft = await AddMaterial(materials, MaterialKind.Product, MaterialApprovalStatus.Draft, "0302", "PDM-PENDING-TEST");
        await repository.SaveProjectBomHeaderBindingAsync(ProjectId, ProjectBomHeaderKind.Master, draft.Id, 0, "admin", default);
        var pending = (await service.ListAsync(ProjectId, "admin", UserRole.Administrator, default)).First();
        await repository.AppendAuditAsync(new AuditEntry(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(1), "admin",
            "bom.header.application.auto-trigger-failed", nameof(Project), ProjectId.ToString(),
            "StandardBOM已批准发布，但自动生成或排队同步BOM料号失败：U9C客户参照查询请求超时。"), default);
        var header = (await service.ListAsync(ProjectId, "admin", UserRole.Administrator, default)).First();
        Assert.Equal("Failed", header.AutomaticStatus);
        Assert.Contains("U9C客户参照查询请求超时", header.AutomaticMessage);
        Assert.Equal(pending.ApplicationId, header.ApplicationId);
        Assert.Equal(MaterialCodeApplicationStatus.Pending, header.ApplicationStatus);
        Assert.Empty(await materials.ListSyncTasksAsync(default));
        Assert.False(await service.ProcessAutomaticQueueAsync(default));
    }

    [Fact]
    public async Task AutomaticQueue_SurvivesRequestCancellationAndWorkerRestart()
    {
        var service = CreateService(out var materials, out var repository, out var client);
        using var request = new CancellationTokenSource();
        var result = await service.EnsureApplicationsAfterBomApprovalAsync(ProjectId, ProjectBomHeaderKind.Standard, "admin", request.Token);
        Assert.Equal(2, result.QueuedApprovalCount);
        Assert.Equal(0, client.ReferenceQueryCount);
        request.Cancel();
        using var stopping = new CancellationTokenSource();
        client.BeforeReferenceQuery = stopping.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ProcessAutomaticQueueAsync(stopping.Token));
        Assert.Empty(await materials.ListSyncTasksAsync(default));
        Assert.All((await service.ListAsync(ProjectId, "admin", UserRole.Administrator, default)).Where(item => item.ApplicationId is not null),
            item => Assert.Equal("ApprovalQueued", item.AutomaticStatus));

        client.BeforeReferenceQuery = null;
        var materialService = new MaterialService(materials, repository, new TestProtector(), client, TimeProvider.System);
        var resumed = new BomHeaderService(repository, materials, materialService,
            new MaterialSyncBatchService(materials, repository, TimeProvider.System), TimeProvider.System);
        await Task.WhenAll(resumed.ProcessAutomaticQueueAsync(default), service.ProcessAutomaticQueueAsync(default));
        Assert.Equal(2, (await materials.ListSyncTasksAsync(default)).Count);
        Assert.Equal(2, (await materials.ListMaterialCodeApplicationsAsync(ProjectId, null, default)).Count);
        Assert.False(await resumed.ProcessAutomaticQueueAsync(default));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AutomaticHeader_RejectsManualApprovalAndRejection(bool approved)
    {
        var service = CreateService(out var materials, out var repository, out var client);
        await service.EnsureApplicationsAfterBomApprovalAsync(ProjectId, ProjectBomHeaderKind.Standard, "admin", default);
        var application = (await materials.ListMaterialCodeApplicationsAsync(ProjectId, null, default)).First();
        var manual = new MaterialService(materials, repository, new TestProtector(), client, TimeProvider.System);
        var error = await Assert.ThrowsAsync<PdmRuleException>(() => manual.DecideMaterialCodeApplicationAsync(
            application.Id, application.RowVersion, approved, "test", "admin", UserRole.Administrator, default));
        Assert.Contains("不能人工批准或退回", error.Message);
        Assert.Empty(await materials.ListSyncTasksAsync(default));
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
        var syncBatches = new MaterialSyncBatchService(materials, repository, time);
        return new BomHeaderService(repository, materials, materialService, syncBatches, time);
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

    [Theory]
    [InlineData(false, 1, 4, 4, 0)]
    [InlineData(false, 1, 1, 4, 3)]
    [InlineData(true, 1, 4, 4, 0)]
    [InlineData(true, 2, 4, 8, 4)]
    [InlineData(true, 2, 8, 8, 0)]
    public async Task ProjectBomU9Sync_SumsRepeatedReleasedMaterialRows(
        bool usePackage, int multiplier, decimal existingTotal, decimal approvedTotal, decimal expectedDelta)
    {
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-07T08:00:00Z"));
        var repository = new InMemoryPdmRepository(time);
        var materials = new InMemoryMaterialRepository(time);
        await materials.SaveIntegrationConfigurationAsync(new(
            "http://u9.example.test/U9", "01", "7", "pdm", "PDM", "protected:test-secret",
            U9MaterialContract.CreatePath, U9MaterialContract.QueryPath, true, "admin", time.GetUtcNow()), default);
        var header = await AddMaterial(materials, MaterialKind.Product, MaterialApprovalStatus.Approved,
            "0201", "02010000101", "02010000101", true);
        var component = await AddMaterial(materials, MaterialKind.Standard, MaterialApprovalStatus.Approved,
            "0102", "01020000056", "01020000056", true);
        await repository.SaveProjectBomHeaderBindingAsync(
            ProjectId, ProjectBomHeaderKind.Standard, header.Id, 0, "admin", default);
        var items = Enumerable.Range(1, 4).Select(sequence => new BomItem(
            Guid.NewGuid(), ProjectId, BomKind.Standard, sequence, component.MaterialCode,
            "平垫", 1, "001", null, "Φ3", "W1", true)).ToArray();
        var version = await repository.SaveBomDraftAsync(ProjectId, BomKind.Standard, items, "reviewer", default);
        await repository.SetBomVersionStateAsync([version.Id], BomVersionState.Released, "reviewer", time.GetUtcNow(), default);
        if (usePackage)
        {
            await repository.CreateReleasePackageAsync(new ReleasePackage(
                Guid.NewGuid(), ProjectId, "RP-DUPLICATE", ReleasePackageState.Published, version.Id, "V1", "V1",
                [], time.GetUtcNow(), time.GetUtcNow(), "C:\\PDM\\Release\\DUPLICATE")
            {
                Scope = ReleaseScope.StandardFormal,
                StandardBomSnapshot = items,
                WholeSetMultiplier = multiplier
            }, default);
        }
        var state = new BomWriteState
        {
            Exists = true,
            Components = [new(10, null, "01020000056", "平垫", null, existingTotal, "001", "个", 1, 0, true, null, null, null, null, 0, 0, false, false)]
        };
        var service = new ProjectBomU9SyncService(repository, materials,
            new U9BomWriteService(materials, repository, new BomWriteClient(state), new BomQueryClient(state), new TestProtector(), time), time);

        var preview = await service.PreviewAsync(ProjectId, ProjectBomHeaderKind.Standard, "admin", UserRole.Administrator, default);

        Assert.Equal(expectedDelta == 0 ? ProjectBomU9SyncState.UpToDate : ProjectBomU9SyncState.ModifyRequired, preview.State);
        var quantity = Assert.Single(preview.WritePreview!.QuantityReconciliations);
        Assert.Equal(approvedTotal, quantity.PlmApprovedTotal);
        Assert.Equal(existingTotal, quantity.U9ExistingTotal);
        Assert.Equal(expectedDelta, quantity.UploadDelta);
        Assert.Equal(0, state.WriteCount);
    }

    [Fact]
    public async Task U9BomWritePreview_UsesOnlyTheMissingApprovedQuantity()
    {
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-05T08:00:00Z"));
        var repository = new InMemoryPdmRepository(time);
        var materials = new InMemoryMaterialRepository(time);
        await materials.SaveIntegrationConfigurationAsync(new(
            "http://u9.example.test/U9", "01", "7", "pdm", "PDM", "protected:test-secret",
            U9MaterialContract.CreatePath, U9MaterialContract.QueryPath, true, "admin", time.GetUtcNow()), default);
        var state = new BomWriteState
        {
            Exists = true,
            Components = [new(10, null, "01020000056", "平垫", null, 1.000000000m, "001", "个", 1.000000000m, 0, true, null, null, null, null, 0, 0, false, false)]
        };
        var service = new U9BomWriteService(
            materials, repository, new BomWriteClient(state), new BomQueryClient(state), new TestProtector(), time);
        var command = new U9BomWriteCommand(
            U9BomWriteOperation.Modify, "02010000101", "A1", "001", 1,
            [new(10, "01020000056", 4, "001")],
            ProjectMapNum: "P700001", Explain: state.Explain, ReconcileComponentTotals: true);

        var preview = await service.PreviewAsync(command, "admin", UserRole.Administrator, default);

        var quantity = Assert.Single(preview.QuantityReconciliations);
        Assert.Equal(4, quantity.PlmApprovedTotal);
        Assert.Equal(1, quantity.U9ExistingTotal);
        Assert.Equal(3, quantity.UploadDelta);
        using var payload = JsonDocument.Parse(preview.RequestPreview);
        Assert.Equal(3, payload.RootElement[0].GetProperty("BOMComponents")[0].GetProperty("UsageQty").GetDecimal());
    }

    [Fact]
    public async Task U9BomWritePreview_BlocksWhenU9TotalExceedsTheApprovedQuantity()
    {
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-05T08:00:00Z"));
        var repository = new InMemoryPdmRepository(time);
        var materials = new InMemoryMaterialRepository(time);
        await materials.SaveIntegrationConfigurationAsync(new(
            "http://u9.example.test/U9", "01", "7", "pdm", "PDM", "protected:test-secret",
            U9MaterialContract.CreatePath, U9MaterialContract.QueryPath, true, "admin", time.GetUtcNow()), default);
        var state = new BomWriteState
        {
            Exists = true,
            Components =
            [
                new(10, null, "01020000056", "平垫", null, 1.000000000m, "001", "个", 1.000000000m, 0, true, null, null, null, null, 0, 0, false, false),
                new(20, null, "01020000056", "平垫", null, 4.000000000m, "001", "个", 1.000000000m, 0, true, null, null, null, null, 0, 0, false, false)
            ]
        };
        var service = new U9BomWriteService(
            materials, repository, new BomWriteClient(state), new BomQueryClient(state), new TestProtector(), time);
        var command = new U9BomWriteCommand(
            U9BomWriteOperation.Modify, "02010000101", "A1", "001", 1,
            [new(10, "01020000056", 4, "001")],
            ProjectMapNum: "P700001", Explain: state.Explain, ReconcileComponentTotals: true);

        var error = await Assert.ThrowsAsync<PdmRuleException>(
            () => service.PreviewAsync(command, "admin", UserRole.Administrator, default));

        Assert.Contains("现有总用量5大于PLM审核总量4", error.Message);
        Assert.Equal(0, state.WriteCount);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(0)]
    public async Task U9ControlledChanges_UpdatesOrDeletesApprovedRowsAndRetainsUnrelatedRows(int desired)
    {
        var (service, state, repository) = await ControlledService();
        var command = ControlledCommand(desired);
        var preview = await service.PreviewAsync(command, "admin", UserRole.Administrator, default);
        Assert.Equal(desired == 0 ? 1 : 0, preview.DeletedComponentCount);
        Assert.Equal(desired == 0 ? 0 : 1, preview.ModifiedComponentCount);
        Assert.Equal(0, preview.AddedComponentCount);
        Assert.Equal(desired - 12, Assert.Single(preview.QuantityReconciliations).UploadDelta);
        using var payload = JsonDocument.Parse(preview.RequestPreview);
        var row = Assert.Single(payload.RootElement[0].GetProperty("BOMComponents").EnumerateArray());
        Assert.Equal(10, row.GetProperty("Sequence").GetInt32());
        if (desired == 0) Assert.True(row.GetProperty("IsDelete").GetBoolean());
        else
        {
            Assert.Equal("8", row.GetProperty("BOMComponentChangeDTOList")[0].GetProperty("PropValue").GetString());
            Assert.Equal(8, row.GetProperty("UsageQty").GetDecimal());
            Assert.True(row.GetProperty("IsEffective").GetBoolean());
        }
        await service.ExecuteAsync(command, preview.RequestSha256, preview.RequiredConfirmation, "admin", UserRole.Administrator, default);
        Assert.Contains(state.Components, item => item.ItemCode == "MANUAL" && item.UsageQty == 9);
        Assert.Equal(desired, state.Components.Where(item => item.ItemCode == "01021000007").Sum(item => item.UsageQty));
        Assert.Contains(await repository.ListAuditAsync("admin", UserRole.Administrator, 100, default), entry => entry.Action == "u9.bom.before-change");
        var repeated = await service.PreviewAsync(command, "admin", UserRole.Administrator, default);
        Assert.Empty(repeated.ComponentChanges);
    }

    [Fact]
    public async Task U9ControlledChanges_BlocksUnknownOwnershipAndConcurrentChanges()
    {
        var (service, state, _) = await ControlledService();
        var command = ControlledCommand(8);
        var unknown = command with { PreviousApprovedComponents = [new(10, "01021000007", 11, "001")] };
        await Assert.ThrowsAsync<PdmRuleException>(() => service.PreviewAsync(unknown, "admin", UserRole.Administrator, default));
        var json = JsonSerializer.Serialize(command);
        Assert.DoesNotContain("PreviousApprovedComponents", json);
        var preview = await service.PreviewAsync(command, "admin", UserRole.Administrator, default);
        state.Components = state.Components.Select(row => row.ItemCode == "MANUAL" ? row with { UsageQty = 10 } : row).ToArray();
        await Assert.ThrowsAsync<PdmRuleException>(() => service.ExecuteAsync(command, preview.RequestSha256, preview.RequiredConfirmation, "admin", UserRole.Administrator, default));
        Assert.Equal(0, state.WriteCount);
    }

    [Fact]
    public async Task U9ControlledChanges_RejectsSuccessfulResponseWhenReadbackDidNotApplyDeletion()
    {
        var (service, state, _) = await ControlledService();
        state.IgnoreControlledChanges = true;
        var command = ControlledCommand(0);
        var preview = await service.PreviewAsync(command, "admin", UserRole.Administrator, default);
        var error = await Assert.ThrowsAsync<PdmRuleException>(() => service.ExecuteAsync(command, preview.RequestSha256, preview.RequiredConfirmation, "admin", UserRole.Administrator, default));
        Assert.Contains("回查不一致", error.Message);
        Assert.Equal(1, state.WriteCount);
    }

    private static U9BomWriteCommand ControlledCommand(int desired) => new(
        U9BomWriteOperation.Modify, "02010000101", "A1", "001", 1,
        desired == 0 ? [] : [new(10, "01021000007", desired, "001")], ReconcileComponentTotals: true)
        { PreviousApprovedComponents = [new(10, "01021000007", 12, "001")] };

    [Fact]
    public async Task ProjectBomU9Sync_ReductionAndDeletionRequireConfirmationButManualExecutionWorks()
    {
        var time = TimeProvider.System;
        var (writer, state, repository) = await ControlledService();
        var materials = new InMemoryMaterialRepository(time);
        var header = await AddMaterial(materials, MaterialKind.Product, MaterialApprovalStatus.Approved, "0201", "02010000101", "02010000101", true);
        await AddMaterial(materials, MaterialKind.Standard, MaterialApprovalStatus.Approved, "0102", "01021000007", "01021000007", true);
        await repository.SaveProjectBomHeaderBindingAsync(ProjectId, ProjectBomHeaderKind.Standard, header.Id, 0, "admin", default);
        var item = new BomItem(Guid.NewGuid(), ProjectId, BomKind.Standard, 1, "01021000007", "平垫", 12, "001", null, null, "W1", true);
        foreach (var (quantity, scope, age) in new[] { (12, ReleaseScope.StandardFormal, -2), (8, ReleaseScope.StandardSupplement, -1) })
            await repository.CreateReleasePackageAsync(new ReleasePackage(Guid.NewGuid(), ProjectId, $"RP-{quantity}", ReleasePackageState.Published,
                Guid.NewGuid(), "BOM", "BOM", [], time.GetUtcNow().AddHours(age), time.GetUtcNow().AddHours(age), "C:\\PDM\\Release")
                { Scope = scope, StandardBomSnapshot = [item with { Quantity = quantity }] }, default);
        var service = new ProjectBomU9SyncService(repository, materials, writer, time);
        var automatic = await service.SynchronizeApprovedAsync(ProjectId, ProjectBomHeaderKind.Standard, "admin", default);
        Assert.Equal(ProjectBomU9AutomaticState.AwaitingConfirmation, automatic.State);
        Assert.Equal(0, state.WriteCount);
        var preview = await service.PreviewAsync(ProjectId, ProjectBomHeaderKind.Standard, "admin", UserRole.Administrator, default);
        Assert.Equal(ProjectBomU9SyncState.ModifyRequired, preview.State);
        Assert.Equal(1, preview.WritePreview!.ModifiedComponentCount);
        await service.ExecuteAsync(ProjectId, ProjectBomHeaderKind.Standard, preview.WritePreview.RequestSha256, preview.WritePreview.RequiredConfirmation, "admin", UserRole.Administrator, default);
        Assert.Equal(8, state.Components.Single(row => row.ItemCode == "01021000007").UsageQty);
        Assert.Equal(ProjectBomU9SyncState.UpToDate, (await service.PreviewAsync(ProjectId, ProjectBomHeaderKind.Standard, "admin", UserRole.Administrator, default)).State);
    }

    [Fact]
    public async Task U9ControlledChanges_ReducesDuplicateHistoricalRowsBySequenceWithoutRebuildingBom()
    {
        var (service, state, _) = await ControlledService();
        state.Components = [state.Components[0] with { UsageQty = 4 }, state.Components[0] with { Sequence = 30, UsageQty = 8 }, state.Components[1]];
        var command = ControlledCommand(3);
        var preview = await service.PreviewAsync(command, "admin", UserRole.Administrator, default);
        Assert.Equal(1, preview.ModifiedComponentCount);
        Assert.Equal(1, preview.DeletedComponentCount);
        await service.ExecuteAsync(command, preview.RequestSha256, preview.RequiredConfirmation, "admin", UserRole.Administrator, default);
        Assert.Equal(3, state.Components.Single(row => row.ItemCode == "01021000007").UsageQty);
        Assert.Contains(state.Components, row => row.ItemCode == "MANUAL");
    }

    [Fact]
    public async Task U9ControlledChanges_DeletesSameMaterialInSeparateVerifiedRequests()
    {
        var (service, state, repository) = await ControlledService();
        state.Components = [state.Components[0] with { UsageQty = 4 }, state.Components[0] with { Sequence = 30, UsageQty = 8 }, state.Components[1]];
        var command = ControlledCommand(0);
        var preview = await service.PreviewAsync(command, "admin", UserRole.Administrator, default);
        await service.ExecuteAsync(command, preview.RequestSha256, preview.RequiredConfirmation, "admin", UserRole.Administrator, default);
        Assert.Equal(2, state.WriteCount);
        Assert.Equal("MANUAL", Assert.Single(state.Components).ItemCode);
        Assert.All(state.Payloads, payload =>
        {
            using var document = JsonDocument.Parse(payload);
            var row = Assert.Single(document.RootElement[0].GetProperty("BOMComponents").EnumerateArray());
            Assert.True(row.GetProperty("IsDelete").GetBoolean());
            Assert.True(row.GetProperty("UsageQty").GetDecimal() > 0);
        });
        var audit = await repository.ListAuditAsync("admin", UserRole.Administrator, 100, default);
        Assert.Equal(2, audit.Count(entry => entry.Action == "u9.bom.step-response"));
        Assert.Equal(2, audit.Count(entry => entry.Action == "u9.bom.step-readback"));
    }

    [Fact]
    public async Task U9ControlledChanges_PreservesOriginalAttributesInQuantityPayload()
    {
        var (service, state, _) = await ControlledService();
        state.Components = [state.Components[0] with { IsEffective = false, ItemVersionCode = "V2", Remark = "原备注",
            EffectiveDate = DateTimeOffset.Parse("2025-01-02T00:00:00+08:00"), DisableDate = DateTimeOffset.Parse("2030-01-02T00:00:00+08:00"),
            IssueStyle = 1, SupplyStyle = 2, IsPhantomPart = true, ProjectMapNum = "原图号" }, state.Components[1]];
        var command = ControlledCommand(8);
        var preview = await service.PreviewAsync(command, "admin", UserRole.Administrator, default);
        using var document = JsonDocument.Parse(preview.RequestPreview);
        var row = document.RootElement[0].GetProperty("BOMComponents")[0];
        Assert.Equal(8, row.GetProperty("UsageQty").GetDecimal());
        Assert.False(row.GetProperty("IsEffective").GetBoolean());
        Assert.Equal("2025-01-02", row.GetProperty("EffectiveDate").GetString());
        Assert.Equal("2030-01-02", row.GetProperty("DisableDate").GetString());
        Assert.Equal("V2", row.GetProperty("ItemVersionCode").GetString());
        Assert.Equal("原备注", row.GetProperty("Remark").GetString());
        Assert.Equal("原图号", row.GetProperty("ProjectMapNum").GetString());
        Assert.Equal(1, row.GetProperty("IssueStyle").GetInt32());
        Assert.Equal(2, row.GetProperty("SupplyStyle").GetInt32());
        Assert.True(row.GetProperty("IsPhantomPart").GetBoolean());
        await service.ExecuteAsync(command, preview.RequestSha256, preview.RequiredConfirmation, "admin", UserRole.Administrator, default);
        Assert.False(state.Components[0].IsEffective);
    }

    [Fact]
    public async Task U9ControlledChanges_StopsAfterUnexpectedEffectiveStateEvenWhenQuantityMatches()
    {
        var (service, state, repository) = await ControlledService();
        state.CorruptEffectiveState = true;
        var command = ControlledCommand(8);
        var preview = await service.PreviewAsync(command, "admin", UserRole.Administrator, default);
        var error = await Assert.ThrowsAsync<PdmRuleException>(() => service.ExecuteAsync(command, preview.RequestSha256, preview.RequiredConfirmation, "admin", UserRole.Administrator, default));
        Assert.Contains("有效状态", error.Message);
        Assert.Contains("0/1", error.Message);
        Assert.Equal(1, state.WriteCount);
        Assert.Contains(await repository.ListAuditAsync("admin", UserRole.Administrator, 100, default), entry => entry.Action == "u9.bom.step-readback");
    }

    [Fact]
    public async Task U9ControlledChanges_StopsBetweenStepsWhenAnotherClientChangesEffectiveState()
    {
        var (service, state, _) = await ControlledService();
        state.Components = [state.Components[0] with { UsageQty = 4 }, state.Components[0] with { Sequence = 30, UsageQty = 8 }, state.Components[1]];
        state.MutateAfterFirstReadback = true;
        var command = ControlledCommand(0);
        var preview = await service.PreviewAsync(command, "admin", UserRole.Administrator, default);
        var error = await Assert.ThrowsAsync<PdmRuleException>(() => service.ExecuteAsync(command, preview.RequestSha256, preview.RequiredConfirmation, "admin", UserRole.Administrator, default));
        Assert.Contains("其他客户端", error.Message);
        Assert.Contains("1/2", error.Message);
        Assert.Equal(1, state.WriteCount);
    }

    [Fact]
    public async Task U9ControlledChanges_MixedPlanUsesSixVerifiedSingleRowRequests()
    {
        var (service, state, _) = await ControlledService();
        var template = state.Components[0];
        state.Components = [template with { Sequence = 140 }, state.Components[1] with { Sequence = 900 },
            template with { Sequence = 10, ItemCode = "01020000056", UsageQty = 1 },
            template with { Sequence = 20, ItemCode = "01020000056", UsageQty = 3 },
            template with { Sequence = 60, ItemCode = "01020014733", UsageQty = 6 }];
        var command = ControlledCommand(8) with
        {
            Components = [new(10, "01021000007", 8, "001"), new(20, "01010000400", 1, "001"), new(30, "01010001817", 1, "001")],
            PreviousApprovedComponents = [new(10, "01021000007", 12, "001"), new(20, "01020000056", 4, "001"), new(30, "01020014733", 6, "001")]
        };
        var preview = await service.PreviewAsync(command, "admin", UserRole.Administrator, default);
        Assert.Equal(6, preview.ComponentChanges.Count);
        await service.ExecuteAsync(command, preview.RequestSha256, preview.RequiredConfirmation, "admin", UserRole.Administrator, default);
        Assert.Equal(6, state.WriteCount);
        Assert.Equal(4, state.Components.Count);
        Assert.Equal(8, state.Components.Single(row => row.Sequence == 140).UsageQty);
        Assert.True(state.Components.Single(row => row.Sequence == 140).IsEffective);
        Assert.DoesNotContain(state.Components, row => row.ItemCode is "01020000056" or "01020014733");
        Assert.All(state.Payloads, payload =>
        {
            using var document = JsonDocument.Parse(payload);
            Assert.Single(document.RootElement[0].GetProperty("BOMComponents").EnumerateArray());
        });
    }

    [Fact]
    public async Task U9ControlledChanges_DoesNotProceedOrRetryAfterFirstDeletionFailsReadback()
    {
        var (service, state, _) = await ControlledService();
        state.Components = [state.Components[0] with { UsageQty = 4 }, state.Components[0] with { Sequence = 30, UsageQty = 8 }, state.Components[1]];
        state.IgnoreControlledChanges = true;
        var command = ControlledCommand(0);
        var preview = await service.PreviewAsync(command, "admin", UserRole.Administrator, default);
        var error = await Assert.ThrowsAsync<PdmRuleException>(() => service.ExecuteAsync(command, preview.RequestSha256, preview.RequiredConfirmation, "admin", UserRole.Administrator, default));
        Assert.Contains("0/2", error.Message);
        Assert.Equal(1, state.WriteCount);
        Assert.Equal(3, state.Components.Count);
    }

    [Theory]
    [InlineData("03021000000", "03021000001")]
    [InlineData("03021000001", "02011000000")]
    [InlineData("02011000000", "01021000008")]
    public async Task NewBomRows_AllThreeLevelsUseVariableQuantityAndSpecialControl(string parent, string child)
    {
        var (service, state, _) = await ControlledService();
        state.Exists = false;
        var command = new U9BomWriteCommand(U9BomWriteOperation.Create, parent, "A1", "001", 1,
            [new(10, child, 1, "001")]);
        var preview = await service.PreviewAsync(command, "admin", UserRole.Administrator, default);
        using var payload = JsonDocument.Parse(preview.RequestPreview);
        var row = payload.RootElement[0].GetProperty("BOMComponents")[0];
        Assert.Equal(1, row.GetProperty("UsageQtyType").GetInt32());
        Assert.True(row.GetProperty("IsSpecialUseItem").GetBoolean());
        Assert.Equal(0, state.WriteCount);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task NewBomRows_RejectWrongUsageTypeOrSpecialControlReadback(int usageType, bool special)
    {
        var (service, state, _) = await ControlledService();
        state.Exists = false;
        state.ApplyControlledChanges = false;
        state.Components = [state.Components[0] with { UsageQtyType = usageType, IsSpecialUseItem = special }];
        var command = new U9BomWriteCommand(U9BomWriteOperation.Create, "02010000101", "A1", "001", 1,
            [new(10, "01021000007", 12, "001")]);
        var preview = await service.PreviewAsync(command, "admin", UserRole.Administrator, default);
        await Assert.ThrowsAsync<PdmRuleException>(() => service.ExecuteAsync(command, preview.RequestSha256,
            preview.RequiredConfirmation, "admin", UserRole.Administrator, default));
        Assert.Equal(1, state.WriteCount);
    }

    private static async Task<(U9BomWriteService, BomWriteState, InMemoryPdmRepository)> ControlledService()
    {
        var time = TimeProvider.System;
        var repository = new InMemoryPdmRepository(time);
        var materials = new InMemoryMaterialRepository(time);
        await materials.SaveIntegrationConfigurationAsync(new("http://u9.example.test/U9", "01", "7", "pdm", "PDM", "protected:test-secret",
            U9MaterialContract.CreatePath, U9MaterialContract.QueryPath, true, "admin", time.GetUtcNow()), default);
        var state = new BomWriteState { Exists = true, ApplyControlledChanges = true, Components = [
            new(10, null, "01021000007", "平垫", null, 12, "001", "个", 1, 0, true, null, null, null, null, 0, 0, false, false),
            new(20, null, "MANUAL", "人工添加", null, 9, "001", "个", 1, 0, true, null, null, null, null, 0, 0, false, false)] };
        return (new(materials, repository, new BomWriteClient(state), new BomQueryClient(state), new TestProtector(), time), state, repository);
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

        var decision = Assert.Single(await materialService.AutomaticallyApproveBomHeaderApplicationsAsync(
            [(application.Id, application.RowVersion)], "standardizer", default));
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
        public Exception? ReferenceFailure { get; set; }
        public Action? BeforeReferenceQuery { get; set; }
        public List<string> ReferenceCodes { get; } = [];
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
            BeforeReferenceQuery?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            if (ReferenceFailure is not null) throw ReferenceFailure;
            var items = ReferenceCodes.Select(code => new U9CustomerReference(code, code)).ToArray();
            return Task.FromResult(new U9CustomerQueryResult(0, null, items, items.Length));
        }
    }

    private sealed class BomWriteState
    {
        public bool ApplyControlledChanges { get; set; }
        public bool IgnoreControlledChanges { get; set; }
        public bool CorruptEffectiveState { get; set; }
        public bool MutateAfterFirstReadback { get; set; }
        public List<string> Payloads { get; } = [];
        public bool Exists { get; set; }
        public int WriteCount { get; set; }
        public IReadOnlyList<U9BomComponentReference> Components { get; set; } =
            [new(10, null, "01020000057", "阀岛", null, 2, "001", "个", 1, 0, true, null, null, null, null, 0, 0, false, false)];
        public string Explain => OwnershipExplain("02010000101", "A1");

        public U9BomReference Bom => new(
            null, "02010000101", "P700001 标准件BOM", "A1", "7", "昆山工厂", 0, 1,
            "001", "个", null, null, 0, 0, 0, "P700001", Explain,
            null, false, 0, 0,
            Components.Select(row => row with { UsageQtyType = row.UsageQtyType ?? 1, IsSpecialUseItem = row.IsSpecialUseItem ?? true, IsIssueOrgFixed = row.IsIssueOrgFixed ?? true, IssueOrgCode = row.IssueOrgCode ?? "7" }).ToArray());

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

        public Task<U9BomQueryResult> QueryBomsAsync(string baseUrl, string path, string token, string payloadJson, CancellationToken cancellationToken)
        {
            var result = new U9BomQueryResult(0, null, state.Exists ? [state.Bom] : []);
            if (state.WriteCount == 1 && state.MutateAfterFirstReadback)
            {
                state.MutateAfterFirstReadback = false;
                state.Components = state.Components.Select(row => row.ItemCode == "MANUAL" ? row with { IsEffective = false } : row).ToArray();
            }
            return Task.FromResult(result);
        }

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
            state.WriteCount++;
            state.Payloads.Add(payloadJson);
            if (state.ApplyControlledChanges && !state.IgnoreControlledChanges)
            {
                using var payload = JsonDocument.Parse(payloadJson);
                // Model the observed server behavior: one row per material per request, direct fields with defaults.
                foreach (var change in payload.RootElement[0].GetProperty("BOMComponents").EnumerateArray()
                    .GroupBy(row => row.GetProperty("ItemMaster").GetProperty("Code").GetString()).Select(group => group.First()))
                {
                    var sequence = change.GetProperty("Sequence").GetInt32();
                    if (change.GetProperty("IsDelete").GetBoolean()) state.Components = state.Components.Where(row => row.Sequence != sequence).ToArray();
                    else
                    {
                        var quantity = change.TryGetProperty("UsageQty", out var usage) ? usage.GetDecimal() : 1;
                        var effective = !state.CorruptEffectiveState && change.TryGetProperty("IsEffective", out var active) && active.GetBoolean();
                        if (state.Components.Any(row => row.Sequence == sequence))
                            state.Components = state.Components.Select(row => row.Sequence == sequence ? row with { UsageQty = quantity, IsEffective = effective } : row).ToArray();
                        else
                            state.Components = [..state.Components, new(sequence, null, change.GetProperty("ItemMaster").GetProperty("Code").GetString(), null, null,
                                quantity, change.GetProperty("IssueUOM").GetProperty("Code").GetString(), null, change.GetProperty("ParentQty").GetDecimal(),
                                0, effective, null, null, null, null, 0, 0, false, false)];
                    }
                }
            }
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
                    row.TryGetProperty("Description", out var description) ? description.GetString() : null)
                    { CreationAttributes = U9MaterialCreationRules.ReadAttributes(row) };
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
                false)
                {
                    UsageQtyType = component.GetProperty("UsageQtyType").GetInt32(),
                    IsSpecialUseItem = component.GetProperty("IsSpecialUseItem").GetBoolean(),
                    IsIssueOrgFixed = component.GetProperty("IsIssueOrgFixed").GetBoolean(),
                    IssueOrgCode = component.GetProperty("IssueOrg").GetProperty("Code").GetString()
                }).ToArray();
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
