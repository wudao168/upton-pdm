using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
namespace Upton.Pdm.Infrastructure;

public sealed record ProjectWorkbookMetadata(string Title, string Company, string Scope, string VersionLabel, string Version,
    DateTimeOffset ExportedAt, IReadOnlyList<KeyValuePair<string,string>> Fields, string ExportedBy = "—", string Notes = "")
{
    public static ProjectWorkbookMetadata ForProject(Project project, Project main, OrganizationDirectory? directory, string title, string versionLabel, string version, DateTimeOffset at, string actor, string scope = "当前项目")
    {
        string Names(IEnumerable<string?> people) => string.Join("、", people.Where(x=>!string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).Select(x=>directory?.Users.FirstOrDefault(u=>u.Username.Equals(x,StringComparison.OrdinalIgnoreCase))?.DisplayName ?? x));
        var child = project.Id != main.Id;
        KeyValuePair<string,string> Field(string label, string? value) => new(label,string.IsNullOrWhiteSpace(value)?"—":value);
        return new(title,project.OrganizationName ?? main.OrganizationName ?? "UPTON 阿普顿",scope,versionLabel,version,at.ToOffset(TimeSpan.FromHours(8)),
        [Field("主项目号",main.Code),Field("主项目名称",main.Name),Field("型号",main.DeviceModel),Field("客户",project.CustomerName ?? main.CustomerName),
         Field("子项目号",child?project.Code:null),Field("子项目名称",child?project.Name:null),Field("子项目型号",child?project.DeviceModel:null),Field("整套数量",project.Quantity.ToString(CultureInfo.InvariantCulture)+"套"),
         Field("事业部",project.ExecutionUnitName ?? main.ExecutionUnitName),Field("项目经理",Names(new[]{project.PrimaryProjectManager ?? main.PrimaryProjectManager}.Concat(project.CollaborativeProjectManagers))),
         Field("主设",Names(project.DesignLeads.Count>0?project.DesignLeads.Select(x=>(string?)x):new[]{project.DesignLead ?? main.DesignLead})),Field("工程师",Names(project.Designers))],Names([actor]));
    }
}

public static partial class ProjectWorkbookHeader
{
    private static readonly XNamespace Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace DocRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    public static byte[] Apply(byte[] workbook, ProjectWorkbookMetadata metadata, int removeRows = 0, int originalHeaderRow = 1)
    {
        using var stream = new MemoryStream(); stream.Write(workbook); stream.Position=0;
        using(var archive = new ZipArchive(stream,ZipArchiveMode.Update,true))
        {
            XDocument Load(string path, string fallback) { var entry=archive.GetEntry(path); if(entry is null)return XDocument.Parse(fallback);using var reader=entry.Open();return XDocument.Load(reader); }
            void Save(string path, XDocument document) { archive.GetEntry(path)?.Delete();using var target=archive.CreateEntry(path).Open();document.Save(target); }
            void Bytes(string path, byte[] data) { archive.GetEntry(path)?.Delete();using var target=archive.CreateEntry(path).Open();target.Write(data); }
            var types=Load("[Content_Types].xml", "");
            void Override(string path,string type) { XNamespace ns="http://schemas.openxmlformats.org/package/2006/content-types";if(!types.Root!.Elements().Any(e=>(string?)e.Attribute("PartName")=="/"+path))types.Root.Add(new XElement(ns+"Override",new XAttribute("PartName","/"+path),new XAttribute("ContentType",type))); }
            var styles=Load("xl/styles.xml",$"<styleSheet xmlns='{Ns}'><fonts count='1'><font><sz val='11'/><name val='Microsoft YaHei'/></font></fonts><fills count='2'><fill><patternFill patternType='none'/></fill><fill><patternFill patternType='gray125'/></fill></fills><borders count='1'><border/></borders><cellStyleXfs count='1'><xf numFmtId='0' fontId='0' fillId='0' borderId='0'/></cellStyleXfs><cellXfs count='1'><xf numFmtId='0' fontId='0' fillId='0' borderId='0' xfId='0'/></cellXfs></styleSheet>");
            int Add(string collection,XElement item) { var parent=styles.Root!.Element(Ns+collection)!;var index=parent.Elements().Count();parent.Add(item);parent.SetAttributeValue("count",index+1);return index; }
            int Font(int size,bool bold=false) => Add("fonts",new XElement(Ns+"font",bold?new XElement(Ns+"b"):null,new XElement(Ns+"sz",new XAttribute("val",size)),new XElement(Ns+"color",new XAttribute("rgb","FF203149")),new XElement(Ns+"name",new XAttribute("val","Microsoft YaHei"))));
            var font=Font(11);var titleFont=Font(18,true);
            var fill=Add("fills",new XElement(Ns+"fill",new XElement(Ns+"patternFill",new XAttribute("patternType","solid"),new XElement(Ns+"fgColor",new XAttribute("rgb","FFF1F5FA")),new XElement(Ns+"bgColor",new XAttribute("indexed",64)))));
            var border=Add("borders",new XElement(Ns+"border",new[]{"left","right","top","bottom"}.Select(name=>new XElement(Ns+name,new XAttribute("style","thin"),new XElement(Ns+"color",new XAttribute("rgb","FFBBC9DA"))))));
            int Style(int f,int bg,int b,string align) => Add("cellXfs",new XElement(Ns+"xf",new XAttribute("numFmtId",0),new XAttribute("fontId",f),new XAttribute("fillId",bg),new XAttribute("borderId",b),new XAttribute("xfId",0),new XAttribute("applyFont",1),new XAttribute("applyFill",1),new XAttribute("applyBorder",1),new XAttribute("applyAlignment",1),new XElement(Ns+"alignment",new XAttribute("horizontal",align),new XAttribute("vertical","center"),new XAttribute("wrapText",1))));
            var titleStyle=Style(titleFont,0,0,"center");var valueStyle=Style(font,0,border,"left");var labelStyle=Style(font,fill,border,"left");var infoStyle=Style(font,0,0,"left");var tableHeaderStyle=Style(font,fill,border,"center");
            Save("xl/styles.xml",styles);Override("xl/styles.xml","application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml");
            var workbookRels=Load("xl/_rels/workbook.xml.rels",$"<Relationships xmlns='{Rel}'/>");
            if(!workbookRels.Root!.Elements().Any(e=>((string?)e.Attribute("Type"))?.EndsWith("/styles",StringComparison.Ordinal)==true))workbookRels.Root.Add(new XElement(Rel+"Relationship",new XAttribute("Id","uplmStyles"),new XAttribute("Type",DocRel.NamespaceName+"/styles"),new XAttribute("Target","styles.xml")));
            Save("xl/_rels/workbook.xml.rels",workbookRels);
            var book=Load("xl/workbook.xml", "");var names=book.Root!.Element(Ns+"definedNames");if(names is null){names=new XElement(Ns+"definedNames");book.Root.Add(names);}
            var sheets=archive.Entries.Where(e=>Regex.IsMatch(e.FullName,@"^xl/worksheets/sheet\d+\.xml$")).Select(e=>e.FullName).ToArray();
            var logoName=typeof(ProjectWorkbookHeader).Assembly.GetManifestResourceNames().First(n=>n.EndsWith("upton-company-logo.png",StringComparison.OrdinalIgnoreCase));
            using(var logo=typeof(ProjectWorkbookHeader).Assembly.GetManifestResourceStream(logoName)!){using var bytes=new MemoryStream();logo.CopyTo(bytes);Bytes("xl/media/uplm-company-logo.png",bytes.ToArray());}
            XNamespace ct="http://schemas.openxmlformats.org/package/2006/content-types";if(!types.Root!.Elements().Any(e=>(string?)e.Attribute("Extension")=="png"))types.Root.AddFirst(new XElement(ct+"Default",new XAttribute("Extension","png"),new XAttribute("ContentType","image/png")));
            for(var index=0;index<sheets.Length;index++)
            {
                var path=sheets[index];var sheet=Load(path, "");var root=sheet.Root!;var data=root.Element(Ns+"sheetData")!;var delta=6-removeRows;
                foreach(var row in data.Elements().Where(r=>(int?)r.Attribute("r")<=removeRows).ToArray())row.Remove();
                if(removeRows>0) foreach(var merge in root.Descendants(Ns+"mergeCell").Where(e=>int.Parse(Regex.Match((string)e.Attribute("ref")!,@"\d+").Value,CultureInfo.InvariantCulture)<=removeRows).ToArray())merge.Remove();
                string Shift(string value) => Regex.Replace(value,@"(\$?[A-Z]+\$?)(\d+)",m=>m.Groups[1].Value+(int.Parse(m.Groups[2].Value,CultureInfo.InvariantCulture)+delta));
                foreach(var element in root.Descendants())foreach(var attribute in element.Attributes().ToArray())
                    if(attribute.Name.LocalName=="r" && element.Name.LocalName=="row")attribute.Value=(int.Parse(attribute.Value,CultureInfo.InvariantCulture)+delta).ToString(CultureInfo.InvariantCulture);
                    else if(new[]{"r","ref","sqref","topLeftCell","activeCell"}.Contains(attribute.Name.LocalName))attribute.Value=Shift(attribute.Value);
                var lastColumn=data.Descendants(Ns+"c").Select(e=>ColumnNumber(Regex.Match((string)e.Attribute("r")!,"[A-Z]+").Value)).DefaultIfEmpty(8).Max();var columns=Math.Max(8,Math.Min(18,lastColumn));
                var merges=root.Element(Ns+"mergeCells");if(merges is null){merges=new XElement(Ns+"mergeCells");data.AddAfterSelf(merges);}
                void Merge(string reference) { merges.Add(new XElement(Ns+"mergeCell",new XAttribute("ref",reference))); }
                XElement Cell(int row,int col,string text,int style) => new(Ns+"c",new XAttribute("r",Column(col)+row),new XAttribute("s",style),new XAttribute("t","inlineStr"),new XElement(Ns+"is",new XElement(Ns+"t",text)));
                XElement Row(int row,int height) => new(Ns+"row",new XAttribute("r",row),new XAttribute("ht",height),new XAttribute("customHeight",1));
                var top=new List<XElement>();var title=Row(1,32);title.Add(Cell(1,0,metadata.Title,titleStyle));top.Add(title);Merge($"A1:{Column(columns-1)}1");
                var summary=Row(2,string.IsNullOrWhiteSpace(metadata.Notes)?24:38);summary.Add(Cell(2,0,$"{metadata.Company}　范围：{metadata.Scope}　{metadata.VersionLabel}：{metadata.Version}　导出日期：{metadata.ExportedAt:yyyy-MM-dd HH:mm}{(string.IsNullOrWhiteSpace(metadata.Notes)?"":"\n"+metadata.Notes)}",infoStyle));top.Add(summary);Merge($"A2:{Column(columns-1)}2");
                for(var r=0;r<3;r++){var row=Row(r+3,28);for(var pair=0;pair<4;pair++){var start=pair*columns/4;var end=(pair+1)*columns/4-1;var field=metadata.Fields.ElementAtOrDefault(r*4+pair);row.Add(Cell(r+3,start,field.Key??"",labelStyle),Cell(r+3,start+1,field.Value??"—",valueStyle));if(end>start+1)Merge($"{Column(start+1)}{r+3}:{Column(end)}{r+3}");}top.Add(row);}
                foreach(var cell in data.Elements(Ns+"row").Where(row=>(int?)row.Attribute("r")==originalHeaderRow+delta).Elements(Ns+"c"))cell.SetAttributeValue("s",tableHeaderStyle);
                top.Add(Row(6,8));data.AddFirst(top);merges.SetAttributeValue("count",merges.Elements().Count());
                var properties = root.Element(Ns+"sheetPr"); if(properties is null){properties=new XElement(Ns+"sheetPr");root.AddFirst(properties);} properties.Element(Ns+"pageSetUpPr")?.Remove();properties.Add(new XElement(Ns+"pageSetUpPr",new XAttribute("fitToPage",1)));
                var pane=root.Descendants(Ns+"pane").FirstOrDefault();if(pane is null){ var views=new XElement(Ns+"sheetViews",new XElement(Ns+"sheetView",new XAttribute("workbookViewId",0),pane=new XElement(Ns+"pane",new XAttribute("state","frozen"),new XAttribute("topLeftCell","A"+(originalHeaderRow+delta+1)),new XAttribute("activePane","bottomLeft"))));root.Add(views); } if(pane is not null)pane.SetAttributeValue("ySplit",Math.Max(originalHeaderRow+delta,(int?)pane.Attribute("ySplit")+delta??0));
                root.Elements(Ns+"dimension").Remove();root.Elements(Ns+"drawing").Remove();root.Elements(Ns+"pageMargins").Remove();root.Elements(Ns+"pageSetup").Remove();root.Elements(Ns+"headerFooter").Remove();
                root.Add(new XElement(Ns+"pageMargins",new XAttribute("left",.3),new XAttribute("right",.3),new XAttribute("top",.3),new XAttribute("bottom",.3),new XAttribute("header",.1),new XAttribute("footer",.1)),new XElement(Ns+"pageSetup",new XAttribute("orientation","landscape"),new XAttribute("paperSize",9),new XAttribute("fitToWidth",1),new XAttribute("fitToHeight",0)),new XElement(Ns+"headerFooter",new XElement(Ns+"oddFooter",$"&L导出人：{metadata.ExportedBy}　来源：UPLM&R第&P页 / 共&N页")));
                var drawing=$"uplm-header{index+1}.xml";root.Add(new XElement(Ns+"drawing",new XAttribute(DocRel+"id","uplmHeader")));
                var relPath="xl/worksheets/_rels/"+Path.GetFileName(path)+".rels";var rels=Load(relPath,$"<Relationships xmlns='{Rel}'/>");rels.Root!.Add(new XElement(Rel+"Relationship",new XAttribute("Id","uplmHeader"),new XAttribute("Type",DocRel.NamespaceName+"/drawing"),new XAttribute("Target","../drawings/"+drawing)));Save(relPath,rels);
                var drawingPath="xl/drawings/"+drawing;Save(drawingPath,XDocument.Parse($"<xdr:wsDr xmlns:xdr='http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing' xmlns:a='http://schemas.openxmlformats.org/drawingml/2006/main' xmlns:r='{DocRel}'><xdr:oneCellAnchor><xdr:from><xdr:col>{columns-3}</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>0</xdr:row><xdr:rowOff>80000</xdr:rowOff></xdr:from><xdr:ext cx='1400000' cy='180000'/><xdr:pic><xdr:nvPicPr><xdr:cNvPr id='1' name='公司LOGO'/><xdr:cNvPicPr/></xdr:nvPicPr><xdr:blipFill><a:blip r:embed='image'/><a:stretch><a:fillRect/></a:stretch></xdr:blipFill><xdr:spPr><a:prstGeom prst='rect'><a:avLst/></a:prstGeom></xdr:spPr></xdr:pic><xdr:clientData/></xdr:oneCellAnchor></xdr:wsDr>"));Override(drawingPath,"application/vnd.openxmlformats-officedocument.drawing+xml");Save("xl/drawings/_rels/"+drawing+".rels",XDocument.Parse($"<Relationships xmlns='{Rel}'><Relationship Id='image' Type='{DocRel}/image' Target='../media/uplm-company-logo.png'/></Relationships>"));
                var sheetId=index.ToString(CultureInfo.InvariantCulture);names.Elements().Where(e=>(string?)e.Attribute("name")=="_xlnm.Print_Titles" && (string?)e.Attribute("localSheetId")==sheetId).Remove();var sheetName=(string?)book.Root.Element(Ns+"sheets")!.Elements().ElementAt(index).Attribute("name")??"Sheet";names.Add(new XElement(Ns+"definedName",new XAttribute("name","_xlnm.Print_Titles"),new XAttribute("localSheetId",sheetId),$"'{sheetName.Replace("'","''")}'!$1:${originalHeaderRow+delta}"));
                var order="sheetPr dimension sheetViews sheetFormatPr cols sheetData sheetProtection protectedRanges scenarios autoFilter sortState dataConsolidate customSheetViews mergeCells phoneticPr conditionalFormatting dataValidations hyperlinks printOptions pageMargins pageSetup headerFooter rowBreaks colBreaks drawing legacyDrawing legacyDrawingHF picture tableParts extLst".Split(' ');var elements=root.Elements().OrderBy(e=>Array.IndexOf(order,e.Name.LocalName) is var o && o>=0?o:99).ToArray();root.ReplaceNodes(elements);Save(path,sheet);
            }
            Save("xl/workbook.xml",book);Save("[Content_Types].xml",types);
        }
        return stream.ToArray();
    }
    private static int ColumnNumber(string value) => value.Aggregate(0,(n,c)=>n*26+c-'A'+1);
    private static string Column(int index){var result="";for(var n=index+1;n>0;n=(n-1)/26)result=(char)('A'+(n-1)%26)+result;return result;}
}