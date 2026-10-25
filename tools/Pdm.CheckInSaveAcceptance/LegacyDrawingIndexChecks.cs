using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

internal static partial class Program
{
 static async Task LegacyDrawingIndexScenario(string output)
 {
  Directory.CreateDirectory(output);var folder=Path.Combine(output,"files");Directory.CreateDirectory(folder);
  var json=new JavaScriptSerializer{MaxJsonLength=int.MaxValue};
  var rows=json.DeserializeObject(File.ReadAllText(Path.Combine(Path.GetDirectoryName(output),"reference-audit.json"))) as object[];
  var drawingRows=rows.Cast<Dictionary<string,object>>().Where(row=>((string)row["FileName"]).EndsWith(".SLDDRW",StringComparison.OrdinalIgnoreCase)).ToArray();
  var root=LinkedNode(folder,"ROOT.SLDASM","Assembly");var nodes=new List<object>{root};var drawings=new List<object>();var models=new List<object>();
  var versions=new Dictionary<Guid,object>();
  foreach(var row in drawingRows)
  {
   var name=(string)row["FileName"];var model=LinkedNode(folder,Path.ChangeExtension(name,".SLDPRT"),"Part");
   var drawing=LinkedNode(folder,name,"Drawing");Set(drawing,"DocumentId",Guid.Parse((string)row["Id"]));Set(drawing,"DrawingViewsRead",true);
   ((IList)Get(root,"Children")).Add(model);((IList)Get(model,"Children")).Add(drawing);((IList)Get(drawing,"ReferencedModels")).Add(model);
   var snapshot=json.Deserialize(json.Serialize(row["Snapshot"]),Type("DocumentReferenceNodeDto"));
   var version=LinkedVersion(drawing,snapshot);Set(version,"Revision",LinkedRevision((string)row["Revision"]));
   versions[(Guid)Get(drawing,"DocumentId")]=version;versions[(Guid)Get(model,"DocumentId")]=LinkedVersion(model,LinkedSnapshot(model));
   drawings.Add(drawing);models.Add(model);nodes.Add(model);nodes.Add(drawing);
  }
  versions[(Guid)Get(root,"DocumentId")]=LinkedVersion(root,LinkedSnapshot(root));
  var plugin=New("PdmAddin");var project=Guid.NewGuid();var session=(Guid)Field(plugin,"checkoutSessionId");SetField(plugin,"authenticatedUsername","tester");
  void Check(bool passed,string text){TreeCheck(passed,text);Console.WriteLine("PASS: "+text);}
  using(var client=(IDisposable)New("PdmApiClient","http://legacy-index.invalid/"))
  using(var handler=new LinkedArchiveHandler(nodes.ToArray(),versions,project,session,new List<string>(),false))
  {
   LinkedClient(plugin,client,handler);
   async Task Refresh()=>await (Task)TreeCall(plugin,"ApplyWorkingStatesAsync",root,CancellationToken.None);
   string Classify(object drawing){var history=Array.CreateInstance(Type("DocumentVersionDto"),1);history.SetValue(versions[(Guid)Get(drawing,"DocumentId")],0);return Convert.ToString(Type("PdmAddin").GetMethod("AnalyzeArchiveNode",Flags).Invoke(null,new[]{drawing,"tester",(object)history,WorkingReuseHash((string)Get(drawing,"FullPath"))}));}
   await Refresh();
   Check(drawings.Count==36&&drawings.All(drawing=>!(bool)Get(drawing,"RequiresModelUpdate")),"真实项目36份历史工程图缺少模型引用索引，不再被误判需要更新");
   Check(drawings.All(drawing=>Classify(drawing)=="Unchanged"&&(bool)Get(drawing,"DrawingReferenceIndexPending")),"最新且未改动的工程图保持未变更，只提示历史引用待补全");
   Set(models[0],"IsModifiedInSolidWorks",true);await Refresh();
   Check(!(bool)Get(drawings[0],"RequiresModelUpdate"),"没有编辑权限的只读重建标志不触发配套工程图更新");
   Set(models[0],"CheckedOutBy","tester");await Refresh();
   Check((bool)Get(drawings[0],"RequiresModelUpdate")&&Classify(drawings[0])=="DrawingUpdate","实际拥有编辑权限且存在未保存模型修改时，配套更新仍然生效");
   Set(models[0],"IsModifiedInSolidWorks",false);Set(models[0],"CheckedOutBy",null);await Refresh();
   using(var dialog=(System.Windows.Forms.Form)New("BatchOperationDialog",root,LinkedItems(nodes.ToArray()),Array.CreateInstance(Type("ProjectDto"),0),null,"tester",Enum.Parse(Type("BatchOperationKind"),"CheckIn"),null,null,null,false,false,0))
   {
    archiveRuleVersions=versions.ToDictionary(pair=>pair.Key,pair=>new[]{pair.Value});archiveRuleDelay=0;
    var loader=dialog.GetType().GetProperty("LoadArchiveVersionsAsync",Flags);loader.SetValue(dialog,Delegate.CreateDelegate(loader.PropertyType,typeof(Program).GetMethod("ArchiveRuleVersionLoader",Flags).MakeGenericMethod(Type("CadTreeNode"),Type("DocumentVersionDto"))));
    await (Task)TreeCall(dialog,"AnalyzeArchiveSelectionAsync",true);
    var selected=((IEnumerable)Get(dialog,"SelectedItems")).Cast<object>().Select(item=>Get(item,"Node")).ToArray();
    Check(!selected.Intersect(drawings).Any(),"勾选变更项不会再自动勾选这36份未修改工程图");
    Check(((string)TreeCall(dialog,"StateText",drawings[0],"tester")).Contains("历史模型引用待补全"),"用户状态明确区分历史引用缺失和工程图真实变更");
   }
   File.AppendAllText((string)Get(drawings[0],"FullPath"),"real drawing change");
   Check(Classify(drawings[0])=="Changed","历史索引缺失不会掩盖工程图文件的真实修改");
  }
 }
}
