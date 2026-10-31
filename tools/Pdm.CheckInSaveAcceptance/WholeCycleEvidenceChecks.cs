using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using System.Runtime.Remoting.Messaging;
internal static partial class Program
{
 static void WholeCycleEvidenceScenario(string snapshotPath,string proofPath)
 {
  void Check(bool ok,string text){if(!ok)throw new Exception(text);Console.WriteLine("PASS: "+text);}
  var json=new JavaScriptSerializer{MaxJsonLength=int.MaxValue};
  var snapshot=(Dictionary<string,object>)json.DeserializeObject(File.ReadAllText(snapshotPath));
  var root=(Dictionary<string,object>)((object[])snapshot["Roots"])[0];
  var nodes=new Dictionary<string,object>();var rows=((object[])root["Nodes"]).Cast<Dictionary<string,object>>().ToArray();
  foreach(var row in rows){var n=New("CadTreeNode");nodes[(string)row["Address"]]=n;Set(n,"FullPath",row["Path"]);Set(n,"SourceReferencePath",row["Source"]);Set(n,"FileName",Path.GetFileName((string)row["Path"]));Set(n,"InstancePath",row["Instance"]);Set(n,"ComponentSelectionName",row["Selection"]);
   foreach(var pair in new[]{new[]{"Kind","CadDocumentKind"},new[]{"Role","CadReferenceRole"},new[]{"Status","CadReferenceStatus"}})Set(n,pair[0],Enum.ToObject(Type(pair[1]),row[pair[0]]));Set(n,"IsContextReference",row["Context"]);Set(n,"IsOutsideNativeFileScope",row["Outside"]);}
  foreach(var row in rows){var n=nodes[(string)row["Address"]];foreach(var id in (object[])row["Children"])((IList)Get(n,"Children")).Add(nodes[(string)id]);foreach(var id in (object[])row["Models"])((IList)Get(n,"ReferencedModels")).Add(nodes[(string)id]);}
  var items=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(Type("BatchOperationItem")));items.Add(New("BatchOperationItem",nodes[(string)root["Root"]],1));
  var analyzer=Type("ArchiveReferenceCycles").GetMethod("Analyze",Flags);var validator=Type("ArchiveReferenceCycles").GetMethod("Validate",Flags);
  Check(((IEnumerable)analyzer.Invoke(null,new object[]{items})).Cast<object>().Count()==11,"生产失败图一次返回全部11组候选环");
  var policyType=assembly.GetType("Upton.Pdm.Domain.ArchiveWarningPolicy",true);var strict=Activator.CreateInstance(policyType);Set(strict,"BlockAll",true);Type("ArchiveWarningGate").GetMethod("Configure",Flags).Invoke(null,new[]{strict});
  try{validator.Invoke(null,new object[]{items});throw new Exception("未阻止未知关系");}catch(System.Reflection.TargetInvocationException e){Check(e.InnerException.Message.Contains("管理员存档警告策略")&&e.InnerException.Message.Contains("引用类型待核对"),"未知类型不会伪装成已确认普通循环，并给出完整组数");}
  Type("ArchiveWarningGate").GetMethod("Configure",Flags).Invoke(null,new[]{Activator.CreateInstance(policyType)});
  var proof=(Dictionary<string,object>)json.DeserializeObject(File.ReadAllText(proofPath));var raw=((object[])proof["rows"]).Cast<Dictionary<string,object>>().ToArray();
  var plugin=New("PdmAddin");var classify=Type("PdmAddin").GetMethod("ClassifyReferenceLinks",Flags);
  var appType=Type("PdmAddin").GetField("application",Flags).FieldType;var interop=appType.Assembly;
  var modelIndex=new Dictionary<string,object>(StringComparer.OrdinalIgnoreCase);
  var manager=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.FeatureManager",true),call=>call.MethodName=="GetFeatures"?new object[0]:call.MethodName=="GetFeatureCount"?(object)0:throw new Exception(call.MethodName)).GetTransparentProxy();
  var sketches=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.SketchManager",true),call=>call.MethodName=="GetSketchBlockDefinitions"?new object[0]:throw new Exception(call.MethodName)).GetTransparentProxy();
  foreach(var row in raw.GroupBy(value=>(string)value["path"],StringComparer.OrdinalIgnoreCase).Select(group=>group.First()))
  {
   var extension=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDocExtension",true),call=>{
    if(call.MethodName!="ListExternalFileReferences2")throw new Exception("禁止其他原生操作："+call.MethodName);
    var count=((object[])row["models"]).Length;
    return new ReturnMessage(null,new object[]{row["models"],row["components"],row["features"],row["types"],row["statuses"],row["entities"],row["owners"],new int[count],new string[count]},9,call.LogicalCallContext,call);
   }).GetTransparentProxy();
   modelIndex[(string)row["path"]]=new AssemblyProxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true),call=>call.MethodName=="GetPathName"?row["path"]:call.MethodName=="GetType"?(object)1:call.MethodName=="get_Extension"?extension:call.MethodName=="get_FeatureManager"?manager:call.MethodName=="get_SketchManager"?sketches:call.MethodName=="GetEquationMgr"||call.MethodName=="GetDesignTable"?null:throw new Exception("禁止额外读取或写图："+call.MethodName)).GetTransparentProxy();
  }
  var app=new Proxy(appType,call=>{if(call.MethodName=="get_ActiveDoc")return null;if(call.MethodName=="GetOpenDocumentByName")return modelIndex.TryGetValue((string)call.Args[0],out var model)?model:null;throw new Exception("禁止打开或修改图档："+call.MethodName);}).GetTransparentProxy();
  SetField(plugin,"application",app);
  var task=(System.Threading.Tasks.Task)Type("PdmAddin").GetMethod("CompleteCycleEvidenceAsync",Flags).Invoke(plugin,new object[]{items,System.Threading.CancellationToken.None});task.GetAwaiter().GetResult();
  Check(((IEnumerable)analyzer.Invoke(null,new object[]{items})).Cast<object>().Count()==0,"自动补证据产品入口用18份真实数据消除全部11组候选环");
  foreach(var owner in nodes.Values)
  {
   var data=raw.Where(r=>string.Equals((string)r["path"],(string)Get(owner,"FullPath"),StringComparison.OrdinalIgnoreCase)).ToArray();if(data.Length==0)continue;
   var sources=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var contexts=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var ordinary=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
   foreach(var r in data){var models=(object[])r["models"];for(var i=0;i<models.Length;i++){if(Convert.ToInt32(((object[])r["statuses"])[i])==0)continue;var path=(string)models[i];sources.Add(path);if(path.EndsWith(".SLDASM",StringComparison.OrdinalIgnoreCase)&&!string.IsNullOrWhiteSpace(Convert.ToString(((object[])r["owners"])[i]))&&!string.IsNullOrWhiteSpace(Convert.ToString(((object[])r["features"])[i])))contexts.Add(path);else ordinary.Add(path);}}
   contexts.ExceptWith(ordinary);
   var owners=Activator.CreateInstance(typeof(List<>).MakeGenericType(Type("NativeDesignDependencies+ContextOwner")));
   var evidence=Activator.CreateInstance(Type("NativeDesignDependencies+CheckInReferences"),Flags,null,new object[]{sources.ToArray(),contexts,owners},null);
   classify.Invoke(plugin,new[]{owner,evidence});
  }
  Check(((IEnumerable)analyzer.Invoke(null,new object[]{items})).Cast<object>().Count()==0,"18份真实解析证据经产品分类入口应用后，11组候选环全部消除");
  validator.Invoke(null,new object[]{items});Check(true,"完整真实引用图通过分类后的文件循环校验，未执行CAD或上传操作");
  var isContext=Type("NativeDesignDependencies").GetMethod("IsAssemblyContext",Flags);
  Check((bool)isContext.Invoke(null,new object[]{@"C:\总装.SLDASM","零件<1>","草图",5}),"悬空状态不改变已有装配环境类型");
  Check(!(bool)isContext.Invoke(null,new object[]{@"C:\总装.SLDASM","","派生",5}),"悬空的普通派生来源不被上下文规则掩盖");
 }
}
