using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Remoting;
using System.Runtime.Remoting.Messaging;
using System.Runtime.Remoting.Proxies;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

internal static partial class Program
{
    static async Task DiscardWorkingPathScenario()
    {
        foreach (var mode in new[] { "clean", "saved-edit", "download-failure", "subsequent-edit", "locked-retry", "same-title" })
        using (var fixture = new DiscardPathFixture())
        {
            if (mode != "clean") File.WriteAllText(fixture.CurrentPath, "selected saved modification");
            DiscardCall(fixture.Plugin, "MarkDiscardedLocalEdit", fixture.Node);
            fixture.AssertOldUntouched();
            var selected = fixture.Records().Single(entry => (string)Get(entry, "SourcePath") == fixture.CurrentPath);
            if (!(bool)Get(selected, "DiscardPending")) throw new Exception("Selected physical path was not marked");
            if (mode == "download-failure") fixture.Handler.Fail = true;
            if (mode == "same-title") fixture.SameTitleOtherLoaded = true;
            if (mode == "subsequent-edit") File.WriteAllText(fixture.CurrentPath, "new user edit after discard intent");
            FileStream held = null;
            if (mode == "locked-retry") held = new FileStream(fixture.CurrentPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var blocked = false;
            try { await fixture.Restore(); }
            catch (IOException) { blocked = true; }
            catch (HttpRequestException) { blocked = true; }
            finally { held?.Dispose(); }
            var shouldBlock = mode == "download-failure" || mode == "subsequent-edit" || mode == "locked-retry" || mode == "same-title";
            if (blocked != shouldBlock) throw new Exception("Wrong recovery outcome: " + mode);
            fixture.AssertOldUntouched();
            if (blocked)
            {
                if (!fixture.Records().Where(entry => (string)Get(entry, "SourcePath") == fixture.CurrentPath).All(entry => (bool)Get(entry, "DiscardPending")))
                    throw new Exception("Failed recovery retired its durable intent: " + mode);
                if (mode != "locked-retry" && fixture.Closes != 0) throw new Exception("Preflight failure closed the original editor");
                if (mode == "subsequent-edit" && File.ReadAllText(fixture.CurrentPath) != "new user edit after discard intent") throw new Exception("New edit overwritten");
                if (mode != "locked-retry") { Console.WriteLine("PASS: physical discard " + mode + " preserves working bytes and pending intent"); continue; }
                await fixture.Restore();
            }
            if (File.ReadAllText(fixture.CurrentPath) != DiscardPathFixture.Canonical
                || (File.GetAttributes(fixture.CurrentPath) & FileAttributes.ReadOnly) == 0
                || fixture.Records().Where(entry => (string)Get(entry, "SourcePath") == fixture.CurrentPath).Any(entry => (bool)Get(entry, "DiscardPending")))
                throw new Exception("Actual working bytes/binding/journal were not completed: " + mode);
            if (mode == "clean" && (fixture.Handler.Downloads != 0 || fixture.Closes != 0 || !fixture.MemoryReadOnly))
                throw new Exception("Unchanged discard used download/close instead of protecting the current working file");
            if (mode == "saved-edit" && fixture.Handler.Downloads != 1) throw new Exception("Modified discard did not fetch canonical bytes directly");
            fixture.AssertOldUntouched();
            Console.WriteLine("PASS: physical discard " + mode + "; actual Working restored/read-only; old same-document workspace untouched");
        }
        using (var fixture = new DiscardPathFixture())
        {
            var second = fixture.AddWorkingPart("SECOND.SLDPRT");
            var secondPath=(string)Get(second,"FullPath");
            File.WriteAllText(fixture.CurrentPath,"first selected edit");File.WriteAllText(secondPath,"second selected edit");
            DiscardCall(fixture.Plugin,"MarkDiscardedLocalEdit",fixture.Node);DiscardCall(fixture.Plugin,"MarkDiscardedLocalEdit",second);
            await (Task)DiscardCall(fixture.Plugin,"RestoreDiscardedWorkingPathsAsync",fixture.Manifest,new[]{fixture.CurrentPath,secondPath});
            if(fixture.Handler.Downloads!=2||new[]{fixture.CurrentPath,secondPath}.Any(path=>File.ReadAllText(path)!=DiscardPathFixture.Canonical)
                ||fixture.Records().Where(entry=>new[]{fixture.CurrentPath,secondPath}.Contains((string)Get(entry,"SourcePath"))).Any(entry=>(bool)Get(entry,"DiscardPending")))throw new Exception("Batch discard restored only its first target");
            fixture.AssertOldUntouched();Console.WriteLine("PASS: two selected physical files both restore canonical bytes and complete their journals");
        }
        using (var fixture = new DiscardPathFixture())
        {
            DiscardCall(fixture.Plugin,"MarkDiscardedLocalEdit",fixture.Node);
            DiscardCall(fixture.Plugin,"RetireLocalRecoveryForPlmOpen",fixture.Manifest);
            if(!fixture.Records().Where(entry=>(string)Get(entry,"SourcePath")==fixture.CurrentPath).All(entry=>(bool)Get(entry,"DiscardPending")))throw new Exception("PLM retirement erased unfinished recovery");
            Console.WriteLine("PASS: PLM retirement never clears unfinished discard intent");
        }
        using (var fixture = new DiscardPathFixture())
        {
            File.WriteAllText(fixture.CurrentPath,"discarded edit after the owner window closed");
            DiscardCall(fixture.Plugin,"MarkDiscardedLocalEdit",fixture.Node);fixture.Loaded=false;
            SetField(fixture.Plugin,"currentTree",null);
            await (Task)DiscardCall(fixture.Plugin,"RestoreCurrentDiscardedEditsForOpenAsync",fixture.Manifest);
            if(File.ReadAllText(fixture.CurrentPath)!=DiscardPathFixture.Canonical
                ||fixture.Records().Where(entry=>(string)Get(entry,"SourcePath")==fixture.CurrentPath).Any(entry=>(bool)Get(entry,"DiscardPending")))throw new Exception("A closed owner window made the explicit physical discard intent unreachable");
            fixture.AssertOldUntouched();Console.WriteLine("PASS: latest-open retry resumes the unique explicit pending working path after the tree/window is gone");
        }
        using (var fixture = new DiscardPathFixture())
        {
            DiscardCall(fixture.Plugin,"MarkDiscardedLocalEdit",fixture.Node);
            var other=New("CadTreeNode");Set(other,"DocumentId",Get(fixture.Node,"DocumentId"));Set(other,"FullPath",fixture.OldPath);Set(other,"FileName","PART.SLDPRT");
            DiscardCall(fixture.Plugin,"MarkDiscardedLocalEdit",other);fixture.Loaded=false;SetField(fixture.Plugin,"currentTree",null);
            var blocked=false;try{await (Task)DiscardCall(fixture.Plugin,"RestoreCurrentDiscardedEditsForOpenAsync",fixture.Manifest);}catch(InvalidOperationException error){blocked=error.Message.Contains("多个工作目录");}
            if(!blocked||fixture.Handler.Downloads!=0||!fixture.Records().All(entry=>(bool)Get(entry,"DiscardPending")))throw new Exception("Ambiguous historical pending paths were guessed/restored together");
            Console.WriteLine("PASS: multiple pending physical paths never become a document-wide guessed restoration");
        }
        using (var fixture = new DiscardPathFixture())
        {
            var dependency=fixture.AddWorkingPart("DEPENDENCY.SLDPRT");fixture.ReferenceTargetPath=(string)Get(dependency,"FullPath");
            File.WriteAllText(fixture.CurrentPath,"selected derived-part edit");DiscardCall(fixture.Plugin,"MarkDiscardedLocalEdit",fixture.Node);
            fixture.FailBindingAfterRewrite=true;var blocked=false;
            try{await fixture.Restore();}catch(IOException){blocked=true;}finally{fixture.ResetBindingDirectory();}
            if(!blocked||File.ReadAllText(fixture.CurrentPath)!=DiscardPathFixture.Canonical
                ||!fixture.Records().Where(entry=>(string)Get(entry,"SourcePath")==fixture.CurrentPath).All(entry=>(bool)Get(entry,"DiscardPending")))throw new Exception("Binding failure left untracked rebased bytes or erased intent");
            fixture.FailBindingAfterRewrite=false;await fixture.Restore();
            if(File.ReadAllText(fixture.CurrentPath)!=DiscardPathFixture.Canonical+"|working-reference"
                ||fixture.Records().Where(entry=>(string)Get(entry,"SourcePath")==fixture.CurrentPath).Any(entry=>(bool)Get(entry,"DiscardPending"))
                ||File.ReadAllText(fixture.ReferenceTargetPath)!=DiscardPathFixture.Canonical)throw new Exception("Binding-failure retry did not revalidate actual working references");
            fixture.AssertOldUntouched();Console.WriteLine("PASS: reference-binding failure rolls back canonical bytes, preserves intent, and retries working-reference rewrite");
        }

        using (var fixture = new DiscardPathFixture())
        {
            var blocked=false;
            try { DiscardCall(fixture.Plugin, "MarkDiscardedWorkingPath", fixture.Node, Guid.NewGuid()); }
            catch (IOException) { blocked=true; }
            if (!blocked || fixture.Records().Count != 1) throw new Exception("Wrong project was accepted from a task/sidebar context");
            fixture.AssertOldUntouched();
            Console.WriteLine("PASS: discard project must match the physical file binding, independent of the task bar");
        }
        using (var fixture = new DiscardPathFixture())
        {
            var json=new JavaScriptSerializer();var type=Type("ControlledOpenManifestDto");
            var context=json.Deserialize(json.Serialize(fixture.Manifest),type);Set(context,"ForEdit",true);
            var local=((IList)Get(context,"Files"))[0];File.WriteAllText(fixture.CurrentPath,"previous verified check-in source bytes");
            var sourceHash=(string)Type("EditDraftJournal").GetMethod("Hash",Flags).Invoke(null,new object[]{fixture.CurrentPath});var sourceLength=new FileInfo(fixture.CurrentPath).Length;
            Set(local,"Sha256",sourceHash);Set(local,"FileLength",sourceLength);
            Type("PdmDocumentIdentityStore").GetMethod("TryWriteControlledVersion",Flags).Invoke(null,new[]{(object)fixture.CurrentPath,Get(local,"DocumentId"),Get(context,"ProjectId"),Get(local,"VersionId"),"W1",sourceHash,sourceLength});
            var oldContext=json.Deserialize(json.Serialize(context),type);
            var contexts=(IDictionary)Field(fixture.Plugin,"controlledOpenManifests");contexts.Add(Path.GetDirectoryName(fixture.CurrentPath),context);contexts.Add(Path.GetDirectoryName(fixture.OldPath),oldContext);
            File.WriteAllText(fixture.CurrentPath,"new edit after the previous source-based check-in");DiscardCall(fixture.Plugin,"MarkDiscardedLocalEdit",fixture.Node);await fixture.Restore();
            var canonical=((IList)Get(fixture.Manifest,"Files"))[0];var after=((IList)Get(context,"Files"))[0];var oldAfter=((IList)Get(oldContext,"Files"))[0];
            var matches=(bool)Type("PdmAddin").GetMethod("WorkingReuseBindingMatches",Flags).Invoke(null,new[]{(object)fixture.CurrentPath,Get(context,"ProjectId"),after});
            if(!matches||!Equals(Get(after,"Sha256"),Get(canonical,"Sha256"))||!Equals(Get(after,"FileLength"),Get(canonical,"FileLength"))
                ||(string)Get(oldAfter,"Sha256")!=sourceHash||!Equals(Get(context,"RootVersionId"),Get(canonical,"VersionId")))throw new Exception("Discard restored bytes/binding but left a stale source fingerprint in the same working context");
            fixture.AssertOldUntouched();Console.WriteLine("PASS: source-to-canonical discard updates only the exact physical context baseline for reacquisition");
        }
        using (var fixture = new DiscardPathFixture())
        {
            DiscardCall(fixture.Plugin,"MarkDiscardedLocalEdit",fixture.Node);
            var file=((IList)Get(fixture.Manifest,"Files"))[0];var verifiedHash=(string)Get(file,"Sha256");
            var records=Array.CreateInstance(Type("LocalEditDraft"),1);records.SetValue(fixture.Records().Single(entry=>(string)Get(entry,"SourcePath")==fixture.CurrentPath),0);
            var expectedMetadata=new object[]{fixture.CurrentPath,null};Type("PdmDocumentIdentityStore").GetMethod("TryReadLocalMetadata",Flags).Invoke(null,expectedMetadata);
            File.WriteAllText(fixture.CurrentPath,"later user bytes after verification");var blocked=false;
            try{DiscardCall(fixture.Plugin,"CompleteDiscardBinding",records,file,Get(fixture.Manifest,"ProjectId"),verifiedHash,expectedMetadata[1]);}catch(IOException){blocked=true;}
            var metadata=new object[]{fixture.CurrentPath,null};Type("PdmDocumentIdentityStore").GetMethod("TryReadLocalMetadata",Flags).Invoke(null,metadata);
            if(!blocked||File.ReadAllText(fixture.CurrentPath)!="later user bytes after verification"
                ||!string.IsNullOrEmpty((string)Get(metadata[1],"ResolvedReferenceSha256"))
                ||!fixture.Records().Where(entry=>(string)Get(entry,"SourcePath")==fixture.CurrentPath).All(entry=>(bool)Get(entry,"DiscardPending")))throw new Exception("Completion certified arbitrary later bytes as a restored version");
            Console.WriteLine("PASS: completion requires the upstream verified hash and never certifies later user bytes");
        }
        using (var fixture = new DiscardPathFixture())
        {
            DiscardCall(fixture.Plugin,"MarkDiscardedLocalEdit",fixture.Node);var identity=Type("PdmDocumentIdentityStore");var file=((IList)Get(fixture.Manifest,"Files"))[0];
            var expected=new object[]{fixture.CurrentPath,null};identity.GetMethod("TryReadLocalMetadata",Flags).Invoke(null,expected);
            var records=Array.CreateInstance(Type("LocalEditDraft"),1);records.SetValue(fixture.Records().Single(entry=>(string)Get(entry,"SourcePath")==fixture.CurrentPath),0);
            var otherId=Guid.NewGuid();identity.GetMethod("TryWriteControlledVersion",Flags).Invoke(null,new[]{(object)fixture.CurrentPath,otherId,Guid.NewGuid(),Guid.NewGuid(),"W1",Get(file,"Sha256"),Get(file,"FileLength")});
            var blocked=false;try{DiscardCall(fixture.Plugin,"CompleteDiscardBinding",records,file,Get(fixture.Manifest,"ProjectId"),Get(file,"Sha256"),expected[1]);}catch(IOException){blocked=true;}
            var after=new object[]{fixture.CurrentPath,null};identity.GetMethod("TryReadLocalMetadata",Flags).Invoke(null,after);
            if(!blocked||(Guid)Get(after[1],"DocumentId")!=otherId||!fixture.Records().Where(entry=>(string)Get(entry,"SourcePath")==fixture.CurrentPath).All(entry=>(bool)Get(entry,"DiscardPending")))throw new Exception("Completion overwrote a concurrently changed physical-file binding");
            Console.WriteLine("PASS: completion compare-and-swap preserves a concurrently changed file binding and pending intent");
        }
        DiscardReferenceRestorationScenario();
        await DiscardDirtyReadOnlySiblingScenario();
        var messages=New("PdmAddin");
        foreach(var error in new Exception[]{new HttpRequestException("connection reset"),new IOException("access denied"),new UnauthorizedAccessException("denied"),new Exception("unexpected failure")})
        {
            var message=(string)DiscardCall(messages,"DiscardRecoveryError",error);
            if(message.Contains("整体存档")||message.Contains(error.Message)||!message.Any(character=>character>='\u3400'&&character<='\u9fff'))throw new Exception("Discard guidance exposed technical English or suggested resubmitting discarded edits");
        }
        Console.WriteLine("PASS: discard failures use Chinese recovery guidance and never request whole-assembly rearchive");
    }

    static object DiscardCall(object target, string name, params object[] arguments)
    {
        try { return target.GetType().GetMethod(name, Flags).Invoke(target, arguments); }
        catch (TargetInvocationException error) { throw error.InnerException ?? error; }
    }

    sealed class DiscardPathFixture : IDisposable
    {
        public const string Canonical="canonical archived bytes";
        public readonly object Plugin, Node, Manifest, Journal, Client;
        public readonly DiscardDownloadHandler Handler=new DiscardDownloadHandler();
        public readonly string DirectoryPath,CurrentPath,OldPath;
        public bool Loaded=true,MemoryReadOnly,SameTitleOtherLoaded;
        public string ReferenceTargetPath; public bool FailBindingAfterRewrite;
        public int Closes;
        readonly string oldJournalPath,oldJournalText;
        readonly FieldInfo bindings,rootResolver,isolatedScope;
        readonly object oldBindings,oldRoot,oldScope;
        readonly CancellationTokenSource lifetime=new CancellationTokenSource();
        public DiscardPathFixture(bool separateHistoricalSession = false)
        {
            DirectoryPath=Path.Combine(Path.GetTempPath(),"UPLM-discard-path-test",Guid.NewGuid().ToString("N"));
            CurrentPath=Path.Combine(DirectoryPath,"View","current","PART.SLDPRT");OldPath=Path.Combine(DirectoryPath,"View","old","PART.SLDPRT");
            Directory.CreateDirectory(Path.GetDirectoryName(CurrentPath));Directory.CreateDirectory(Path.GetDirectoryName(OldPath));
            File.WriteAllText(CurrentPath,Canonical);File.WriteAllText(OldPath,"untouched historical user modification");
            var identity=Type("PdmDocumentIdentityStore");bindings=identity.GetField("bindingDirectory",Flags);rootResolver=identity.GetField("workspaceRootResolver",Flags);isolatedScope=identity.GetField("isolatedReadScope",Flags);
            oldBindings=bindings.GetValue(null);oldRoot=rootResolver.GetValue(null);oldScope=isolatedScope.GetValue(null);
            bindings.SetValue(null,Path.Combine(DirectoryPath,"bindings"));rootResolver.SetValue(null,new Func<string>(()=>DirectoryPath));isolatedScope.SetValue(null,true);
            Plugin=New("PdmAddin");SetField(Plugin,"lifetime",lifetime);SetField(Plugin,"authenticatedUsername","tester");SetField(Plugin,"currentProjectId",Guid.NewGuid());
            var id=Guid.NewGuid();var project=Guid.NewGuid();var version=Guid.NewGuid();
            var hash=(string)Type("EditDraftJournal").GetMethod("Hash",Flags).Invoke(null,new object[]{CurrentPath});
            foreach(var path in new[]{CurrentPath,OldPath})identity.GetMethod("TryWriteControlledVersion",Flags).Invoke(null,new object[]{path,id,project,version,"W1",hash,(long)System.Text.Encoding.UTF8.GetByteCount(Canonical)});
            Node=New("CadTreeNode");Set(Node,"DocumentId",id);Set(Node,"FullPath",CurrentPath);Set(Node,"FileName","PART.SLDPRT");Set(Node,"Kind",Enum.Parse(Property(Node,"Kind").PropertyType,"Part"));Set(Node,"CheckedOutBy","tester");Set(Node,"CheckoutSessionId",Field(Plugin,"checkoutSessionId"));
            Manifest=New("ControlledOpenManifestDto");Set(Manifest,"ProjectId",project);Set(Manifest,"RootDocumentId",id);Set(Manifest,"RootVersionId",version);Set(Manifest,"RootRelativePath","PART.SLDPRT");
            var file=New("ControlledOpenFileDto");Set(file,"DocumentId",id);Set(file,"VersionId",version);Set(file,"FileName","PART.SLDPRT");Set(file,"RelativePath","PART.SLDPRT");Set(file,"Revision","W1");Set(file,"Sha256",hash);Set(file,"FileLength",(long)System.Text.Encoding.UTF8.GetByteCount(Canonical));
            var files=(IList)Activator.CreateInstance(Property(Manifest,"Files").PropertyType);files.Add(file);Set(Manifest,"Files",files);
            Client=New("PdmApiClient","http://discard-physical.invalid/");((HttpClient)Field(Client,"httpClient")).Dispose();
            var http=new HttpClient(Handler){BaseAddress=new Uri("http://discard-physical.invalid/")};http.DefaultRequestHeaders.Authorization=new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer","fixture");SetField(Client,"httpClient",http);SetField(Plugin,"apiClient",Client);
            Journal=New("EditDraftJournal",Path.Combine(DirectoryPath,"journal"),"fixture");SetField(Plugin,"editJournal",Journal);SetField(Plugin,"editJournalScope",(string)Get(Client,"EditJournalScope")+"|tester");
            var old=New("LocalEditDraft");Set(old,"DocumentId",id);Set(old,"ProjectId",project);Set(old,"SessionId",separateHistoricalSession ? Guid.NewGuid() : Field(Plugin,"checkoutSessionId"));Set(old,"SourcePath",OldPath);Set(old,"BaseVersionId",version);Set(old,"BaseSha256",hash);Set(old,"RestoreOnOpen",true);
            DiscardCall(Journal,"Save",old);oldJournalPath=Directory.GetFiles(Path.Combine(DirectoryPath,"journal"),"*.xml",SearchOption.AllDirectories).Single();oldJournalText=File.ReadAllText(oldJournalPath);
            ((IDictionary)Field(Plugin,"localEdits")).Add(id,old);
            var appType=Plugin.GetType().GetField("application",Flags).FieldType;
            var model=new Proxy(appType.Assembly.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true),call=>{
                switch(call.MethodName){case "get_Visible":return false;case "GetPathName":return CurrentPath;case "GetTitle":return "PART.SLDPRT";case "GetType":return 1;case "GetSaveFlag":return false;case "IsOpenedReadOnly":return MemoryReadOnly;case "SetReadOnlyState":MemoryReadOnly=(bool)call.Args[0];return true;default:throw new Exception("Unexpected model operation: "+call.MethodName);}
            }).GetTransparentProxy();
            var other=new Proxy(appType.Assembly.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true),call=>{
                switch(call.MethodName){case "GetPathName":return OldPath;case "GetTitle":return "PART.SLDPRT";case "GetType":return 1;case "GetSaveFlag":return false;case "IsOpenedReadOnly":return true;default:throw new Exception("Unselected duplicate-title document must not change: "+call.MethodName);}
            }).GetTransparentProxy();
            SetField(Plugin,"application",new Proxy(appType,call=>{
                if(call.MethodName=="GetDocuments")return Loaded?(SameTitleOtherLoaded?new[]{model,other}:new[]{model}):Array.Empty<object>();
                if(call.MethodName=="get_ActiveDoc")return null;
                if(call.MethodName=="CloseDoc"){Loaded=false;Closes++;return null;}
                if(call.MethodName=="GetDocumentDependencies2")
                {
                    if(ReferenceTargetPath==null||Path.GetFileName((string)call.Args[0])!="PART.SLDPRT")return Array.Empty<string>();
                    var owner=(string)call.Args[0];var rewritten=owner==CurrentPath&&File.ReadAllText(owner).EndsWith("|working-reference");
                    return new[]{Path.GetFileName(ReferenceTargetPath),rewritten?ReferenceTargetPath:Path.Combine(DirectoryPath,"Snapshots",Path.GetFileName(ReferenceTargetPath))};
                }
                if(call.MethodName=="ReplaceReferencedDocument")
                {
                    if((string)call.Args[0]!=CurrentPath||(string)call.Args[2]!=ReferenceTargetPath)throw new Exception("Reference rewrite touched another file");
                    File.AppendAllText(CurrentPath,"|working-reference");
                    if(FailBindingAfterRewrite){var blocker=Path.Combine(DirectoryPath,"binding-blocker");File.WriteAllText(blocker,"not a directory");bindings.SetValue(null,blocker);}
                    return true;
                }
                throw new Exception("Unexpected CAD operation: "+call.MethodName);
            }).GetTransparentProxy());
        }
        public List<object> Records()=>((IEnumerable)DiscardCall(Journal,"ReadAll")).Cast<object>().ToList();
        public void ResetBindingDirectory()=>bindings.SetValue(null,Path.Combine(DirectoryPath,"bindings"));
        public object AddWorkingPart(string name)
        {
            var path=Path.Combine(Path.GetDirectoryName(CurrentPath),name);File.WriteAllText(path,Canonical);var id=Guid.NewGuid();var version=Guid.NewGuid();var project=(Guid)Get(Manifest,"ProjectId");
            var files=(IList)Get(Manifest,"Files");var baseline=files[0];var file=New("ControlledOpenFileDto");
            Set(file,"DocumentId",id);Set(file,"VersionId",version);Set(file,"FileName",name);Set(file,"RelativePath",name);Set(file,"Revision","W1");Set(file,"Sha256",Get(baseline,"Sha256"));Set(file,"FileLength",Get(baseline,"FileLength"));files.Add(file);
            Type("PdmDocumentIdentityStore").GetMethod("TryWriteControlledVersion",Flags).Invoke(null,new[]{(object)path,id,project,version,"W1",Get(file,"Sha256"),Get(file,"FileLength")});
            var node=New("CadTreeNode");Set(node,"DocumentId",id);Set(node,"FullPath",path);Set(node,"FileName",name);Set(node,"Kind",Enum.Parse(Property(node,"Kind").PropertyType,"Part"));return node;
        }
        public Task Restore()=>(Task)DiscardCall(Plugin,"RestoreDiscardedWorkingPathsAsync",Manifest,new[]{CurrentPath});
        public void AssertOldUntouched(){if(File.ReadAllText(OldPath)!="untouched historical user modification"||File.ReadAllText(oldJournalPath)!=oldJournalText)throw new Exception("Discard touched another same-document physical workspace/journal");}
        public void Dispose(){bindings.SetValue(null,oldBindings);rootResolver.SetValue(null,oldRoot);isolatedScope.SetValue(null,oldScope);((IDisposable)Client).Dispose();lifetime.Dispose();File.SetAttributes(CurrentPath,FileAttributes.Normal);}
    }

    sealed class DiscardDownloadHandler : HttpMessageHandler
    {
        public bool Fail;public int Downloads,LimitedVersionReads;public object OperationManifest;public Action OnOperation;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            if(OperationManifest!=null&&!request.RequestUri.AbsolutePath.EndsWith("/file"))
            {
                var path=request.RequestUri.AbsolutePath;object value;OnOperation?.Invoke();
                if(path.EndsWith("/versions")){if(request.RequestUri.Query=="?take=1")LimitedVersionReads++;value=new[]{new{id=Get(OperationManifest,"RootVersionId")}};}
                else if(path.EndsWith("/discard-checkout"))value=new{id=Guid.Parse(path.Split('/')[3]),projectId=Get(OperationManifest,"ProjectId")};
                else if(path.EndsWith("/open-manifest"))value=OperationManifest;
                else throw new Exception("Unexpected discard operation request "+path);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(new JavaScriptSerializer().Serialize(value))});
            }
            if(request.Method!=HttpMethod.Get||!request.RequestUri.AbsolutePath.EndsWith("/file"))throw new Exception("Unexpected business write/request");
            Downloads++;if(Fail)throw new HttpRequestException("模拟下载失败");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(DiscardPathFixture.Canonical)});
        }
    }

    static void DiscardReferenceRestorationScenario()
    {
        var directory=Path.Combine(Path.GetTempPath(),"UPLM-discard-reference-test",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        var canonical=Path.Combine(directory,"canonical.SLDDRW");var target=Path.Combine(directory,"working.SLDDRW");var untouched=Path.Combine(directory,"PART.SLDPRT");
        File.WriteAllText(canonical,"canonical drawing");File.WriteAllText(untouched,"unselected local part edit");
        foreach(var mode in new[]{"success","replace-failure","later-edit"})
        {
            var fail=mode=="replace-failure";
            File.Copy(canonical,target,true);var plugin=New("PdmAddin");var appType=plugin.GetType().GetField("application",Flags).FieldType;var replaced=0;
            var oldPath=Path.Combine(directory,"Snapshots","PART.SLDPRT");
            SetField(plugin,"application",new Proxy(appType,call=>{
                if(call.MethodName=="ReplaceReferencedDocument"){if((string)call.Args[0]!=target||(string)call.Args[1]!=oldPath||(string)call.Args[2]!=untouched)throw new Exception("Wrong reference rewrite scope");replaced++;File.AppendAllText(target,"|working reference");return !fail;}
                if(call.MethodName=="GetDocumentDependencies2"){if(mode=="later-edit")File.WriteAllText(target,"later external user edit");return new[]{"PART.SLDPRT",untouched};}
                throw new Exception("Unexpected native operation: "+call.MethodName);
            }).GetTransparentProxy());
            var canonicalHash=(string)Type("EditDraftJournal").GetMethod("Hash",Flags).Invoke(null,new object[]{canonical});
            var blocked=false;try{DiscardCall(plugin,"RestoreDiscardWorkingReferences",target,canonical,new Dictionary<string,string>{{oldPath,untouched}},canonicalHash);}catch(IOException){blocked=true;}
            var expected=mode=="later-edit"?"later external user edit":fail?"canonical drawing":"canonical drawing|working reference";
            if(blocked!=(mode!="success")||replaced!=1||File.ReadAllText(untouched)!="unselected local part edit"||File.ReadAllText(target)!=expected)throw new Exception("Discard reference recovery failed its scope/rollback guarantee");
            File.SetAttributes(target,FileAttributes.Normal);
            Console.WriteLine("PASS: selected-file working-reference "+(mode=="later-edit"?"preserves unknown later edits instead of rollback":fail?"failure rolls back canonical bytes":"rewrite verifies actual working destination")+"; unselected file unchanged");
        }
    }

    static async Task DiscardDirtyReadOnlySiblingScenario()
    {
        using(var fixture=new DiscardPathFixture())
        {
            File.WriteAllText(fixture.CurrentPath,"selected edit to discard");DiscardCall(fixture.Plugin,"MarkDiscardedLocalEdit",fixture.Node);
            var sibling=fixture.AddWorkingPart("DIRTY-SIBLING.SLDPRT");var siblingPath=(string)Get(sibling,"FullPath");File.SetAttributes(siblingPath,FileAttributes.ReadOnly);
            var ownerPath=Path.Combine(Path.GetDirectoryName(fixture.CurrentPath),"OWNER.SLDASM");File.WriteAllText(ownerPath,"unchanged parent assembly");
            var appType=fixture.Plugin.GetType().GetField("application",Flags).FieldType;var interop=appType.Assembly;var modelType=interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true);
            object Part(string path,bool dirty)=>new Proxy(modelType,call=>{
                switch(call.MethodName){case "GetPathName":return path;case "GetTitle":return Path.GetFileName(path);case "GetType":return 1;case "GetSaveFlag":return dirty;case "IsOpenedReadOnly":return dirty;default:throw new Exception("Part memory must remain intact: "+call.MethodName);}
            }).GetTransparentProxy();
            var a=Part(fixture.CurrentPath,false);var b=Part(siblingPath,true);
            object Component(object model,string path)=>new Proxy(interop.GetType("SolidWorks.Interop.sldworks.Component2",true),call=>call.MethodName=="GetModelDoc2"?model:call.MethodName=="GetPathName"?path:throw new Exception(call.MethodName)).GetTransparentProxy();
            var owner=new DiscardAssemblyProxy(modelType,call=>{
                switch(call.MethodName){case "GetPathName":return ownerPath;case "GetTitle":return "OWNER.SLDASM";case "GetType":return 2;case "GetSaveFlag":return false;case "IsOpenedReadOnly":return true;case "GetComponents":return new[]{Component(a,fixture.CurrentPath),Component(b,siblingPath)};default:throw new Exception("Owner must not change: "+call.MethodName);}
            }).GetTransparentProxy();
            var closes=0;SetField(fixture.Plugin,"application",new Proxy(appType,call=>{
                if(call.MethodName=="GetDocuments")return new[]{owner,a,b};if(call.MethodName=="CloseDoc"){closes++;return null;}throw new Exception("No native mutation expected: "+call.MethodName);
            }).GetTransparentProxy());
            var blocked=false;try{await fixture.Restore();}catch(InvalidOperationException error){blocked=error.Message.Contains("DIRTY-SIBLING");}
            if(!blocked||closes!=0||fixture.Handler.Downloads!=0||File.ReadAllText(fixture.CurrentPath)!="selected edit to discard"
                ||!fixture.Records().Where(entry=>(string)Get(entry,"SourcePath")==fixture.CurrentPath).All(entry=>(bool)Get(entry,"DiscardPending")))throw new Exception("Readonly dirty sibling could be unloaded without preserving its memory");
            fixture.AssertOldUntouched();Console.WriteLine("PASS: unselected readonly dirty sibling blocks parent closure before download or native mutation");
        }
    }

    sealed class DiscardAssemblyProxy:RealProxy,IRemotingTypeInfo
    {
        readonly System.Type modelType;readonly Func<IMethodCallMessage,object> callback;
        public DiscardAssemblyProxy(System.Type modelType,Func<IMethodCallMessage,object> callback):base(modelType){this.modelType=modelType;this.callback=callback;}
        public string TypeName{get;set;}
        public bool CanCastTo(System.Type type,object value)=>type==typeof(object)||type.Namespace=="SolidWorks.Interop.sldworks"&&new[]{"ModelDoc2","IModelDoc2","AssemblyDoc","IAssemblyDoc"}.Contains(type.Name);
        public override IMessage Invoke(IMessage message){var call=(IMethodCallMessage)message;try{var value=call.MethodName=="GetType"&&(call.MethodBase as MethodInfo)?.ReturnType==typeof(System.Type)?modelType:callback(call);return new ReturnMessage(value,call.Args,call.ArgCount,call.LogicalCallContext,call);}catch(Exception error){return new ReturnMessage(error,call);}}
    }
}
