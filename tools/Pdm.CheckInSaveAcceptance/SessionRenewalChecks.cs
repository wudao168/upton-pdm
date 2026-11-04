using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

internal static partial class Program
{
 static async Task SessionRenewalScenario()
 {
  void Check(bool ok,string text){if(!ok)throw new Exception(text);Console.WriteLine("PASS: "+text);}
  foreach(var mode in new[]{"expiry","concurrent","401-write","transient","revoked","wrong-user","wrong-company","scope-change","dispose-race"}){
   var client=New("PdmApiClient","http://session-fixture.invalid/");var company=Guid.NewGuid();var now=DateTimeOffset.UtcNow;
   SetField(client,"sessionNow",(Func<DateTimeOffset>)(()=>now));
   var json=new JavaScriptSerializer();var renewals=0;var business=0;var expired=0;var authorizedWrites=0;var bodies=new List<string>();var tokens=new List<string>();
   var started=new TaskCompletionSource<bool>();var finish=new TaskCompletionSource<bool>();
   string Login(string access)=>json.Serialize(new{accessToken=access,resumeToken="resume-"+access,expiresAt=now.AddHours(8).ToString("o"),username=mode=="wrong-user"&&access!="A"?"other":"tester",activeCompanyId=mode=="wrong-company"&&access!="A"?Guid.NewGuid():company});
   var handler=new SessionFixtureHandler(async(request,ct)=>{
    var path=request.RequestUri.AbsolutePath;var access=request.Headers.Authorization?.Parameter;
    HttpResponseMessage Response(HttpStatusCode status,string text)=>new HttpResponseMessage(status){Content=new StringContent(text)};
    if(path=="/api/auth/login")return Response(HttpStatusCode.OK,Login("A"));
    if(path=="/api/auth/resume"){
     var count=Interlocked.Increment(ref renewals);var body=await request.Content.ReadAsStringAsync();
     Check(body.Contains("resume-A")||body.Contains("resume-B"),mode+"：复用当前会话凭据，不重发账号密码登录");
     if(mode=="dispose-race"){started.TrySetResult(true);await finish.Task;}
     await Task.Delay(15,ct);
     if(mode=="transient"&&count==1)return Response(HttpStatusCode.ServiceUnavailable,"{}");
     if(mode=="revoked")return Response(HttpStatusCode.Unauthorized,"{}");
     if(mode=="scope-change")TreeCall(client,"SetActiveCompany",Guid.NewGuid());
     return Response(HttpStatusCode.OK,Login("B"));
    }
    if(path=="/api/auth/logout")return Response(HttpStatusCode.OK,"true");
    lock(tokens)tokens.Add(access);
    if(path=="/write"){
     var payload=await request.Content.ReadAsStringAsync();lock(bodies)bodies.Add(payload);
     if(access=="A")return Response(HttpStatusCode.Unauthorized,"{}");
     Interlocked.Increment(ref authorizedWrites);return Response(HttpStatusCode.OK,"true");
    }
    Interlocked.Increment(ref business);return Response(HttpStatusCode.OK,"{}");
   });
   foreach(var field in new[]{"httpClient","controlledOpenHttpClient"}){
    var http=(HttpClient)Field(client,field);var invoker=typeof(HttpMessageInvoker).GetFields(BindingFlags.Instance|BindingFlags.NonPublic).Single(f=>typeof(HttpMessageHandler).IsAssignableFrom(f.FieldType));
    var wrapper=(DelegatingHandler)invoker.GetValue(http);var old=wrapper.InnerHandler;wrapper.InnerHandler=handler;old.Dispose();
   }
   Type("PdmApiClient").GetEvent("AuthenticationExpired").AddEventHandler(client,(EventHandler)((_,__)=>expired++));
   var login=(Task)TreeCall(client,"LoginAsync","tester","fixture",CancellationToken.None);await login;
   var httpMain=(HttpClient)Field(client,"httpClient");var httpLarge=(HttpClient)Field(client,"controlledOpenHttpClient");
   if(mode!="401-write")now=now.AddHours(8);
   try{
    if(mode=="concurrent"){
     await Task.WhenAll(Enumerable.Range(0,20).Select(async i=>{using(var response=await (i%2==0?httpMain:httpLarge).GetAsync("probe"))response.EnsureSuccessStatusCode();}));
     Check(renewals==1&&business==20&&tokens.All(t=>t=="B"),"两个HTTP通道并发过期只续期一次，全部使用新令牌");
    }else if(mode=="401-write"){
     using(var content=new StringContent("{\"batchId\":\"fixed\"}"))using(var response=await httpMain.PostAsync("write",content))response.EnsureSuccessStatusCode();
     Check(renewals==1&&authorizedWrites==1&&bodies.Count==2&&bodies[0]==bodies[1],"401在执行端点前拒绝后仅重发一次同一内容，不重复已接受的业务写入");
    }else if(mode=="transient"){
     try{using(var response=await httpMain.GetAsync("probe"))throw new Exception("未报告暂时不可用");}catch(Exception error)when(error.GetType().Name=="ServiceUnavailableException"){}
     Check((bool)Get(client,"IsAuthenticated")&&expired==0,"续期短暂不可用保留登录状态，不误报过期");
     using(var response=await httpMain.GetAsync("probe"))response.EnsureSuccessStatusCode();Check(renewals==2&&tokens.Last()=="B","连接恢复后自动续期并继续原账号请求");
    }else if(mode=="dispose-race"){
     var pending=httpMain.GetAsync("probe");await started.Task;((IDisposable)client).Dispose();finish.TrySetResult(true);
     try{using(var response=await pending)throw new Exception("退出后请求仍执行");}catch(Exception error)when(error is InvalidOperationException||error is OperationCanceledException||error is ObjectDisposedException){}
     Check(!(bool)Get(client,"IsAuthenticated")&&business==0,"退出与续期竞争不会恢复登录或执行原请求");
    }else if(mode=="expiry"){
     await (Task)TreeCall(client,"HeartbeatAsync");using(var response=await httpLarge.GetAsync("probe"))response.EnsureSuccessStatusCode();
     Check(renewals==1&&tokens.All(t=>t=="B")&&expired==0,"无需CAD线程参与，心跳延续8小时会话，普通与长请求通道均保持登录");
    }else{
     Exception failure=null;try{using(var response=await httpMain.GetAsync("probe"))response.EnsureSuccessStatusCode();}catch(Exception error){failure=error;}
     Check(failure!=null&&business==0,mode+"：服务端撤销或身份/公司变化时不执行原请求");
     if(mode=="revoked")Check(expired==1&&!(bool)Get(client,"IsAuthenticated"),"服务端撤销仍结束登录，不绕过会话安全规则");
    }
   }finally{finish.TrySetResult(true);((IDisposable)client).Dispose();}
  }
 }
 sealed class SessionFixtureHandler:HttpMessageHandler
 {
  readonly Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> respond;
  internal SessionFixtureHandler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> respond)=>this.respond=respond;
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>respond(request,ct);
 }
}
