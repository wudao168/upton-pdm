using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class BomWorkbookTests
{
    [Fact]
    public void WriteAndRead_RoundTripsStandardBomColumns()
    {
        var projectId = Guid.NewGuid();
        var source = new[]
        {
            new BomItem(Guid.NewGuid(), projectId, BomKind.Electrical, 1, "EL-001", "光电传感器", 4, "件", null, "M18 PNP", "A", true),
            new BomItem(Guid.NewGuid(), projectId, BomKind.Electrical, 2, "EL-002", "伺服驱动器", 2.5m, "件", "铝", "750W", "W2", false)
        };

        using var stream = new MemoryStream(BomWorkbook.Write(source));
        var imported = BomWorkbook.Read(stream);

        Assert.Equal(2, imported.Count);
        Assert.Equal("EL-001", imported[0].DrawingNumber);
        Assert.Equal(4, imported[0].Quantity);
        Assert.True(imported[0].IsComplete);
        Assert.Equal("750W", imported[1].Specification);
        Assert.False(imported[1].IsComplete);
    }
}
