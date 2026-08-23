using System.Text.Json;
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
    public async Task GenerateHierarchy_CreatesPendingApplicationsWithoutExposingPredictedCodesAndIsIdempotent()
    {
        var service = CreateService(out var materials, out var repository);
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

        Assert.Equal(8, first.ExpectedCount);
        Assert.Equal(8, first.GeneratedCount);
        Assert.Equal(0, first.ExistingCount);
        Assert.Equal(8, first.Headers.Count);
        Assert.Equal(2, first.Headers.Count(header => header.Kind == ProjectBomHeaderKind.Master));
        Assert.All(first.Headers.Where(header => header.Kind == ProjectBomHeaderKind.Master), header => Assert.Equal("0302", header.CategoryCode));
        Assert.All(first.Headers.Where(header => header.Kind != ProjectBomHeaderKind.Master), header => Assert.Equal("0201", header.CategoryCode));
        Assert.All(first.Headers, header => Assert.Null(header.MaterialCode));
        Assert.All(first.Headers, header => Assert.Equal(MaterialApprovalStatus.Draft, header.ApprovalStatus));
        var pendingMaterials = await materials.ListMaterialsAsync(null, null, false, 100, default);
        Assert.Equal(8, pendingMaterials.Count);
        Assert.Equal(8, pendingMaterials.Select(material => material.MaterialCode).Distinct().Count());

        var second = await service.GenerateHierarchyMaterialsAsync(root.Id, "admin", UserRole.Administrator, default);
        Assert.Equal(0, second.GeneratedCount);
        Assert.Equal(8, second.ExistingCount);
        Assert.Equal(first.Headers.Select(header => header.MaterialId), second.Headers.Select(header => header.MaterialId));
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
    {
        var time = TimeProvider.System;
        repository = new InMemoryPdmRepository(time);
        materials = new InMemoryMaterialRepository(time);
        materials.SaveIntegrationConfigurationAsync(new(
            "http://u9.example.test/U9", "01", "7", "pdm", "PDM", "protected:test-secret",
            U9MaterialContract.CreatePath, U9MaterialContract.QueryPath, false, "admin", time.GetUtcNow(),
            UnitCodeMappings: new Dictionary<string, string>()), default).GetAwaiter().GetResult();
        var materialService = new MaterialService(materials, repository, new TestProtector(), new AvailableCodeClient(), time);
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

    private sealed class TestProtector : IU9SecretProtector
    {
        public string Protect(string secret) => $"protected:{secret}";
        public string Unprotect(string ciphertext) => ciphertext[10..];
    }

    private sealed class AvailableCodeClient : IU9OpenApiClient
    {
        public Task<U9AuthenticationResult> AuthenticateAsync(U9AuthenticationRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new U9AuthenticationResult("token"));

        public Task<U9ItemQueryResult> QueryItemsAsync(string baseUrl, string path, string token, string payloadJson, CancellationToken cancellationToken) =>
            Task.FromResult(new U9ItemQueryResult(0, null, []));

        public Task<U9UomQueryResult> QueryUomsAsync(string baseUrl, string token, string payloadJson, CancellationToken cancellationToken) =>
            Task.FromResult(new U9UomQueryResult(0, null, [new U9UomReference("uom-001", "001")]));

        public Task<U9BusinessBatchResult> PostBatchAsync(string baseUrl, string path, string token, string payloadJson, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<U9CustomerQueryResult> QueryCustomerReferencesAsync(string baseUrl, string path, string token, string payloadJson, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
