using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Remoting.Messaging;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

internal static partial class Program
{
 static async Task VirtualCycleGraphScenario(string output,string statePath)
 {
  output=Path.GetFullPath(output);Directory.CreateDirectory(output);
  void Check(bool ok,string text){if(!ok)throw new Exception(text);Console.WriteLine("PASS: "+text);}
  var json=new JavaScriptSerializer{MaxJsonLength=int.MaxValue,RecursionLimit=512};
  var state=(Dictionary<string,object>)json.DeserializeObject(File.ReadAllText(statePath));
  var input=(Dictionary<string,object>)((object[])state["Roots"]).Single();
  var rows=((object[])input["Nodes"]).Cast<Dictionary<string,object>>().ToArray();
  var paths=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);var ids=new Dictionary<string,Guid>(StringComparer.OrdinalIgnoreCase);
  var nodes=new Dictionary<string,object>();
  foreach(var row in rows){
   var original=(string)row["Path"];if(!paths.TryGetValue(original,out var path)){path=Path.Combine(output,"files",paths.Count.ToString(),Path.GetFileName(original));Directory.CreateDirectory(Path.GetDirectoryName(path));File.WriteAllText(path,"isolated native bytes");paths[original]=path;ids[original]=Guid.NewGuid();}
   var n=New("CadTreeNode");nodes[(string)row["Address"]]=n;
   Set(n,"FullPath",path);Set(n,"SourceReferencePath",path);Set(n,"FileName",Path.GetFileName(original));Set(n,"DisplayName",Path.GetFileName(original));Set(n,"InstancePath",row["Instance"]);Set(n,"ComponentSelectionName",row["Selection"]);
   Set(n,"DocumentId",(Guid?)ids[original]);Set(n,"Revision","W1");Set(n,"CurrentRevision","W1");Set(n,"NativeFileScopeVerified",true);
   foreach(var pair in new[]{new[]{"Kind","CadDocumentKind"},new[]{"Role","CadReferenceRole"},new[]{"Status","CadReferenceStatus"}})Set(n,pair[0],Enum.ToObject(Type(pair[1]),row[pair[0]]));
   Set(n,"IsContextReference",row["Context"]);Set(n,"ReferenceTypeVerified",row["Verified"]);Set(n,"ReferenceTypeUnverified",row["Unverified"]);Set(n,"IsOutsideNativeFileScope",row["Outside"]);
  }
  var scope=new HashSet<string>(paths.Values,StringComparer.OrdinalIgnoreCase);
  foreach(var row in rows){var n=nodes[(string)row["Address"]];Set(n,"NativeFileScopePaths",scope);foreach(var a in (object[])row["Children"])((IList)Get(n,"Children")).Add(nodes[(string)a]);foreach(var a in (object[])row["Models"])((IList)Get(n,"ReferencedModels")).Add(nodes[(string)a]);}
  var names=new[]{"70004449.SLDASM","70004449.F1.SLDASM","70004449.F1.01.02.SLDASM","70004449.F1.01-01.SLDPRT","70004449.F1.01.02-04.SLDPRT"};
  var selected=names.Select(name=>nodes.Values.Where(n=>(string)Get(n,"FileName")==name&&Convert.ToInt32(Get(n,"Role"))==0).OrderByDescending(n=>((IList)Get(n,"Children")).Count).First()).ToArray();
  var items=LinkedItems(selected);var analyzer=Type("ArchiveReferenceCycles").GetMethod("Analyze",Flags);var validate=Type("ArchiveReferenceCycles").GetMethod("Validate",Flags);
  int Groups()=>((IEnumerable)analyzer.Invoke(null,new object[]{items})).Cast<object>().Count();
  Check(Groups()==1,"真实虚拟嵌套图在前置阶段识别同一候选环，不再到第7步才发现");
  var policyType=assembly.GetType("Upton.Pdm.Domain.ArchiveWarningPolicy",true);
  void Policy(bool strict){var p=Activator.CreateInstance(policyType);Set(p,"BlockAll",strict);Type("ArchiveWarningGate").GetMethod("Configure",Flags).Invoke(null,new[]{p});}
  Policy(false);
  var plugin=New("PdmAddin");var appType=Type("PdmAddin").GetField("application",Flags).FieldType;var interop=appType.Assembly;
  var reads=new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);var evidenceMode=false;
  object Model(string path){
   var ext=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDocExtension",true),call=>{
    if(call.MethodName!="ListExternalFileReferences2")throw new Exception("禁止额外原生操作："+call.MethodName);
    return new ReturnMessage(null,new object[]{new[]{(string)Get(selected[0],"FullPath")},new[]{""},new[]{"孔1"},new[]{"面"},new[]{3},new[]{""},new[]{"零件<1>"},new[]{0},new[]{""}},9,call.LogicalCallContext,call);
   }).GetTransparentProxy();
   var manager=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.FeatureManager",true),call=>call.MethodName=="GetFeatures"?new object[0]:call.MethodName=="GetFeatureCount"?(object)0:throw new Exception(call.MethodName)).GetTransparentProxy();
   var sketches=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.SketchManager",true),call=>call.MethodName=="GetSketchBlockDefinitions"?new object[0]:throw new Exception(call.MethodName)).GetTransparentProxy();
   return new AssemblyProxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true),call=>call.MethodName=="GetPathName"?path:call.MethodName=="GetType"?(object)1:call.MethodName=="get_Extension"?ext:call.MethodName=="get_FeatureManager"?manager:call.MethodName=="get_SketchManager"?sketches:call.MethodName=="GetEquationMgr"||call.MethodName=="GetDesignTable"?null:throw new Exception("禁止改图："+call.MethodName)).GetTransparentProxy();
  }
  SetField(plugin,"application",new Proxy(appType,call=>{
   if(call.MethodName=="get_ActiveDoc")return null;
   if(call.MethodName=="GetOpenDocumentByName"){var path=(string)call.Args[0];reads[path]=reads.TryGetValue(path,out var count)?count+1:1;return evidenceMode?Model(path):null;}
   throw new Exception("禁止整装加载、保存、改名或上传："+call.MethodName);
  }).GetTransparentProxy());
  await (Task)TreeCall(plugin,"CompleteCycleEvidenceAsync",items,CancellationToken.None);
  Check(reads.Count==2&&reads.Values.All(count=>count==1),"只核对两个未分类来源，每个文件只读一次");
  var unverified=nodes.Values.Where(n=>(bool)Get(n,"ReferenceTypeUnverified")).ToArray();
  Check(unverified.Length==2&&unverified.All(n=>!(bool)Get(n,"IsContextReference")),"未取得原生证据时保留未知标记，不冒充上下文");
  Check(selected.All(n=>(bool)Get(n,"StructureIndexPending")),"五份环内图档保留结构待核对提示");
  var hashes=paths.Values.ToDictionary(p=>p,WorkingReuseHash);
  var capture=Type("PdmApiClient").GetMethod("CaptureArchiveSnapshot",Flags);var snapshotType=Type("PdmApiClient+ArchiveReferenceSnapshot");
  void Frozen(string name){
   var snapshots=(IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(Guid),snapshotType));var roots=new List<object>();
   foreach(var n in selected){var snap=capture.Invoke(null,new object[]{n,scope});snapshots.Add((Guid)Get(n,"DocumentId"),snap);roots.Add(TreeCall(snap,"CreateRoot"));}
   Type("FrozenArchiveSubmissionGraph").GetMethod("Validate",Flags).Invoke(null,new object[]{snapshots});
   File.WriteAllText(Path.Combine(output,name+".json"),json.Serialize(roots));
  }
  Frozen("unknown-final-roots");Check(true,"真实现场图默认警告后最终快照通过，未知类型在原生清单中保留");
  Policy(true);try{validate.Invoke(null,new object[]{items});throw new Exception("严格策略未阻止");}catch(TargetInvocationException e){Check(e.InnerException.Message.Contains("管理员存档警告策略"),"管理员严格策略在早期阻止未知回指");}
  Policy(false);foreach(var n in unverified)Set(n,"ReferenceTypeVerified",true);
  try{validate.Invoke(null,new object[]{items});throw new Exception("真实环未阻止");}catch(TargetInvocationException e){Check(e.InnerException.Message.Contains("已确认文件依赖存在循环"),"虚拟层级内已确认的真实循环仍强制阻止");}
  foreach(var n in unverified){Set(n,"ReferenceTypeVerified",false);Set(n,"ReferenceTypeUnverified",false);}
  evidenceMode=true;reads.Clear();await (Task)TreeCall(plugin,"CompleteCycleEvidenceAsync",items,CancellationToken.None);
  Check(reads.Count==2&&reads.Values.All(count=>count==1)&&unverified.All(n=>(bool)Get(n,"IsContextReference")&&!(bool)Get(n,"ReferenceTypeUnverified")),"取得明确原生上下文证据后才转换类型，仅针对两个来源");
  Frozen("context-final-roots");Check(Groups()==0,"明确上下文在早期图和最终快照中一致通过");
  Check(hashes.All(p=>WorkingReuseHash(p.Key)==p.Value),"回放过程中原生占位文件字节不变");
  Check(items.Length==5,"仅回放原五份报错图档，不扩大提交范围");
 }
}
