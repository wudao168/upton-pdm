using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static partial class Program
{
 static async Task NewDrawingDiscoveryScenario(string output)
 {
  Directory.CreateDirectory(output);
  foreach(var mode in new[]{"part-saved","assembly-saved","loaded-new-view","different-folder","different-name","second-sheet","repeat","unrelated","same-name-other-copy","unsaved-drawing","drawing-entry","pure-2d"})
  {
   var folder=Path.Combine(Path.GetTempPath(),"pdm-new-drawing-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
   var root=LinkedNode(folder,"CW0030002.10.SLDASM","Assembly");var part=LinkedNode(folder,"7000093.101.02-01.SLDPRT","Part");
   var other=LinkedNode(folder,"OTHER.SLDPRT","Part");var untouched=new List<object>{root,part,other};
   ((IList)Get(root,"Children")).Add(part);((IList)Get(root,"Children")).Add(other);
   for(var i=0;i<212;i++){var node=LinkedNode(folder,"UNTOUCHED-"+i+".SLDPRT","Part");untouched.Add(node);((IList)Get(root,"Children")).Add(node);}
   archiveRuleVersions=untouched.ToDictionary(node=>(Guid)Get(node,"DocumentId"),node=>new[]{LinkedVersion(node,LinkedSnapshot(node))});
   foreach(var node in untouched){Set(node,"StoredVersionStateKnown",true);Set(node,"HasStoredVersion",true);Set(node,"NativeFileScopeVerified",true);}
   File.AppendAllText((string)Get(part,"FullPath")," actual saved geometry modification");Set(part,"CheckedOutBy","tester");
   var before=untouched.ToDictionary(node=>(string)Get(node,"FullPath"),node=>WorkingReuseHash((string)Get(node,"FullPath")));
   var tree=mode=="assembly-saved"||mode=="second-sheet"?root:part;
   var drawingFolder=mode=="different-folder"?Path.Combine(folder,"新工程图"):folder;Directory.CreateDirectory(drawingFolder);
   var drawingPath=Path.Combine(drawingFolder,mode=="different-name"?"新增加工工程图.SLDDRW":"7000093.101.02-01.SLDDRW");
   var modelPath=(string)Get(part,"FullPath");var diskPath=modelPath;
   if(mode=="same-name-other-copy"){diskPath=Path.Combine(folder,"other-workspace",Path.GetFileName(modelPath));Directory.CreateDirectory(Path.GetDirectoryName(diskPath));File.WriteAllText(diskPath,"another physical model");}
   if(mode=="unrelated")diskPath=(string)Get(other,"FullPath");
   // The drawing is created after the model/assembly tree was cached.
   if(mode!="unsaved-drawing")File.WriteAllText(drawingPath,"new native drawing saved after cached model tree");
   var bytes=File.Exists(drawingPath)?WorkingReuseHash(drawingPath):null;
   var loaded=mode=="loaded-new-view"||mode=="different-folder"||mode=="second-sheet"||mode=="pure-2d"||mode=="drawing-entry"||mode=="unsaved-drawing";
   var appType=Type("PdmAddin").GetField("application",Flags).FieldType;var interop=appType.Assembly;var modelType=interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true);
   var reads=0;var writes=new List<string>();var currentDirectory=folder;
   var nativePart=new Proxy(modelType,call=>call.MethodName=="GetPathName"?modelPath:call.MethodName=="GetType"?1:throw new Exception("重新读取了组件结构："+call.MethodName)).GetTransparentProxy();
   var view=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.View",true),call=>call.MethodName=="get_ReferencedDocument"?nativePart:call.MethodName=="get_ReferencedConfiguration"?"加工配置":call.MethodName=="GetReferencedModelName"?(mode=="unsaved-drawing"?Path.GetFileName(modelPath):modelPath):throw new Exception(call.MethodName)).GetTransparentProxy();
   var header=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.View",true),call=>call.MethodName=="GetNextView"?view:throw new Exception(call.MethodName)).GetTransparentProxy();
   var drawing=new AssemblyProxy(modelType,call=>{
    if(call.MethodName=="GetType"&&(call.MethodBase as MethodInfo)?.ReturnType==typeof(System.Type))return modelType;
    if(call.MethodName=="GetType")return 3;if(call.MethodName=="GetPathName")return mode=="unsaved-drawing"?"":drawingPath;
    if(call.MethodName=="GetFirstView")return header;
    if(call.MethodName=="GetViews")return mode=="pure-2d"?new object[]{new object[]{new object()}}:mode=="second-sheet"?new object[]{new object[]{new object()},new object[]{new object(),view}}:new object[]{new object[]{new object(),view}};
    writes.Add(call.MethodName);throw new Exception("额外工程图操作："+call.MethodName);
   }).GetTransparentProxy();
   var app=new Proxy(appType,call=>{
    switch(call.MethodName){case "GetType":return appType;case "get_ActiveDoc":return drawing;case "GetDocuments":return loaded?new[]{nativePart,drawing}:new[]{nativePart};case "GetOpenDocumentByName":return loaded&&(string)call.Args[0]==drawingPath?drawing:null;
     case "GetCurrentWorkingDirectory":return currentDirectory;case "SetCurrentWorkingDirectory":currentDirectory=(string)call.Args[0];return true;
     case "GetDocumentDependencies2":reads++;return mode=="loaded-new-view"||mode=="pure-2d"?new[]{"stale disk model","Z:\\old\\OTHER.SLDPRT"}:new[]{Path.GetFileName(diskPath),diskPath};
     default:writes.Add(call.MethodName);throw new Exception("发现工程图不允许打开、关闭、保存或全树扫描："+call.MethodName);}
   }).GetTransparentProxy();
   var scanner=New("SolidWorksReferenceTreeScanner",app);
   if(mode=="drawing-entry")
   {
    tree=New("CadTreeNode");Set(tree,"FullPath",drawingPath);Set(tree,"FileName",Path.GetFileName(drawingPath));Set(tree,"Kind",Enum.Parse(Type("CadDocumentKind"),"Drawing"));
    TreeCall(scanner,"UpdateDrawingReferences",drawing,tree);
    if(((IList)Get(tree,"ReferencedModels")).Count!=1)throw new Exception("工程图入口丢失真实模型关联");
   }
   else
   {
    // Baseline uses the old cached-tree behavior; the new entry point must add the drawing.
    var refresh=scanner.GetType().GetMethod("RefreshRelatedDrawings",Flags);if(refresh!=null)refresh.Invoke(scanner,new[]{tree});
    var nodes=CollectNewDrawingNodes(tree);var expected=!new[]{"unrelated","same-name-other-copy","unsaved-drawing","pure-2d"}.Contains(mode);
    if(nodes.Count!=(expected?1:0))throw new Exception("缓存设计树漏掉新增工程图或误带无关文件："+mode+" drawings="+nodes.Count);
    if(expected)
    {
     var found=nodes.Single();if((string)Get(found,"FullPath")!=drawingPath||((IList)Get(found,"ReferencedModels")).Count!=1)throw new Exception("工程图关联不准确："+mode);
     var all=(IEnumerable)TreeCall(New("PdmAddin"),"BuildBatchOperationItems",tree);
     var selected=LinkedItems(part);var include=Type("PdmAddin").GetMethod("IncludeSelectedModelDrawings",Flags);
     var complete=((IEnumerable)include.Invoke(null,new[]{selected,all})).Cast<object>().Select(item=>Get(item,"Node")).ToArray();
     if(complete.Length!=2||!complete.Contains(part)||!complete.Contains(found))throw new Exception("零件勾选没有带入新增工程图："+mode);
     if(mode=="repeat"){var count=reads;refresh.Invoke(scanner,new[]{tree});if(CollectNewDrawingNodes(tree).Count!=1||reads!=count)throw new Exception("重复检查重复添加或重读未变化工程图");}
     if(Get(found,"DocumentId")!=null)throw new Exception("新增工程图被错误关联到已有图档");
     if(mode=="part-saved"||mode=="assembly-saved")
     {
      var project=New("ProjectDto");var projectId=Guid.NewGuid();Set(project,"Id",projectId);Set(project,"Code","P700014-1");Set(project,"Name","AAAA");Set(project,"CanSubmitArchive",true);
      var projects=Array.CreateInstance(Type("ProjectDto"),1);projects.SetValue(project,0);
      using(var dialog=(Form)New("BatchOperationDialog",tree,all,projects,projectId,"tester",Enum.Parse(Type("BatchOperationKind"),"CheckIn"),null,null,null,false,false,0))
      {
       var loader=dialog.GetType().GetProperty("LoadArchiveVersionsAsync",Flags);loader.SetValue(dialog,Delegate.CreateDelegate(loader.PropertyType,typeof(Program).GetMethod("ArchiveRuleVersionLoader",Flags).MakeGenericMethod(Type("CadTreeNode"),Type("DocumentVersionDto"))));
       await (Task)TreeCall(dialog,"AnalyzeArchiveSelectionAsync",true);
       var checkedNodes=((IEnumerable)Get(dialog,"SelectedItems")).Cast<object>().Select(item=>Get(item,"Node")).ToArray();
       if(checkedNodes.Length!=2||!checkedNodes.Contains(part)||!checkedNodes.Contains(found))throw new Exception("存档页面未准确勾选修改零件和新增工程图："+mode);
      }
     }
    }
   }
   if(writes.Count!=0||currentDirectory!=folder)throw new Exception("发现工程图改变了原生窗口、源文件或搜索目录："+mode);
   if(mode=="unsaved-drawing")
   using(var pane=(Control)New("PdmTaskPaneControl","新工程图路径检查"))
   using(var client=(IDisposable)New("PdmApiClient","http://new-drawing-fixture.invalid/"))
   {
    var plugin=New("PdmAddin");SetField(plugin,"application",app);SetField(plugin,"apiClient",client);SetField(plugin,"taskPaneControl",pane);pane.CreateControl();
    var unsaved=New("CadTreeNode");Set(unsaved,"FullPath","");Set(unsaved,"FileName","工程图1");Set(unsaved,"Kind",Enum.Parse(Type("CadDocumentKind"),"Drawing"));
    TreeCall(plugin,"ResolveActiveDrawingSource",unsaved,part);
    ((IDictionary)Field(plugin,"controlledOpenManifests")).Add(folder,New("ControlledOpenManifestDto"));
    TreeCall(plugin,"CacheActiveTree","unsaved-drawing",unsaved,null);
    if(!(bool)TreeCall(plugin,"TryRestoreActiveTreeFromCache","unsaved-drawing"))throw new Exception("未保存工程图的空路径导致缓存树刷新失败");
   }
   foreach(var pair in before)if(WorkingReuseHash(pair.Key)!=pair.Value)throw new Exception("改写了其他模型："+mode);
   if(bytes!=null&&WorkingReuseHash(drawingPath)!=bytes)throw new Exception("发现阶段改写了工程图");
   Console.WriteLine("PASS: new drawing discovery "+mode+"; 215 model files unchanged, exact source association, no open/close/save or full assembly scan");
  }
  await Task.CompletedTask;
 }
 static List<object> CollectNewDrawingNodes(object root)
 {
  var result=new List<object>();foreach(var child in (IEnumerable)Get(root,"Children")){if(Get(child,"Kind").ToString()=="Drawing")result.Add(child);else result.AddRange(CollectNewDrawingNodes(child));}return result;
 }
}
