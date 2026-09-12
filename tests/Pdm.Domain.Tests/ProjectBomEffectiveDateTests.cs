using System.Reflection;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class ProjectBomEffectiveDateTests
{
    [Fact]
    public async Task AllNewHierarchyLevels_Use20190101_ForHeaderAndComponents()
    {
        var time = TimeProvider.System;
        var repository = new InMemoryPdmRepository(time);
        var materials = new InMemoryMaterialRepository(time);
        var root = await repository.CreateNumberedProjectAsync(new(
            Guid.Parse("70000000-0000-0000-0000-000000000001"), "P", 2,
            Guid.Parse("c0046500-0000-0000-0000-000000000001"), "日期规则主项目", null,
            new DateOnly(2026, 9, 9), 1, "admin", @"D:\PDM\Vault", @"D:\PDM\Release"), default);
        var rootId = root.Id;
        var child = await repository.CreateSubprojectAsync(new(rootId, "日期规则子项目", null, 1), default);
        var sequence = 1;
        async Task<PdmMaterial> Material(string categoryCode)
        {
            var code = categoryCode + (sequence++).ToString("D7");
            var now = time.GetUtcNow();
            var category = await materials.FindCategoryAsync(categoryCode, default) ?? throw new InvalidOperationException();
            return await materials.CreateMaterialAsync(new(
                Guid.NewGuid(), code, code, MaterialKind.Product, MaterialSupplyMode.Manufacture, "001",
                null, null, null, null, null, null, null, null, MaterialApprovalStatus.Approved, "admin", now,
                categoryCode, null, code, MaterialSyncStatus.NotQueued, "admin", now, "admin", now, 1,
                categoryCode, U9SyncConfirmed: true), category, default);
        }
        foreach (var id in new[] { rootId, child.Id })
        {
            var master = await Material("0302");
            await repository.SaveProjectBomHeaderBindingAsync(id, ProjectBomHeaderKind.Master, master.Id, 0, "admin", default);
        }
        foreach (var (headerKind, bomKind) in new[] {
            (ProjectBomHeaderKind.Standard, BomKind.Standard),
            (ProjectBomHeaderKind.NonStandard, BomKind.NonStandard),
            (ProjectBomHeaderKind.Electrical, BomKind.Electrical) })
        {
            var header = await Material("0201");
            var part = await Material("0201");
            await repository.SaveProjectBomHeaderBindingAsync(child.Id, headerKind, header.Id, 0, "admin", default);
            var item = new BomItem(Guid.NewGuid(), child.Id, bomKind, 1, part.U9ItemCode!, part.Name, 2, "001", null, null, "A1", true);
            var version = await repository.SaveBomDraftAsync(child.Id, bomKind, [item], "admin", default);
            await repository.SetBomVersionStateAsync([version.Id], BomVersionState.Released, "admin", time.GetUtcNow(), default);
        }
        // Inspect the common command builder without configuring any external U9 writer.
        var service = new ProjectBomU9SyncService(repository, materials, null!, time);
        var method = typeof(ProjectBomU9SyncService).GetMethod("BuildCommandAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach (var (id, kind) in new[] {
            (rootId, ProjectBomHeaderKind.Master), (child.Id, ProjectBomHeaderKind.Master),
            (child.Id, ProjectBomHeaderKind.Standard), (child.Id, ProjectBomHeaderKind.NonStandard),
            (child.Id, ProjectBomHeaderKind.Electrical) })
        {
            var task = (Task)method.Invoke(service, [id, kind, CancellationToken.None])!;
            await task;
            var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
            var command = (U9BomWriteCommand)result.GetType().GetProperty("Command")!.GetValue(result)!;
            Assert.Equal(new DateOnly(2019, 1, 1), command.EffectiveDate);
            Assert.Equal(new DateOnly(9999, 12, 31), command.DisableDate);
            Assert.NotEmpty(command.Components);
            Assert.All(command.Components, row => Assert.Equal(new DateOnly(2019, 1, 1), row.EffectiveDate));
        }
    }
}
