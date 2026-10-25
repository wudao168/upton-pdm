using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

internal static partial class Program
{
 static async Task NativeMaterializationScenario(string output)
 {
  Directory.CreateDirectory(output);var identity=Type("PdmDocumentIdentityStore");var binding=identity.GetField("bindingDirectory",Flags);var oldBinding=binding.GetValue(null);binding.SetValue(null,Path.Combine(output,"bindings"));
  var original=Encoding.UTF8.GetBytes("immutable source native file");var body=Encoding.UTF8.GetBytes("verified independent native body");
  string Hash(byte[] bytes){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","");}
  var rawSha=Hash(original);var bodySha=Hash(body);var document=Guid.NewGuid();var version=Guid.NewGuid();var snapshot=Guid.NewGuid();
  try
  {
   foreach(var mode in new[]{"independent","wrong-source","bad-bytes","legacy"})
   using(var client=(IDisposable)New("PdmApiClient","http://native-materialization.invalid/"))
   using(var http=new HttpClient(new NativeMaterializationHandler(mode,original,body,rawSha,bodySha,snapshot)){BaseAddress=new Uri("http://native-materialization.invalid/")})
   {
    SetField(client,"httpClient",http);string stage=null;Exception failure=null;
    try{var task=(Task)Type("PdmApiClient").GetMethod("DownloadVersionToWorkspaceStageAsync",Flags).Invoke(client,new object[]{document,version,"SOURCE.SLDPRT",rawSha,CancellationToken.None});await task;stage=(string)Get(task,"Result");}catch(Exception error){failure=error;}
    if(mode=="wrong-source"||mode=="bad-bytes")
    {if(failure==null||stage!=null)throw new Exception("不安全的固化响应被接受："+mode);Console.WriteLine("PASS: native stage rejects "+mode+" before updating any working file");continue;}
    if(failure!=null||stage==null)throw new Exception("固化文件读取失败："+mode,failure);
    if(mode=="legacy"){if(File.ReadAllText(stage)!="immutable source native file")throw new Exception("旧服务响应兼容失败");Console.WriteLine("PASS: a legacy response retains exact original hash verification");Directory.Delete(Path.GetDirectoryName(stage),true);continue;}
    var args=new object[]{stage,null};if(!(bool)identity.GetMethod("TryReadLocalMetadata",Flags).Invoke(null,args))throw new Exception("未保存固化来源记录");var metadata=args[1];
    if((Guid)Get(metadata,"VersionId")!=version||(Guid)Get(metadata,"NativeIsolationSnapshotId")!=snapshot||(string)Get(metadata,"Sha256")!=rawSha||(string)Get(metadata,"ResolvedReferenceSha256")!=bodySha)throw new Exception("原文指纹和固化指纹混淆");
    Console.WriteLine("PASS: staged independent bytes retain immutable source and exact isolation generation");
    var target=Path.Combine(output,"working.SLDPRT");File.Copy(stage,target);Type("ControlledReferenceMaterializer").GetMethod("CopyResolvedBinding",Flags).Invoke(null,new object[]{stage,target});
    args=new object[]{target,null};if(!(bool)identity.GetMethod("TryReadLocalMetadata",Flags).Invoke(null,args)||(Guid)Get(args[1],"NativeIsolationSnapshotId")!=snapshot)throw new Exception("工作副本丢失固化代次");
    Console.WriteLine("PASS: copied working files preserve the independent generation and source fingerprint");
    var descriptor=New("NativeFileIsolationDto");Set(descriptor,"SourceVersionId",version);Set(descriptor,"SourceSha256",rawSha);Set(descriptor,"Sha256",bodySha);Set(descriptor,"Length",(long)body.Length);Set(descriptor,"ReferenceSnapshotId",snapshot);
    var file=New("ControlledOpenFileDto");Set(file,"DocumentId",document);Set(file,"VersionId",version);Set(file,"Sha256",rawSha);Set(file,"FileLength",(long)original.Length);Set(file,"NativeIsolation",descriptor);Set(file,"IsNativeIsolated",true);
    if(!(bool)Type("ControlledReferenceMaterializer").GetMethod("Matches",Flags).Invoke(null,new[]{target,file}))throw new Exception("合法独立工作副本被误拒绝");
    Console.WriteLine("PASS: actual independent working bytes satisfy the controlled manifest");
    var stale=Path.Combine(output,"stale-original.SLDPRT");File.WriteAllBytes(stale,original);if((bool)Type("ControlledReferenceMaterializer").GetMethod("Matches",Flags).Invoke(null,new[]{stale,file}))throw new Exception("旧原文被误认为固化副本");
    Console.WriteLine("PASS: an old raw cache cannot impersonate an independent native copy");Directory.Delete(Path.GetDirectoryName(stage),true);
   }
  }
  finally{binding.SetValue(null,oldBinding);}
 }
 sealed class NativeMaterializationHandler:HttpMessageHandler
 {
  readonly string mode,rawSha,bodySha;readonly byte[] original,body;readonly Guid snapshot;
  public NativeMaterializationHandler(string mode,byte[] original,byte[] body,string rawSha,string bodySha,Guid snapshot){this.mode=mode;this.original=original;this.body=body;this.rawSha=rawSha;this.bodySha=bodySha;this.snapshot=snapshot;}
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
  {
   if(!request.RequestUri.Query.Contains("preferNativeIsolation=true"))throw new Exception("工作副本读取没有请求独立原生文件");
   var response=new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(mode=="legacy"||mode=="bad-bytes"?original:body)};
   if(mode!="legacy")foreach(var pair in new[]{new[]{"X-UPLM-Native-Snapshot-Id",snapshot.ToString("D")},new[]{"X-UPLM-Native-Source-Sha256",mode=="wrong-source"?new string('A',64):rawSha},new[]{"X-UPLM-Native-Source-Length",original.Length.ToString()},new[]{"X-UPLM-Native-Content-Sha256",bodySha},new[]{"X-UPLM-Native-Content-Length",body.Length.ToString()}})response.Headers.Add(pair[0],pair[1]);
   return Task.FromResult(response);
  }
 }
}
