using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.Remoting.Messaging;
using System.Threading;
using System.Threading.Tasks;

internal static partial class Program
{
 static async Task ReferenceNoticeContractsScenario()
 {
  void Check(bool ok,string text){if(!ok)throw new Exception(text);Console.WriteLine("PASS: "+text);}
  foreach(var mode in new[]{"supported","unsupported","old-server"})
  {
   var requests=new List<string>();
   using(var handler=new ReferenceRecoveryHandler(request=>{
    requests.Add(request.RequestUri.AbsolutePath);
    return new HttpResponseMessage(mode=="old-server"?HttpStatusCode.NotFound:HttpStatusCode.OK){Content=new StringContent(mode=="supported"?"{\"referenceWarnings\":true}":"{\"referenceWarnings\":false}")};
   }))
   using(var client=(IDisposable)New("PdmApiClient","http://fixture.invalid/"))
   {
    ((HttpClient)Field(client,"httpClient")).Dispose();SetField(client,"httpClient",new HttpClient(handler){BaseAddress=new Uri("http://fixture.invalid/")});
    Exception failure=null;try{await (Task)TreeCall(client,"RequireReferenceWarningsSupportAsync",CancellationToken.None);}catch(Exception error){failure=error;}
    Check((failure==null)==(mode=="supported"),mode+"：服务器必须支持永久保存提示才能执行带提示存档");
    Check(requests.SequenceEqual(new[]{"/api/archive-capabilities"}),mode+"：能力核对不检出、不登记、不上传");
   }
  }
  var interop=Type("PdmAddin").GetField("application",Flags).FieldType.Assembly;
  object Mock(string name,Func<IMethodCallMessage,object> handler)=>new Proxy(interop.GetType("SolidWorks.Interop.sldworks."+name,true),handler).GetTransparentProxy();
  var ole=Mock("SwOLEObject",call=>call.MethodName=="get_IsLinked"?(object)true:call.MethodName=="get_FileName"?@"Z:\来源\参数.xlsx":throw new Exception("不得激活或改写链接对象："+call.MethodName));
  var feature=Mock("Feature",call=>call.MethodName=="GetTypeName2"?"MacroFeature":call.MethodName=="get_Name"?"用户宏":throw new Exception("不得修改宏特征："+call.MethodName));
  var manager=Mock("FeatureManager",call=>call.MethodName=="GetFeatures"?new[]{feature}:call.MethodName=="GetFeatureCount"?(object)1:throw new Exception(call.MethodName));
  var extension=Mock("ModelDocExtension",call=>call.MethodName=="GetOLEObjects"?new[]{ole}:call.MethodName=="GetOLEObjectCount"?(object)1:call.MethodName=="GetRenderMaterials"?new object[0]:throw new Exception("不得固化或保存模型："+call.MethodName));
  var model=Mock("ModelDoc2",call=>call.MethodName=="get_Extension"?extension:call.MethodName=="get_FeatureManager"?manager:throw new Exception(call.MethodName));
  var warnings=new List<string>();Action<string,string> record=(source,reason)=>warnings.Add(source+"；"+reason);
  var sources=(IEnumerable)Type("ArchiveResourceIsolation").GetMethod("ReadLinkedSources",Flags).Invoke(null,new object[]{model,record});
  Check(sources.Cast<string>().SequenceEqual(new[]{@"Z:\来源\参数.xlsx"}),"链接Excel按文件来源收集，不强制转换为嵌入对象");
  Check(warnings.Count==1&&warnings[0].Contains("宏特征：用户宏")&&warnings[0].Contains("保持原样"),"宏特征依赖未核实形成提示，参数和特征不被改写");
 }
}
