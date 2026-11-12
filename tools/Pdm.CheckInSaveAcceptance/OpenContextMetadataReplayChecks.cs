using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

internal static partial class Program
{
 static async Task OpenContextMetadataReplay(string directory)
 {
  var json=new JavaScriptSerializer{MaxJsonLength=32*1024*1024};
  var entry=json.Deserialize<Dictionary<string,object>>(File.ReadAllText(Path.Combine(directory,"real-cache-entry.json")));
  var owner=json.Deserialize(json.Serialize(entry["Manifest"]),Type("ControlledOpenManifestDto"));
  var versions=json.Deserialize<object[]>(File.ReadAllText(Path.Combine(directory,"real-versions.json")))
   .Cast<Dictionary<string,object>>().ToDictionary(row=>Guid.Parse((string)row["DocumentId"]));
  var ownerJson=json.Serialize(owner);
  var files=((IEnumerable)Get(owner,"Files")).Cast<object>().ToArray();
  var expected=new HashSet<Guid>(files.Where(file=>new[]{"标签机.SLDASM","CW004-3.SLDASM"}.Contains((string)Get(file,"FileName"))).Select(file=>(Guid)Get(file,"DocumentId")));
  void Assert(bool value,string message){if(!value)throw new Exception(message);Console.WriteLine("PASS: "+message);}
  Assert(files.Length==43&&expected.Count==2,"真实缓存43份文件，装配环境为标签机及CW004-3");
  foreach(var name in new[]{"标签机钣金.SLDPRT","标签机钣金.SLDDRW"})
  {
   var requested=json.Deserialize(ownerJson,owner.GetType());
   var requestedFiles=((IEnumerable)Get(requested,"Files")).Cast<object>().ToArray();
   var root=requestedFiles.Single(file=>(string)Get(file,"FileName")==name);
   Set(requested,"RootDocumentId",Get(root,"DocumentId"));Set(requested,"RootVersionId",Get(root,"VersionId"));Set(requested,"RootRelativePath",name);
   foreach(var file in requestedFiles){Set(file,"IsRoot",ReferenceEquals(file,root));if(expected.Contains((Guid)Get(file,"DocumentId")))Set(file,"Configuration","");}
   var calls=new List<Guid>();var plugin=New("PdmAddin");
   using(var lifetime=new CancellationTokenSource())
   using(var client=(IDisposable)New("PdmApiClient","http://real-context-replay.invalid/"))
   using(var handler=new ReferenceRecoveryHandler(request=>
   {
    var path=request.RequestUri.AbsolutePath.Split('/');
    if(path.Last()!="versions"||request.Method!=HttpMethod.Get)throw new Exception("重放只能读取捕获的版本元数据");
    var id=Guid.Parse(path[path.Length-2]);calls.Add(id);
    if(!versions.TryGetValue(id,out var version))throw new Exception("不得扩大到其他图档元数据："+id);
    return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(json.Serialize(new[]{version}))};
   }))
   using(var http=new HttpClient(handler){BaseAddress=new Uri("http://real-context-replay.invalid/")})
   {
    http.DefaultRequestHeaders.Authorization=new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer","fixture");
    SetField(client,"httpClient",http);SetField(client,"controlledOpenHttpClient",http);SetField(plugin,"apiClient",client);SetField(plugin,"lifetime",lifetime);
    var task=(Task)TreeCall(plugin,"VerifiedOpenContextIdsAsync",requested,false);await task;
    var actual=new HashSet<Guid>(((IEnumerable)Get(task,"Result")).Cast<Guid>());
    Assert(actual.SetEquals(expected),name+"：真实W3快照仅识别两个已确认装配环境");
    Assert(!actual.Contains((Guid)Get(root,"DocumentId")),name+"：当前图档和实际视图模型不会当成上下文放行");
    Assert(calls.Count==(name.EndsWith("SLDDRW")?2:1)&&calls.Distinct().Count()==calls.Count,name+"：仅按需读取当前图档及直接模型，不遍历43份元数据");
    Assert(requestedFiles.Where(file=>!actual.Contains((Guid)Get(file,"DocumentId"))).All(file=>
     {var prior=files.Single(original=>Get(original,"DocumentId").Equals(Get(file,"DocumentId")));return Get(prior,"VersionId").Equals(Get(file,"VersionId"))&&Equals(Get(prior,"Configuration"),Get(file,"Configuration"));}),name+"：真实模型版本和配置保持严格校验输入");
   }
  }
  Assert(json.Serialize(owner)==ownerJson,"捕获的原工作区清单未改变，无真实CAD、文件覆盖或PLM写入");
 }
}
