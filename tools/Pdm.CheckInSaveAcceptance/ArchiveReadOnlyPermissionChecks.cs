using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

internal static partial class Program
{
 static async Task ArchiveReadOnlyPermissionScenario(string folder)
 {
  Directory.CreateDirectory(folder);
  Type("ArchiveBatchJournal").GetField("directory",Flags).SetValue(null,Path.Combine(Path.GetFullPath(folder),"batch-journal"));
  void Check(bool ok,string text){if(!ok)throw new Exception(text);Console.WriteLine("PASS: "+text);}
  foreach(var kind in new[]{"Part","Assembly","Drawing"})
  foreach(var mode in new[]{"new","existing","owned","denied"})
  {
   var path=Path.Combine(folder,kind+"-"+mode+(kind=="Part"?".SLDPRT":kind=="Assembly"?".SLDASM":".SLDDRW"));
   File.WriteAllText(path,"saved original bytes; no real CAD opened");File.SetAttributes(path,FileAttributes.ReadOnly);
   var original=File.ReadAllBytes(path);var project=Guid.NewGuid();var id=Guid.NewGuid();var session=Guid.NewGuid();
   var addin=New("PdmAddin");SetField(addin,"authenticatedUsername","tester");SetField(addin,"checkoutSessionId",session);
   var node=New("CadTreeNode");Set(node,"Kind",Enum.Parse(Type("CadDocumentKind"),kind));Set(node,"FullPath",path);Set(node,"FileName",Path.GetFileName(path));
   if(mode!="new")Set(node,"DocumentId",id);
   if(mode=="owned"){Set(node,"CheckedOutBy","tester");Set(node,"CheckoutSessionId",session);}
   SetField(addin,"currentTree",node);
   var appType=Type("PdmAddin").GetField("application",Flags).FieldType;var nativeCalls=0;var dirty=false;
   var model=new Proxy(appType.Assembly.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true),call=>{
    switch(call.MethodName){case "GetPathName":return path;case "GetSaveFlag":return dirty;case "IsOpenedReadOnly":return true;
     case "SetReadOnlyState":nativeCalls++;return false;default:throw new Exception("存档准备不得保存、关闭或切换文档："+call.MethodName);}
   }).GetTransparentProxy();
   SetField(addin,"application",new Proxy(appType,call=>call.MethodName=="get_ActiveDoc"?model:call.MethodName=="GetDocuments"?new[]{model}:throw new Exception("不得关闭、重开或切换文档："+call.MethodName)).GetTransparentProxy());
   var requests=new List<string>();
   using(var handler=new ReferenceRecoveryHandler(request=>{
    requests.Add(request.RequestUri.AbsolutePath);
    if(mode=="denied")return new HttpResponseMessage(HttpStatusCode.Forbidden){Content=new StringContent("{\"title\":\"当前用户没有存档权限。\"}")};
    return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(new JavaScriptSerializer().Serialize(new{
     id,projectId=project,fileName=Path.GetFileName(path),kind=Convert.ToInt32(Get(node,"Kind")),checkedOutBy="tester",checkoutSessionId=session,
     revision=new{display="W1"}}))};
   }))
   using(var client=(IDisposable)New("PdmApiClient","http://fixture.invalid/"))
   {
    ((HttpClient)Field(client,"httpClient")).Dispose();SetField(client,"httpClient",new HttpClient(handler){BaseAddress=new Uri("http://fixture.invalid/")});
    Set(client,"EditJournalScope","readonly-permission-fixture-"+session.ToString("N"));SetField(addin,"apiClient",client);
    var item=New("BatchOperationItem",node,0);var reserved=new List<Guid>();var batch=Guid.NewGuid();Exception failure=null;
    Type("ArchiveBatchJournal").GetMethod("Begin",Flags).Invoke(null,new object[]{batch,session,"readonly-fixture","tester"});
    try{await (Task)TreeCall(addin,"PrepareBatchCheckInPermissionAsync",item,project,CancellationToken.None,null,batch,reserved);}catch(Exception error){failure=error;}
    if(failure!=null&&mode!="denied")Console.WriteLine("DIAGNOSTIC: "+failure);
    Check((failure!=null)==(mode=="denied"),kind+"/"+mode+"：已保存只读图档可准备存档，PLM拒绝权限仍拦截");
    Check(nativeCalls==0&&(File.GetAttributes(path)&FileAttributes.ReadOnly)!=0,kind+"/"+mode+"：不强制切换可编辑，不改源文件只读属性");
    Check(Convert.ToBase64String(File.ReadAllBytes(path))==Convert.ToBase64String(original),kind+"/"+mode+"：原文件名和内容不变");
    Check(requests.Count==(mode=="owned"?0:1)&&requests.TrueForAll(value=>value.EndsWith(mode=="new"?"/registrations":"/checkout")),kind+"/"+mode+"：只准备PLM权限，不上传或提交版本");
    if(mode!="denied")
    {
     var copier=Array.Find(Type("PdmAddin").GetMethods(Flags),method=>method.Name=="CreateCheckInUploadCopy"&&method.GetParameters().Length==2&&method.GetParameters()[0].ParameterType==typeof(string));
     string copy;using(var held=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))copy=(string)copier.Invoke(null,new object[]{path,id});
     Check(Path.GetFileName(copy)==Path.GetFileName(path)&&Convert.ToBase64String(File.ReadAllBytes(copy))==Convert.ToBase64String(original)
      &&(File.GetAttributes(path)&FileAttributes.ReadOnly)!=0,kind+"/"+mode+"：共享只读源可生成同名完整上传副本，源文件保持只读");
     Type("PdmAddin").GetMethod("DeleteCheckInUploadCopy",Flags).Invoke(null,new object[]{copy});
     // Network access can be read-only while the file's DOS attribute is not set.
     File.SetAttributes(path,FileAttributes.Normal);
     TreeCall(addin,"ProtectLoadedDocument",path,true);
     Check((File.GetAttributes(path)&FileAttributes.ReadOnly)==0&&nativeCalls==0,
      kind+"/"+mode+"：存档后只保护PLM工作副本，不写共享源属性或切换原窗口");
     File.SetAttributes(path,FileAttributes.ReadOnly);
    }
    if(kind!="Drawing"&&mode=="new")
    {
     dirty=true;Set(node,"IsModifiedInSolidWorks",true);
     var items=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(Type("BatchOperationItem")));items.Add(item);
     Exception saveFailure=null;try{TreeCall(addin,"ValidateBatchSavedState",items,null,false);}catch(Exception error){saveFailure=error;}
     Check(saveFailure!=null&&saveFailure.ToString().Contains("未保存"),kind+"：真实未保存修改仍在上传前拦截");
    }
   }
   File.SetAttributes(path,FileAttributes.Normal);
  }
  var message=(string)Type("PdmAddin").GetMethod("ArchivePreflightError",Flags).Invoke(null,new object[]{new IOException("测试.SLDPRT已获取PLM编辑权限，但SolidWorks仍以只读方式打开。请关闭该图档后重新获取权限。")});
  Check(message.Contains("只读")&&!message.Contains("无法读取或被占用"),"权限切换失败保留明确中文原因，不误报文件占用");
 }
}
