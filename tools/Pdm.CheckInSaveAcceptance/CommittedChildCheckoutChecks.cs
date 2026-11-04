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
 static async Task CommittedChildCheckoutScenario(string output)
 {
  Directory.CreateDirectory(output);output=Path.GetFullPath(output);
  var identity=Type("PdmDocumentIdentityStore");var bindings=identity.GetField("bindingDirectory",Flags);var resolver=identity.GetField("workspaceRootResolver",Flags);
  var oldBindings=bindings.GetValue(null);var oldResolver=resolver.GetValue(null);bindings.SetValue(null,Path.Combine(output,"bindings"));resolver.SetValue(null,new Func<string>(()=>output));
  var json=new JavaScriptSerializer{MaxJsonLength=int.MaxValue};
  void Check(bool ok,string text){if(!ok)throw new Exception(text);Console.WriteLine("PASS: "+text);}
  try
  {
   foreach(var mode in new[]{"committed","hidden","property-edit","geometry-edit","uncommitted","source-changed","wrong-copy-hash","other-user","other-server","other-instance","new-server-version","selected-child","edit-during-validation","edit-during-checkout","server-race"})
   {
    var folder=Path.Combine(output,"View",mode);Directory.CreateDirectory(folder);
    var plugin=New("PdmAddin");SetField(plugin,"authenticatedUsername","tester");var project=Guid.NewGuid();var session=(Guid)Field(plugin,"checkoutSessionId");
    var owner=New("ControlledOpenManifestDto");Set(owner,"Id",Guid.NewGuid());Set(owner,"ProjectId",project);Set(owner,"ProjectCode","QA-COMMITTED");Set(owner,"RootRevision","W1");
    var files=(IList)Activator.CreateInstance(Property(owner,"Files").PropertyType);
    for(var i=0;i<43;i++)
    {
     var name=i==0?"CW004-3.SLDASM":i==1?"标签机钣金.SLDPRT":i==2?"标签机钣金.SLDDRW":"PART-"+i+".SLDPRT";var path=Path.Combine(folder,name);File.WriteAllText(path,"saved source "+name);
     var file=New("ControlledOpenFileDto");Set(file,"DocumentId",Guid.NewGuid());Set(file,"VersionId",Guid.NewGuid());Set(file,"FileName",name);Set(file,"RelativePath",name);Set(file,"Configuration","Default");Set(file,"Revision",i==1?"W3":"W1");Set(file,"Sha256",WorkingReuseHash(path));Set(file,"FileLength",new FileInfo(path).Length);Set(file,"IsRoot",i==0);files.Add(file);
     identity.GetMethod("TryWriteControlledVersion",Flags).Invoke(null,new[]{(object)path,Get(file,"DocumentId"),project,Get(file,"VersionId"),Get(file,"Revision"),Get(file,"Sha256"),Get(file,"FileLength")});File.SetAttributes(path,FileAttributes.ReadOnly);
    }
    Set(owner,"Files",files);Set(owner,"RootDocumentId",Get(files[0],"DocumentId"));Set(owner,"RootVersionId",Get(files[0],"VersionId"));Set(owner,"RootRelativePath",Get(files[0],"FileName"));
    var latest=json.Deserialize(json.Serialize(owner),owner.GetType());Set(latest,"ForEdit",true);var latestFiles=((IEnumerable)Get(latest,"Files")).Cast<object>().ToArray();
    var rootPath=Path.Combine(folder,(string)Get(files[0],"FileName"));var partPath=Path.Combine(folder,(string)Get(files[1],"FileName"));
    var events=new List<string>();var appType=Type("PdmAddin").GetField("application",Flags).FieldType;var interop=appType.Assembly;
    var native=new Model(interop.GetType("SolidWorks.Interop.sldworks.IModelDoc2",true),partPath,true,events,false);var part=native.GetTransparentProxy();
    using(var memory=(IDisposable)New("PdmAddin+ArchiveMemoryCopy",part,partPath))
    {
     var copyPath=Path.Combine(folder,"copy",Path.GetFileName(partPath));Directory.CreateDirectory(Path.GetDirectoryName(copyPath));File.WriteAllText(copyPath,"server-confirmed current memory W4");
     TreeCall(memory,"ReplaceCopy",copyPath,WorkingReuseHash(partPath),new FileInfo(partPath).Length,WorkingReuseHash(copyPath),1,native.UpdateStamp,0);
     var version=New("DocumentVersionDto");Set(version,"Id",Guid.NewGuid());Set(version,"Sha256",WorkingReuseHash(copyPath));Set(version,"FileLength",new FileInfo(copyPath).Length);
     SetField(memory,"CommittedVersion",mode=="uncommitted"?null:version);SetField(memory,"CommittedDocumentId",Get(files[1],"DocumentId"));SetField(memory,"CommittedUser",mode=="other-user"?"other":"tester");
     Set(latestFiles[1],"VersionId",Get(version,"Id"));Set(latestFiles[1],"Sha256",Get(version,"Sha256"));Set(latestFiles[1],"FileLength",Get(version,"FileLength"));Set(latestFiles[1],"Revision","W4");
     ((IDictionary)Field(plugin,"archiveMemoryCopies")).Add(partPath,memory);
     var currentPart=mode=="other-instance"?new Model(interop.GetType("SolidWorks.Interop.sldworks.IModelDoc2",true),partPath,true,events,false).GetTransparentProxy():part;
     var config=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.Configuration",true),call=>call.MethodName=="get_Name"?"Default":throw new Exception(call.MethodName)).GetTransparentProxy();
     var manager=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ConfigurationManager",true),call=>call.MethodName=="get_ActiveConfiguration"?config:throw new Exception(call.MethodName)).GetTransparentProxy();
     var rootReadOnly=true;var unlocks=0;var mutations=0;
     var components=files.Cast<object>().Where(file=>((string)Get(file,"FileName")).EndsWith("SLDPRT")).Select(file=>new AssemblyProxy(interop.GetType("SolidWorks.Interop.sldworks.Component2",true),call=>
     {switch(call.MethodName){case "GetPathName":return Path.Combine(folder,(string)Get(file,"FileName"));case "get_ReferencedConfiguration":return "Default";case "get_IsVirtual":return false;case "GetModelDoc2":return ReferenceEquals(file,files[1])?currentPart:null;case "GetSuppression":return 2;default:throw new Exception(call.MethodName);}}).GetTransparentProxy()).ToArray();
     var root=new DiscardAssemblyProxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true),call=>
     {switch(call.MethodName){case "GetPathName":return rootPath;case "GetTitle":return Path.GetFileName(rootPath);case "GetType":return 2;case "GetSaveFlag":return false;case "IsOpenedReadOnly":return rootReadOnly;case "get_ConfigurationManager":return manager;case "GetComponents":return components;case "SetReadOnlyState":rootReadOnly=(bool)call.Args[0];unlocks++;return true;default:mutations++;throw new Exception("不能保存/重开总装："+call.MethodName);}}).GetTransparentProxy();
     SetField(plugin,"application",new Proxy(appType,call=>{switch(call.MethodName){case "GetDocuments":return new[]{root,currentPart};case "get_ActiveDoc":return root;default:mutations++;throw new Exception("不能下载/关闭/保存："+call.MethodName);}}).GetTransparentProxy());
     ((IDictionary)Field(plugin,"controlledOpenManifests")).Add(folder,owner);
     var rootNode=New("CadTreeNode");Set(rootNode,"DocumentId",Get(files[0],"DocumentId"));Set(rootNode,"FullPath",rootPath);Set(rootNode,"FileName",Get(files[0],"FileName"));Set(rootNode,"Kind",Enum.Parse(Type("CadDocumentKind"),"Assembly"));Set(rootNode,"OpenedVersionId",Get(files[0],"VersionId"));SetField(plugin,"currentTree",rootNode);
     var selected=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(Type("BatchOperationItem")));selected.Add(New("BatchOperationItem",rootNode,0));
     if(mode=="selected-child"){var node=New("CadTreeNode");Set(node,"DocumentId",Get(files[1],"DocumentId"));Set(node,"FullPath",partPath);Set(node,"FileName",Get(files[1],"FileName"));Set(node,"Kind",Enum.Parse(Type("CadDocumentKind"),"Part"));selected.Add(New("BatchOperationItem",node,1));}
     var doc=New("DocumentDto");Set(doc,"Id",Get(files[0],"DocumentId"));Set(doc,"ProjectId",project);Set(doc,"FileName",Get(files[0],"FileName"));Set(doc,"Kind",0);
     var children=files.Cast<object>().Where(file=>((string)Get(file,"FileName")).EndsWith("SLDPRT")).Select(file=>new{documentId=Get(file,"DocumentId"),fileName=Get(file,"FileName"),kind=1,configuration="Default",quantity=1,status=0,children=new object[0]}).ToArray();
     var checkouts=new List<Guid>();var unexpected=new List<string>();var manifestReads=0;
     using(var lifetime=new CancellationTokenSource())
     using(var client=(IDisposable)New("PdmApiClient","http://committed-child.invalid/"))
     using(var handler=new ReferenceRecoveryHandler(request=>
     {
      var path=request.RequestUri.AbsolutePath;object data;
      if(path.EndsWith("/document-operation-rules"))data=new{disabledRuleIds=new string[0]};
      else if(path.EndsWith("/open-manifest")){manifestReads++;if(mode=="server-race"&&manifestReads>1)Set(latestFiles[1],"VersionId",Guid.NewGuid());data=latest;}
      else if(path.EndsWith("/document-relations"))data=new object[0];
      else if(path.EndsWith("/documents"))data=new[]{doc};
      else if(path.EndsWith("/versions")){if(mode=="edit-during-validation")native.Modify(false);data=new[]{new{id=Get(files[0],"VersionId"),sha256=Get(files[0],"Sha256"),fileLength=Get(files[0],"FileLength"),referenceSnapshot=new{documentId=Get(files[0],"DocumentId"),fileName=Get(files[0],"FileName"),kind=0,configuration="Default",children=children}}};}
      else if(path.EndsWith("/checkout")){var id=Guid.Parse(path.Split('/').Reverse().Skip(1).First());checkouts.Add(id);if(mode=="edit-during-checkout")native.Modify(false);Set(doc,"CheckedOutBy","tester");Set(doc,"CheckoutSessionId",session);Set(doc,"CheckedOutAt",new DateTime(2026,11,4,14,37,0,DateTimeKind.Utc));data=doc;}
      else{unexpected.Add(path);throw new Exception("不得下载、检入或扩大权限："+path);}
      return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(json.Serialize(data))};
     }))
     using(var http=new HttpClient(handler){BaseAddress=new Uri("http://committed-child.invalid/")})
     {
      http.DefaultRequestHeaders.Authorization=new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer","fixture");SetField(client,"httpClient",http);SetField(client,"controlledOpenHttpClient",http);SetField(plugin,"apiClient",client);SetField(plugin,"lifetime",lifetime);SetField(memory,"CommittedScope",mode=="other-server"?"other-server":Get(client,"EditJournalScope"));
      var scope=(string)Get(client,"EditJournalScope")+"|tester";SetField(plugin,"editJournal",New("EditDraftJournal",Path.Combine(folder,"journal"),scope));SetField(plugin,"editJournalScope",scope);
      if(mode=="hidden")native.Hide();if(mode=="property-edit")native.Modify(false);if(mode=="geometry-edit")native.Modify();if(mode=="source-changed"){File.SetAttributes(partPath,FileAttributes.Normal);File.AppendAllText(partPath," later edit");}if(mode=="wrong-copy-hash")SetField(memory,"CopyHash",new string('A',64));if(mode=="new-server-version")Set(latestFiles[1],"VersionId",Guid.NewGuid());
      var before=files.Cast<object>().ToDictionary(file=>(string)Get(file,"FileName"),file=>new{Hash=WorkingReuseHash(Path.Combine(folder,(string)Get(file,"FileName"))),Attributes=File.GetAttributes(Path.Combine(folder,(string)Get(file,"FileName")))});var sourceVersion=Get(files[1],"VersionId");
      Exception failure=null;var success=false;try{var task=(Task)TreeCall(plugin,"TryAcquireReusableWorkingCopyAsync",selected,project,Get(files[0],"DocumentId"));await task;success=(bool)Get(task,"Result");}catch(InvalidOperationException error){failure=error;}
      var expected=mode=="committed"||mode=="hidden";if(expected&&failure!=null)Console.WriteLine("DIAGNOSTIC: "+failure);
      Check(success==expected&&(failure==null)==expected,mode+"：已提交子件与未提交修改正确区分");
      Check(checkouts.All(id=>id.Equals(Get(files[0],"DocumentId")))&&checkouts.Count==(expected||mode=="edit-during-checkout"||mode=="server-race"?1:0),mode+"：仅总装获取权限，不扩至子件或工程图");
      Check(unlocks==(expected?1:0)&&rootReadOnly!=expected&&mutations==0&&unexpected.Count==0&&events.Count==0,mode+"：不保存、下载、关闭或重开图档；校验后才解锁总装");
      Check(files.Cast<object>().All(file=>{var name=(string)Get(file,"FileName");return WorkingReuseHash(Path.Combine(folder,name))==before[name].Hash&&(name=="CW004-3.SLDASM"&&expected||File.GetAttributes(Path.Combine(folder,name))==before[name].Attributes);}),mode+"：43份原文件字节及未选文件属性保留");
      Check(Get(files[1],"VersionId").Equals(sourceVersion)&&Get(owner,"RootVersionId").Equals(Get(files[0],"VersionId")),mode+"：不将旧磁盘基准冒写成已提交内存版本");
     }
    }
   }
  }finally{bindings.SetValue(null,oldBindings);resolver.SetValue(null,oldResolver);}
 }
}
