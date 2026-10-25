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
using System.Windows.Forms;

internal static partial class Program
{
 static async Task DiscardOwnerRepeatScenario()
 {
  foreach(var mode in new[]{"part-active","assembly-active-hidden-part","two-owners","repeat-clean","repeat-restored","stale-repeat","reacquire","dirty-owner","restore-owner-failed","unrelated-active"})
  using(var fixture=new DiscardPathFixture(true))
  using(var pane=(Control)New("PdmTaskPaneControl","放弃编辑窗口和重复操作回归"))
  {
   var plugin=fixture.Plugin;var partNode=fixture.Node;var folder=Path.GetDirectoryName(fixture.CurrentPath);var project=(Guid)Get(fixture.Manifest,"ProjectId");var session=(Guid)Field(plugin,"checkoutSessionId");
   SetField(plugin,"taskPaneControl",pane);SetField(plugin,"currentProjectId",project);pane.CreateControl();
   var rootPath=Path.Combine(folder,"CW0030002.10.SLDASM");var secondPath=Path.Combine(folder,"SECOND.SLDASM");var unrelatedPath=Path.Combine(folder,"UNRELATED.SLDPRT");
   File.WriteAllText(rootPath,"unchanged first assembly");File.WriteAllText(secondPath,"unchanged second assembly");File.WriteAllText(unrelatedPath,"unrelated user document");
   for(var i=0;i<209;i++)File.WriteAllText(Path.Combine(folder,"UNTOUCHED-"+i+".SLDPRT"),"untouched "+i);
   var before=Directory.GetFiles(folder).Where(path=>path!=fixture.CurrentPath).ToDictionary(path=>path,path=>WorkingReuseHash(path));
   var hasEdit=mode!="repeat-clean";if(hasEdit)File.WriteAllText(fixture.CurrentPath,"selected real saved part edit");
   var modelType=Type("PdmAddin").GetField("application",Flags).FieldType.Assembly.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true);var appType=Type("PdmAddin").GetField("application",Flags).FieldType;var interop=appType.Assembly;
   var rootLoaded=true;var secondLoaded=mode=="two-owners";var unrelatedLoaded=mode=="unrelated-active";var partLoaded=true;var partVisible=mode!="assembly-active-hidden-part";var rootReadOnly=mode!="dirty-owner";var partReadOnly=false;
   var active=mode=="assembly-active-hidden-part"?rootPath:mode=="unrelated-active"?unrelatedPath:fixture.CurrentPath;
   var initialActive=active;var closes=new List<string>();var opens=new List<string>();var activations=new List<string>();var nativeWrites=new List<string>();
   object Manager(string name)
   {
    var configuration=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.Configuration",true),call=>call.MethodName=="get_Name"?name:throw new Exception(call.MethodName)).GetTransparentProxy();
    return new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ConfigurationManager",true),call=>call.MethodName=="get_ActiveConfiguration"?configuration:throw new Exception(call.MethodName)).GetTransparentProxy();
   }
   var managers=new Dictionary<string,object>{{rootPath,Manager("总装配置")},{secondPath,Manager("第二总装配置")},{fixture.CurrentPath,Manager("零件配置")},{unrelatedPath,Manager("其他配置")}};
   object Common(System.Runtime.Remoting.Messaging.IMethodCallMessage call,string path,int kind)
   {
    switch(call.MethodName){case "GetPathName":return path;case "GetTitle":return Path.GetFileName(path);case "GetType":return kind;case "get_Visible":return path!=fixture.CurrentPath||partVisible;case "GetSaveFlag":return mode=="dirty-owner"&&path==rootPath;case "IsOpenedReadOnly":return path==fixture.CurrentPath?partReadOnly:path==rootPath?rootReadOnly:true;case "get_ConfigurationManager":return managers[path];
     case "SetReadOnlyState":if(path!=fixture.CurrentPath)throw new Exception("改变了非所选图档权限");partReadOnly=(bool)call.Args[0];return true;
     default:nativeWrites.Add(call.MethodName);throw new Exception("Unexpected native mutation "+call.MethodName);}
   }
   var part=new Proxy(modelType,call=>Common(call,fixture.CurrentPath,1)).GetTransparentProxy();
   var component=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.Component2",true),call=>call.MethodName=="GetModelDoc2"?part:call.MethodName=="GetPathName"?fixture.CurrentPath:throw new Exception(call.MethodName)).GetTransparentProxy();
   var root=new DiscardAssemblyProxy(modelType,call=>call.MethodName=="GetComponents"?new[]{component}:Common(call,rootPath,2)).GetTransparentProxy();
   var second=new DiscardAssemblyProxy(modelType,call=>call.MethodName=="GetComponents"?new[]{component}:Common(call,secondPath,2)).GetTransparentProxy();
   var other=new Proxy(modelType,call=>Common(call,unrelatedPath,1)).GetTransparentProxy();
   var all=new Dictionary<string,object>{{fixture.CurrentPath,part},{rootPath,root},{secondPath,second},{unrelatedPath,other}};
   SetField(plugin,"application",new Proxy(appType,call=>{
    switch(call.MethodName){case "GetDocuments":return all.Where(pair=>pair.Key==fixture.CurrentPath?partLoaded:pair.Key==rootPath?rootLoaded:pair.Key==secondPath?secondLoaded:unrelatedLoaded).Select(pair=>pair.Value).ToArray();case "get_ActiveDoc":return active==null?null:all[active];
     case "GetDocumentDependencies2":return Array.Empty<string>();
     case "CloseDoc":var title=(string)call.Args[0];var path=all.Keys.Single(value=>Path.GetFileName(value)==title);closes.Add(path);if(path==rootPath)rootLoaded=false;else if(path==secondPath)secondLoaded=false;else if(path==fixture.CurrentPath){partLoaded=false;partVisible=false;}else throw new Exception("关闭了无关窗口");active=null;return null;
     case "OpenDoc6":var target=(string)call.Args[0];opens.Add(target);if(target==rootPath&&mode=="restore-owner-failed")throw new IOException("模拟总装窗口恢复失败");
      var expected=target==rootPath?"总装配置":target==secondPath?"第二总装配置":"零件配置";if((string)call.Args[3]!=expected)throw new Exception("丢失原配置");
      if(target==rootPath)rootLoaded=true;else if(target==secondPath)secondLoaded=true;else if(target==fixture.CurrentPath)partVisible=true;else throw new Exception("打开了无关图档");partLoaded=true;partReadOnly=true;active=target;return all[target];
     case "ActivateDoc3":var requested=all.Keys.Single(value=>Path.GetFileName(value)==(string)call.Args[0]);activations.Add(requested);if(requested==fixture.CurrentPath)partVisible=true;active=requested;return all[requested];
     default:nativeWrites.Add(call.MethodName);throw new Exception("Unexpected CAD mutation "+call.MethodName);}
   }).GetTransparentProxy());
   var current=mode=="assembly-active-hidden-part"?New("CadTreeNode"):partNode;
   if(!ReferenceEquals(current,partNode)){Set(current,"FullPath",rootPath);Set(current,"FileName",Path.GetFileName(rootPath));Set(current,"Kind",Enum.Parse(Type("CadDocumentKind"),"Assembly"));((IList)Get(current,"Children")).Add(partNode);}
   SetField(plugin,"currentTree",current);
   using(var handler=new OwnerDiscardHandler(fixture.Manifest,session))
   using(var http=new HttpClient(handler){BaseAddress=new Uri("http://discard-physical.invalid/")})
   {
    http.DefaultRequestHeaders.Authorization=new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer","fixture");SetField(fixture.Client,"httpClient",http);SetField(fixture.Client,"controlledOpenHttpClient",http);
    var acquired=await CheckoutFixturePart(fixture,handler);Set(partNode,"CheckedOutBy","tester");Set(partNode,"CheckoutSessionId",session);
    Exception failure=null;try{await (Task)DiscardCall(plugin,"DiscardSelectedWorkingFilesAsync",DiscardNodes(partNode));}catch(Exception error){failure=error;}
    var fails=mode=="dirty-owner"||mode=="restore-owner-failed";
    if((failure!=null)!=fails)throw new Exception("Unexpected discard result "+mode,failure);
    if(mode=="dirty-owner"){if(closes.Count!=0||!rootLoaded||!partLoaded||File.ReadAllText(fixture.CurrentPath)!="selected real saved part edit")throw new Exception("丢弃了上层总装未保存修改");}
    else if(mode=="restore-owner-failed"){if(failure==null||!failure.Message.Contains("原窗口"))throw new Exception("总装恢复失败被报告为成功");}
    else
    {
     if(!rootLoaded||!partLoaded||mode=="two-owners"&&!secondLoaded||active!=initialActive)throw new Exception("放弃零件后原总装或焦点丢失 "+mode);
     if(mode=="assembly-active-hidden-part"&&partVisible)throw new Exception("把隐藏组件打开成了独立窗口");
     if(mode!="assembly-active-hidden-part"&&!partVisible)throw new Exception("未恢复原有零件窗口");
     if(hasEdit&&(!opens.Contains(rootPath)||mode=="two-owners"&&!opens.Contains(secondPath)))throw new Exception("只恢复了零件，未恢复原有引用总装 "+mode);
     if(!hasEdit&&(closes.Count!=0||opens.Count!=0))throw new Exception("无修改时不应重开窗口");
     var beforeCloses=closes.Count;var beforeOpens=opens.Count;var beforeDownloads=handler.Downloads;var beforeDiscards=handler.Discards;
     if(mode.StartsWith("repeat-")||mode=="stale-repeat")
     {
      if(mode=="stale-repeat"){Set(partNode,"CheckedOutBy","tester");Set(partNode,"CheckoutSessionId",session);DiscardCall(plugin,"RememberLocalEdit",partNode,acquired);}
      await (Task)DiscardCall(plugin,"DiscardSelectedWorkingFilesAsync",DiscardNodes(partNode));
      if(closes.Count!=beforeCloses||opens.Count!=beforeOpens||handler.Downloads!=beforeDownloads||handler.Discards!=beforeDiscards||active!=initialActive||!rootLoaded)throw new Exception("第二次放弃再次恢复或关闭了窗口 "+mode);
     }
     if(mode=="reacquire")
     {
      await CheckoutFixturePart(fixture,handler);File.SetAttributes(fixture.CurrentPath,FileAttributes.Normal);partReadOnly=false;
      await (Task)DiscardCall(plugin,"DiscardSelectedWorkingFilesAsync",DiscardNodes(partNode));
      if(handler.Discards!=beforeDiscards+1||closes.Count!=beforeCloses||opens.Count!=beforeOpens||!rootLoaded||active!=initialActive)throw new Exception("再次获取后的有效放弃被旧完成状态屏蔽");
     }
    }
    foreach(var pair in before)if(WorkingReuseHash(pair.Key)!=pair.Value)throw new Exception("修改了其他源文件 "+mode+" "+pair.Key);
    if(nativeWrites.Count!=0||handler.OtherWrites!=0)throw new Exception("额外原生写入或归档提交 "+mode+" "+string.Join(",",nativeWrites));
    fixture.AssertOldUntouched();Console.WriteLine("PASS: discard owners and repeat "+mode+"; exact windows, focus/configuration, source bytes and permission generations verified");
   }
  }
 }

 static async Task<object> CheckoutFixturePart(DiscardPathFixture fixture,OwnerDiscardHandler handler)
 {
  var client=fixture.Client;var task=(Task)DiscardCall(client,"CheckoutAsync",Get(fixture.Node,"DocumentId"),Field(fixture.Plugin,"checkoutSessionId"),"fixture-machine",CancellationToken.None,null);await task;
  var document=Get(task,"Result");DiscardCall(fixture.Plugin,"ApplyCheckoutDocument",fixture.Node,document);return document;
 }
 sealed class OwnerDiscardHandler:HttpMessageHandler
 {
  readonly object manifest;readonly Guid session;public int Downloads,Discards,OtherWrites,Checkouts;object document;
  public OwnerDiscardHandler(object manifest,Guid session){this.manifest=manifest;this.session=session;}
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
  {
   var path=request.RequestUri.AbsolutePath;object value;
   if(path.EndsWith("/document-operation-rules"))value=new{disabledRuleIds=new string[0]};
   else if(path.EndsWith("/checkout")){Checkouts++;document=new{id=Get(manifest,"RootDocumentId"),projectId=Get(manifest,"ProjectId"),kind=1,fileName="PART.SLDPRT",checkedOutBy="tester",checkoutSessionId=(Guid?)session,checkedOutAt=new DateTime(2026,10,24,9,0,0,DateTimeKind.Utc).AddSeconds(Checkouts)};value=document;}
   else if(path.EndsWith("/discard-checkout")){Discards++;document=new{id=Get(manifest,"RootDocumentId"),projectId=Get(manifest,"ProjectId"),kind=1,fileName="PART.SLDPRT",checkedOutBy=(string)null,checkoutSessionId=(Guid?)null};value=document;}
   else if(path.EndsWith("/documents"))value=new[]{document};
   else if(path.EndsWith("/versions"))value=new[]{new{id=Get(manifest,"RootVersionId")}};
   else if(path.EndsWith("/open-manifest"))value=manifest;
   else if(path.EndsWith("/file")){Downloads++;return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(DiscardPathFixture.Canonical)});}
   else{OtherWrites++;throw new Exception("Unexpected business request "+path);}
   return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(new JavaScriptSerializer().Serialize(value))});
  }
 }
}
