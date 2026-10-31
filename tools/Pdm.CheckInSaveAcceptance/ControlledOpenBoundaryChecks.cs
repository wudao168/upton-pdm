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
 static async Task ControlledOpenBoundaryScenario(string output)
 {
  output=Path.GetFullPath(output);Directory.CreateDirectory(output);
  var identity=Type("PdmDocumentIdentityStore");var bindings=identity.GetField("bindingDirectory",Flags);var resolver=identity.GetField("workspaceRootResolver",Flags);
  var oldBindings=bindings.GetValue(null);var oldResolver=resolver.GetValue(null);
  bindings.SetValue(null,Path.Combine(output,"bindings"));resolver.SetValue(null,new Func<string>(()=>output));
  void Check(bool ok,string text){if(!ok)throw new Exception(text);Console.WriteLine("PASS: "+text);}
  try
  {
   var directory=Path.Combine(output,"View","controlled");var original=Path.Combine(output,"original");Directory.CreateDirectory(directory);Directory.CreateDirectory(original);
   var manifest=New("ControlledOpenManifestDto");Set(manifest,"ProjectId",Guid.NewGuid());var files=(IList)Activator.CreateInstance(Property(manifest,"Files").PropertyType);
   foreach(var name in new[]{"ROOT.SLDASM","PART.SLDPRT","DRAWING.SLDDRW"})
   {var file=New("ControlledOpenFileDto");Set(file,"FileName",name);Set(file,"RelativePath",name);files.Add(file);File.WriteAllText(Path.Combine(directory,name),"controlled");File.WriteAllText(Path.Combine(original,name),"different original geometry");}
   Set(manifest,"Files",files);
   var plugin=New("PdmAddin");var node=New("CadTreeNode");Set(node,"FullPath",Path.Combine(directory,"ROOT.SLDASM"));SetField(plugin,"currentTree",node);
   ((IDictionary)Field(plugin,"controlledOpenManifests")).Add(directory,manifest);
   foreach(var name in new[]{"ROOT.SLDASM","PART.SLDPRT","DRAWING.SLDDRW"})
   {
    var path=Path.Combine(original,name);Check((int)TreeCall(plugin,"OnFileOpening",path)==1,"原生打开前拒绝旧目录的同名文件："+name);
    Check((int)Field(plugin,"nativeFileOpenInProgress")==0,"取消打开不遗留原生加载忙碌标记："+name);
    Check(((string)Field(plugin,"blockedControlledOpenMessage")).Contains(path),"加载回调结束后以中文提示实际冲突路径："+name);
    Check(File.ReadAllText(path)=="different original geometry","保留原图文件及内容："+name);
   }
   var configBefore=New("ControlledOpenFileDto");var configAfter=New("ControlledOpenFileDto");Set(configBefore,"FileName","PART.SLDDRW");Set(configAfter,"FileName","PART.SLDDRW");Set(configBefore,"Configuration","图纸");Set(configAfter,"Configuration","默认");
   Check((bool)TreeCall(New("PdmAddin"),"WorkingConfigurationMatches",configBefore,configAfter),"工程图显示占位不作为3D配置冲突");
   Set(configBefore,"FileName","PART.SLDPRT");Set(configAfter,"FileName","PART.SLDPRT");Check(!(bool)TreeCall(New("PdmAddin"),"WorkingConfigurationMatches",configBefore,configAfter),"实际零件配置不一致仍严格拦截");
   foreach(var path in new[]{Path.Combine(directory,"ROOT.SLDASM"),Path.Combine(directory,"PART.SLDPRT"),Path.Combine(directory,"DRAWING.SLDDRW")})
   {Check((int)TreeCall(plugin,"OnFileOpening",path)==0,"允许当前工作区原生打开："+Path.GetFileName(path));TreeCall(plugin,"OnFileOpened",path);}
   var unrelated=Path.Combine(original,"UNRELATED.SLDPRT");Check((int)TreeCall(plugin,"OnFileOpening",unrelated)==0,"受控打开之外允许用户打开不重名的新图档");TreeCall(plugin,"OnFileOpened",unrelated);
   var reference=New("CadTreeNode");Set(reference,"FullPath",Path.Combine(original,"PART.SLDPRT"));((IList)Get(node,"Children")).Add(reference);
   var sourceRejected=false;try{TreeCall(plugin,"ValidateControlledArchiveSources",node);}catch(System.Reflection.TargetInvocationException error)when(error.InnerException is InvalidOperationException){sourceRejected=true;}
   Check(sourceRejected,"存档前发现旧目录实际引用时提前拦截，不等待上传阶段");
   Set(reference,"FullPath",Path.Combine(directory,"PART.SLDPRT"));TreeCall(plugin,"ValidateControlledArchiveSources",node);Check(true,"存档引用已在当前受控目录时允许继续");
   foreach(var path in new[]{Path.Combine(original,"PART.SLDPRT"),Path.Combine(original,"MISSING.SLDPRT"),Path.Combine(original,"UNKNOWN.SLDDRW")})
   using(var boundary=(IDisposable)New("ControlledOpenBoundary",directory,manifest))
   {
    Check((int)TreeCall(plugin,"OnFileOpening",path)==1,"受控加载不使用原图或未登记的包外图档："+Path.GetFileName(path));
    var rejected=false;try{TreeCall(boundary,"Verify");}catch(System.Reflection.TargetInvocationException error)when(error.InnerException is InvalidOperationException){rejected=true;}Check(rejected,"取消外部加载后不能误报打开成功");
   }
   foreach(var mode in new[]{"linked-parameter","linked-import","read-deps-failed","replacement-failed","replacement-not-persisted","resource-link-lost","untracked-present","untracked-missing"})
   {
    var folder=Path.Combine(output,"resource-"+mode);var external=Path.Combine(folder,"original");var localFolder=Path.Combine(folder,"View");Directory.CreateDirectory(external);Directory.CreateDirectory(localFolder);
    var ownerPath=Path.Combine(localFolder,"OWNER.SLDPRT");var resourceName=mode=="linked-import"?"SOURCE.step":"PARAMETERS.txt";var resourcePath=Path.Combine(localFolder,resourceName);var oldSource=Path.Combine(external,resourceName);
    File.WriteAllText(ownerPath,"saved native geometry");if(!mode.StartsWith("untracked-"))File.WriteAllText(resourcePath,"controlled source data");if(mode!="untracked-missing")File.WriteAllText(oldSource,"different source data");
    var package=New("ControlledOpenManifestDto");var project=Guid.NewGuid();Set(package,"ProjectId",project);var entries=(IList)Activator.CreateInstance(Property(package,"Files").PropertyType);
    foreach(var path in mode.StartsWith("untracked-")?new[]{ownerPath}:new[]{ownerPath,resourcePath})
    {
     var file=New("ControlledOpenFileDto");Set(file,"DocumentId",Guid.NewGuid());Set(file,"VersionId",Guid.NewGuid());Set(file,"FileName",Path.GetFileName(path));Set(file,"RelativePath",Path.GetFileName(path));Set(file,"Sha256",WorkingReuseHash(path));Set(file,"FileLength",new FileInfo(path).Length);entries.Add(file);
     identity.GetMethod("TryWriteControlledVersion",Flags).Invoke(null,new[]{(object)path,Get(file,"DocumentId"),project,Get(file,"VersionId"),"W1",Get(file,"Sha256"),Get(file,"FileLength")});
    }
    Set(package,"Files",entries);var sourceAddress=oldSource;var replaced=0;var resourceInterop=Type("PdmAddin").GetField("application",Flags).FieldType;
    var app=new Proxy(resourceInterop,call=>{
     switch(call.MethodName){case "GetDocuments":return new object[0];case "GetDocumentDependencies2":if(mode=="read-deps-failed")throw new IOException("native dependency read fixture");return mode=="resource-link-lost"&&replaced>0?new string[0]:new[]{resourceName,sourceAddress};
      case "ReplaceReferencedDocument":replaced++;File.AppendAllText(ownerPath,"|controlled address");if(mode!="replacement-not-persisted")sourceAddress=resourcePath;return mode!="replacement-failed";
      default:throw new Exception("不得打开任何来源或修改配置："+call.MethodName);}
    }).GetTransparentProxy();
    Exception failure=null;try{Type("ControlledReferenceMaterializer").GetMethod("Prepare",Flags).Invoke(null,new[]{app,(object)ownerPath,package});}catch(System.Reflection.TargetInvocationException error){failure=error.InnerException;}
    var expected=mode=="linked-parameter"||mode=="linked-import"||mode=="untracked-missing";
    Check((failure==null)==expected,"受控资源引用核对："+mode);
    Check(!File.Exists(oldSource)||File.ReadAllText(oldSource)=="different source data","资源原文件保持原样，未作为PLM副本加载："+mode);
    if(!expected)Check(File.ReadAllText(ownerPath)=="saved native geometry","外部资源定位不安全时先恢复原生副本再停止打开："+mode);
   }
   using(var boundary=(IDisposable)New("ControlledOpenBoundary",directory,manifest))
   {
    Check((int)TreeCall(plugin,"OnFileOpening",Path.Combine(directory,"PART.SLDPRT"))==0,"受控加载允许清单指定的完整路径");TreeCall(plugin,"OnFileOpened",Path.Combine(directory,"PART.SLDPRT"));
    using(var nested=(IDisposable)New("ControlledOpenBoundary",original,manifest))
     Check((int)TreeCall(plugin,"OnFileOpening",Path.Combine(original,"PART.SLDPRT"))==0,"嵌套受控读取按自身清单核对，不受旧活动树误拦截");
    Check((int)TreeCall(plugin,"OnFileOpening",Path.Combine(directory,"PART.SLDPRT"))==0,"嵌套范围结束后恢复外层边界");TreeCall(plugin,"OnFileOpened",Path.Combine(directory,"PART.SLDPRT"));
   }
   foreach(var mode in new[]{"original-present","original-missing","original-open","different-project","stale-version","stale-config","actual-config","occurrence-config","unknown-edit","owned-edit","source-copy","acquire-inplace"})
   {
    var folder=Path.Combine(output,"View",mode);Directory.CreateDirectory(folder);var partPath=Path.Combine(folder,"PART.SLDPRT");File.WriteAllText(partPath,"saved model bytes");
    var oldPath=Path.Combine(output,"source-"+mode,"PART.SLDPRT");Directory.CreateDirectory(Path.GetDirectoryName(oldPath));if(mode!="original-missing")File.WriteAllText(oldPath,"other original geometry");
    var source=New("ControlledOpenFileDto");var project=Guid.NewGuid();Set(source,"DocumentId",Guid.NewGuid());Set(source,"VersionId",Guid.NewGuid());Set(source,"FileName","PART.SLDPRT");Set(source,"RelativePath","PART.SLDPRT");Set(source,"Configuration","Default");Set(source,"Sha256",WorkingReuseHash(partPath));Set(source,"FileLength",new FileInfo(partPath).Length);Set(source,"Revision","W2");
    identity.GetMethod("TryWriteControlledVersion",Flags).Invoke(null,new[]{(object)partPath,Get(source,"DocumentId"),project,Get(source,"VersionId"),"W2",Get(source,"Sha256"),Get(source,"FileLength")});
    var owner=New("ControlledOpenManifestDto");Set(owner,"ProjectId",mode=="different-project"?Guid.NewGuid():project);var ownerFiles=(IList)Activator.CreateInstance(Property(owner,"Files").PropertyType);ownerFiles.Add(source);Set(owner,"Files",ownerFiles);
    Set(owner,"RootDocumentId",Get(source,"DocumentId"));Set(owner,"RootVersionId",Get(source,"VersionId"));Set(owner,"RootRelativePath","PART.SLDPRT");
    var requested=New("ControlledOpenManifestDto");Set(requested,"ProjectId",project);Set(requested,"RootDocumentId",Get(source,"DocumentId"));Set(requested,"RootVersionId",Get(source,"VersionId"));Set(requested,"RootRelativePath","PART.SLDPRT");
    Set(requested,"ForEdit",mode=="acquire-inplace");
    var json=new JavaScriptSerializer();var requestedFile=json.Deserialize(json.Serialize(source),source.GetType());Set(requestedFile,"IsRoot",true);
    if(mode=="stale-version")Set(requestedFile,"VersionId",Guid.NewGuid());if(mode=="stale-config")Set(requestedFile,"Configuration","Other");if(mode=="source-copy"){Set(requestedFile,"Sha256",new string('A',64));Set(requestedFile,"FileLength",1000L);}
    if(mode=="occurrence-config")Set(source,"Configuration","AssemblyOccurrence");
    var requestedFiles=(IList)Activator.CreateInstance(Property(requested,"Files").PropertyType);requestedFiles.Add(requestedFile);Set(requested,"Files",requestedFiles);
    var addin=New("PdmAddin");var session=(Guid)Field(addin,"checkoutSessionId");SetField(addin,"authenticatedUsername","tester");((IDictionary)Field(addin,"controlledOpenManifests")).Add(folder,owner);
    var appType=Type("PdmAddin").GetField("application",Flags).FieldType;var interop=appType.Assembly;var extension=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDocExtension",true),call=>call.MethodName=="ListExternalFileReferences2"?null:throw new Exception(call.MethodName)).GetTransparentProxy();
    var configuration=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.Configuration",true),call=>call.MethodName=="get_Name"?(mode=="actual-config"?"Other":"Default"):throw new Exception(call.MethodName)).GetTransparentProxy();
    var configurationManager=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ConfigurationManager",true),call=>call.MethodName=="get_ActiveConfiguration"?configuration:throw new Exception(call.MethodName)).GetTransparentProxy();var readOnly=mode!="owned-edit";var unlocks=0;
    object Model(string path)=>new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true),call=>{
     switch(call.MethodName){case "GetPathName":return path;case "GetTitle":return "PART.SLDPRT";case "GetType":return 1;case "GetSaveFlag":return mode=="owned-edit";case "IsOpenedReadOnly":return readOnly;case "SetReadOnlyState":if(mode!="acquire-inplace"||path!=partPath)throw new Exception("越界解锁");readOnly=(bool)call.Args[0];unlocks++;return true;case "get_ConfigurationManager":return configurationManager;case "get_Extension":return extension;case "GetEquationMgr":case "GetDesignTable":return null;default:throw new Exception("模型窗口和几何不得修改："+call.MethodName);}
    }).GetTransparentProxy();
    var local=Model(partPath);var originalModel=Model(oldPath);var active=(object)null;var nativeMutations=0;var search=false;
    SetField(addin,"application",new Proxy(appType,call=>{
     switch(call.MethodName){case "GetDocuments":return new[]{local}.Concat(mode=="original-open"?new[]{originalModel}:new object[0]).ToArray();case "get_ActiveDoc":return active;
      case "ActivateDoc3":active=local;return local;case "GetCurrentWorkingDirectory":case "GetSearchFolders":return "";case "SetCurrentWorkingDirectory":case "SetSearchFolders":return true;case "GetUserPreferenceToggle":return search;case "SetUserPreferenceToggle":search=(bool)call.Args[1];return true;
      default:nativeMutations++;throw new Exception("禁止新开、关闭、保存或重写模型："+call.MethodName);}
    }).GetTransparentProxy());
    var tree=New("CadTreeNode");Set(tree,"FullPath",partPath);Set(tree,"FileName","PART.SLDPRT");Set(tree,"DocumentId",Get(source,"DocumentId"));Set(tree,"OpenedVersionId",Get(source,"VersionId"));Set(tree,"Kind",Enum.Parse(Type("CadDocumentKind"),"Part"));SetField(addin,"currentTree",tree);
    var checkoutDocument=New("DocumentDto");Set(checkoutDocument,"Id",Get(source,"DocumentId"));Set(checkoutDocument,"ProjectId",project);Set(checkoutDocument,"FileName","PART.SLDPRT");Set(checkoutDocument,"Kind",1);var checkouts=0;
    if(mode=="owned-edit"){Set(checkoutDocument,"CheckedOutBy","tester");Set(checkoutDocument,"CheckoutSessionId",session);Set(checkoutDocument,"CheckedOutAt",new DateTime(2026,10,27,6,0,0,DateTimeKind.Utc));}
    using(var lifetime=new CancellationTokenSource())
    using(var client=(IDisposable)New("PdmApiClient","http://controlled-reuse.invalid/"))
    using(var handler=new ReferenceRecoveryHandler(request=>{
     object data;
     if(request.RequestUri.AbsolutePath.EndsWith("/versions"))data=new[]{new{id=Get(requestedFile,"VersionId"),sha256=Get(requestedFile,"Sha256"),fileLength=Get(requestedFile,"FileLength"),propertySnapshot=new Dictionary<string,string>{{"SourceFileSha256",(string)Get(source,"Sha256")}},referenceSnapshot=new{documentId=Get(source,"DocumentId"),fileName="PART.SLDPRT",configuration="Default",kind=1,children=new object[0]}}};
     else if(request.RequestUri.AbsolutePath.EndsWith("/documents"))data=mode=="owned-edit"||mode=="acquire-inplace"?new[]{checkoutDocument}:new object[0];
     else if(request.RequestUri.AbsolutePath.EndsWith("/document-operation-rules"))data=new{disabledRuleIds=new string[0]};
     else if(request.RequestUri.AbsolutePath.EndsWith("/document-relations"))data=new object[0];
     else if(request.RequestUri.AbsolutePath.EndsWith("/open-manifest"))data=requested;
     else if(request.RequestUri.AbsolutePath.EndsWith("/checkout")&&mode=="acquire-inplace"){checkouts++;Set(checkoutDocument,"CheckedOutBy","tester");Set(checkoutDocument,"CheckoutSessionId",session);Set(checkoutDocument,"CheckedOutAt",new DateTime(2026,10,27,6,0,0,DateTimeKind.Utc));data=checkoutDocument;}
     else throw new Exception("复用不应下载或检入文件："+request.RequestUri.AbsolutePath);
     return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(json.Serialize(data))};
    }))
    using(var http=new HttpClient(handler){BaseAddress=new Uri("http://controlled-reuse.invalid/")})
    {
     http.DefaultRequestHeaders.Authorization=new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer","fixture");
     SetField(client,"httpClient",http);SetField(client,"controlledOpenHttpClient",http);SetField(addin,"apiClient",client);SetField(addin,"lifetime",lifetime);
     SetField(addin,"editJournal",New("EditDraftJournal",Path.Combine(folder,"journal"),"fixture"));SetField(addin,"editJournalScope",(string)Get(client,"EditJournalScope")+"|tester");
     if(mode=="owned-edit")
     {
      var editNode=New("CadTreeNode");Set(editNode,"DocumentId",Get(source,"DocumentId"));Set(editNode,"OpenedVersionId",Get(source,"VersionId"));Set(editNode,"FullPath",partPath);
      var document=New("DocumentDto");Set(document,"Id",Get(source,"DocumentId"));Set(document,"ProjectId",project);Set(document,"CheckedOutBy","tester");Set(document,"CheckoutSessionId",session);Set(document,"CheckedOutAt",new DateTime(2026,10,27,6,0,0,DateTimeKind.Utc));TreeCall(addin,"RememberLocalEdit",editNode,document);
     }
     if(mode=="owned-edit"||mode=="unknown-edit")File.AppendAllText(partPath,"|local geometry edit");
     var bytes=File.ReadAllBytes(partPath);Exception failure=null;bool reused=false;
     try{var task=(Task)TreeCall(addin,"TryOpenModelInLoadedContextAsync",requested,mode=="acquire-inplace");await task;reused=(bool)Get(task,"Result");}catch(InvalidOperationException error){failure=error;}
     var shouldPass=new[]{"original-present","original-missing","occurrence-config","owned-edit","source-copy","acquire-inplace"}.Contains(mode);
     if(shouldPass&&failure!=null)Console.WriteLine("DIAGNOSTIC: "+failure);
     Check(reused==shouldPass&&(failure==null)==shouldPass,"模型工作副本复用及真实冲突判定："+mode);
     Check(nativeMutations==0&&File.ReadAllBytes(partPath).SequenceEqual(bytes),"保留当前模型窗口和本地内容，不生成第二套副本："+mode);
     Check(!File.Exists(oldPath)||File.ReadAllText(oldPath)=="other original geometry","原目录文件不被读取为模型或修改："+mode);
     if(mode=="acquire-inplace")Check(checkouts==1&&unlocks==1&&!readOnly,"获取编辑权限复用同一窗口，仅解锁选中的模型，不重新打开或下载");
    }
   }
  }
  finally{bindings.SetValue(null,oldBindings);resolver.SetValue(null,oldResolver);}
 }
}
