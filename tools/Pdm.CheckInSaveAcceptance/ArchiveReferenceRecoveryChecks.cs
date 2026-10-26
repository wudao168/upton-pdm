using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

internal static partial class Program
{
 static async Task ArchiveReferenceRecoveryScenario(string output)
 {
  Directory.CreateDirectory(output);
  foreach(var mode in new[]{"original","library","identical","ambiguous","missing-batch","missing-source-node","missing-component","project","other-project","standard","unreadable-project","unsafe-package","changed-copy","nested","later-directory","rescan","removed","embedded","stored-import","cancel"})
  {
   var folder=Path.Combine(Path.GetFullPath(output),mode);Directory.CreateDirectory(folder);
   Type("PdmDocumentIdentityStore").GetMethod("ConfigureBrowserReadScope",Flags).Invoke(null,new object[]{Path.Combine(folder,"workspace")});
   var source=Path.Combine(folder,"drawings");Directory.CreateDirectory(source);
   var library=Path.Combine(folder,"library");Directory.CreateDirectory(library);
   var nested=Path.Combine(library,"nested");Directory.CreateDirectory(nested);
   var owner=Path.Combine(source,mode=="stored-import"||mode=="missing-component"?"LHH.SLDASM":"BP1-直线滑轨HG30.SLDPRT");File.WriteAllText(owner,"original smart component; never changed");
   var old=mode=="stored-import"?@"C:\Users\li.pw\AppData\Local\Temp\swx14872\IC~~\LHH.STEP.SLDASM":Path.Combine(folder,"old-profile","BP1-滑块HG.SLDPRT");
   var needed=Path.Combine(nested,Path.GetFileName(old));var bytes=Encoding.UTF8.GetBytes("original associated component data");
   if(new[]{"library","identical","ambiguous","changed-copy","nested","later-directory","rescan","removed"}.Contains(mode))File.WriteAllBytes(needed,bytes);
   if(mode=="nested")File.WriteAllBytes(Path.Combine(nested,"SECOND.SLDPRT"),bytes);
   if(mode=="later-directory"){var later=Path.Combine(folder,"later","subfolder");Directory.CreateDirectory(later);File.WriteAllBytes(Path.Combine(later,"SECOND.SLDPRT"),bytes);}
   var virtualFile=Path.Combine(source,"INNER.SLDPRT");if(mode=="embedded")File.WriteAllBytes(virtualFile,bytes);
   if(mode=="original"){Directory.CreateDirectory(Path.GetDirectoryName(old));File.WriteAllBytes(old,bytes);}
   if(mode=="identical"||mode=="ambiguous"){
    var other=Path.Combine(library,"another");Directory.CreateDirectory(other);File.WriteAllBytes(Path.Combine(other,Path.GetFileName(old)),mode=="identical"?bytes:Encoding.UTF8.GetBytes("different source geometry"));
   }
   var project=Guid.NewGuid();var targetProject=mode=="other-project"?Guid.NewGuid():project;
   var docId=Guid.NewGuid();var version=Guid.NewGuid();var revision=Guid.NewGuid();var category=Guid.NewGuid();
   string Hash(byte[] value){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(value)).Replace("-","");}
   var json=new JavaScriptSerializer();var requests=new List<string>();var fileGets=0;
   using(var handler=new ReferenceRecoveryHandler(request=>{
    var path=request.RequestUri.AbsolutePath;requests.Add(request.Method+" "+path);
    HttpResponseMessage Reply(object value)=>new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(json.Serialize(value),Encoding.UTF8,"application/json")};
    if(path=="/api/projects")return Reply(new[]{new{id=targetProject,code="P-TEST",canReadContent=mode!="unreadable-project",documentCount=1}});
    if(path=="/api/projects/"+targetProject+"/documents")return Reply(mode=="project"||mode=="other-project"||mode=="unsafe-package"?new[]{new{id=docId,projectId=targetProject,fileName=Path.GetFileName(old),kind=1,storedVersionCount=1}}:new object[0]);
    if(path=="/api/standard-structures"){
     if(mode!="standard")return new HttpResponseMessage(HttpStatusCode.Forbidden);
     var fileList=new[]{new{key="native",fileName=Path.GetFileName(old),length=bytes.Length,sha256=Hash(bytes)},new{key="preview",fileName="preview.step",length=7,sha256=Hash(Encoding.UTF8.GetBytes("preview"))}};
     var revisionData=new{id=revision,version=1,root=new{fileKey="native",previewFileKey="preview",children=new object[0]},files=fileList};
     var structureData=new{id=Guid.NewGuid(),categoryId=category,isActive=true,revisions=new[]{revisionData}};
     return Reply(new{catalog=new{categories=new[]{new{id=category,isActive=true}},structures=new[]{structureData}}});
    }
    if(path=="/api/documents/"+docId+"/versions")return Reply(new[]{new{id=version,documentId=docId,sha256=Hash(bytes),revision=new{display="W1"},propertySnapshot=new{}}});
    if(path=="/api/documents/"+docId+"/open-manifest")return Reply(new{id=Guid.NewGuid(),projectId=targetProject,projectCode="P-TEST",rootDocumentId=docId,rootVersionId=version,rootRelativePath=Path.GetFileName(old),files=new[]{new{documentId=docId,versionId=version,fileName=mode=="unsafe-package"?"..\\outside.SLDPRT":Path.GetFileName(old),relativePath=Path.GetFileName(old),fileLength=bytes.Length,sha256=Hash(bytes),revision="W1",isRoot=true}}});
    if(path=="/api/documents/"+docId+"/versions/"+version+"/file"||path=="/api/standard-structures/revisions/"+revision+"/files/native"){
     fileGets++;return new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)};
    }
    throw new Exception("引用查找不得登记、检出或上传；未模拟请求："+request.Method+" "+path);
   }))
   {
    var client=New("PdmApiClient","http://fixture.invalid/");
    ((HttpClient)Field(client,"httpClient")).Dispose();((HttpClient)Field(client,"controlledOpenHttpClient")).Dispose();
    SetField(client,"httpClient",new HttpClient(handler,false){BaseAddress=new Uri("http://fixture.invalid/")});
    SetField(client,"controlledOpenHttpClient",new HttpClient(handler,false){BaseAddress=new Uri("http://fixture.invalid/")});
    var plugin=New("PdmAddin");SetField(plugin,"apiClient",client);
    SetField(plugin,"controlledWorkspace",New("ControlledWorkspaceManager",client,Path.Combine(folder,"workspace")));
    var appType=Type("PdmAddin").GetField("application",Flags).FieldType;var directory=source;var referenceRemoved=false;
    var app=new Proxy(appType,call=>{
     switch(call.MethodName){
      case "GetDocuments":return new object[0];case "GetOpenDocumentByName":return null;
      case "GetCurrentWorkingDirectory":return directory;case "SetCurrentWorkingDirectory":directory=(string)call.Args[0];return true;
      case "GetSearchFolders":case "GetUserPreferenceStringListValue":return library;
      case "GetDocumentDependencies2":
       if(referenceRemoved)return new string[0];
       if((string)call.Args[0]!=owner)return (mode=="nested"||mode=="later-directory")&&Path.GetFileName((string)call.Args[0])==Path.GetFileName(old)
        ?new[]{"nested source",Path.Combine(folder,mode=="later-directory"?"later":"absent","SECOND.SLDPRT")}:new string[0];
       if((bool)call.Args[2])directory=source;
       if(mode=="embedded")return new[]{"embedded",virtualFile};
       return mode=="missing-batch"?new[]{"slider",old,"another",Path.Combine(folder,"absent","SECOND.SLDPRT")}:new[]{"slider",old};
      default:throw new Exception("引用补齐不得打开、保存、断链或关闭真实CAD："+call.MethodName);
     }
    }).GetTransparentProxy();SetField(plugin,"application",app);
    var node=New("CadTreeNode");Set(node,"FullPath",owner);Set(node,"FileName",Path.GetFileName(owner));Set(node,"Kind",Enum.Parse(Type("CadDocumentKind"),"Part"));SetField(plugin,"currentTree",node);
    if(mode=="embedded"){
     var inner=New("CadTreeNode");Set(inner,"FullPath",virtualFile);Set(inner,"FileName",Path.GetFileName(virtualFile));Set(inner,"Status",Enum.Parse(Type("CadReferenceStatus"),"Virtual"));((IList)Get(node,"Children")).Add(inner);
    }
    if(mode=="stored-import"){
     Set(node,"Kind",Enum.Parse(Type("CadDocumentKind"),"Assembly"));
     var inner=New("CadTreeNode");Set(inner,"FullPath",Path.Combine(Path.GetTempPath(),"swx99999","IC~~","LHH.STEP.SLDASM"));
     Set(inner,"ImportedStorageOwnerPath",owner);((IList)Get(node,"Children")).Add(inner);
    }
    if(mode=="missing-component"){
     Set(node,"Kind",Enum.Parse(Type("CadDocumentKind"),"Assembly"));var child=New("CadTreeNode");
     Set(child,"FullPath",old);Set(child,"FileName",Path.GetFileName(old));Set(child,"Kind",Enum.Parse(Type("CadDocumentKind"),"Part"));
     Set(child,"Status",Enum.Parse(Type("CadReferenceStatus"),"Missing"));((IList)Get(node,"Children")).Add(child);
    }
    if(mode=="missing-source-node"){
     var child=New("CadTreeNode");Set(child,"FullPath",old);Set(child,"FileName",Path.GetFileName(old));
     Set(child,"Role",Enum.Parse(Type("CadReferenceRole"),"ExternalFileDependency"));((IList)Get(node,"Children")).Add(child);
    }
    var item=New("BatchOperationItem",node,0);var selection=Array.CreateInstance(Type("BatchOperationItem"),1);selection.SetValue(item,0);
    var method=Type("PdmAddin").GetMethod("RecoverPdmReferencesAsync",Flags);
    using(var cancel=new CancellationTokenSource()){
     if(mode=="cancel")cancel.Cancel();
     Exception failure=null;Task task=null;object result=null;
     try{task=(Task)method.Invoke(plugin,new object[]{selection,project,cancel.Token});await task;result=task.GetType().GetProperty("Result").GetValue(task);}
     catch(Exception error){failure=error is TargetInvocationException invocation?invocation.InnerException:error;}
     void Check(bool condition,string text){if(!condition)throw new Exception(mode+"："+text+(failure==null?"":"；"+failure));Console.WriteLine("PASS: "+mode+"："+text);}
     if(mode=="ambiguous"||mode=="missing-component"){
      Check(failure is InvalidOperationException,"未找到或多候选时在上传前停止");
      if(mode=="ambiguous")Check(failure.Message.Contains("不同内容"),"不从不同内容的同名文件中任选");
      if(mode=="missing-component")Check(((IList)Get(node,"ReferenceWarnings")).Count==0,"实际装配子件缺失不能伪装为旧来源提示");
      Check(fileGets==0,"未确定唯一候选前不下载或登记图档");
     }else if(mode=="unreadable-project"){
      Check(failure==null&&((IList)Get(node,"ReferenceWarnings")).Count==1,"其他无权读取项目不能取得来源时保留提示，不阻止原文存档");
      Check(!requests.Any(path=>path.Contains("/documents"))&&fileGets==0,"不越权搜索、下载或暴露其他项目图档");
     }else if(mode=="missing-source-node"){
      Check(failure==null&&((IList)Get(node,"ReferenceWarnings")).Count==1,"未入库的缺失来源节点转为版本提示");
      Check(((IList)Get(node,"Children")).Count==0&&((IEnumerable)result).Cast<object>().Count()==1,"不存在的来源节点不再作为独立图档阻断后续存档");
     }else if(mode=="missing-batch"){
      Check(failure==null&&((IEnumerable)result).Cast<object>().Count()==1,"旧设计来源缺失仍允许原生图档存档，不制造源文件");
      Check(((IList)Get(node,"ReferenceWarnings")).Count==2,"全部缺失路径形成版本引用提示");
      Type("PdmAddin").GetMethod("ValidatePdmNativeDependencyPaths",Flags).Invoke(plugin,new object[]{selection,CancellationToken.None});
      Check(((IList)Get(node,"ReferenceWarnings")).Count==2,"提前检查接受引用提示，重复检查不重复记录");
      var completed=(Task)Type("PdmAddin").GetMethod("CompletePdmCheckInDependenciesAsync",Flags).Invoke(plugin,new object[]{selection,project,CancellationToken.None});await completed;
      Check(((IList)Get(node,"ReferenceWarnings")).Count==2&&((IList)Get(node,"Children")).Count==0,"最终提交检查仍放行，提示不变成BOM或新图档");
      Set(node,"DocumentId",docId);Set(node,"Revision","W1");
      var snapshot=Type("PdmApiClient").GetMethod("CaptureArchiveSnapshot",Flags).Invoke(null,new object[]{node,new[]{owner}});
      var data=(IDictionary)TreeCall(snapshot,"CreateRoot");
      Check(((IEnumerable)data["referenceWarnings"]).Cast<object>().Count()==2,"固定上传快照携带全部引用提示，交给服务器永久保存");
      Check(fileGets==0&&!requests.Any(value=>value.Contains("register")),"缺失提示不伪造下载、登记或升版");
     }else if(mode=="unsafe-package"){
      Check(failure is InvalidDataException,"拒绝引用包中的越界文件名");
      Check(fileGets==0&&!File.Exists(Path.Combine(folder,"outside.SLDPRT")),"越界包不下载、不写入范围外文件");
     }else if(mode=="cancel")Check(failure is OperationCanceledException,"取消后不继续查找或收集");
     else if(mode=="embedded"||mode=="stored-import"){
      Check(failure==null&&((IEnumerable)result).Cast<object>().Count()==1,"嵌入组件不独立登记、不变成新增图档");
      if(mode=="embedded")Check(File.ReadAllBytes(virtualFile).SequenceEqual(bytes),"嵌入内容保持原样随父文件保存");
      else Check(!requests.Any(value=>value=="GET /api/projects"),"已确认嵌入的旧用户IC缓存不再搜索外部源文件");
     }else{
      Check(failure==null,"引用自动补齐完成");
      var link=((IList)Get(node,"Children")).Cast<object>().Single();var path=(string)Get(link,"FullPath");
      Check(Path.GetFileName(path)==Path.GetFileName(old)&&File.ReadAllBytes(path).SequenceEqual(bytes),"原名原文一致，复制到受控工作副本");
      Check(!requests.Any(value=>value.Contains("checkout")||value.Contains("archive-batches")||value.Contains("register")),"查找阶段不检出、不升版、不上传");
      if(mode=="project")Check((Guid?)Get(link,"DocumentId")==docId&&((IEnumerable)result).Cast<object>().Count()==1,"同项目原图档固定版本复用，不误新增");
      else Check(!((Guid?)Get(link,"DocumentId")).HasValue&&((IEnumerable)result).Cast<object>().Count()==(mode=="nested"||mode=="later-directory"?3:2),"外部原件及其递归依赖加入本项目待存档清单");
      if(mode=="standard")Check(fileGets==1,"只下载标准结构原文，忽略预览文件");
      if(mode=="project")Check((File.GetAttributes(path)&FileAttributes.ReadOnly)!=0,"复用原图档的参考副本保持只读，不隐式获取编辑权限");
      if(mode=="changed-copy"){
       File.WriteAllText(path,"changed recovered document bytes, verification must stop");
       try{Type("PdmAddin").GetMethod("TryGetRecoveredArchiveReference",Flags).Invoke(plugin,new object[]{owner,old,null});throw new Exception("未拦截补齐副本变化");}
       catch(TargetInvocationException error)when(error.InnerException is InvalidOperationException){Console.WriteLine("PASS: changed-copy：补齐副本被修改后重新核对，不静默使用");}
      }
      if(mode=="rescan"||mode=="removed"){
       File.WriteAllText(owner,"legitimate normal owner save");referenceRemoved=mode=="removed";
       var fresh=New("CadTreeNode");Set(fresh,"FullPath",owner);Set(fresh,"FileName",Path.GetFileName(owner));Set(fresh,"Kind",Enum.Parse(Type("CadDocumentKind"),"Part"));
       var restored=(IEnumerable)Type("PdmAddin").GetMethod("RestoreRecoveredArchiveReferences",Flags).Invoke(plugin,new[]{fresh,result});
       Check(restored.Cast<object>().Count()==(referenceRemoved?1:2),"正常保存后复核范围，不误报补齐文件消失");
       Check(((IList)Get(fresh,"Children")).Count==(referenceRemoved?0:1),referenceRemoved?"删除关联后不继续收集旧依赖":"保留关联时恢复已校验的补齐依赖");
       if(!referenceRemoved)Check((bool)Type("PdmAddin").GetMethod("TryGetRecoveredArchiveReference",Flags).Invoke(plugin,new object[]{owner,old,null}),"正常保存后更新基准，保留补齐目标校验");
      }
     }
     Check(File.ReadAllText(owner)==(mode=="rescan"||mode=="removed"?"legitimate normal owner save":"original smart component; never changed")&&directory==source,"原图档和SolidWorks工作目录均保留");
     Check(requests.Count(value=>value=="GET /api/projects")<=1,"同批引用只读取一次PLM目录");
    }
    ((IDisposable)client).Dispose();
   }
  }
 }
 sealed class ReferenceRecoveryHandler:HttpMessageHandler
 {
  readonly Func<HttpRequestMessage,HttpResponseMessage> respond;
  internal ReferenceRecoveryHandler(Func<HttpRequestMessage,HttpResponseMessage> respond){this.respond=respond;}
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token){token.ThrowIfCancellationRequested();return Task.FromResult(respond(request));}
 }
}
