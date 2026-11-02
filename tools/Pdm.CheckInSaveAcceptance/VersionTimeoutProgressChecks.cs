using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static partial class Program
{
 sealed class VersionTimingHandler:HttpMessageHandler
 {
  internal Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> Send;
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>Send(request,token);
 }
 static async Task VersionTimeoutProgressScenario(Form host)
 {
  void Check(bool ok,string text){if(!ok)throw new Exception(text);Console.WriteLine("PASS: "+text);}
  var session=Type("ArchiveSession").GetMethod("Start",Flags).Invoke(null,new object[]{host.Handle,CancellationToken.None});await (Task)Get(session,"Ready");
  try
  {
   foreach(var mode in new[]{"slow-success","timeout","unavailable","connection","cancel"})
   using(var cancel=new CancellationTokenSource())
   using(var client=(IDisposable)New("PdmApiClient","http://version-fixture.invalid/"))
   using(var fastHandler=new ReferenceRecoveryHandler(_=>throw new Exception("版本核对不应使用通用短超时连接")))
   using(var fast=new HttpClient(fastHandler){BaseAddress=new Uri("http://version-fixture.invalid/"),Timeout=TimeSpan.FromMilliseconds(5)})
   {
    var calls=0;var delays=0;var requestType=Type("DocumentVersionVerificationRequestDto");var requests=Array.CreateInstance(requestType,1);var row=Activator.CreateInstance(requestType);Set(row,"DocumentId",Guid.NewGuid());requests.SetValue(row,0);
    using(var handler=new VersionTimingHandler{Send=async(request,token)=>{
     calls++;
     if(mode=="cancel"){cancel.Cancel();token.ThrowIfCancellationRequested();}
     if(calls==1&&mode=="timeout")throw new TaskCanceledException("fixture timeout");
     if(calls==1&&mode=="connection")throw new HttpRequestException("fixture connection failure");
     if(calls==1&&mode=="unavailable")return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable){Content=new StringContent("{}")};
     await Task.Delay(30,token);return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("[]")};
    }})
    using(var slow=new HttpClient(handler){BaseAddress=fast.BaseAddress,Timeout=TimeSpan.FromSeconds(15)})
    {
     SetField(client,"httpClient",fast);SetField(client,"controlledOpenHttpClient",slow);SetField(client,"archiveRecoveryDelay",new Func<TimeSpan,CancellationToken,Task>((_,token)=>{delays++;return Task.CompletedTask;}));
     Check((TimeSpan)Field(client,"versionVerificationTimeout")==TimeSpan.FromSeconds(120),mode+"：版本接口独立120秒上限");
     var before=ArchiveHistory(session);Exception failure=null;
     try{await (Task)TreeCall(client,"VerifyReferenceVersionsAsync",requests,cancel.Token);}catch(Exception error){failure=error;}
     if(mode=="cancel")Check(failure is OperationCanceledException&&calls==1&&delays==0,"用户取消不自动重试");
     else{
      Check(failure==null&&calls==(mode=="slow-success"?1:2),mode+"：使用独立连接完成校验，短超时不影响请求");
      var history=ArchiveHistory(session).Substring(before.Length);
      if(mode=="timeout")Check(history.Contains("服务器响应超时")&&!history.Contains("连接失败"),"响应超时不再显示网络断线");
      if(mode=="unavailable")Check(history.Contains("服务暂时不可用"),"503显示服务不可用");
      if(mode=="connection")Check(history.Contains("PLM连接失败"),"连接异常单独提示");
     }
    }
   }
   var ids=Enumerable.Range(0,205).Select(_=>Guid.NewGuid()).ToArray();var histories=(IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(Guid),typeof(IReadOnlyList<>).MakeGenericType(Type("DocumentVersionDto"))));
   foreach(var id in ids)histories.Add(id,Activator.CreateInstance(typeof(List<>).MakeGenericType(Type("DocumentVersionDto"))));
   var batchCalls=0;
   using(var client=(IDisposable)New("PdmApiClient","http://progress-fixture.invalid/"))
   using(var handler=new ReferenceRecoveryHandler(request=>{
    var json=new JavaScriptSerializer();var body=json.Deserialize<Dictionary<string,object>>(request.Content.ReadAsStringAsync().GetAwaiter().GetResult());var batch=((IEnumerable)body["Documents"]).Cast<Dictionary<string,object>>().ToArray();if(batch.Length>100)throw new Exception("批次范围扩大");batchCalls++;
    return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(json.Serialize(batch.Select(r=>new{documentId=Guid.Parse((string)r["DocumentId"]),accessible=true,latest=(object)null}).ToArray()))};
   }))
   using(var http=new HttpClient(handler){BaseAddress=new Uri("http://progress-fixture.invalid/")})
   {
    SetField(client,"httpClient",http);SetField(client,"controlledOpenHttpClient",http);TreeCall(session,"SetStep",7,"核对引用版本",205);
    await (Task)TreeCall(client,"RefreshArchiveHistoriesAsync",histories,CancellationToken.None);
    var history=ArchiveHistory(session);
    Check(batchCalls==3,"205份图档仍按100份分组，不扩大查询范围");
    Check((int)Field(session,"stepCompleted")==205&&(int)Field(session,"stepTotal")==205,"显示实际完成205/205，而非一直0/205");
    Check(history.Contains("100/205")&&history.Contains("200/205")&&history.Contains("205/205"),"每个完成批次都有明确进度");
   }
  }
  finally{ArchiveClose(session);await (Task)Get(session,"Closed");((IDisposable)session).Dispose();}
 }
}
