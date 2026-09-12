using Upton.Pdm.Application;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class ProcurementWarehouseRepositoryTests
{
    [Theory]
    [InlineData("ISSUE")]
    [InlineData("TRANSFER")]
    [InlineData("DIRECT")]
    public async Task WarehouseSnapshotPreservesDateUnitAndStrictProjectScope(string kind)
    {
        var repository = new InMemoryU9ProcurementRepository();
        var at = DateTimeOffset.Parse("2026-09-09T11:28:38+08:00");
        var row = new U9ProcurementSourceRow("7", kind, "1", null, "CRKD1", 10, 2, false, at,
            "00123", "物料", null, null, "P1", null, "P1-1", 0, 0, 0, 0, null, null, null)
            { MovementDate = at, MovementQuantity = 2.5m, MovementUnit = "米", SourcePoLineId = "1002912015526078" };
        await repository.ReplaceSnapshotAsync(Guid.NewGuid(), [
            row, row with { LineId = "2", ProjectCode = null },
            row with { LineId = "3", Subproject = "P1-2" },
            row with { LineId = "4", ProjectCode = null, Subproject = null },
            row with { LineId = "5", ProjectCode = "P2" }
        ], at, default);
        var rows = await repository.ListForProjectAsync("P1", "P1-1", default);
        Assert.Equal(new[] { "1", "2" }, rows.Select(item => item.LineId));
        Assert.All(rows, item => { Assert.Equal(at, item.MovementDate); Assert.Equal(2.5m, item.MovementQuantity); Assert.Equal("米", item.MovementUnit); });
        Assert.Empty(await repository.ListForProjectAsync("P1", null, default));
        Assert.All(rows, item => Assert.Equal("1002912015526078", item.SourcePoLineId));
    }
}
