using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

internal static partial class Program
{
    static void DraftRelocatedBaselineScenario()
    {
        var root=Path.Combine(Path.GetTempPath(),"pdm-draft-relocated",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Type("PdmDocumentIdentityStore").GetMethod("ConfigureBrowserReadScope",Flags).Invoke(null,new object[]{root});
        var path=Path.Combine(root,"ROOT.SLDASM");var snapshot=Path.Combine(root,"saved-draft.SLDASM");
        File.WriteAllText(path,"archived native bytes");File.WriteAllText(snapshot,"retained native draft");
        var hash=(string)Type("EditDraftJournal").GetMethod("Hash",Flags).Invoke(null,new object[]{path});
        var id=Guid.NewGuid();var project=Guid.NewGuid();var version=Guid.NewGuid();
        Type("PdmDocumentIdentityStore").GetMethod("TryWriteControlledVersion",Flags).Invoke(null,new object[]{path,id,project,version,"W1",hash,new FileInfo(path).Length});
        var metadata=new object[]{path,null};Type("PdmDocumentIdentityStore").GetMethod("TryReadLocalMetadata",Flags).Invoke(null,metadata);
        using(var client=(IDisposable)New("PdmApiClient","http://draft-qa.invalid/"))
        {
            ((HttpClient)Field(client,"httpClient")).DefaultRequestHeaders.Authorization=new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer","isolated");
            var plugin=New("PdmAddin");var journal=New("EditDraftJournal",Path.Combine(root,"journal"),"isolated");
            SetField(plugin,"apiClient",client);SetField(plugin,"authenticatedUsername","tester");SetField(plugin,"editJournal",journal);
            SetField(plugin,"editJournalScope",(string)Get(client,"EditJournalScope")+"|tester");EmptyApplication(plugin);
            var entry=New("LocalEditDraft");Set(entry,"DocumentId",id);Set(entry,"ProjectId",project);Set(entry,"SessionId",Guid.NewGuid());
            Set(entry,"BaseVersionId",version);Set(entry,"BaseSha256",hash);Set(entry,"SourcePath",path);
            File.WriteAllText(path,"earlier saved working bytes");Invoke(journal,"Preserve",entry,snapshot,true);
            var latest=New("ControlledOpenFileDto");Set(latest,"DocumentId",id);Set(latest,"VersionId",version);Set(latest,"Sha256",hash);
            Set(latest,"FileName","ROOT.SLDASM");Set(latest,"Revision","W1");
            File.WriteAllText(path,"unrecognized local edit");
            var rejected=false;
            try{Invoke(plugin,"RestoreEditDraftBeforeOpen",id,path,latest);}catch(TargetInvocationException e)when(e.InnerException is InvalidOperationException){rejected=true;}
            Require(rejected&&File.ReadAllText(path)=="unrecognized local edit","Draft recovery overwrote unrelated working changes");
            File.WriteAllText(path,"verified relocated archive bytes");
            var resolved=(string)Type("EditDraftJournal").GetMethod("Hash",Flags).Invoke(null,new object[]{path});
            Require((bool)Type("PdmDocumentIdentityStore").GetMethod("TryRecordResolvedReferences",Flags).Invoke(null,new[]{(object)path,metadata[1],resolved}),"Cannot bind relocated base");
            Invoke(plugin,"RestoreEditDraftBeforeOpen",id,path,latest);
            Require(File.ReadAllText(path)=="retained native draft"&&(bool)Invoke(journal,"HasVerifiedDraft",entry),"Verified relocated root could not resume its draft");
            Console.WriteLine("PASS: assembly draft re-entry accepts verified relocation while preserving unrelated current edits and the retained draft");
        }
        Directory.Delete(root,true);
    }

    static async Task DiscardRestoreScenario(bool relocated = false)
    {
        var root=Path.Combine(Path.GetTempPath(),"pdm-discard-restore",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Type("PdmDocumentIdentityStore").GetMethod("ConfigureBrowserReadScope",Flags).Invoke(null,new object[]{root});
        var id=Guid.NewGuid();var project=Guid.NewGuid();var version=Guid.NewGuid();
        var manifest=New("ControlledOpenManifestDto");var file=New("ControlledOpenFileDto");
        Set(manifest,"Id",Guid.NewGuid());Set(manifest,"ProjectId",project);Set(manifest,"ProjectCode","QA");
        Set(manifest,"RootDocumentId",id);Set(manifest,"RootVersionId",version);Set(manifest,"RootRelativePath","PART.SLDPRT");
        Set(file,"DocumentId",id);Set(file,"VersionId",version);Set(file,"FileName","PART.SLDPRT");Set(file,"RelativePath","PART.SLDPRT");Set(file,"Revision","W1");
        var files=(IList)Activator.CreateInstance(manifest.GetType().GetProperty("Files").PropertyType);files.Add(file);Set(manifest,"Files",files);
        var baseline=Encoding.UTF8.GetBytes("server archived bytes");
        using(var handler=new DownloadHandler(baseline))
        using(var client=(IDisposable)New("PdmApiClient","http://discard-qa.invalid/"))
        {
            ((HttpClient)Field(client,"httpClient")).Dispose();
            var http=new HttpClient(handler){BaseAddress=new Uri("http://discard-qa.invalid/")};
            http.DefaultRequestHeaders.Authorization=new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer","isolated");
            SetField(client,"httpClient",http);
            var workspace=New("ControlledWorkspaceManager",client,root);
            var path=(string)Invoke(workspace,"GetWorkingFilePath",manifest,id);
            Directory.CreateDirectory(Path.GetDirectoryName(path));File.WriteAllBytes(path,baseline);
            var hash=(string)Type("EditDraftJournal").GetMethod("Hash",Flags).Invoke(null,new object[]{path});
            Set(file,"Sha256",hash);Set(file,"FileLength",(long)baseline.Length);
            Type("PdmDocumentIdentityStore").GetMethod("TryWriteControlledVersion",Flags).Invoke(null,new object[]{path,id,project,version,"W1",hash,(long)baseline.Length});
            File.WriteAllText(path,"saved edit being discarded");
            var plugin=New("PdmAddin");var journal=New("EditDraftJournal",Path.Combine(root,"journal"),"isolated");
            SetField(plugin,"lifetime",new CancellationTokenSource());
            SetField(plugin,"apiClient",client);SetField(plugin,"controlledWorkspace",workspace);
            SetField(plugin,"authenticatedUsername","tester");SetField(plugin,"editJournal",journal);
            SetField(plugin,"editJournalScope",(string)Get(client,"EditJournalScope")+"|tester");
            var node=New("CadTreeNode");Set(node,"DocumentId",id);Set(node,"FullPath",path);Set(node,"FileName","PART.SLDPRT");
            var loaded=true;var closes=0;var appType=Type("PdmAddin").GetField("application",Flags).FieldType;
            var model=new Proxy(appType.Assembly.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true),call=>{
                switch(call.MethodName){case "GetPathName":return path;case "GetTitle":return "PART.SLDPRT";case "GetType":return 1;
                    case "GetSaveFlag":return true;case "IsOpenedReadOnly":return false;default:throw new Exception(call.MethodName);}
            }).GetTransparentProxy();
            SetField(plugin,"application",new Proxy(appType,call=>{
                if(call.MethodName=="GetDocuments")return loaded?new[]{model}:Array.Empty<object>();
                if(call.MethodName=="GetDocumentDependencies2")return Array.Empty<string>();
                if(call.MethodName=="CloseDoc"){loaded=false;closes++;return null;}throw new Exception(call.MethodName);
            }).GetTransparentProxy());
            Invoke(plugin,"MarkDiscardedLocalEdit",node);
            var secondPath=Path.Combine(root,"other-context","PART.SLDPRT");
            Directory.CreateDirectory(Path.GetDirectoryName(secondPath));File.WriteAllText(secondPath,"second discarded workspace");
            Type("PdmDocumentIdentityStore").GetMethod("TryWriteControlledVersion",Flags).Invoke(null,new object[]{secondPath,id,project,version,"W1",hash,(long)baseline.Length});
            var secondNode=New("CadTreeNode");Set(secondNode,"DocumentId",id);Set(secondNode,"FullPath",secondPath);Set(secondNode,"FileName","PART.SLDPRT");
            Invoke(plugin,"MarkDiscardedLocalEdit",secondNode);
            Require(((IEnumerable)Invoke(journal,"ReadAll")).Cast<object>().Count()==2,"A second workspace overwrote the first discard record");
            Require(((IEnumerable)Invoke(journal,"ReadAll")).Cast<object>().All(e=>(bool)Get(e,"DiscardPending")&&!(bool)Get(e,"RestoreOnOpen")),
                "Explicit discard was not durable");
            handler.Fail=true;
            try{await (Task)Invoke(plugin,"RestoreDiscardedEditsAsync",manifest);throw new Exception("Download failure ignored");}
            catch(InvalidOperationException e) when(e.InnerException is HttpRequestException)
            {
                Require(e.Message.Contains("放弃状态"),"恢复失败没有中文处理建议");
                Require(loaded&&closes==0&&File.ReadAllText(path)=="saved edit being discarded","Failed download closed or replaced editor");
            }
            var blocked=false;
            try{await (Task)Invoke(plugin,"FinishPendingCloseBeforeCheckoutAsync",id,CancellationToken.None);}
            catch(InvalidOperationException){blocked=true;}
            Require(blocked,"Pending discard could acquire editing permissions before reset");
            handler.Fail=false;
            File.WriteAllText(path,"new edit after discard request");
            try{await (Task)Invoke(plugin,"RestoreDiscardedEditsAsync",manifest);throw new Exception("Changed working file was overwritten");}
            catch(InvalidOperationException e) when(e.InnerException is IOException)
            {
                Require(loaded&&closes==0&&File.ReadAllText(path)=="new edit after discard request",
                    "Pre-existing local conflict closed the editor before validation");
            }
            var preview=(Task)Invoke(plugin,"RestoreDiscardedEditsForOpenAsync",manifest,true);
            await preview;
            Require(((string)Get(preview,"Result")).Contains("只读副本")&&loaded&&closes==0,
                "Pending working restoration prevented independent server preview or closed a dirty editor");
            Require(((IEnumerable)Invoke(journal,"ReadAll")).Cast<object>().All(e=>(bool)Get(e,"DiscardPending")),
                "Readonly fallback silently completed a failed restoration");
            try{await (Task)Invoke(plugin,"RestoreDiscardedEditsForOpenAsync",manifest,false);throw new Exception("Editing ignored unresolved local conflict");}
            catch(InvalidOperationException e) when(e.InnerException is IOException){}
            File.WriteAllText(path,"saved edit being discarded");
            var expectedBytes = baseline;
            if (relocated)
            {
                var prepared = (Task)Invoke(workspace, "PrepareReadOnlyAsync", manifest, false, CancellationToken.None);
                await prepared;
                var readOnlyPath = (string)Get(prepared, "Result");
                Type("PdmDocumentIdentityStore").GetMethod("TryWriteControlledVersion",Flags).Invoke(null,new object[]{readOnlyPath,id,project,version,"W1",hash,(long)baseline.Length});
                var metadataArguments = new object[] { readOnlyPath, null };
                Type("PdmDocumentIdentityStore").GetMethod("TryReadLocalMetadata",Flags).Invoke(null,metadataArguments);
                var relocatedBytes = Encoding.UTF8.GetBytes("verified relocated native references");
                File.SetAttributes(readOnlyPath,FileAttributes.Normal); File.WriteAllBytes(readOnlyPath,relocatedBytes);
                var relocatedHash = (string)Type("EditDraftJournal").GetMethod("Hash",Flags).Invoke(null,new object[]{readOnlyPath});
                Require((bool)Type("PdmDocumentIdentityStore").GetMethod("TryRecordResolvedReferences",Flags).Invoke(null,new[]{(object)readOnlyPath,metadataArguments[1],relocatedHash}),"Unable to bind relocated fixture");
                File.SetAttributes(readOnlyPath,FileAttributes.ReadOnly);
            }
            await (Task)Invoke(plugin,"RestoreDiscardedEditsAsync",manifest);
            Require(!loaded&&closes==1&&File.ReadAllBytes(path).SequenceEqual(expectedBytes),"Discard did not replace actual working bytes");
            Require(File.ReadAllBytes(secondPath).SequenceEqual(expectedBytes),"Discard left another recorded workspace modified");
            Require(((IEnumerable)Invoke(journal,"ReadAll")).Cast<object>().All(e=>!(bool)Get(e,"DiscardPending")&&!(bool)Get(e,"RestoreOnOpen")),
                "Completed discard could restore old drafts");
            var query=new object[]{manifest,null,false};
            Require((bool)Type("ControlledWorkspaceManager").GetMethod("TryGetExistingWorkingRoot",Flags).Invoke(workspace,query)&&(bool)query[2],
                "Next edit still reused mismatched local bytes");
            await (Task)Invoke(plugin,"FinishPendingCloseBeforeCheckoutAsync",id,CancellationToken.None);
            Console.WriteLine("PASS: explicit discard retries failed download, closes only owned editor, restores verified bytes/binding and allows clean reacquisition; relocated=" + relocated);
        }
        foreach(var path in Directory.GetFiles(root,"*",SearchOption.AllDirectories))File.SetAttributes(path,FileAttributes.Normal);
        Directory.Delete(root,true);
    }

    sealed class DownloadHandler:HttpMessageHandler
    {
        readonly byte[] bytes;public bool Fail;
        public DownloadHandler(byte[] bytes){this.bytes=bytes;}
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            if(Fail)throw new HttpRequestException("模拟下载中断");
            Require(request.Method==HttpMethod.Get&&request.RequestUri.AbsolutePath.EndsWith("/file"),"Unexpected write request");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)});
        }
    }

    static void StandardPreviewFailureScenario()
    {
        var plugin=New("PdmAddin");var appType=Type("PdmAddin").GetField("application",Flags).FieldType;
        var interop=appType.Assembly;var source=Path.Combine(Path.GetTempPath(),"qa-preview.SLDDRW");
        var visible=false;
        var extension=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDocExtension",true),call=>{
            if(call.MethodName=="SaveAs")return false;throw new Exception(call.MethodName);
        }).GetTransparentProxy();
        var document=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true),call=>{
            switch(call.MethodName){case "GetPathName":return source;case "get_Visible":return visible;
                case "set_Visible":visible=(bool)call.Args[0];return null;case "ClearSelection2":return null;case "get_Extension":return extension;
                default:throw new Exception(call.MethodName);}
        }).GetTransparentProxy();
        SetField(plugin,"application",new Proxy(appType,call=>{
            if(call.MethodName=="get_ActiveDoc"||call.MethodName=="ActivateDoc3")return document;throw new Exception(call.MethodName);
        }).GetTransparentProxy());
        var node=New("CadTreeNode");Set(node,"FullPath",source);Set(node,"FileName",Path.GetFileName(source));
        Set(node,"Kind",Enum.Parse(Type("CadDocumentKind"),"Drawing"));
        Require(!(bool)Invoke(plugin,"TryExportStandardStructurePreview",document,node,Path.ChangeExtension(source,".pdf"))&&!visible,
            "Preview failure blocked the source archive or failed to restore visibility");
        Console.WriteLine("PASS: standard-structure preview export failure returns optional-preview status and preserves native editor visibility");
    }
}
