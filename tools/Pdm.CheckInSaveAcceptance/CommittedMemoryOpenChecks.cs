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
 static async Task CommittedMemoryOpenScenario(string output)
 {
  output=Path.GetFullPath(output);Directory.CreateDirectory(output);
  var identity=Type("PdmDocumentIdentityStore");var bindings=identity.GetField("bindingDirectory",Flags);var resolver=identity.GetField("workspaceRootResolver",Flags);
  var oldBindings=bindings.GetValue(null);var oldResolver=resolver.GetValue(null);
  bindings.SetValue(null,Path.Combine(output,"bindings"));resolver.SetValue(null,new Func<string>(()=>output));
  var json=new JavaScriptSerializer();
  try
  {
   foreach(var mode in new[]{"committed","same-version","hidden","geometry-edit","property-edit","reloaded","closed","other-instance","other-user","other-server","new-server-version","wrong-hash","wrong-length","disk-changed","binding-changed","uncommitted","configuration","dependency-version","edit-during-validation","edit-during-activation","acquire"})
   {
    var folder=Path.Combine(output,"View",mode);Directory.CreateDirectory(folder);
    var owner=SpeedManifest("标签机钣金.SLDPRT",System.Text.Encoding.UTF8.GetBytes("old saved bytes"));
    var sources=((IList)Get(owner,"Files")).Cast<object>().Take(43).ToArray();
    var ownerFiles=(IList)Activator.CreateInstance(Property(owner,"Files").PropertyType);
    foreach(var source in sources)
    {
     Set(source,"Configuration","Default");ownerFiles.Add(source);
     var path=Path.Combine(folder,(string)Get(source,"FileName"));File.WriteAllText(path,"old saved bytes");File.SetAttributes(path,FileAttributes.ReadOnly);
     identity.GetMethod("TryWriteControlledVersion",Flags).Invoke(null,new[]{(object)path,Get(source,"DocumentId"),Get(owner,"ProjectId"),Get(source,"VersionId"),"W1",Get(source,"Sha256"),Get(source,"FileLength")});
    }
    Set(owner,"Files",ownerFiles);
    var requested=json.Deserialize(json.Serialize(owner),owner.GetType());var requestedFiles=((IList)Get(requested,"Files")).Cast<object>().ToArray();var root=requestedFiles[0];
    var partPath=Path.Combine(folder,(string)Get(root,"FileName"));var plugin=New("PdmAddin");SetField(plugin,"authenticatedUsername","tester");
    ((IDictionary)Field(plugin,"controlledOpenManifests")).Add(folder,owner);
    var appType=Type("PdmAddin").GetField("application",Flags).FieldType;var events=new List<string>();
    var native=new Model(appType.Assembly.GetType("SolidWorks.Interop.sldworks.IModelDoc2",true),partPath,true,events,false);
    var model=native.GetTransparentProxy();var memory=New("PdmAddin+ArchiveMemoryCopy",model,partPath);
    var copyPath=Path.Combine(folder,"snapshot",Path.GetFileName(partPath));Directory.CreateDirectory(Path.GetDirectoryName(copyPath));File.WriteAllText(copyPath,"committed memory geometry");
    TreeCall(memory,"ReplaceCopy",copyPath,WorkingReuseHash(partPath),new FileInfo(partPath).Length,WorkingReuseHash(copyPath),1,native.UpdateStamp,0);
    var version=New("DocumentVersionDto");Set(version,"Id",mode=="same-version"?Get(root,"VersionId"):Guid.NewGuid());Set(version,"Sha256",WorkingReuseHash(copyPath));Set(version,"FileLength",new FileInfo(copyPath).Length);
    SetField(memory,"CommittedVersion",mode=="uncommitted"?null:version);SetField(memory,"CommittedDocumentId",Get(root,"DocumentId"));SetField(memory,"CommittedUser",mode=="other-user"?"someone-else":"tester");
    Set(root,"VersionId",Get(version,"Id"));Set(root,"Sha256",Get(version,"Sha256"));Set(root,"FileLength",Get(version,"FileLength"));Set(requested,"RootVersionId",Get(version,"Id"));
    ((IDictionary)Field(plugin,"archiveMemoryCopies")).Add(partPath,memory);
    object active=null;var activations=0;var search=false;var nativeMutations=0;
    var current=mode=="other-instance"?new Model(appType.Assembly.GetType("SolidWorks.Interop.sldworks.IModelDoc2",true),partPath,true,events,false).GetTransparentProxy():model;
    SetField(plugin,"application",new Proxy(appType,call=>{
     switch(call.MethodName){case "GetDocuments":return new[]{current};case "get_ActiveDoc":return active;
      case "ActivateDoc3":activations++;active=current;if(mode=="edit-during-activation")native.Modify(false);return current;
      case "GetCurrentWorkingDirectory":case "GetSearchFolders":return "";case "SetCurrentWorkingDirectory":case "SetSearchFolders":return true;
      case "GetUserPreferenceToggle":return search;case "SetUserPreferenceToggle":search=(bool)call.Args[1];return true;
      default:nativeMutations++;throw new Exception("不得重新打开、保存、关闭或替换模型："+call.MethodName);}
    }).GetTransparentProxy());
    using(var lifetime=new CancellationTokenSource())
    using(var client=(IDisposable)New("PdmApiClient","http://memory-open.invalid/"))
    using(var handler=new ReferenceRecoveryHandler(request=>{
     if(request.RequestUri.AbsolutePath.EndsWith("/documents"))return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("[]")};
     if(mode=="edit-during-validation"&&request.RequestUri.AbsolutePath.EndsWith("/versions"))
     {
      native.Modify(false);var file=requestedFiles[1];
      return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(json.Serialize(new[]{new{id=Get(file,"VersionId"),sha256=Get(file,"Sha256"),fileLength=Get(file,"FileLength"),propertySnapshot=new Dictionary<string,string>{{"SourceFileSha256",(string)Get(sources[1],"Sha256")}}}}))};
     }
     throw new Exception("复用不应下载、提交或获取权限："+request.RequestUri.AbsolutePath);
    }))
    using(var http=new HttpClient(handler){BaseAddress=new Uri("http://memory-open.invalid/")})
    {
     SetField(client,"httpClient",http);SetField(plugin,"apiClient",client);SetField(plugin,"lifetime",lifetime);
     SetField(memory,"CommittedScope",mode=="other-server"?"other-server":Get(client,"EditJournalScope"));
     switch(mode)
     {
      case "hidden":native.Hide();break;case "geometry-edit":native.Modify();break;case "property-edit":native.Modify(false);break;case "reloaded":native.Reload();break;case "closed":native.Destroy();break;
      case "new-server-version":Set(root,"VersionId",Guid.NewGuid());break;case "wrong-hash":Set(root,"Sha256",new string('A',64));break;case "wrong-length":Set(root,"FileLength",999L);break;
      case "disk-changed":File.SetAttributes(partPath,FileAttributes.Normal);File.AppendAllText(partPath,"later disk edit");break;
      case "binding-changed":identity.GetMethod("TryWriteControlledVersion",Flags).Invoke(null,new[]{(object)partPath,Get(root,"DocumentId"),Get(owner,"ProjectId"),Guid.NewGuid(),"W9",Get(sources[0],"Sha256"),Get(sources[0],"FileLength")});break;
      case "configuration":Set(root,"Configuration","Other");break;case "dependency-version":Set(requestedFiles[1],"VersionId",Guid.NewGuid());break;
      case "edit-during-validation":Set(requestedFiles[1],"Sha256",new string('B',64));break;
     }
     var diskHashes=sources.ToDictionary(file=>(string)Get(file,"FileName"),file=>WorkingReuseHash(Path.Combine(folder,(string)Get(file,"FileName"))));
     var attributes=File.GetAttributes(partPath);var reused=false;Exception failure=null;
     try{var task=(Task)TreeCall(plugin,"TryOpenModelInLoadedContextAsync",requested,mode=="acquire");await task;reused=(bool)Get(task,"Result");}catch(InvalidOperationException error){failure=error;}
     var expected=mode=="committed"||mode=="same-version"||mode=="hidden";
     if(reused!=expected||(failure==null)!=expected||activations!=(expected||mode=="edit-during-activation"?1:0))throw new Exception("已存档内存模型打开判定不正确："+mode+"；"+failure);
     if(nativeMutations!=0||events.Count!=0||File.GetAttributes(partPath)!=attributes||sources.Any(file=>WorkingReuseHash(Path.Combine(folder,(string)Get(file,"FileName")))!=diskHashes[(string)Get(file,"FileName")]))throw new Exception("打开改变了窗口、原文件、属性或未选零件："+mode);
     if(!Equals(Get(sources[0],"VersionId"),Get(owner,"RootVersionId")))throw new Exception("旧磁盘清单被冒写为已存档内存版本");
     Console.WriteLine("PASS: 43文件工作区已存档内存零件打开："+mode+"；原文件、权限、其他零件和磁盘基准保持不变");
    }
    ((IDisposable)memory).Dispose();
   }
  }
  finally{bindings.SetValue(null,oldBindings);resolver.SetValue(null,oldResolver);}
 }
}
