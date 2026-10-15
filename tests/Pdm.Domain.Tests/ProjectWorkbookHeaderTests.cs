using System.IO.Compression;
using System.Xml.Linq;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;
namespace Upton.Pdm.Domain.Tests;
public class ProjectWorkbookHeaderTests
{
    private static ProjectWorkbookMetadata Header(string version="B01") => new("标准件BOM","测试公司","当前子项目","BOM版本",version,DateTimeOffset.UtcNow,
        new[]{"主项目号","主项目名称","型号","客户","子项目号","子项目名称","子项目型号","整套数量","事业部","项目经理","主设","工程师"}.Select((key,index)=>new KeyValuePair<string,string>(key,index==0?"P700014":index==4?"P700014-1":"示例")).ToArray(),"工程师");
    [Fact]
    public void Common_header_preserves_BOM_data_and_adds_logo_print_titles_and_freeze()
    {
        var item=new BomItem(Guid.NewGuid(),Guid.NewGuid(),BomKind.Standard,1,"01021000007","平垫",12.5m,"个",null,"Φ6","W1",true);
        var bytes=ProjectWorkbookHeader.Apply(BomWorkbook.Write([item]),Header());
        using var stream=new MemoryStream(bytes);var parsed=Assert.Single(BomWorkbook.Read(stream));Assert.Equal(item.Quantity,parsed.Quantity);Assert.Equal(item.DrawingNumber,parsed.DrawingNumber);Assert.Equal(item.Revision,parsed.Revision);
        using var zip=new ZipArchive(new MemoryStream(bytes));Assert.NotNull(zip.GetEntry("xl/media/uplm-company-logo.png"));
        XDocument Read(string path){using var source=zip.GetEntry(path)!.Open();return XDocument.Load(source);}
        XNamespace ns="http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var sheet=Read("xl/worksheets/sheet1.xml");Assert.Contains(sheet.Descendants(ns+"t"),e=>e.Value=="P700014-1");Assert.Equal("7",sheet.Descendants(ns+"pane").Single().Attribute("ySplit")!.Value);
        Assert.Contains(Read("xl/workbook.xml").Descendants(ns+"definedName"),e=>e.Attribute("name")?.Value=="_xlnm.Print_Titles");
        Assert.DoesNotContain(zip.Entries.Where(e=>e.FullName.EndsWith(".xml")),e=>e.Length==0);
    }
    [Fact]
    public void Common_header_is_readable_by_BOM_import_when_version_column_is_missing()
    {
        var item=new BomItem(Guid.NewGuid(),Guid.NewGuid(),BomKind.Standard,1,"01021000007","平垫",2,"个",null,null,"W1",true);
        var bytes=ProjectWorkbookHeader.Apply(BomWorkbook.Write([item]),Header("W9"));
        using var stream=new MemoryStream();stream.Write(bytes);stream.Position=0;
        using(var zip=new ZipArchive(stream,ZipArchiveMode.Update,true)){
          XNamespace ns="http://schemas.openxmlformats.org/spreadsheetml/2006/main";var entry=zip.GetEntry("xl/worksheets/sheet1.xml")!;XDocument sheet;using(var source=entry.Open())sheet=XDocument.Load(source);
          var header=sheet.Descendants(ns+"row").Single(r=>(string?)r.Attribute("r")=="7");var cell=header.Elements(ns+"c").Single(c=>c.Value=="版本");var column=new string(((string)cell.Attribute("r")!).Where(char.IsLetter).ToArray());sheet.Descendants(ns+"c").Where(c=>((string?)c.Attribute("r"))?.StartsWith(column)==true).ToArray().ToList().ForEach(c=>c.Remove());entry.Delete();using var target=zip.CreateEntry("xl/worksheets/sheet1.xml").Open();sheet.Save(target);
        }
        stream.Position=0;Assert.Equal("W9",Assert.Single(BomWorkbook.Read(stream)).Revision);
    }
}