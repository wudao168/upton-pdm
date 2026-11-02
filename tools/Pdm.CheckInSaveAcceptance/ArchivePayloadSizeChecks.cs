using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

internal static partial class Program
{
 static async Task ArchivePayloadSizeScenario(string output)
 {
  void Check(bool ok,string text){if(!ok)throw new Exception(text);Console.WriteLine("PASS: "+text);}
  using(var client=(IDisposable)New("PdmApiClient","http://size-fixture.invalid/"))
  {
   var batch=Guid.NewGuid();var session=Guid.NewGuid();var json=new JavaScriptSerializer();
   var files=new object[]{new{documentId=Guid.NewGuid(),request=new{fileName="总装😀.SLDASM",comment="引用<部件>\\路径",properties=new Dictionary<string,string>{{"描述","中文😀"}}}},new{documentId=Guid.NewGuid(),request=new{fileName="零件.SLDPRT"}}};
   var bytes=(long)TreeCall(client,"ArchiveBatchEnvelopeBytes",batch,session);
   for(var i=0;i<files.Length;i++)bytes+=(long)TreeCall(client,"ArchiveBatchFileBytes",files[i])+(i==0?0:1);
   Check(bytes==Encoding.UTF8.GetByteCount(json.Serialize(new{batchId=batch,checkoutSessionId=session,files})),"逐文件计数与最终UTF-8提交字节一致，包含中文、转义和代理字符");
   long limit=bytes;var status=200;var calls=0;
   using(var handler=new ReferenceRecoveryHandler(request=>{calls++;if(request.Method!=HttpMethod.Get)throw new Exception("容量核对不能上传或提交");return new HttpResponseMessage((HttpStatusCode)status){Content=new StringContent(json.Serialize(new{maxRequestBytes=limit}))};}))
   using(var http=new HttpClient(handler){BaseAddress=new Uri("http://size-fixture.invalid/")})
   {
    SetField(client,"httpClient",http);
    await (Task)TreeCall(client,"ValidateArchivePayloadSizeAsync",bytes,2,true,CancellationToken.None);
    Check(true,"恰好等于管理员上限时允许继续");
    Exception failure=null;try{await (Task)TreeCall(client,"ValidateArchivePayloadSizeAsync",bytes+1,2,true,CancellationToken.None);}catch(Exception e){failure=e;}
    Check(failure!=null&&failure.Message.Contains("本次尚未上传文件")&&Equals(failure.Data["ArchiveBatchNotCommitted"],true),"超出一个字节就在上传前提示，标明未提交");
    limit=bytes+1;await (Task)TreeCall(client,"ValidateArchivePayloadSizeAsync",bytes+1,2,true,CancellationToken.None);
    Check(calls==3,"每次操作读取管理员最新上限，不沿用过期缓存");
    status=404;await (Task)TreeCall(client,"ValidateArchivePayloadSizeAsync",30000000L,916,true,CancellationToken.None);
    failure=null;try{await (Task)TreeCall(client,"ValidateArchivePayloadSizeAsync",30000001L,916,true,CancellationToken.None);}catch(Exception e){failure=e;}
    Check(failure!=null&&failure.Message.Contains("28.61"),"旧服务器缺少配置接口时保守采用原30,000,000字节限制");
    status=200;limit=0;failure=null;try{await (Task)TreeCall(client,"ValidateArchivePayloadSizeAsync",1L,1,true,CancellationToken.None);}catch(Exception e){failure=e;}
    Check(failure!=null&&failure.Message.Contains("设置无效"),"无效设置不被当作无限容量");
    var message=(string)Type("PdmApiClient").GetMethod("GetErrorMessage",Flags).Invoke(null,new object[]{"RequestEntityTooLarge","<html>413 Request Entity Too Large</html>"});
    Check(message.Contains("大小")&&!message.Contains("RequestEntity")&&!message.Contains("<html>"),"413即使返回英文HTML也显示明确中文提示");
   }
  }
 }
}
