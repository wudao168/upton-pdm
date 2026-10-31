using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

internal static partial class Program
{
 static async Task SelectedRootReferenceScenario(string output)
 {
  output=Path.GetFullPath(output);Directory.CreateDirectory(output);
  void Check(bool ok,string text){if(!ok)throw new Exception(text);Console.WriteLine("PASS: "+text);}
  var scopeRoot=LinkedNode(output,"ROOT.SLDASM","Assembly");var selectedPart=LinkedNode(output,"底座.SLDPRT","Part");((IList)Get(scopeRoot,"Children")).Add(selectedPart);
  var extras=new List<object>();
  for(var i=0;i<12;i++){var node=LinkedNode(output,"UNSELECTED-"+i+".SLDPRT","Part");Set(node,"Role",Enum.Parse(Type("CadReferenceRole"),"ExternalFileDependency"));Set(node,"StoredVersionStateKnown",true);Set(node,"HasStoredVersion",true);extras.Add(node);((IList)Get(scopeRoot,"Children")).Add(node);}
  var selected=LinkedItems(scopeRoot,selectedPart);var all=LinkedItems(new[]{scopeRoot,selectedPart}.Concat(extras).ToArray());
  var selector=Type("PdmAddin").GetMethod("IncludeRequiredUnstoredDependencies",Flags);
  var scoped=selector==null?all:selector.Invoke(null,new object[]{selected,all});
  Check(((IEnumerable)scoped).Cast<object>().Count()==2,"复现：总装和一个零件勾选后，不把12个已入库外部引用加入变更分析");
  var required=extras[0];Set(required,"DocumentId",null);
  scoped=selector.Invoke(null,new object[]{selected,all});Check(((IEnumerable)scoped).Cast<object>().Count()==3,"真正未入库的必要引用仍进入首次存档，不遗漏新文件");
  Set(required,"DocumentId",Guid.NewGuid());Set(required,"HasStoredVersion",false);
  scoped=selector.Invoke(null,new object[]{selected,all});Check(((IEnumerable)scoped).Cast<object>().Count()==3,"登记占位但没有存档版本的引用仍正常准备");
  Set(required,"HasStoredVersion",true);var explicitItems=LinkedItems(scopeRoot,selectedPart,required);
  scoped=selector.Invoke(null,new object[]{explicitItems,all});Check(((IEnumerable)scoped).Cast<object>().Count()==3,"用户明确勾选的已入库引用不被移除");

  foreach(var mode in new[]{"bulk-351","bound","source-copy","fallback","multi-bound","legacy","denied","missing-row","wrong-document","wrong-bound","forbidden","server-error","changed-bytes","scope-change"})
  {
   var folder=Path.Combine(output,mode);Directory.CreateDirectory(folder);var root=LinkedNode(folder,"ROOT.SLDASM","Assembly");var part=LinkedNode(folder,"底座.SLDPRT","Part");((IList)Get(root,"Children")).Add(part);
   var count=mode=="bulk-351"?351:3;var nodes=new List<object>();var descriptors=new Dictionary<Guid,ReferenceMetadataFixture>();
   for(var i=0;i<count;i++)
   {
    var node=LinkedNode(folder,"REF-"+i+".SLDPRT","Part");var id=(Guid)Get(node,"DocumentId");var actual=WorkingReuseHash((string)Get(node,"FullPath"));var old=Guid.NewGuid();var latest=mode=="bound"&&i==0||mode=="fallback"&&i==0||mode=="multi-bound"&&i==0?Guid.NewGuid():old;
    Set(node,"OpenedVersionId",mode=="fallback"&&i==0?null:(object)old);
    var record=new ReferenceMetadataFixture{Id=id,Latest=latest,LatestHash=latest==old?actual:new string('A',64),Original=old,OriginalHash=actual};
    if(mode=="source-copy"&&i==0){record.LatestHash=new string('B',64);record.SourceHash=actual;}
    descriptors[id]=record;nodes.Add(node);((IList)Get(root,"Children")).Add(node);
   }
   if(mode=="multi-bound")
   {
    var first=nodes[0];var another=New("CadTreeNode");foreach(var name in new[]{"FullPath","FileName","Kind","DocumentId"})Set(another,name,Get(first,name));var second=Guid.NewGuid();Set(another,"OpenedVersionId",second);((IList)Get(root,"Children")).Add(another);nodes.Add(another);descriptors[(Guid)Get(first,"DocumentId")].Second=second;
   }
   var network=new ReferenceMetadataHandler(descriptors,mode);
   using(var client=(IDisposable)New("PdmApiClient","http://reference-metadata.invalid/"))using(var http=new HttpClient(network){BaseAddress=new Uri("http://reference-metadata.invalid/")})using(var lifetime=new CancellationTokenSource())
   {
    SetField(client,"httpClient",http);var plugin=New("PdmAddin");SetField(plugin,"apiClient",client);SetField(plugin,"lifetime",lifetime);SetField(plugin,"authenticatedUsername","tester");SetField(plugin,"currentTree",root);
    var project=Guid.NewGuid();var session=(Guid)Field(plugin,"checkoutSessionId");var cache=New("PdmAddin+BatchReferenceVersionCache",project,session,Get(client,"EditJournalScope"),"tester");
    var method=Type("PdmAddin").GetMethod("ResolveBatchReferenceVersionsCachedAsync",Flags);var items=LinkedItems(root,part);Exception failure=null;
    var timer=System.Diagnostics.Stopwatch.StartNew();
    for(var pass=0;pass<3;pass++)
    {
     if(pass==1&&mode=="changed-bytes")File.AppendAllText((string)Get(nodes[0],"FullPath"),"real modification");
     if(pass==1&&mode=="scope-change")SetField(plugin,"authenticatedUsername","different-user");
     try{await (Task)method.Invoke(plugin,new object[]{items,project,null,CancellationToken.None,cache});}catch(Exception error){failure=error is TargetInvocationException?error.InnerException:error;break;}
    }
    var mustReject=new[]{"denied","missing-row","wrong-document","wrong-bound","forbidden","server-error","changed-bytes","scope-change"}.Contains(mode);
    Check((failure!=null)==mustReject,mode+"：保持权限、身份、内容及会话冲突保护");
    Check(network.Writes==0,mode+"：版本摘要仅核对，不登记、保存、获取权限或提交图档");
    if(!mustReject)
    {
     Check(network.Histories==(mode=="fallback"?1:mode=="legacy"?count:0),mode+"：只在摘要无法匹配或旧服务器兼容时读取必要历史");
     Check(network.Batches==(mode=="legacy"?1:mode=="bulk-351"?4:mode=="multi-bound"?2:1),mode+"：同批三次内容复核复用固定版本摘要，避免逐文件HTTP请求");
     Check(nodes.All(node=>!string.IsNullOrWhiteSpace((string)Get(node,"CurrentRevision"))),mode+"：所有引用均获得真实存档版本，不跳过引用校验");
     if(mode=="bound")Check((Guid?)Get(nodes[0],"OpenedVersionId")==descriptors[(Guid)Get(nodes[0],"DocumentId")].Original,"历史绑定版本优先保留，不擅自切到最新版");
     if(mode=="multi-bound")Check((Guid?)Get(nodes[0],"OpenedVersionId")!=(Guid?)Get(nodes.Last(),"OpenedVersionId"),"同一图档不同绑定版本均单独核对并保留");
    }
    else if(mode!="changed-bytes")Check(network.Histories==0,mode+"：拒绝或损坏的批量响应不通过历史接口绕过");
    if(mode=="bulk-351")Console.WriteLine("METRIC: references=351 checkpoints=3 batchRequests="+network.Batches+" fullHistories="+network.Histories+" elapsedMs="+timer.ElapsedMilliseconds);
   }
  }
 }
 sealed class ReferenceMetadataFixture
 {
  internal Guid Id,Latest,Original;internal Guid? Second;internal string LatestHash,OriginalHash,SourceHash;
  internal object Summary(Guid id)=>new{Id=id,DocumentId=Id,Revision=id==Latest?"W2":"W1",FileLength=10L,Sha256=id==Latest?LatestHash:OriginalHash,SourceFileSha256=id==Latest?SourceHash:null};
  internal object Full(Guid id)=>new{Id=id,Revision=new{Display=id==Latest?"W2":"W1"},Sha256=id==Latest?LatestHash:OriginalHash,PropertySnapshot=new Dictionary<string,string>{{"SourceFileSha256",id==Latest?SourceHash??"":OriginalHash}}};
 }
 sealed class ReferenceMetadataHandler:HttpMessageHandler
 {
  readonly Dictionary<Guid,ReferenceMetadataFixture> versions;readonly string mode;internal int Batches,Histories,Writes;
  internal ReferenceMetadataHandler(Dictionary<Guid,ReferenceMetadataFixture> versions,string mode){this.versions=versions;this.mode=mode;}
  protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
  {
   var json=new JavaScriptSerializer();var path=request.RequestUri.AbsolutePath;
   if(request.Method==HttpMethod.Post&&path.EndsWith("/documents/version-verifications"))
   {
    Batches++;if(mode=="legacy")return new HttpResponseMessage(HttpStatusCode.NotFound);
    if(mode=="forbidden"||mode=="server-error")return new HttpResponseMessage(mode=="forbidden"?HttpStatusCode.Forbidden:HttpStatusCode.InternalServerError){Content=new StringContent("{\"detail\":\"版本核对未通过\"}")};
    var payload=json.DeserializeObject(await request.Content.ReadAsStringAsync()) as IDictionary<string,object>;var entries=((IEnumerable)payload["Documents"]).Cast<IDictionary<string,object>>().ToArray();
    if(entries.Length>100||entries.Select(row=>row["DocumentId"]).Distinct().Count()!=entries.Length)throw new Exception("每批最多100个不同图档");
    var result=entries.Select((row,index)=>
    {
     var id=Guid.Parse((string)row["DocumentId"]);var fixture=versions[id];var bound=row["BoundVersionId"]==null?(Guid?)null:Guid.Parse((string)row["BoundVersionId"]);
     var requested=bound.HasValue&&bound!=fixture.Latest?bound:null;var actualBound=mode=="wrong-bound"?Guid.NewGuid():requested;
     return (object)new{DocumentId=id,Latest=mode=="wrong-document"?new ReferenceMetadataFixture{Id=Guid.NewGuid(),Latest=fixture.Latest,LatestHash=fixture.LatestHash}.Summary(fixture.Latest):fixture.Summary(fixture.Latest),Bound=actualBound.HasValue?fixture.Summary(actualBound.Value):null,Accessible=mode!="denied"};
    }).ToArray();
    if(mode=="wrong-bound"&&result.Length>0){var id=Guid.Parse((string)entries[0]["DocumentId"]);result[0]=new{DocumentId=id,Latest=versions[id].Summary(versions[id].Latest),Bound=versions[id].Summary(Guid.NewGuid()),Accessible=true};}
    return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(json.Serialize(mode=="missing-row"?result.Skip(1).ToArray():result))};
   }
   if(request.Method==HttpMethod.Get&&path.EndsWith("/versions"))
   {
    Histories++;var id=Guid.Parse(path.Split('/')[3]);var v=versions[id];return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(json.Serialize(new[]{v.Full(v.Latest),v.Full(v.Original)}))};
   }
   Writes++;throw new Exception("禁止的业务变更请求："+request.Method+" "+path);
  }
 }
}
