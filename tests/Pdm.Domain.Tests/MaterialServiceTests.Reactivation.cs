using System.Text.Json;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed partial class MaterialServiceTests
{
    private static async Task<PdmMaterial> ArchivedU9Material(InMemoryMaterialRepository materials, MaterialService service)
    {
        var item = await service.CreateAsync(new(null, "启停同步回归", MaterialKind.Standard, MaterialSupplyMode.Purchase,
            "001", "AVAILABILITY-" + Guid.NewGuid(), null, null, "TEST", null, null, null, CategoryCode: "0102"),
            "admin", UserRole.Administrator, default);
        item = await materials.UpdateMaterialAsync(item with { SourceSystem = MaterialDataSource.U9C, MasterOwner = MaterialMasterOwner.U9C,
            U9SyncConfirmed = true, ApprovalStatus = MaterialApprovalStatus.Approved, LastU9SyncedAt = DateTimeOffset.UtcNow.AddDays(-2) }, item.RowVersion, default);
        return await materials.ArchiveMaterialAsync(item.Id, item.RowVersion, "admin", DateTimeOffset.UtcNow, default);
    }

    private static U9ItemReference U9State(string code, bool? enabled) => new("u9-existing", code)
    { CreationAttributes = enabled.HasValue ? new Dictionary<string, string?> { ["Effective.IsEffective"] = enabled.Value ? "true" : "false" } : new Dictionary<string, string?>() };

    private static U9MaterialIntegrationService AvailabilityExecutor(InMemoryMaterialRepository materials, AvailableCodeClient client) =>
        new(materials, new InMemoryPdmRepository(TimeProvider.System), new TestProtector(), client, TimeProvider.System);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Availability_PlmDecidesAndPersistsTaskBeforeU9Write(bool enabled)
    {
        var service = CreateService(out var materials, out var client);
        var item = await ArchivedU9Material(materials, service);
        if (!enabled) item = await materials.ReactivateMaterialAsync(item.Id, item.RowVersion, "admin", DateTimeOffset.UtcNow, default);
        await EnableU9WritesAsync(materials);
        client.ItemsByCode[item.MaterialCode] = U9State(item.MaterialCode, !enabled);
        var changed = await service.SetAvailabilityAsync(item.Id, item.RowVersion, enabled, "admin", UserRole.Administrator, default);
        Assert.Equal(!enabled, changed.Material.IsArchived);
        Assert.Equal(MaterialMasterOwner.Pdm, changed.Material.MasterOwner);
        Assert.Equal(MaterialSyncStatus.PreviewReady, changed.Material.SyncStatus);
        Assert.Equal(0, client.AuthenticationCount);
        var task = Assert.IsType<MaterialSyncTask>(changed.Task);
        Assert.Equal(MaterialSyncOperation.SetAvailability, task.Operation);
        var result = await AvailabilityExecutor(materials, client).ExecuteApprovedTaskAsync(task.Id, "system", default);
        Assert.Equal(MaterialSyncStatus.Succeeded, result.Task.Status);
        Assert.Equal(enabled, result.Material.U9IsEffective);
        Assert.Equal(!enabled, result.Material.IsArchived);
        Assert.Equal(1, client.ModifyCount);
        using var json = JsonDocument.Parse(client.LastPostPayload);
        Assert.Equal(item.MaterialCode, json.RootElement[0].GetProperty("Code").GetString());
        var attribute = Assert.Single(json.RootElement[0].GetProperty("Attributes").EnumerateArray());
        Assert.Equal("Effective.IsEffective", attribute.GetProperty("AttributeName").GetString());
        Assert.Equal(enabled ? "true" : "false", attribute.GetProperty("AttributeValue").GetString());
        Assert.Equal(item.ApprovalStatus, result.Material.ApprovalStatus);
    }

    [Fact]
    public async Task Availability_AlreadyMatchedU9IsConfirmedWithoutDuplicateWrite()
    {
        var service = CreateService(out var materials, out var client);
        var item = await ArchivedU9Material(materials, service);
        await EnableU9WritesAsync(materials);
        client.ItemsByCode[item.MaterialCode] = U9State(item.MaterialCode, true);
        var change = await service.SetAvailabilityAsync(item.Id, item.RowVersion, true, "admin", UserRole.Administrator, default);
        var result = await AvailabilityExecutor(materials, client).ExecuteApprovedTaskAsync(change.Task!.Id, "system", default);
        Assert.True(result.AlreadyExisted);
        Assert.False(result.Material.IsArchived);
        Assert.Equal(0, client.ModifyCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Availability_RejectedOrUnconfirmedWritePreservesPlmDecisionAndAllowsRetry(bool rejected)
    {
        var service = CreateService(out var materials, out var client);
        var item = await ArchivedU9Material(materials, service);
        await EnableU9WritesAsync(materials);
        client.ItemsByCode[item.MaterialCode] = U9State(item.MaterialCode, false);
        if (rejected) client.ModifyResult = new(1, "拒绝启用", []); else client.ModifyEnablesItem = false;
        var change = await service.SetAvailabilityAsync(item.Id, item.RowVersion, true, "admin", UserRole.Administrator, default);
        var executor = AvailabilityExecutor(materials, client);
        await Assert.ThrowsAsync<PdmRuleException>(() => executor.ExecuteApprovedTaskAsync(change.Task!.Id, "system", default));
        var failed = (await materials.FindMaterialAsync(item.Id, default))!;
        Assert.False(failed.IsArchived);
        Assert.Contains(failed.SyncStatus, new[] { MaterialSyncStatus.Failed, MaterialSyncStatus.NeedsReview });
        client.ModifyResult = new(0, null, []);
        client.ModifyEnablesItem = true;
        var success = await executor.ExecuteApprovedTaskAsync(change.Task!.Id, "system", default);
        Assert.Equal(MaterialSyncStatus.Succeeded, success.Material.SyncStatus);
        Assert.True(success.Material.U9IsEffective);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Availability_ConnectionOrUnknownStateFailureIsVisibleWithoutLosingPlmIntent(bool authenticationFailure)
    {
        var service = CreateService(out var materials, out var client);
        var item = await ArchivedU9Material(materials, service);
        await EnableU9WritesAsync(materials);
        client.ItemsByCode[item.MaterialCode] = U9State(item.MaterialCode, null);
        if (authenticationFailure) client.AuthenticationError = new HttpRequestException("upstream connection refused");
        var change = await service.SetAvailabilityAsync(item.Id, item.RowVersion, true, "admin", UserRole.Administrator, default);
        var error = await Assert.ThrowsAsync<PdmRuleException>(() => AvailabilityExecutor(materials, client).ExecuteApprovedTaskAsync(change.Task!.Id, "system", default));
        Assert.DoesNotContain("connection refused", error.Message);
        Assert.False((await materials.FindMaterialAsync(item.Id, default))!.IsArchived);
        Assert.Equal(MaterialSyncStatus.Failed, (await materials.FindSyncTaskAsync(change.Task!.Id, default))!.Status);
        Assert.Equal(0, client.ModifyCount);
    }

    [Fact]
    public async Task Availability_StaleVersionAndSupersededIntentNeverWriteU9()
    {
        var service = CreateService(out var materials, out var client);
        var item = await ArchivedU9Material(materials, service);
        await EnableU9WritesAsync(materials);
        await Assert.ThrowsAsync<PdmConflictException>(() => service.ReactivateAsync(item.Id, item.RowVersion - 1, "admin", UserRole.Administrator, default));
        Assert.Empty(await materials.ListSyncTasksAsync(default));
        var first = await service.SetAvailabilityAsync(item.Id, item.RowVersion, true, "admin", UserRole.Administrator, default);
        var second = await service.SetAvailabilityAsync(item.Id, first.Material.RowVersion, false, "admin", UserRole.Administrator, default);
        Assert.Equal(MaterialSyncStatus.Superseded, (await materials.FindSyncTaskAsync(first.Task!.Id, default))!.Status);
        await Assert.ThrowsAsync<PdmRuleException>(() => AvailabilityExecutor(materials, client).ExecuteApprovedTaskAsync(first.Task!.Id, "system", default));
        Assert.True(second.Material.IsArchived);
        Assert.Equal(0, client.ModifyCount);
    }
}
