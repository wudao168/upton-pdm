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
 static async Task ArchiveNetworkRecoveryScenario(string output)
 {
  void Check(bool ok,string message){if(!ok)throw new Exception(message);Console.WriteLine("PASS: "+message);}
  var journal=Type("ArchiveUploadJournal").GetField("directory",Flags);var old=journal.GetValue(null);
  var journalRoot=Path.Combine(Path.GetTempPath(),"pdm-net-"+Guid.NewGuid().ToString("N"));
  var json=new JavaScriptSerializer();
  HttpResponseMessage Reply(object value,int status=200)=>new HttpResponseMessage((HttpStatusCode)status){Content=new StringContent(json.Serialize(value))};
  void FastRetry(object client,Func<TimeSpan,CancellationToken,Task> delay=null){Type("PdmApiClient").GetField("archiveRecoveryDelay",Flags)?.SetValue(client,delay??new Func<TimeSpan,CancellationToken,Task>((_,token)=>{token.ThrowIfCancellationRequested();return Task.CompletedTask;}));}
  try
  {
   foreach(var mode in new[]{"before-chunk","lost-chunk-reply","timeout","502","503","504","create-unavailable","complete-reply-lost","401","403","409","500","wrong-hash","wrong-offset","cancel","cancel-wait","deadline","deadline-wait","account-changed"})
   {
    var folder=Path.Combine(Path.GetFullPath(output),mode);Directory.CreateDirectory(folder);journal.SetValue(null,Path.Combine(journalRoot,mode));
    var path=Path.Combine(folder,"只读零件.SLDPRT");var bytes=Enumerable.Range(0,10).Select(i=>(byte)i).ToArray();File.WriteAllBytes(path,bytes);File.SetAttributes(path,FileAttributes.ReadOnly);
    var hash=WorkingReuseHash(path);var project=Guid.NewGuid();var doc=Guid.NewGuid();var session=Guid.NewGuid();var batch=Guid.NewGuid();
    var received=0;var puts=0;var gets=0;var creates=0;var completes=0;var failed=false;var receivedBytes=new List<byte>();
    using(var cancel=new CancellationTokenSource())
    using(var client=(IDisposable)New("PdmApiClient","http://network-fixture.invalid/"))
    using(var handler=new ReferenceRecoveryHandler(request=>{
     var route=request.RequestUri.AbsolutePath;
     object Session()=>new{id=session,projectId=project,totalLength=10,chunkSize=4,expectedSha256=mode=="wrong-hash"&&gets>0?new string('F',64):hash,receivedLength=mode=="wrong-offset"&&gets>0?3:received,expiresAt=DateTime.UtcNow.AddHours(1),targetRelativePath="qa/file.SLDPRT"};
     if(route.EndsWith("/uploads/sessions")){
      creates++;if(mode=="create-unavailable"&&creates==1)return Reply(new{message="暂不可用"},503);return Reply(Session());
     }
     if(request.Method==HttpMethod.Get){gets++;return Reply(Session());}
     if(route.Contains("/chunks/")){
      puts++;var payload=request.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
      if(!failed&&mode!="create-unavailable"&&mode!="complete-reply-lost"){
       failed=true;
       if(mode=="lost-chunk-reply"){received+=payload.Length;receivedBytes.AddRange(payload);}
       if(mode=="cancel"){cancel.Cancel();throw new TaskCanceledException();}
       if(mode=="401"||mode=="403"||mode=="409"||mode=="500"||mode=="502"||mode=="503"||mode=="504")return Reply(new{message="模拟服务响应"},int.Parse(mode));
       if(mode=="timeout")throw new TaskCanceledException("request timeout");
       throw new HttpRequestException("response lost");
      }
      received+=payload.Length;receivedBytes.AddRange(payload);return Reply(Session());
     }
     if(route.EndsWith("/complete")){
      completes++;if(mode=="complete-reply-lost"&&completes==1)throw new HttpRequestException("completion receipt lost");
      return Reply(new{relativePath="qa/file.SLDPRT",length=10,sha256=hash});
     }
     throw new Exception("Unexpected route "+route);
    }))
    using(var http=new HttpClient(handler){BaseAddress=new Uri("http://network-fixture.invalid/")})
    {
     SetField(client,"httpClient",http);FastRetry(client);
     if(mode=="deadline")Type("PdmApiClient").GetField("archiveRecoveryWindow",Flags)?.SetValue(client,TimeSpan.Zero);
     if(mode=="deadline-wait"){
      Type("PdmApiClient").GetField("archiveRecoveryWindow",Flags)?.SetValue(client,TimeSpan.FromMilliseconds(30));
      FastRetry(client,(_,token)=>Task.Delay(1000,token));
     }
     if(mode=="cancel-wait")FastRetry(client,(_,token)=>{cancel.Cancel();return Task.Delay(1000,token);});
     if(mode=="account-changed")FastRetry(client,(_,token)=>{Set(client,"EditJournalScope","different-user");return Task.CompletedTask;});
     Exception error=null;object result=null;
     try{var task=(Task)TreeCall(client,"UploadVersionFileAsync",project,path,doc,path,cancel.Token,null,null,hash,batch,Guid.NewGuid(),null);await task;result=Get(task,"Result");}catch(Exception e){error=e;}
     var success=new[]{"before-chunk","lost-chunk-reply","timeout","502","503","504","create-unavailable","complete-reply-lost"}.Contains(mode);
     if(success&&error!=null)throw new Exception(mode+"：未恢复",error);
     Check(success?error==null&&result!=null:error!=null,mode+"：临时故障可恢复，业务错误和无法确认的状态停止");
     if(success){
      Check(receivedBytes.SequenceEqual(bytes)&&received==10,mode+"：恢复后文件字节完整且未重复累计");
      if(mode!="create-unavailable"&&mode!="complete-reply-lost")Check(gets==1,mode+"：重试分块前查询服务器接收位置");
      if(mode=="lost-chunk-reply")Check(puts==3,"服务器已收到的分块不重传");
      if(mode=="complete-reply-lost")Check(completes==2&&puts==3,"完成响应丢失按原会话取回结果，不重传文件");
     }else{
      Check(puts==1&&completes==0,mode+"：不盲目继续写入或完成上传");
      if(mode.StartsWith("cancel"))Check(error is OperationCanceledException,"用户取消不会被当作网络中断重试："+mode);
      if(mode.StartsWith("deadline"))Check(error.Message.Contains("PLM请求重试已超时"),"等待到期明确报告，本地续传记录保留："+mode);
     }
     Check(File.ReadAllBytes(path).SequenceEqual(bytes)&&File.GetAttributes(path)==FileAttributes.ReadOnly,mode+"：只读原图不改写、不扩权");
     File.SetAttributes(path,FileAttributes.Normal);
    }
   }
   foreach(var mode in new[]{"committed-reply-lost","not-committed","receipt-unavailable","commit-503","commit-500","commit-409","receipt-incomplete","commit-offline"})
   {
    var batch=Guid.NewGuid();var doc=Guid.NewGuid();var posts=0;var gets=0;var requests=new List<string>();
    object Receipt()=>new{batchId=batch,results=mode=="receipt-incomplete"?new object[0]:new object[]{new{document=new{id=doc},version=new{id=Guid.NewGuid()}}}};
    using(var client=(IDisposable)New("PdmApiClient","http://network-fixture.invalid/"))
    using(var handler=new ReferenceRecoveryHandler(request=>{
     if(request.RequestUri.AbsolutePath.EndsWith("/archive-batch-limits"))return Reply(new{maxRequestBytes=256L*1024*1024});
     requests.Add(request.Method.Method);
     if(request.Method==HttpMethod.Get){gets++;if(mode=="commit-offline")throw new HttpRequestException("still offline");if(mode=="receipt-unavailable"&&gets==1)return Reply(new{message="暂不可用"},503);return mode=="not-committed"||mode=="commit-503"||mode=="commit-500"||mode=="commit-409"?Reply(new{},404):Reply(Receipt());}
     posts++;
     var body=json.Deserialize<Dictionary<string,object>>(request.Content.ReadAsStringAsync().GetAwaiter().GetResult());if((string)body["batchId"]!=batch.ToString())throw new Exception("批次编号被替换");
     if(mode=="commit-500"||mode=="commit-409"||mode=="commit-503"&&posts==1)return Reply(new{message="模拟服务响应"},int.Parse(mode.Substring(7)));
     if(posts==1)throw new HttpRequestException("commit response lost");return Reply(Receipt());
    }))
    using(var http=new HttpClient(handler){BaseAddress=new Uri("http://network-fixture.invalid/")})
    {
     SetField(client,"httpClient",http);SetField(client,"controlledOpenHttpClient",http);FastRetry(client);
     if(mode=="commit-offline")Type("PdmApiClient").GetField("archiveRecoveryWindow",Flags)?.SetValue(client,TimeSpan.Zero);
     Exception error=null;object result=null;
     try{var task=(Task)TreeCall(client,"CommitArchiveBatchAsync",batch,Guid.NewGuid(),new object[]{new{documentId=doc}},CancellationToken.None);await task;result=Get(task,"Result");}catch(Exception e){error=e;}
     var success=mode!="commit-500"&&mode!="commit-409"&&mode!="receipt-incomplete"&&mode!="commit-offline";
     if(success&&error!=null)throw new Exception(mode+"：未取得回执",error);
     Check(success?error==null&&result!=null:error!=null,mode+"：整批提交按真实回执判断结果");
     Check(posts==(mode=="not-committed"||mode=="commit-503"?2:1),mode+"：已完成批次及业务错误不重复提交");
     Check(gets>0&&requests[1]=="GET",mode+"：失联后先查原批次，不直接重发提交");
     if(mode=="commit-offline"){
      var message=(string)Type("PdmAddin").GetMethod("ArchivePreflightError",Flags).Invoke(null,new object[]{error});
      Check(message.Contains("整批提交结果暂未确认")&&message.Contains("请勿重新提交")&&!error.Data.Contains("ArchiveBatchNotCommitted"),"无法取回批次回执时不误报未提交、不提示直接重提");
     }
    }
   }
   foreach(var mode in new[]{"read-timeout","read-503","registration-reply-lost","business-error","unsafe-write"})
   {
    var calls=0;var bodies=new List<string>();var delays=new List<double>();
    using(var client=(IDisposable)New("PdmApiClient","http://network-fixture.invalid/"))
    using(var handler=new ReferenceRecoveryHandler(request=>{
     calls++;bodies.Add(request.Content?.ReadAsStringAsync().GetAwaiter().GetResult());
     if(mode=="business-error")return Reply(new{message="版本冲突"},409);
     if(calls<=3){if(mode=="read-503")return Reply(new{message="服务暂不可用"},503);if(mode=="read-timeout")throw new TaskCanceledException("fixture timeout");throw new HttpRequestException("fixture response loss");}
     return Reply(new{value="same-result"});
    }))
    using(var http=new HttpClient(handler){BaseAddress=new Uri("http://network-fixture.invalid/")})
    {
     SetField(client,"httpClient",http);FastRetry(client,(delay,token)=>{delays.Add(delay.TotalSeconds);return Task.CompletedTask;});
     Check((TimeSpan)Field(client,"archiveRecoveryWindow")==TimeSpan.FromMinutes(5),"默认临时故障等待窗口为5分钟："+mode);
     var requestType=Type("PdmApiClient").GetNestedType("ArchiveRequest",Flags);var request=Activator.CreateInstance(requestType,true);
     requestType.GetField("Path",Flags).SetValue(request,"api/fixture");requestType.GetField("FileName",Flags).SetValue(request,"零件.SLDPRT");
     if(mode=="registration-reply-lost"||mode=="unsafe-write")requestType.GetField("Json",Flags).SetValue(request,"{\"batchId\":\"fixed\"}");
     requestType.GetField("RetrySafe",Flags)?.SetValue(request,mode=="registration-reply-lost");
     Exception error=null;try{var task=(Task)Type("PdmApiClient").GetMethod("ReadArchiveResponseAsync",Flags).MakeGenericMethod(typeof(Dictionary<string,string>)).Invoke(client,new[]{request,(object)CancellationToken.None});await task;}catch(Exception e){error=e;}
     var recover=mode!="business-error"&&mode!="unsafe-write";
     Check(recover?error==null&&calls==4:error!=null&&calls==1,mode+"：只有已确认可重试的操作自动恢复");
     if(recover)Check(delays.SequenceEqual(new[]{1d,3d,10d}),mode+"：按1、3、10秒退避，无紧密循环");
     if(mode=="registration-reply-lost")Check(bodies.Distinct().Count()==1,"暂存登记重试保留同一批次及相同请求，不扩大登记范围");
    }
   }
  }
  finally{
   journal.SetValue(null,old);
   var full=Path.GetFullPath(journalRoot);var temp=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
   if(Directory.Exists(full)&&full.StartsWith(temp,StringComparison.OrdinalIgnoreCase)&&Path.GetFileName(full).StartsWith("pdm-net-",StringComparison.Ordinal))Directory.Delete(full,true);
  }
 }
}
