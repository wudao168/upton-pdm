using Upton.Pdm.Domain;

namespace Pdm.Api.Tests;

public sealed class BomPropertyMappingCatalogTests
{
    [Fact]
    public void Apply_MergesServerPropertiesAndKeepsSystemSourcesFixed()
    {
        var settings = new PdmSystemSettings("vault", "release")
        {
            BomPropertyMappings =
            [
                new("kind", "物料分类", "分类", BomPropertyMappingCatalog.SolidWorksSource, true),
                new("wearPart", "易损件", "易损件标识", BomPropertyMappingCatalog.SolidWorksSource, true),
                new("drawingNumber", "物料编码", "图号", BomPropertyMappingCatalog.SolidWorksSource, true),
                new("heatTreatment", "热处理", "热处理方式", BomPropertyMappingCatalog.SolidWorksSource, true),
                new("quantity", "数量", "错误来源", BomPropertyMappingCatalog.SolidWorksSource, true)
            ]
        };

        var normalized = BomPropertyMappingCatalog.Apply(settings);

        Assert.Equal("图号", normalized.BomDrawingNumberProperty);
        Assert.Equal("分类", BomPropertyMappingCatalog.SolidWorksProperty(normalized, "kind", "物料分类"));
        Assert.Contains(normalized.BomPropertyMappings, item =>
            item.PdmPropertyKey == "wearPart"
            && item.PdmPropertyName == "易损件"
            && item.SolidWorksProperty == "易损件标识"
            && item.MappingEditable);
        Assert.Contains(normalized.BomPropertyMappings, item =>
            item.PdmPropertyKey == "heatTreatment"
            && item.PdmPropertyName == "热处理"
            && item.SolidWorksProperty == "热处理方式"
            && item.MappingEditable);
        Assert.Contains(normalized.BomPropertyMappings, item =>
            item.PdmPropertyKey == "quantity"
            && item.Source == BomPropertyMappingCatalog.AssemblySource
            && item.SolidWorksProperty == string.Empty
            && !item.MappingEditable);
        Assert.Contains(normalized.BomPropertyMappings, item =>
            item.PdmPropertyKey == "revision"
            && item.Source == BomPropertyMappingCatalog.PdmSource
            && !item.MappingEditable);
    }
}
