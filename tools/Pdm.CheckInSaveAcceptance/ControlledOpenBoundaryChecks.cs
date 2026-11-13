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
    var rejected=false;try{TreeCall(boundary,"Verify",null,null);}catch(System.Reflection.TargetInvocationException error)when(error.InnerException is InvalidOperationException){rejected=true;}Check(rejected,"取消外部加载后不能误报打开成功");
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
   foreach(var mode in new[]{"original-present","original-missing","original-open","different-project","stale-version","stale-config","actual-config","occurrence-config","unknown-edit","owned-edit","source-copy","acquire-inplace","acquire-occurrence","latest-occurrence","acquire-dependency-version","acquire-dependency-bytes","acquire-dependency-denied",
    "release-promotion","release-acquire","release-denied","release-other-project","release-binding","release-owned","release-missing-draft","release-session-mismatch","release-draft-base",
    "release-rebind-during-versions","release-rebind-after-activation","release-config-during-versions","release-draft-after-activation",
    "context-environment","context-older-environment","context-ordinary-driver","context-unverified","context-wrong-name","context-duplicate-driver","context-nested-driver","context-virtual","context-tampered","context-root-race"})
   {
    var acquiring = mode == "acquire-inplace" || mode.StartsWith("acquire-dependency-") || mode == "acquire-occurrence";
    var folder=Path.Combine(output,"View",mode);Directory.CreateDirectory(folder);var partPath=Path.Combine(folder,"PART.SLDPRT");File.WriteAllText(partPath,"saved model bytes");
    var oldPath=Path.Combine(output,"source-"+mode,"PART.SLDPRT");Directory.CreateDirectory(Path.GetDirectoryName(oldPath));if(mode!="original-missing")File.WriteAllText(oldPath,"other original geometry");
    var source=New("ControlledOpenFileDto");var project=Guid.NewGuid();Set(source,"DocumentId",Guid.NewGuid());Set(source,"VersionId",Guid.NewGuid());Set(source,"FileName","PART.SLDPRT");Set(source,"RelativePath","PART.SLDPRT");Set(source,"Configuration","Default");Set(source,"Sha256",WorkingReuseHash(partPath));Set(source,"FileLength",new FileInfo(partPath).Length);Set(source,"Revision","W2");
    identity.GetMethod("TryWriteControlledVersion",Flags).Invoke(null,new[]{(object)partPath,Get(source,"DocumentId"),project,Get(source,"VersionId"),"W2",Get(source,"Sha256"),Get(source,"FileLength")});
    var owner=New("ControlledOpenManifestDto");Set(owner,"ProjectId",mode=="different-project"||mode=="release-other-project"?Guid.NewGuid():project);var ownerFiles=(IList)Activator.CreateInstance(Property(owner,"Files").PropertyType);ownerFiles.Add(source);Set(owner,"Files",ownerFiles);
    Set(owner,"RootDocumentId",Get(source,"DocumentId"));Set(owner,"RootVersionId",Get(source,"VersionId"));Set(owner,"RootRelativePath","PART.SLDPRT");
    var requested=New("ControlledOpenManifestDto");Set(requested,"ProjectId",project);Set(requested,"RootDocumentId",Get(source,"DocumentId"));Set(requested,"RootVersionId",Get(source,"VersionId"));Set(requested,"RootRelativePath","PART.SLDPRT");
    Set(requested,"ForEdit",acquiring); if(acquiring)Set(owner,"ForEdit",true);
    var json=new JavaScriptSerializer();var requestedFile=json.Deserialize(json.Serialize(source),source.GetType());Set(requestedFile,"IsRoot",true);
    if(mode=="stale-version")Set(requestedFile,"VersionId",Guid.NewGuid());if(mode=="stale-config")Set(requestedFile,"Configuration","Other");if(mode=="source-copy"){Set(requestedFile,"Sha256",new string('A',64));Set(requestedFile,"FileLength",1000L);}
    if(mode.StartsWith("release-")){Set(requestedFile,"VersionId",Guid.NewGuid());Set(requestedFile,"Revision","A");Set(requested,"RootVersionId",Get(requestedFile,"VersionId"));}
    if(mode=="release-binding")identity.GetMethod("TryWriteControlledVersion",Flags).Invoke(null,new[]{(object)partPath,Get(source,"DocumentId"),project,Guid.NewGuid(),"W9",Get(source,"Sha256"),Get(source,"FileLength")});
    if(mode=="occurrence-config")Set(source,"Configuration","AssemblyOccurrence");
    var requestedFiles=(IList)Activator.CreateInstance(Property(requested,"Files").PropertyType);requestedFiles.Add(requestedFile);Set(requested,"Files",requestedFiles);
    object occurrenceDependency = null;
    if (acquiring && mode != "acquire-inplace" || mode == "latest-occurrence")
    {
     var depPath=Path.Combine(folder,"BC1-波纹管接头A.SLDPRT");File.WriteAllText(depPath,"one file, all occurrence configurations");File.SetAttributes(depPath,FileAttributes.ReadOnly);
     occurrenceDependency=New("ControlledOpenFileDto");Set(occurrenceDependency,"DocumentId",Guid.NewGuid());Set(occurrenceDependency,"VersionId",Guid.NewGuid());Set(occurrenceDependency,"FileName",Path.GetFileName(depPath));Set(occurrenceDependency,"RelativePath",Path.GetFileName(depPath));Set(occurrenceDependency,"Configuration","PA-M32(AD34.5)");Set(occurrenceDependency,"Sha256",WorkingReuseHash(depPath));Set(occurrenceDependency,"FileLength",new FileInfo(depPath).Length);Set(occurrenceDependency,"Revision","W1");ownerFiles.Add(occurrenceDependency);
     identity.GetMethod("TryWriteControlledVersion",Flags).Invoke(null,new[]{(object)depPath,Get(occurrenceDependency,"DocumentId"),project,Get(occurrenceDependency,"VersionId"),"W1",Get(occurrenceDependency,"Sha256"),Get(occurrenceDependency,"FileLength")});
     var dep=json.Deserialize(json.Serialize(occurrenceDependency),occurrenceDependency.GetType());Set(dep,"Configuration","PA-M20(AD21.2)");requestedFiles.Add(dep);
     if(mode=="acquire-dependency-version")Set(dep,"VersionId",Guid.NewGuid());
     if(mode=="acquire-dependency-bytes"){File.SetAttributes(depPath,FileAttributes.Normal);File.AppendAllText(depPath," unverified changes");}
    }
    object parent=null;object parentReference=null;string parentPath=null;
    if(mode.StartsWith("context-")||mode=="release-acquire")
    {
     parentPath=Path.Combine(folder,"CONTEXT.SLDASM");File.WriteAllText(parentPath,"saved parent assembly");
     parent=New("ControlledOpenFileDto");Set(parent,"DocumentId",Guid.NewGuid());Set(parent,"VersionId",Guid.NewGuid());Set(parent,"FileName","CONTEXT.SLDASM");Set(parent,"RelativePath","CONTEXT.SLDASM");Set(parent,"Configuration","Default");Set(parent,"Sha256",WorkingReuseHash(parentPath));Set(parent,"FileLength",new FileInfo(parentPath).Length);Set(parent,"Revision","W2");ownerFiles.Add(parent);
     identity.GetMethod("TryWriteControlledVersion",Flags).Invoke(null,new[]{(object)parentPath,Get(parent,"DocumentId"),project,Get(parent,"VersionId"),"W2",Get(parent,"Sha256"),Get(parent,"FileLength")});
     var requestedParent=json.Deserialize(json.Serialize(parent),parent.GetType());Set(requestedParent,"Configuration","");
     if(mode=="context-older-environment"){Set(requestedParent,"VersionId",Guid.NewGuid());Set(requestedParent,"Sha256",new string('B',64));Set(requestedParent,"Revision","W1");}
     if(mode=="release-acquire")
     {
      Set(owner,"RootDocumentId",Get(parent,"DocumentId"));Set(owner,"RootVersionId",Get(parent,"VersionId"));Set(owner,"RootRelativePath","CONTEXT.SLDASM");Set(owner,"ForEdit",true);Set(parent,"IsRoot",true);
     }
     else requestedFiles.Add(requestedParent);
     parentReference=new{documentId=Get(parent,"DocumentId"),fileName=mode=="context-wrong-name"?"OTHER.SLDASM":"CONTEXT.SLDASM",kind=0,role=1,isContextReference=mode!="context-ordinary-driver",referenceTypeUnverified=mode=="context-unverified",status=mode=="context-virtual"?4:0,children=new object[0]};
     if(mode=="context-tampered")File.AppendAllText(parentPath," unknown edits");
    }
    var addin=New("PdmAddin");var session=(Guid)Field(addin,"checkoutSessionId");SetField(addin,"authenticatedUsername","tester");((IDictionary)Field(addin,"controlledOpenManifests")).Add(folder,owner);
    var appType=Type("PdmAddin").GetField("application",Flags).FieldType;var interop=appType.Assembly;var extension=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDocExtension",true),call=>call.MethodName=="ListExternalFileReferences2"?null:throw new Exception(call.MethodName)).GetTransparentProxy();
    var configChanged=false;
    var configuration=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.Configuration",true),call=>call.MethodName=="get_Name"?(mode=="actual-config"||configChanged?"Other":"Default"):throw new Exception(call.MethodName)).GetTransparentProxy();
    var editablePromotion=new[]{"release-owned","release-missing-draft","release-session-mismatch","release-draft-base","release-draft-after-activation"}.Contains(mode);
    var configurationManager=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ConfigurationManager",true),call=>call.MethodName=="get_ActiveConfiguration"?configuration:throw new Exception(call.MethodName)).GetTransparentProxy();var readOnly=mode!="owned-edit"&&!editablePromotion;var unlocks=0;
    object local=null;
    var component=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.Component2",true),call=>{
     switch(call.MethodName){case "GetPathName":return partPath;case "get_ReferencedConfiguration":return "Default";case "get_IsVirtual":return false;case "GetModelDoc2":return local;case "GetSuppression":return 2;case "GetChildren":return new object[0];default:throw new Exception("不得改变父装配引用："+call.MethodName);}
    }).GetTransparentProxy();
    object Model(string path)
    {
     Func<System.Runtime.Remoting.Messaging.IMethodCallMessage,object> callback=call=>{
      switch(call.MethodName){case "GetPathName":return path;case "GetTitle":return Path.GetFileName(path);case "GetType":return path==parentPath?2:1;case "GetSaveFlag":return mode=="owned-edit";case "IsOpenedReadOnly":return path==parentPath||readOnly;case "SetReadOnlyState":if(!acquiring&&mode!="release-acquire"||path!=partPath)throw new Exception("越界解锁");readOnly=(bool)call.Args[0];unlocks++;return true;case "get_ConfigurationManager":return configurationManager;case "get_Extension":return extension;case "GetEquationMgr":case "GetDesignTable":return null;case "GetComponents":return new[]{component};default:throw new Exception("模型窗口和几何不得修改："+call.MethodName);}
     };
     return mode=="release-acquire"&&path==parentPath?new AssemblyProxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true),callback).GetTransparentProxy():new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true),callback).GetTransparentProxy();
    }
    local=Model(partPath);var originalModel=Model(oldPath);var active=(object)null;var nativeMutations=0;var search=false;
    SetField(addin,"application",new Proxy(appType,call=>{
      switch(call.MethodName){case "GetDocuments":return new[]{local}.Concat(mode=="original-open"?new[]{originalModel}:new object[0]).Concat(parent==null?new object[0]:new[]{Model(parentPath)}).ToArray();case "get_ActiveDoc":return active;
      case "ActivateDoc3":active=local;
       if(mode=="release-rebind-after-activation")identity.GetMethod("TryWriteControlledVersion",Flags).Invoke(null,new[]{(object)partPath,Get(source,"DocumentId"),project,Guid.NewGuid(),"W9",Get(source,"Sha256"),Get(source,"FileLength")});
       if(mode=="release-draft-after-activation")Set(((IDictionary)Field(addin,"localEdits"))[Get(source,"DocumentId")],"SessionId",Guid.NewGuid());
       return local;case "GetCurrentWorkingDirectory":case "GetSearchFolders":return "";case "SetCurrentWorkingDirectory":case "SetSearchFolders":return true;case "GetUserPreferenceToggle":return search;case "SetUserPreferenceToggle":search=(bool)call.Args[1];return true;
      default:nativeMutations++;throw new Exception("禁止新开、关闭、保存或重写模型："+call.MethodName);}
    }).GetTransparentProxy());
    var tree=New("CadTreeNode");Set(tree,"FullPath",partPath);Set(tree,"FileName","PART.SLDPRT");Set(tree,"DocumentId",Get(source,"DocumentId"));Set(tree,"OpenedVersionId",Get(source,"VersionId"));Set(tree,"Kind",Enum.Parse(Type("CadDocumentKind"),"Part"));SetField(addin,"currentTree",tree);
    var checkoutDocument=New("DocumentDto");Set(checkoutDocument,"Id",Get(source,"DocumentId"));Set(checkoutDocument,"ProjectId",project);Set(checkoutDocument,"FileName","PART.SLDPRT");Set(checkoutDocument,"Kind",1);var checkouts=0;
    if(mode=="owned-edit"||editablePromotion){Set(checkoutDocument,"CheckedOutBy","tester");Set(checkoutDocument,"CheckoutSessionId",session);Set(checkoutDocument,"CheckedOutAt",new DateTime(2026,10,27,6,0,0,DateTimeKind.Utc));}
    using(var lifetime=new CancellationTokenSource())
    using(var client=(IDisposable)New("PdmApiClient","http://controlled-reuse.invalid/"))
    using(var handler=new ReferenceRecoveryHandler(request=>{
     object data;
     if(request.RequestUri.AbsolutePath.EndsWith("/versions"))
     {
      if(mode=="release-acquire"&&request.RequestUri.AbsolutePath.Contains(Get(parent,"DocumentId").ToString()))
      {
       data=new[]{new{id=Get(parent,"VersionId"),documentId=Get(parent,"DocumentId"),sha256=Get(parent,"Sha256"),fileLength=Get(parent,"FileLength"),referenceSnapshot=new{documentId=Get(parent,"DocumentId"),fileName="CONTEXT.SLDASM",configuration="Default",kind=0,children=new[]{new{documentId=Get(source,"DocumentId"),fileName="PART.SLDPRT",configuration="Default",kind=1,quantity=1,children=new object[0]}}}}};
       return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(json.Serialize(data))};
      }
      if(mode.StartsWith("release-"))
      {
       var snapshot=new{documentId=Get(source,"DocumentId"),fileName="PART.SLDPRT",configuration="Default",kind=1,children=new object[0]};
       var properties=new Dictionary<string,string>{{"SourceFileSha256",(string)Get(source,"Sha256")}};
       data=new object[]{new{id=Get(requestedFile,"VersionId"),documentId=Get(source,"DocumentId"),sourceVersionId=Get(source,"VersionId"),status=1,revision=new{display="A"},createdAt=new DateTime(2026,11,12),sha256=Get(source,"Sha256"),fileLength=Get(source,"FileLength"),propertySnapshot=properties,referenceSnapshot=snapshot},
        new{id=Get(source,"VersionId"),documentId=Get(source,"DocumentId"),status=0,revision=new{display="W2"},createdAt=new DateTime(2026,11,11),sha256=Get(source,"Sha256"),fileLength=Get(source,"FileLength"),propertySnapshot=properties,referenceSnapshot=snapshot}};
       if(mode=="release-rebind-during-versions")identity.GetMethod("TryWriteControlledVersion",Flags).Invoke(null,new[]{(object)partPath,Get(source,"DocumentId"),project,Guid.NewGuid(),"W9",Get(source,"Sha256"),Get(source,"FileLength")});
       if(mode=="release-config-during-versions")configChanged=true;
       return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(json.Serialize(data))};
      }
      var refs=parentReference==null?new List<object>():new List<object>{parentReference};
      var driver=parent==null?null:new{documentId=Get(parent,"DocumentId"),fileName="CONTEXT.SLDASM",kind=0,role=1,isContextReference=false,referenceTypeUnverified=false,children=new object[0]};
      if(mode=="context-duplicate-driver")refs.Add(driver);
      if(mode=="context-nested-driver")refs.Add(new{documentId=Guid.NewGuid(),fileName="DRIVER.SLDPRT",kind=1,children=new[]{driver}});
      data=new[]{new{id=mode=="context-root-race"?Guid.NewGuid():Get(requestedFile,"VersionId"),sha256=Get(requestedFile,"Sha256"),fileLength=Get(requestedFile,"FileLength"),propertySnapshot=new Dictionary<string,string>{{"SourceFileSha256",(string)Get(source,"Sha256")}},referenceSnapshot=new{documentId=Get(source,"DocumentId"),fileName="PART.SLDPRT",configuration="Default",kind=1,children=refs.ToArray()}}};
     }
     else if(request.RequestUri.AbsolutePath.EndsWith("/documents"))data=mode=="owned-edit"||acquiring||mode=="release-acquire"||editablePromotion?new[]{checkoutDocument}:new object[0];
     else if(request.RequestUri.AbsolutePath.EndsWith("/document-operation-rules"))data=new{disabledRuleIds=new string[0]};
     else if(request.RequestUri.AbsolutePath.EndsWith("/document-relations"))data=new object[0];
     else if(request.RequestUri.AbsolutePath.EndsWith("/open-manifest")){if(mode=="release-denied")return new HttpResponseMessage(HttpStatusCode.Forbidden){Content=new StringContent("{\"message\":\"当前账号没有该项目的获取权限。\"}")};data=mode=="release-acquire"||acquiring?owner:requested;}
     else if(request.RequestUri.AbsolutePath.EndsWith("/checkout")&&(acquiring||mode=="release-acquire")){checkouts++;if(mode=="acquire-dependency-denied")return new HttpResponseMessage(HttpStatusCode.Forbidden){Content=new StringContent("{\"message\":\"当前账号没有该项目的获取权限。\"}")};Set(checkoutDocument,"CheckedOutBy","tester");Set(checkoutDocument,"CheckoutSessionId",session);Set(checkoutDocument,"CheckedOutAt",new DateTime(2026,10,27,6,0,0,DateTimeKind.Utc));data=checkoutDocument;}
     else throw new Exception("复用不应下载或检入文件："+request.RequestUri.AbsolutePath);
     return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(json.Serialize(data))};
    }))
    using(var http=new HttpClient(handler){BaseAddress=new Uri("http://controlled-reuse.invalid/")})
    {
     http.DefaultRequestHeaders.Authorization=new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer","fixture");
     SetField(client,"httpClient",http);SetField(client,"controlledOpenHttpClient",http);SetField(addin,"apiClient",client);SetField(addin,"lifetime",lifetime);
     SetField(addin,"editJournal",New("EditDraftJournal",Path.Combine(folder,"journal"),"fixture"));SetField(addin,"editJournalScope",(string)Get(client,"EditJournalScope")+"|tester");
     if(mode=="owned-edit"||editablePromotion&&mode!="release-missing-draft")
     {
      var editNode=New("CadTreeNode");Set(editNode,"DocumentId",Get(source,"DocumentId"));Set(editNode,"OpenedVersionId",Get(source,"VersionId"));Set(editNode,"FullPath",partPath);
      var document=New("DocumentDto");Set(document,"Id",Get(source,"DocumentId"));Set(document,"ProjectId",project);Set(document,"CheckedOutBy","tester");Set(document,"CheckoutSessionId",session);Set(document,"CheckedOutAt",new DateTime(2026,10,27,6,0,0,DateTimeKind.Utc));TreeCall(addin,"RememberLocalEdit",editNode,document);
     }
     if(mode=="release-session-mismatch")Set(checkoutDocument,"CheckoutSessionId",Guid.NewGuid());
     if(mode=="release-draft-base")Set(((IDictionary)Field(addin,"localEdits"))[Get(source,"DocumentId")],"BaseVersionId",Guid.NewGuid());
     if(mode=="owned-edit"||mode=="unknown-edit")File.AppendAllText(partPath,"|local geometry edit");
     var bytes=File.ReadAllBytes(partPath);var parentBytes=parent==null?null:File.ReadAllBytes(parentPath);var parentAttributes=parent==null?FileAttributes.Normal:File.GetAttributes(parentPath);Exception failure=null;bool reused=false;
     try{var task=(Task)TreeCall(addin,"TryOpenModelInLoadedContextAsync",requested,acquiring||mode=="release-acquire"||mode=="release-denied");await task;reused=(bool)Get(task,"Result");}catch(InvalidOperationException error){failure=error;}
     var shouldPass=new[]{"original-present","original-missing","occurrence-config","owned-edit","source-copy","acquire-inplace","acquire-occurrence","latest-occurrence","context-environment","context-older-environment","release-promotion","release-acquire","release-owned"}.Contains(mode);
     if(shouldPass&&failure!=null)Console.WriteLine("DIAGNOSTIC: "+failure);
     Check(reused==shouldPass&&(failure==null)==shouldPass,"模型工作副本复用及真实冲突判定："+mode);
     Check(nativeMutations==0&&File.ReadAllBytes(partPath).SequenceEqual(bytes),"保留当前模型窗口和本地内容，不生成第二套副本："+mode);
     if(parent!=null)Check(File.ReadAllBytes(parentPath).SequenceEqual(parentBytes)&&File.GetAttributes(parentPath)==parentAttributes&&(string)Get(parent,"Configuration")=="Default", "装配上下文保持原字节、属性和原配置："+mode);
     Check(!File.Exists(oldPath)||File.ReadAllText(oldPath)=="other original geometry","原目录文件不被读取为模型或修改："+mode);
     if(mode=="acquire-inplace"||mode=="acquire-occurrence")Check(checkouts==1&&unlocks==1&&!readOnly,"获取编辑权限复用同一窗口，仅解锁选中的模型，不重新打开或下载");
     if(mode.StartsWith("acquire-dependency-"))Check(failure!=null&&unlocks==0&&checkouts==(mode=="acquire-dependency-denied"?1:0),"关联配置等价不放行真实版本、字节冲突或未授权："+mode);
     if(occurrenceDependency!=null)Check((string)Get(occurrenceDependency,"Configuration")=="PA-M32(AD34.5)","获取不改写装配实例配置");
     if(mode=="release-acquire")Check(checkouts==1&&unlocks==1&&!readOnly&&Equals(Get(((IList)Get(owner,"Files"))[0],"VersionId"),Get(source,"VersionId"))&&Equals(Get(((IDictionary)Field(addin,"localEdits"))[Get(source,"DocumentId")],"BaseVersionId"),Get(source,"VersionId")),"LatestEdit发布等价在父装配原基线中获取成功，回调返回新会话供激活后复核，草稿仍保留原版本");
     if(mode=="release-promotion"||mode=="release-owned")Check(Equals(Get(source,"VersionId"),Get(owner,"RootVersionId")),"审批发布等价打开保留原工作基线，不将父清单改写为发布版本");
     if(editablePromotion)Check(mode=="release-owned"||failure!=null&&failure.Message.Contains("获取会话"),"发布等价模型即使尚未修改，也必须核对完整编辑基线及获取会话："+mode);
     if(mode.StartsWith("release-rebind-"))Check(failure!=null&&failure.Message.Contains("绑定发生变化"),"发布核对及激活后同步重核精确绑定，拒绝同字节重绑定："+mode);
     if(mode=="release-config-during-versions")Check(failure!=null&&failure.Message.Contains("配置发生变化"),"版本请求返回后重新核对实际配置");
     if(mode=="release-denied")Check(failure!=null&&failure.Message.Contains("获取权限")&&checkouts==0&&unlocks==0,"审批发布等价不绕过服务器获取授权");
    }
   }
   await UnloadedControlledModelOpenScenario(output);
  }
  finally{bindings.SetValue(null,oldBindings);resolver.SetValue(null,oldResolver);}
 }
}
