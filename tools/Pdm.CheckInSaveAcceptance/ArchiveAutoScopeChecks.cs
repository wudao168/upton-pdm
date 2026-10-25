using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static partial class Program
{
 static async Task ArchiveAutoScopeScenario(Form host,string output)
 {
  Directory.CreateDirectory(output);
  var directory=Path.Combine(Path.GetTempPath(),"pdm-auto-scope-"+Guid.NewGuid().ToString("N"));var folder=Path.Combine(directory,"View","P700014-1");Directory.CreateDirectory(folder);
  var identity=Type("PdmDocumentIdentityStore");var bindings=identity.GetField("bindingDirectory",Flags);var rootResolver=identity.GetField("workspaceRootResolver",Flags);
  var previousBindings=bindings.GetValue(null);var previousRoot=rootResolver.GetValue(null);
  bindings.SetValue(null,Path.Combine(directory,"bindings"));rootResolver.SetValue(null,new Func<string>(()=>directory));
  try
  {
   var root=LinkedNode(folder,"CW0030002.10.SLDASM","Assembly");var part=LinkedNode(folder,"7000093.101.03-02.SLDPRT","Part");var drawing=LinkedNode(folder,"7000093.101.03-02.SLDDRW","Drawing");
   var valve=LinkedNode(folder,"SS5Y-8位电磁阀座.SLDASM","Assembly");var imported=LinkedNode(folder,"LHH30CA2R1000Z0C-E20E20(1).SLDASM","Assembly");
   var nodes=new List<object>{root,part,drawing,valve,imported};
   ((IList)Get(root,"Children")).Add(part);((IList)Get(root,"Children")).Add(valve);((IList)Get(root,"Children")).Add(imported);((IList)Get(part,"Children")).Add(drawing);((IList)Get(drawing,"ReferencedModels")).Add(part);
   for(var index=0;index<210;index++){var node=LinkedNode(folder,"UNTOUCHED-"+index+".SLDPRT","Part");nodes.Add(node);((IList)Get(root,"Children")).Add(node);}
   var project=Guid.NewGuid();archiveRuleVersions=nodes.ToDictionary(node=>(Guid)Get(node,"DocumentId"),node=>new[]{LinkedVersion(node,LinkedSnapshot(node))});
   foreach(var node in nodes)
   {
    var version=archiveRuleVersions[(Guid)Get(node,"DocumentId")][0];Set(node,"OpenedVersionId",Get(version,"Id"));Set(node,"StoredVersionStateKnown",true);Set(node,"HasStoredVersion",true);
    Set(node,"LatestStoredSha256",Get(version,"Sha256"));Set(node,"LatestVersionSha256",Get(version,"Sha256"));Set(node,"LatestStructureIndexPending",false);Set(node,"NativeFileScopeVerified",true);
    if(!(bool)identity.GetMethod("TryWriteControlledVersion",Flags).Invoke(null,new object[]{Get(node,"FullPath"),Get(node,"DocumentId"),project,Get(version,"Id"),"W1",Get(version,"Sha256"),Get(version,"FileLength")}))throw new Exception("Cannot create authoritative isolated file binding");
    File.SetAttributes((string)Get(node,"FullPath"),FileAttributes.ReadOnly);
   }
   var valveChild=New("CadTreeNode");Set(valveChild,"FileName","厂家内部件");Set(valveChild,"Kind",Enum.Parse(Type("CadDocumentKind"),"Part"));((IList)Get(valve,"Children")).Add(valveChild);
   var embedded=New("CadTreeNode");Set(embedded,"FileName","LHH30CA2R1000Z0C-E20E20(1).STEP");Set(embedded,"Kind",Enum.Parse(Type("CadDocumentKind"),"Assembly"));Set(embedded,"Status",Enum.Parse(Type("CadReferenceStatus"),"Virtual"));((IList)Get(imported,"Children")).Add(embedded);
   Set(Get(archiveRuleVersions[(Guid)Get(imported,"DocumentId")][0],"ReferenceSnapshot"),"StructureIndexPending",true);
   var analyze=Type("PdmAddin").GetMethod("AnalyzeArchiveNode",Flags);
   string Classify(object node){var versions=Array.CreateInstance(Type("DocumentVersionDto"),1);versions.SetValue(archiveRuleVersions[(Guid)Get(node,"DocumentId")][0],0);return Convert.ToString(analyze.Invoke(null,new[]{node,"tester",(object)versions,WorkingReuseHash((string)Get(node,"FullPath"))}));}
   TreeCheck(Classify(valve)=="Unchanged","未修改电磁阀座的结构展开仍被误判变更");Console.WriteLine("PASS: exact unchanged bound SS5Y assembly bytes remain unchanged after passive child expansion");
   TreeCheck(Classify(imported)=="Unchanged","嵌入导入结构索引补全被误判工程修改");Console.WriteLine("PASS: unchanged imported assembly remains unchanged when embedded tree and historical index become complete");
   Set(valve,"IsModifiedInSolidWorks",true);TreeCheck(Classify(valve)=="Pending","真实未保存修改被未变更规则掩盖");Set(valve,"IsModifiedInSolidWorks",false);
   Console.WriteLine("PASS: an actual native unsaved flag still requires validation and is not hidden by unchanged disk bytes");
   Set(valve,"CheckedOutBy","tester");TreeCheck(Classify(valve)=="Changed","已授权的真实结构修改被忽略");Set(valve,"CheckedOutBy",null);
   Console.WriteLine("PASS: explicitly editable assembly structure changes still remain archive candidates");
   File.SetAttributes((string)Get(part,"FullPath"),FileAttributes.Normal);File.AppendAllText((string)Get(part,"FullPath")," actual geometry change");Set(part,"CheckedOutBy","tester");
   Set(drawing,"DrawingViewsRead",true);Set(drawing,"RequiresModelUpdate",true);
   TreeCheck(Classify(part)=="Changed"&&Classify(drawing)=="DrawingUpdate","零件真实修改或配套2D更新被漏掉");Console.WriteLine("PASS: changed 7000093 model and its paired 2D remain actionable while other 213 files stay unchanged");
   var dto=New("ProjectDto");Set(dto,"Id",project);Set(dto,"Code","P700014-1");Set(dto,"Name","AAAA");Set(dto,"CanSubmitArchive",true);var projects=Array.CreateInstance(Type("ProjectDto"),1);projects.SetValue(dto,0);
   using(var dialog=(Form)New("BatchOperationDialog",root,LinkedItems(nodes.ToArray()),projects,project,"tester",Enum.Parse(Type("BatchOperationKind"),"CheckIn"),null,null,null,false,false,0))
   {
    var loader=dialog.GetType().GetProperty("LoadArchiveVersionsAsync",Flags);loader.SetValue(dialog,Delegate.CreateDelegate(loader.PropertyType,typeof(Program).GetMethod("ArchiveRuleVersionLoader",Flags).MakeGenericMethod(Type("CadTreeNode"),Type("DocumentVersionDto"))));
    await (Task)TreeCall(dialog,"AnalyzeArchiveSelectionAsync",true);
    var selected=((IEnumerable)Get(dialog,"SelectedItems")).Cast<object>().Select(item=>Get(item,"Node")).ToArray();
    TreeCheck(selected.Length==2&&selected.Contains(part)&&selected.Contains(drawing),"勾选变更项仍勾选无关装配体");
    TreeCheck(!(bool)TreeCall(dialog,"CanSelectNode",valve)&&!(bool)TreeCall(dialog,"CanSelectNode",imported),"未修改的非编辑装配仍允许被全选或手动误提交");
    Console.WriteLine("PASS: actual 215-file selection checks exactly the modified part and paired drawing; unchanged supplier/import assemblies are disabled");
    dialog.TopLevel=false;host.ClientSize=dialog.ClientSize;host.Controls.Add(dialog);dialog.Show();
    while((bool)Field(dialog,"archiveAnalysisInProgress"))await Task.Delay(5);
    var recheck=(Button)Field(dialog,"recheck");recheck.PerformClick();
    while((bool)Field(dialog,"archiveAnalysisInProgress"))await Task.Delay(5);
    TreeCheck(!dialog.IsDisposed&&dialog.DialogResult!=DialogResult.OK&&((Button)Field(dialog,"execute")).Enabled,"重新检查错误提交或确认按钮仍不可用");
    Console.WriteLine("PASS: optional recheck preserves the page and selection, while one confirm can start the automatic archive workflow");
    TreeCall(dialog,"ToggleShowCheckedOnly");((TreeView)Field(dialog,"files")).ExpandAll();
    using(var bitmap=new Bitmap(dialog.Width,dialog.Height)){dialog.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));bitmap.Save(Path.Combine(output,"corrected-single-model-selection.png"));}
    foreach(var node in nodes.Where(node=>!ReferenceEquals(node,part)))TreeCheck(WorkingReuseHash((string)Get(node,"FullPath"))==(string)Get(archiveRuleVersions[(Guid)Get(node,"DocumentId")][0],"Sha256"),"检查过程改写了未修改源文件");
    Console.WriteLine("PASS: classification and optional recheck preserve all unselected file bytes and the complete embedded tree");
   }
  }
  finally
  {
   bindings.SetValue(null,previousBindings);rootResolver.SetValue(null,previousRoot);
   foreach(var file in Directory.GetFiles(directory,"*",SearchOption.AllDirectories))File.SetAttributes(file,FileAttributes.Normal);
   Directory.Delete(directory,true);
  }
 }
}
