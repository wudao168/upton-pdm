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
    static async Task LoadedWorkingReferenceScenario(string output)
    {
        var identity=Type("PdmDocumentIdentityStore");
        var binding=identity.GetField("bindingDirectory",Flags);
        var resolver=identity.GetField("workspaceRootResolver",Flags);
        var isolated=identity.GetField("isolatedReadScope",Flags);
        var oldBinding=binding.GetValue(null);var oldResolver=resolver.GetValue(null);var oldIsolated=isolated.GetValue(null);
        var area=Path.GetFullPath(Path.Combine(output,"saved-open"));
        binding.SetValue(null,Path.Combine(area,"bindings"));resolver.SetValue(null,new Func<string>(()=>area));isolated.SetValue(null,true);
        try
        {
            foreach(var mode in new[]{"view","edit","foreign","wrong-lease","revoked","release-race","changed-version","unsaved","historical"})
            {
                var dir=Path.Combine(area,"View",mode,"装配工作区");
                Directory.CreateDirectory(dir);
                var root=Path.Combine(dir,"ROOT.SLDASM");File.WriteAllText(root,"archived assembly");
                var part=Path.Combine(mode=="foreign"?area:dir,"NEW.SLDPRT");File.WriteAllText(part,"new local part");
                var project=Guid.NewGuid();var id=Guid.NewGuid();var ver=Guid.NewGuid();var sha=WorkingReuseHash(root);
                var length=new FileInfo(root).Length;
                identity.GetMethod("TryWriteControlledVersion",Flags).Invoke(null,new object[]{root,id,project,ver,"W1",sha,length});
                var file=New("ControlledOpenFileDto");Set(file,"DocumentId",id);Set(file,"VersionId",ver);Set(file,"FileName","ROOT.SLDASM");
                Set(file,"RelativePath","ROOT.SLDASM");Set(file,"Sha256",sha);Set(file,"FileLength",length);Set(file,"Revision","W1");Set(file,"IsRoot",true);
                var manifest=New("ControlledOpenManifestDto");Set(manifest,"ProjectId",project);Set(manifest,"RootDocumentId",id);Set(manifest,"RootVersionId",ver);
                Set(manifest,"RootRelativePath","ROOT.SLDASM");Set(manifest,"UsesLatestModelReferences",mode!="historical");
                var files=(IList)Activator.CreateInstance(Property(manifest,"Files").PropertyType);
                files.Add(file);Set(manifest,"Files",files);
                var plugin=New("PdmAddin");SetField(plugin,"authenticatedUsername","tester");var session=(Guid)Field(plugin,"checkoutSessionId");
                var when=new DateTime(2026,10,27,6,0,0,DateTimeKind.Utc);var doc=New("DocumentDto");Set(doc,"Id",id);Set(doc,"ProjectId",project);Set(doc,"FileName","ROOT.SLDASM");
                Set(doc,"CheckedOutAt",when);Set(doc,"CheckoutSessionId",mode=="wrong-lease"?Guid.NewGuid():session);Set(doc,"CheckedOutBy","tester");
                var draft=New("LocalEditDraft");Set(draft,"DocumentId",id);Set(draft,"ProjectId",project);Set(draft,"SessionId",session);
                Set(draft,"CheckedOutAt",when);Set(draft,"SourcePath",root);Set(draft,"BaseVersionId",ver);Set(draft,"BaseSha256",sha);
                ((IDictionary)Field(plugin,"localEdits")).Add(id,draft);
                ((IDictionary)Field(plugin,"controlledOpenManifests")).Add(dir,manifest);
                File.AppendAllText(root," with one new part");var saved=WorkingReuseHash(root);
                var appType=Type("PdmAddin").GetField("application",Flags).FieldType;var interop=appType.Assembly;
                var modelType=interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true);
                var child=new Proxy(modelType,call=>{
                    switch(call.MethodName){case "GetPathName":return part;case "GetType":return 1;case "get_Extension":return null;
                        case "GetSaveFlag":return false;case "GetEquationMgr":case "GetDesignTable":return null;default:throw new Exception(call.MethodName);}
                }).GetTransparentProxy();
                var component=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.Component2",true),call=>{
                    switch(call.MethodName){case "GetPathName":return part;case "GetModelDoc2":return child;case "get_Name2":return "NEW-1";
                        case "get_IsVirtual":return false;default:throw new Exception(call.MethodName);}
                }).GetTransparentProxy();
                var model=new AssemblyProxy(modelType,call=>{
                    switch(call.MethodName){case "GetPathName":return root;case "GetTitle":return "ROOT";case "GetType":return 2;
                        case "GetSaveFlag":return mode=="unsaved";case "IsOpenedReadOnly":return false;case "GetComponents":return new[]{component};
                        case "get_Extension":case "get_ConfigurationManager":case "GetEquationMgr":case "GetDesignTable":return null;default:throw new Exception(call.MethodName);}
                }).GetTransparentProxy();
                SetField(plugin,"application",new Proxy(appType,call=>{
                    switch(call.MethodName){case "get_ActiveDoc":return model;case "GetDocuments":return new[]{model,child};
                        case "GetDocumentDependencies2":return new[]{"NEW.SLDPRT",part};
                        case "GetSearchFolders":case "GetCurrentWorkingDirectory":return "";
                        case "SetSearchFolders":case "SetCurrentWorkingDirectory":case "SetUserPreferenceToggle":return true;
                        case "GetUserPreferenceToggle":return false;default:throw new Exception("复用工作副本不得替换或保存窗口："+call.MethodName);}
                }).GetTransparentProxy());
                var version=New("DocumentVersionDto");Set(version,"Id",mode=="changed-version"?Guid.NewGuid():ver);
                var renewals=0;var json=new JavaScriptSerializer();
                using(var lifetime=new CancellationTokenSource())
                using(var client=(IDisposable)New("PdmApiClient","http://saved-open.invalid/"))
                using(var handler=new ReferenceRecoveryHandler(request=>{
                    var url=request.RequestUri.AbsolutePath;object response;
                    if(url.EndsWith("/documents"))response=new[]{doc};
                    else if(url.EndsWith("/versions"))response=new[]{version};
                    else if(url.EndsWith("/document-relations"))response=new object[0];
                    else if(url.EndsWith("/heartbeat"))
                    {
                        renewals++;if(mode=="revoked")return new HttpResponseMessage(HttpStatusCode.Forbidden){Content=new StringContent("{\"message\":\"无获取权限。\"}")};
                        response=new {sessionId=session,activeDocumentIds=mode=="release-race"?new Guid[0]:new[]{id},
                            lostDocumentIds=mode=="release-race"?new[]{id}:new Guid[0]};
                    }
                    else throw new Exception("不应产生其他业务操作："+url);
                    return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(json.Serialize(response))};
                }))
                using(var http=new HttpClient(handler){BaseAddress=new Uri("http://saved-open.invalid/")})
                {
                    http.DefaultRequestHeaders.Authorization=new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer","fixture");
                    SetField(client,"httpClient",http);SetField(plugin,"apiClient",client);SetField(plugin,"lifetime",lifetime);
                    Exception failure=null;bool reused=false;
                    var editRequested=mode=="edit"||mode=="revoked"||mode=="release-race";
                    try{var operation=(Task)TreeCall(plugin,"TryOpenModelInLoadedContextAsync",manifest,editRequested);
                        await operation;reused=(bool)Get(operation,"Result");}
                    catch(InvalidOperationException error){failure=error;}
                    var expected=mode=="view"||mode=="edit";
                    if(reused!=expected||(failure==null)!=expected ||WorkingReuseHash(root)!=saved||files.Count!=1
                        || ((IDictionary)Get(manifest,"RetainedLocalReferences")).Count!=0||renewals!=(editRequested?1:0))
                        throw new Exception("已打开总装含新增零件，获取/查看结果或编辑保护不符合预期："+mode,failure);
                    Console.WriteLine("PASS: 已保存工作副本、原生新增引用、权限及只读边界："+mode);
                }
            }
        }
        finally{binding.SetValue(null,oldBinding);resolver.SetValue(null,oldResolver);isolated.SetValue(null,oldIsolated);}
    }
}
