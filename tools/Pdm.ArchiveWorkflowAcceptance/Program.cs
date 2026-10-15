using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

internal static class Program
{
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    static Assembly assembly;
    static string directory;
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    static int passed;
    static int Main(string[] args)
    {
        directory = Path.GetFullPath(args[1]); Directory.CreateDirectory(directory);
        var dll = Path.GetFullPath(args[0]);
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) => { var path = Path.Combine(Path.GetDirectoryName(dll), new AssemblyName(e.Name).Name + ".dll"); return File.Exists(path) ? Assembly.LoadFrom(path) : null; };
        assembly = Assembly.LoadFrom(dll);
        if(args.Length>2 && args[2]=="assembly-facts")
        {
            foreach(var name in new[]{"CheckInBatchAsync","PrepareBatchCheckInPermissionAsync","ResolveBatchReferenceVersionsAsync"})
            {
                var method=T("PdmAddin").GetMethod(name,Flags);
                var state=((System.Runtime.CompilerServices.AsyncStateMachineAttribute)method.GetCustomAttributes(typeof(System.Runtime.CompilerServices.AsyncStateMachineAttribute),false).Single()).StateMachineType;
                Console.WriteLine(name+" parameters="+method.GetParameters().Length+" state="+state.Name);
                var body=state.GetMethod("MoveNext",Flags).GetMethodBody().GetILAsByteArray();
                for(var i=0;i<body.Length-4;i++)if(body[i]==0x72){try{var text=state.Module.ResolveString(BitConverter.ToInt32(body,i+1));if(text.Contains("未成功")||text.Contains("批次")||text.Contains("尚未产生"))Console.WriteLine(text);}catch(ArgumentException){}}
            }
            return 0;
        }
        T("ArchiveUploadJournal").GetField("directory", Flags).SetValue(null, Path.Combine(directory, "journals"));
        T("ArchiveBatchJournal").GetField("directory", Flags).SetValue(null, Path.Combine(directory, "batches"));
        T("PdmDocumentIdentityStore").GetField("bindingDirectory", Flags).SetValue(null, Path.Combine(directory, "bindings"));
        T("PdmDocumentIdentityStore").GetField("workspaceRootResolver", Flags).SetValue(null, new Func<string>(() => Path.Combine(directory, "workspace")));
        try
        {
            if (args.Length > 2 && args[2] == "cycle-only") { Run("模型工程图双向引用不递归溢出", ReferenceCycle); return 0; }
            Run("模型工程图双向引用不递归溢出", ReferenceCycle);
            Run("日志限量、并发记录、诊断汇总与凭据脱敏", Diagnostics);
            Run("上传分块中断后只续传剩余块", () => Upload(false));
            Run("完成响应丢失后复用同一上传目标", () => Upload(true));
            Run("整批上传绑定服务器分配路径，批次记录只在结果确认后清理", BatchUploadOwnership);
            Run("重复刷新复用已确认历史，版本号或数量变化立即失效", VersionHistoryCache);
            Run("来源文件修改不伪装成原样副本", CopyHashes);
            Run("首次存档重试沿用登记编号，版本、用户、电脑和项目变化仍拦截", PendingFirstArchive);
            Run("首次存档读取多层压缩结构，保留实例与压缩状态并拒绝错误配置和循环", LocalSuppressedStructure);
            Run("服务端预检使用实际属性，不再发送空属性表", PreflightProperties);
            Run("多配置同文件实例在保留配置与数量的同时同步图档身份，冲突编号仍拦截", MultiConfigurationIdentity);
            Run("整批提交响应丢失后复用同一批次，已提交回执可恢复成功", AtomicArchiveRetry);
            Run("上传完成期间源文件变化时阻止版本登记", UploadSourceChanged);
            Run("本机原目录基准优先于界面最新版本", Baseline);
            Run("内容相同的工作版发布后可继续存档，真实差异仍拦截", ReleasedBaseline);
            Run("同字节但结构不同不能推断最新基准", UnknownBaseline);
            Run("两个根装配体使用独立工作区", RootDirectories);
            Run("只读打开保护总装及全部引用，并保留Working本地修改", ReadOnlyWorkspace);
            Run("工程图模型引用变化参与升版判断", DrawingReferences);
            Run("已纳入树的子装配体不自动成为独立根", RootAdmission);
            Run("分散导入缓存允许存档，跨项目与未知受控身份仍阻止", ExternalImport);
            Run("压缩零件保留引用，缺失文件及未展开子装配体阻止漏传", SuppressedReferences);
            Run("已存档压缩子装配体沿用准确版本及内部结构，修改和配置冲突仍拦截", ArchivedSuppressedAssembly);
            Run("跨子项目已存档同名零件按当前项目文件内容复用并锁定引用版本", SubprojectReference);
            Run("多层压缩结构的重复实例在权限准备后重新匹配存档身份", NestedReferenceIdentity);
            Run("身份不完整的历史压缩结构不能复用，未登记引用在本地预检拦截", IncompleteArchivedReferences);
            Run("选中模型自动纳入其异名、异目录及多模型工程图，排除无关图纸", SelectedModelDrawings);
            Run("工程图内的独立模型引用同步身份和准确版本，双向关系不会递归溢出", NestedDrawingReferenceIdentity);
            Run("失败登记不写永久身份，旧空登记可重新准备，历史和跨项目身份保持", PendingRegistrationIdentity);
            Run("预检与提交复用固定快照，多配置及工程图引用不能被异步刷新改写", FrozenArchiveSnapshot);
            Run("计划内缺失身份或版本也必须在上传前拦截", IncompletePlannedSnapshot);
            Run("中断批次记录隔离服务器与账号，存活进程不被误清理", InterruptedBatchJournal);
            Run("重启后核对已提交、未提交、并发完成和服务器不可达的批次", InterruptedBatchRecovery);
            if (args.Length > 2) Run("实际P700012发布版本与工作区编辑基准兼容", () => RealReleasedBaseline(args[2]));
            Console.WriteLine("PASS " + passed + " archive scenarios (protocol and COM-independent simulation)");
            return 0;
        }
        catch (Exception error) { Console.WriteLine("FAIL " + Unwrap(error)); return 1; }
    }
    static void Run(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
    static void Upload(bool lostCompletion)
    {
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".SLDPRT"); File.WriteAllText(path, "abcdefghij");
        var project = Guid.NewGuid(); var document = Guid.NewGuid();
        using (var handler = new UploadHandler(project, Hash(path), lostCompletion))
        using (var client = (IDisposable)Client(handler))
        {
            Action attempt = () => Await(Call(client, "UploadVersionFileAsync", project, path, document, path, CancellationToken.None, null, null,
                Hash(path), null, null));
            Expect<InvalidOperationException>(attempt); attempt();
            Assert(handler.Starts == 1, "created another upload session");
            Assert(handler.Chunks[0] == 1, "reuploaded acknowledged chunk zero");
            Assert(handler.StoredTargets.Distinct().Count() == 1, "completion target changed");
            Assert(handler.Received == 10, "missing bytes");
        }
    }
    static void UploadSourceChanged()
    {
        foreach (var exact in new[] { false, true })
        {
            var project = Guid.NewGuid(); var path = Path.Combine(directory, "race-" + exact + ".SLDDRW");
            var copy = path + ".upload"; File.WriteAllText(path, "0123456789"); File.WriteAllText(copy, exact ? "0123456789" : "9876543210");
            var baseline = Hash(path);
            using (var handler = new UploadHandler(project, Hash(copy), false))
            using (var client = (IDisposable)New("PdmApiClient", "http://fixture.invalid/"))
            {
                handler.OnComplete = () => File.WriteAllText(path, "later edit");
                T("PdmApiClient").GetField("httpClient", Flags).SetValue(client, new HttpClient(handler) { BaseAddress = new Uri("http://fixture.invalid/") });
                Expect<IOException>(() => Await(Call(client, "UploadVersionFileAsync", project, copy, Guid.NewGuid(), path, CancellationToken.None, null, exact ? baseline : null, baseline, null, null)));
                Assert(handler.Received == 10 && handler.StoredTargets.Count == 1 && File.ReadAllText(path) == "later edit", "upload race did not preserve and detect late edit");
            }
        }
    }
    static void BatchUploadOwnership()
    {
        var project = Guid.NewGuid(); var document = Guid.NewGuid(); var batch = Guid.NewGuid(); var checkout = Guid.NewGuid();
        var source = Path.Combine(directory, "owned.SLDPRT"); File.WriteAllText(source, "abcdefghij");
        var serverTarget = ".versions/" + document.ToString("N") + "/server-assigned/owned.SLDPRT";
        using (var handler = new UploadHandler(project, Hash(source), true) { TargetRelativePath = serverTarget })
        using (var client = (IDisposable)Client(handler))
        {
            Action attempt = () => Await(Call(client, "UploadVersionFileAsync", project, source, document, source,
                CancellationToken.None, null, null, Hash(source), batch, checkout));
            Expect<InvalidOperationException>(attempt); attempt();
            Assert(handler.Starts == 1 && handler.StoredTargets.All(target => target == serverTarget), "owned upload restarted or ignored server target");
            Assert((string)handler.StartBody["batchId"] == batch.ToString() && (string)handler.StartBody["documentId"] == document.ToString()
                && (string)handler.StartBody["checkoutSessionId"] == checkout.ToString(), "upload owner missing");
            var records = Directory.GetFiles(Path.Combine(directory, "journals"), "*." + batch.ToString("N") + ".json");
            Assert(records.Length == 1, "receipt recovery record missing before commit");
            Static("ArchiveBatchJournal", "Complete", batch);
            Assert(!File.Exists(records[0]) && File.ReadAllText(source) == "abcdefghij", "batch cleanup did not preserve source");
        }
    }
    static void VersionHistoryCache()
    {
        var document = Guid.NewGuid(); var requests = 0; var revision = "W1";
        using (var client = (IDisposable)Client(new JsonHandler(request =>
        {
            Assert(request.RequestUri.AbsolutePath.EndsWith("/versions"), "unexpected cache request"); requests++;
            return new[] { new { id = Guid.NewGuid(), documentId = document, revision = new { display = revision } } };
        })))
        {
            var plugin = New("PdmAddin"); T("PdmAddin").GetField("apiClient", Flags).SetValue(plugin, client);
            var metadata = New("DocumentDto"); Set(metadata, "Id", document); Set(metadata, "StoredVersionCount", 1);
            var label = New("RevisionDto"); Set(label, "Display", "W1"); Set(metadata, "Revision", label);
            var documents = Array.CreateInstance(T("DocumentDto"), 1); documents.SetValue(metadata, 0);
            Await(Call(plugin, "GetTreeVersionsAsync", document, CancellationToken.None)); Thread.Sleep(2200);
            for (var i = 0; i < 100; i++)
            { Call(plugin, "InvalidateChangedVersionHistories", documents); Await(Call(plugin, "GetTreeVersionsAsync", document, CancellationToken.None)); }
            Assert(requests == 1, "unchanged tree repeatedly fetched history");
            revision = "W2"; Set(label, "Display", revision); Call(plugin, "InvalidateChangedVersionHistories", documents);
            Await(Call(plugin, "GetTreeVersionsAsync", document, CancellationToken.None));
            Assert(requests == 2, "changed revision reused stale history");
            Set(metadata, "StoredVersionCount", 2); Call(plugin, "InvalidateChangedVersionHistories", documents);
            Await(Call(plugin, "GetTreeVersionsAsync", document, CancellationToken.None));
            Assert(requests == 3, "changed version count reused stale history");
        }
    }
    static void CopyHashes()
    {
        var source = Path.Combine(directory, "original.SLDPRT"); var copy = source + ".copy";
        File.WriteAllText(source, "source geometry"); File.WriteAllText(copy, "updated drawing bytes");
        Expect<IOException>(() => Static("CheckInUploadHashes", "Read", copy, source, Hash(source), CancellationToken.None, null));
        var result = Static("CheckInUploadHashes", "Read", copy, source, null, CancellationToken.None, Hash(source));
        Assert((string)Get(result, "Source") == Hash(source) && (string)Get(result, "Upload") == Hash(copy), "source/upload hashes lost");
    }
    static void PendingFirstArchive()
    {
        var project = Guid.NewGuid(); var id = Guid.NewGuid(); var path = Path.Combine(directory, "pending.SLDASM");
        File.WriteAllText(path, "file changed after failed first archive");
        foreach (var difference in new[] { "none", "versions", "user", "machine", "project", "kind", "state" })
        {
            var node = Node("pending.SLDASM", id, 0); Set(node, "DocumentId", null);
            var document = New("DocumentDto"); Set(document, "Id", id); Set(document, "ProjectId", difference == "project" ? Guid.NewGuid() : project);
            Set(document, "FileName", "pending.SLDASM"); Set(document, "Kind", difference == "kind" ? 1 : 0);
            Set(document, "State", difference == "state" ? 2 : 0);
            Set(document, "CheckedOutBy", difference == "user" ? "other" : "engineer");
            Set(document, "CheckoutMachine", difference == "machine" ? "OTHER-PC" : "QA-PC");
            var versions = Array.CreateInstance(T("DocumentVersionDto"), difference == "versions" ? 1 : 0);
            if (versions.Length > 0) versions.SetValue(New("DocumentVersionDto"), 0);
            using (var handler = new JsonHandler(_ => versions))
            using (var client = (IDisposable)Client(handler))
            {
                var addin = New("PdmAddin"); T("PdmAddin").GetField("apiClient", Flags).SetValue(addin, client);
                T("PdmAddin").GetField("authenticatedUsername", Flags).SetValue(addin, "engineer");
                T("PdmAddin").GetField("checkoutMachineName", Flags).SetValue(addin, "QA-PC");
                Action retry = () => Await(Call(addin, "RestorePendingFirstArchiveAsync", node, document, project, CancellationToken.None));
                if (difference == "none")
                {
                    retry(); Assert((Guid?)Get(node, "DocumentId") == id, "pending identity replaced");
                    Assert((Guid?)Get(Static("PdmDocumentIdentityStore", "ReadBinding", path), "ProjectId") == project, "pending binding not restored");
                }
                else { Expect<InvalidOperationException>(retry); Assert(Get(node, "DocumentId") == null, "unsafe pending identity assigned"); }
            }
        }
    }
    static Func<object, object> localStructureReader;
    static void AtomicArchiveRetry()
    {
        foreach (var mode in new[] { "retry", "receipt", "partial" })
        {
            var batch = Guid.NewGuid(); var posts = 0;
            using (var handler = new AtomicReceiptHandler(batch, mode))
            using (var client = (IDisposable)Client(handler))
            {
                var files = Enumerable.Range(0, 6).Select(_ => (object)new { documentId = Guid.NewGuid() }).ToArray();
                if (mode == "partial")
                {
                    try { Await(Call(client, "CommitArchiveBatchAsync", batch, Guid.NewGuid(), files, CancellationToken.None)); throw new Exception("six-file submission accepted a four-file receipt"); }
                    catch (Exception error) when (Unwrap(error) is InvalidOperationException)
                    { Assert(!(Unwrap(error).Data["ArchiveBatchNotCommitted"] is bool known && known), "incomplete receipt incorrectly treated as definitely uncommitted"); }
                    continue;
                }
                var result = Await(Call(client, "CommitArchiveBatchAsync", batch, Guid.NewGuid(), files, CancellationToken.None));
                Assert((Guid)Get(result, "BatchId") == batch, "batch identity changed");
                Assert(((IList)Get(result, "Results")).Count == 6, "six-file receipt incomplete");
                posts = handler.Posts;
                Assert(posts == (mode == "retry" ? 2 : 1), "unexpected retry count");
            }
        }
    }
    sealed class AtomicReceiptHandler : HttpMessageHandler
    {
        readonly Guid batch; readonly string mode; public int Posts; Guid[] documents;
        public AtomicReceiptHandler(Guid batch, string mode) { this.batch = batch; this.mode = mode; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method == HttpMethod.Post)
            {
                Posts++; var body = Json.Deserialize<Dictionary<string, object>>(request.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                documents = ((IList)body["files"]).Cast<Dictionary<string, object>>().Select(file => Guid.Parse((string)file["documentId"])).ToArray();
                Assert(Guid.Parse((string)body["batchId"]) == batch, "retry used another batch");
                if (Posts == 1 && mode == "retry") throw new HttpRequestException("simulated response loss");
                if (mode == "receipt") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("{\"message\":\"响应发送失败\"}") });
            }
            else Assert(request.RequestUri.AbsolutePath.EndsWith(batch.ToString()), "wrong receipt query");
            return Task.FromResult(Reply(new { batchId = batch, results = documents.Take(mode == "partial" ? 4 : documents.Length).Select(id => new { document = new { id } }).ToArray() }));
        }
    }
    static void MultiConfigurationIdentity()
    {
        var source = Node("MULTI.SLDPRT", Guid.NewGuid(), 1); Set(source, "CurrentRevision", "W1"); Set(source, "Revision", "W1");
        Set(source, "Configuration", "D13-V4-T4"); Set(source, "InstancePath", "ROOT/PART-1"); Set(source, "Quantity", 1);
        var repeated = Node("MULTI.SLDPRT", Guid.NewGuid(), 1); Set(repeated, "DocumentId", null); Set(repeated, "Configuration", "D14-V6-T8");
        Set(repeated, "InstancePath", "ROOT/PART-2"); Set(repeated, "Quantity", 2);
        var heldRoot = Node("HELD.SLDASM", Guid.NewGuid(), 0); ((IList)Get(heldRoot, "Children")).Add(source); ((IList)Get(heldRoot, "Children")).Add(repeated);
        var items = Array.CreateInstance(T("BatchOperationItem"), 1); items.SetValue(New("BatchOperationItem", heldRoot, 0), 0);
        Static("PdmAddin", "SynchronizeBatchFileIdentities", items);
        Assert((Guid?)Get(repeated, "DocumentId") == (Guid?)Get(source, "DocumentId") && (string)Get(repeated, "CurrentRevision") == "W1", "repeated identity not propagated on held tree");
        Assert((string)Get(repeated, "Configuration") == "D14-V6-T8" && (int)Get(repeated, "Quantity") == 2 && (string)Get(repeated, "InstancePath") == "ROOT/PART-2", "configuration/quantity/instance merged");
        var request = Static("PdmApiClient", "ToRequestNode", heldRoot, true, null);
        var body = Json.Deserialize<Dictionary<string, object>>(Json.Serialize(request)); var children = (IList)body["children"];
        Assert(children.Count == 2 && ((Dictionary<string, object>)children[0])["documentId"].Equals(((Dictionary<string, object>)children[1])["documentId"]), "serialized multi configuration identity differs");
        Set(repeated, "DocumentId", Guid.NewGuid()); Expect<InvalidOperationException>(() => Static("PdmAddin", "SynchronizeBatchFileIdentities", items));
    }
    static void PreflightProperties()
    {
        var node = Node("inspected.SLDPRT", Guid.NewGuid(), 1); var path = (string)Get(node, "FullPath"); File.WriteAllText(path, "native file");
        Set(node, "Revision", "W1");
        var items = Array.CreateInstance(T("BatchOperationItem"), 1); items.SetValue(New("BatchOperationItem", node, 0), 0);
        var properties = new Dictionary<string, IReadOnlyDictionary<string, string>> { [path] = new Dictionary<string, string> { ["全局/名称"] = "样件" } };
        var posted = false;
        using (var handler = new JsonHandler(request =>
        {
            if (request.RequestUri.AbsolutePath.EndsWith("/versions")) return new object[0];
            Assert(request.RequestUri.AbsolutePath.EndsWith("/checkin/preflight"), "unexpected request");
            var body = Json.Deserialize<Dictionary<string, object>>(request.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            var values = (Dictionary<string, object>)body["properties"];
            Assert((string)values["全局/名称"] == "样件", "actual properties omitted"); posted = true; return new { };
        }))
        using (var client = (IDisposable)Client(handler))
        {
            var addin = New("PdmAddin"); T("PdmAddin").GetField("apiClient", Flags).SetValue(addin, client);
            Await(Call(addin, "PreflightBatchCheckInItemsAsync", items, Guid.NewGuid(), null, null, null,
                new HashSet<string> { path }, null, CancellationToken.None, null, null, properties, null));
            Assert(posted, "server preflight not called");
        }
    }
    static void FrozenArchiveSnapshot()
    {
        var root=Node("FROZEN.SLDASM",Guid.NewGuid(),0);Set(root,"Revision","W1");
        var child=Node("REPEATED.SLDPRT",Guid.NewGuid(),1);Set(child,"Revision","W1");Set(child,"CurrentRevision","W1");Set(child,"Configuration","D13");
        var alias=Node("REPEATED.SLDPRT",(Guid)Get(child,"DocumentId"),1);Set(alias,"Revision","W1");Set(alias,"CurrentRevision","W1");Set(alias,"Configuration","D14");
        ((IList)Get(root,"Children")).Add(child);((IList)Get(root,"Children")).Add(alias);
        var paths=new HashSet<string>{(string)Get(root,"FullPath"),(string)Get(child,"FullPath")};
        var posts=new List<Dictionary<string,object>>();var reads=0;
        using(var handler=new JsonHandler(request=>{if(request.Method==HttpMethod.Get){reads++;return new object[0];}posts.Add(Json.Deserialize<Dictionary<string,object>>(request.Content.ReadAsStringAsync().GetAwaiter().GetResult()));return new{};}))
        using(var client=(IDisposable)Client(handler))
        {
            var snapshot=Await(Call(client,"PrepareArchiveSnapshotAsync",root,paths,null,CancellationToken.None));
            var original=Json.Serialize(Call(snapshot,"CreateRoot"));
            Set(child,"DocumentId",null);Set(child,"Configuration","changed");((IList)Get(root,"Children")).Clear();Set(root,"FileName","changed.SLDASM");
            Await(Call(client,"PreflightCheckInAsync",Get(root,"DocumentId"),Guid.NewGuid(),root,new Dictionary<string,string>(),Guid.NewGuid(),true,null,null,CancellationToken.None,null,paths,null,snapshot));
            var stored=New("StoredVersionFile","vault/source",4L,"hash",new Dictionary<string,string>());
            var prepared=Await(Call(client,"PrepareArchiveBatchFileAsync",Get(root,"DocumentId"),Guid.NewGuid(),root,"test",stored,new Dictionary<string,string>(),Guid.NewGuid(),true,null,null,null,null,paths,CancellationToken.None,snapshot));
            var request=(Dictionary<string,object>)Json.Deserialize<Dictionary<string,object>>(Json.Serialize(prepared))["request"];
            Assert(Json.Serialize(posts.Single()["root"])==original&&Json.Serialize(request["root"])==original,"frozen references changed between preflight and commit");
            Assert((string)request["fileName"]=="FROZEN.SLDASM"&&reads==1,"filename changed or base versions queried repeatedly");
        }
        var drawing=Node("MULTI.SLDDRW",Guid.NewGuid(),2);Set(drawing,"Revision","W1");
        Set(child,"DocumentId",Guid.NewGuid());Set(child,"CurrentRevision","");Set(child,"Revision","W1");((IList)Get(drawing,"ReferencedModels")).Add(child);
        var captured=Static("PdmApiClient","CaptureArchiveSnapshot",drawing,new HashSet<string>{(string)Get(child,"FullPath")});
        var body=Json.Deserialize<Dictionary<string,object>>(Json.Serialize(Call(captured,"CreateRoot")));
        Assert(((IList)body["children"]).Count==1,"planned drawing model was dropped");
    }
    static void IncompletePlannedSnapshot()
    {
        var root=Node("INCOMPLETE.SLDASM",Guid.NewGuid(),0);Set(root,"Revision","W1");
        var child=Node("355.0700A.01.SLDPRT",Guid.NewGuid(),1);Set(child,"DocumentId",null);Set(child,"InstancePath","ROOT/SUB/355-4");Set(child,"Configuration","默认");((IList)Get(root,"Children")).Add(child);
        var paths=new HashSet<string>{(string)Get(child,"FullPath")};
        Expect<InvalidOperationException>(()=>Static("PdmApiClient","CaptureArchiveSnapshot",root,paths));
        Set(child,"DocumentId",Guid.NewGuid());Expect<InvalidOperationException>(()=>Static("PdmApiClient","CaptureArchiveSnapshot",root,paths));
        Set(child,"Revision","W1");Static("PdmApiClient","CaptureArchiveSnapshot",root,paths);
    }
    static void InterruptedBatchJournal()
    {
        var batch=Guid.NewGuid();var session=Guid.NewGuid();Static("ArchiveBatchJournal","Begin",batch,session,"server|company","engineer");
        Assert(!((IEnumerable)Static("ArchiveBatchJournal","Interrupted","server|company","engineer")).Cast<object>().Any(),"live process recovered");
        var path=Path.Combine(directory,"batches",batch.ToString("N")+".json");var state=Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(path));state["ProcessStartUtcTicks"]=0;File.WriteAllText(path,Json.Serialize(state));
        Assert(((IEnumerable)Static("ArchiveBatchJournal","Interrupted","server|company","engineer")).Cast<object>().Count()==1,"crash record lost");
        Assert(!((IEnumerable)Static("ArchiveBatchJournal","Interrupted","other","engineer")).Cast<object>().Any(),"server scope leaked");
        Assert(!((IEnumerable)Static("ArchiveBatchJournal","Interrupted","server|company","other")).Cast<object>().Any(),"actor scope leaked");
        Static("ArchiveBatchJournal","Complete",batch);Assert(!File.Exists(path),"receipt journal not cleaned");
    }
    static void InterruptedBatchRecovery()
    {
        foreach(var mode in new[]{"committed","pending","concurrent-commit","unreachable"})
        {
            var batch=Guid.NewGuid();var session=Guid.NewGuid();var reads=0;var cancellations=0;
            using(var handler=new JsonHandler(request=>
            {
                if(mode=="unreachable")throw new HttpRequestException("fixture unavailable");
                if(request.Method==HttpMethod.Post){Assert(request.RequestUri.AbsolutePath.EndsWith("/cancel-registration"),"recovery reposted archive versions");cancellations++;return new{};}
                reads++;
                return mode=="committed"||mode=="concurrent-commit"&&reads>1?(object)new{batchId=batch,results=new object[0]}:new HttpResponseMessage(HttpStatusCode.NotFound){Content=new StringContent("{}")};
            }))
            using(var client=(IDisposable)Client(handler))
            using(var pane=(IDisposable)New("PdmTaskPaneControl","中断恢复"))
            using(var lifetime=new CancellationTokenSource())
            {
                Call(client,"SetActiveCompany",Guid.Empty);var scope=(string)Get(client,"EditJournalScope");
                Static("ArchiveBatchJournal","Begin",batch,session,scope,"engineer");
                var path=Path.Combine(directory,"batches",batch.ToString("N")+".json");var state=Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(path));state["ProcessStartUtcTicks"]=0;File.WriteAllText(path,Json.Serialize(state));
                var plugin=New("PdmAddin");T("PdmAddin").GetField("apiClient",Flags).SetValue(plugin,client);T("PdmAddin").GetField("taskPaneControl",Flags).SetValue(plugin,pane);T("PdmAddin").GetField("lifetime",Flags).SetValue(plugin,lifetime);T("PdmAddin").GetField("authenticatedUsername",Flags).SetValue(plugin,"engineer");
                var previousContext=SynchronizationContext.Current;
                try{SynchronizationContext.SetSynchronizationContext(null);Await(Call(plugin,"RecoverInterruptedArchiveBatchesAsync"));}
                finally{SynchronizationContext.SetSynchronizationContext(previousContext);}
                Assert(File.Exists(path)==(mode=="unreachable"),"unconfirmed outcome journal lost or confirmed journal retained");
                Assert(cancellations==((mode=="pending"||mode=="concurrent-commit")?1:0),"unexpected recovery cancellation");
                Static("ArchiveBatchJournal","Complete",batch);
            }
        }
    }
    static Task<T> ReadLocalStructure<T>(T node) => Task.FromResult((T)localStructureReader(node));
    static void LocalSuppressedStructure()
    {
        var callback = Delegate.CreateDelegate(typeof(Func<,>).MakeGenericType(T("CadTreeNode"), typeof(Task<>).MakeGenericType(T("CadTreeNode"))),
            typeof(Program).GetMethod("ReadLocalStructure", Flags).MakeGenericMethod(T("CadTreeNode")));
        foreach (var mode in new[] { "nested", "empty", "configuration", "changed", "cycle", "missing" })
        {
            var root = Node("local-" + mode + ".SLDASM", Guid.NewGuid(), 0);
            var path = (string)Get(root, "FullPath"); File.WriteAllText(path, "original assembly");
            Set(root, "DocumentId", null); Set(root, "Configuration", "TP900"); Set(root, "InstancePath", "TOP/SUB-2");
            Set(root, "Status", Enum.Parse(T("CadReferenceStatus"), "Suppressed"));
            var reads = 0;
            localStructureReader = node =>
            {
                reads++;
                var result = Node((string)Get(node, "FileName"), Guid.NewGuid(), 0);
                Set(result, "Configuration", mode == "configuration" ? "Other" : "TP900"); Set(result, "InstancePath", "READ");
                if (mode == "changed") File.WriteAllText(path, "changed during read");
                if (mode == "empty") return result;
                var nested = mode == "nested" && reads == 1;
                var child = Node(mode == "cycle" ? (string)Get(root, "FileName") : "local-" + mode + (nested ? "-child.SLDASM" : "-part.SLDPRT"), Guid.NewGuid(), nested || mode == "cycle" ? 0 : 1);
                File.WriteAllText((string)Get(child, "FullPath"), mode == "cycle" ? "original assembly" : "child bytes");
                Set(child, "DocumentId", null); Set(child, "Configuration", "TP900"); Set(child, "InstancePath", "READ/CHILD");
                if (nested || mode == "cycle") Set(child, "Status", Enum.Parse(T("CadReferenceStatus"), "Suppressed"));
                if (mode == "missing") { File.Delete((string)Get(child, "FullPath")); Set(child, "Status", Enum.Parse(T("CadReferenceStatus"), "Missing")); }
                ((IList)Get(result, "Children")).Add(child); return result;
            };
            Action expand = () => Await(Static("SuppressedAssemblyLocalStructure", "ExpandAsync", root, callback, CancellationToken.None));
            if (mode == "configuration" || mode == "changed" || mode == "cycle") { Expect<InvalidOperationException>(expand); continue; }
            expand();
            if (mode == "missing") { Expect<InvalidOperationException>(() => Static("PdmApiClient", "ValidateArchiveReferenceCompleteness", root)); continue; }
            Static("PdmApiClient", "ValidateArchiveReferenceCompleteness", root);
            Assert(Get(root, "Status").ToString() == "Suppressed" && File.ReadAllText(path) == "original assembly", "suppression/source changed");
            if (mode == "nested")
            {
                Assert(reads == 2, "nested assembly omitted");
                var child = ((IList)Get(root, "Children"))[0]; var leaf = ((IList)Get(child, "Children"))[0];
                Assert((string)Get(leaf, "InstancePath") == "TOP/SUB-2/CHILD/CHILD", "instance ancestry lost");
                Assert(((ICollection)Static("PdmAddin", "BuildBatchOperationItems", root)).Count == 3, "new child missing from archive selection");
            }
            var before = reads; expand(); Assert(reads == before, "unchanged structure re-read");
            File.WriteAllText(path, "saved new structure"); expand(); Assert(reads > before, "changed source reused stale local structure");
        }
    }
    static object Node(string filename, Guid id, int kind)
    {
        var node = New("CadTreeNode"); Set(node, "FullPath", Path.Combine(directory, filename)); Set(node, "FileName", filename);
        Set(node, "DocumentId", id); Set(node, "Kind", Enum.ToObject(T("CadDocumentKind"), kind)); return node;
    }
    static void Baseline()
    {
        var id = Guid.NewGuid(); var baseline = Guid.NewGuid(); var node = Node("bound.SLDPRT", id, 1); File.WriteAllText((string)Get(node, "FullPath"), "native");
        Set(node, "OpenedVersionId", Guid.NewGuid());
        Assert((bool)Static("PdmDocumentIdentityStore", "TryWriteControlledVersion", Get(node, "FullPath"), id, Guid.NewGuid(), baseline, "W1", Hash((string)Get(node, "FullPath")), 6L), "binding failed");
        using (var handler = new JsonHandler(_ => new[] { new { id = Guid.NewGuid(), status = 0 } }))
        using (var client = (IDisposable)Client(handler))
            Assert((Guid?)Await(Call(client, "BaseVersionAsync", node, id, CancellationToken.None)) == baseline, "latest overwritten known baseline");
    }
    static void ReleasedBaseline()
    {
        foreach (var difference in new[] { "none", "hash", "properties", "references", "work", "missing" })
        {
            var id = Guid.NewGuid(); var baseline = Guid.NewGuid(); var latest = Guid.NewGuid();
            var node = Node("released-" + difference + ".SLDPRT", id, 1);
            var path = (string)Get(node, "FullPath"); File.WriteAllText(path, "edited locally after workspace download");
            Assert((bool)Static("PdmDocumentIdentityStore", "TryWriteControlledVersion", path, id, Guid.NewGuid(), baseline, "W1", new string('A', 64), 9L), "release binding failed");
            var source = new { id = baseline, status = 0, revision = new { display = "W1" }, sha256 = new string('A', 64),
                propertySnapshot = new { material = "steel" }, referenceSnapshot = new { documentId = id, fileName = "part.SLDPRT", configuration = "Default", children = new object[0] } };
            var current = new { id = latest, status = difference == "work" ? 0 : 1, revision = new { display = "A" }, sha256 = new string(difference == "hash" ? 'B' : 'A', 64),
                propertySnapshot = new { material = difference == "properties" ? "plastic" : "steel" }, referenceSnapshot = new { documentId = id, fileName = "part.SLDPRT", configuration = difference == "references" ? "Other" : "Default", children = new object[0] } };
            var parsedSource = Json.Deserialize(Json.Serialize(source), T("DocumentVersionDto"));
            var parsedCurrent = Json.Deserialize(Json.Serialize(current), T("DocumentVersionDto"));
            Assert((bool)Static("PdmApiClient", "ReleasedArchiveBaseMatches", parsedSource, parsedCurrent) == (difference == "none" || difference == "missing"), "pure rule mismatch: " + difference);
            using (var handler = new JsonHandler(_ => difference == "missing" ? new object[] { current } : new object[] { current, source }))
            using (var client = (IDisposable)Client(handler))
                Assert((Guid?)Await(Call(client, "BaseVersionAsync", node, id, CancellationToken.None)) == (difference == "none" ? latest : baseline), "release baseline mismatch: " + difference);
            Assert(File.ReadAllText(path) == "edited locally after workspace download", "local modification overwritten");
        }
    }
    static void RealReleasedBaseline(string fixture)
    {
        var entries = (object[])Json.DeserializeObject(File.ReadAllText(fixture));
        var entry = entries.Cast<Dictionary<string, object>>().Single(item => ((Dictionary<string, object>)item["Document"])["fileName"].ToString() == "7002541.00-04.SLDPRT");
        var versions = (object[])entry["Versions"];
        var current = (Dictionary<string, object>)versions[0]; var source = (Dictionary<string, object>)versions[1];
        var id = Guid.Parse(((Dictionary<string, object>)entry["Document"])["id"].ToString());
        var node = Node("actual-case.SLDPRT", id, 1); var path = (string)Get(node, "FullPath");
        File.WriteAllText(path, "local edits must survive");
        Static("PdmDocumentIdentityStore", "TryWriteControlledVersion", path, id, Guid.NewGuid(), Guid.Parse(source["id"].ToString()), "W1", source["sha256"].ToString(), 76682L);
        using (var handler = new JsonHandler(_ => versions))
        using (var client = (IDisposable)Client(handler))
            Assert((Guid?)Await(Call(client, "BaseVersionAsync", node, id, CancellationToken.None)) == Guid.Parse(current["id"].ToString()), "actual version promotion rejected");
        Assert(File.ReadAllText(path) == "local edits must survive", "local content overwritten");
    }
    static void UnknownBaseline()
    {
        var node = Node("unbound.SLDASM", Guid.NewGuid(), 0); File.WriteAllText((string)Get(node, "FullPath"), "same assembly bytes");
        var version = new { id = Guid.NewGuid(), sha256 = Hash((string)Get(node, "FullPath")), propertySnapshot = new { }, referenceSnapshot = new { documentId = Get(node, "DocumentId"), fileName = "unbound.SLDASM", kind = 0, quantity = 1, children = new[] { new { documentId = Guid.NewGuid(), fileName = "child.SLDPRT", kind = 1 } } } };
        using (var handler = new JsonHandler(_ => new[] { version }))
        using (var client = (IDisposable)Client(handler))
            Assert(Await(Call(client, "BaseVersionAsync", node, Get(node, "DocumentId"), CancellationToken.None)) == null, "guessed baseline from hash with incompatible references");
    }
    static void RootDirectories()
    {
        using (var handler = new JsonHandler(_ => throw new Exception("unexpected network")))
        using (var client = (IDisposable)Client(handler))
        {
            var manager = New("ControlledWorkspaceManager", client, Path.Combine(directory, "workspace"));
            var a = New("ControlledOpenManifestDto"); var b = New("ControlledOpenManifestDto"); var project = Guid.NewGuid();
            Set(a, "ProjectId", project); Set(b, "ProjectId", project); Set(a, "RootDocumentId", Guid.NewGuid()); Set(b, "RootDocumentId", Guid.NewGuid());
            Assert(!Equals(Call(manager, "WorkingDirectory", a), Call(manager, "WorkingDirectory", b)), "roots share directory");
        }
    }
    static void ReadOnlyWorkspace()
    {
        using(var handler=new JsonHandler(_=>throw new Exception("unexpected network")))
        using(var client=(IDisposable)Client(handler))
        {
            var manager=New("ControlledWorkspaceManager",client,Path.Combine(directory,"read-only-workspace"));
            var manifest=New("ControlledOpenManifestDto");Set(manifest,"Id",Guid.NewGuid());Set(manifest,"ProjectId",Guid.NewGuid());Set(manifest,"ProjectCode","QA-READONLY");Set(manifest,"RootDocumentId",Guid.NewGuid());Set(manifest,"RootVersionId",Guid.NewGuid());Set(manifest,"RootRelativePath","ROOT.SLDASM");
            var list=Activator.CreateInstance(typeof(List<>).MakeGenericType(T("ControlledOpenFileDto")));Set(manifest,"Files",list);
            var cache=(string)Call(manager,"ReadOnlyDirectory",manifest,false);Directory.CreateDirectory(cache);
            foreach(var name in new[]{"ROOT.SLDASM","CHILD.SLDPRT"})
            {
                var path=Path.Combine(cache,name);File.WriteAllText(path,"archived bytes");
                var file=New("ControlledOpenFileDto");Set(file,"DocumentId",name=="ROOT.SLDASM"?Get(manifest,"RootDocumentId"):Guid.NewGuid());Set(file,"VersionId",Guid.NewGuid());Set(file,"FileName",name);Set(file,"RelativePath",name);Set(file,"FileLength",new FileInfo(path).Length);Set(file,"Sha256",Hash(path));Set(file,"Revision","W1");Set(file,"IsRoot",name=="ROOT.SLDASM");((IList)list).Add(file);
            }
            var working=(string)Call(manager,"WorkingDirectory",manifest);Directory.CreateDirectory(working);var local=Path.Combine(working,"ROOT.SLDASM");File.WriteAllText(local,"local changes must survive");
            var opened=(string)Await(Call(manager,"PrepareReadOnlyAsync",manifest,false,CancellationToken.None));
            Assert(opened==Path.Combine(cache,"ROOT.SLDASM"),"used editable Working file");
            Assert(Directory.GetFiles(cache).All(path=>(File.GetAttributes(path)&FileAttributes.ReadOnly)!=0),"root or child not read-only");
            Assert(File.ReadAllText(local)=="local changes must survive"&&(File.GetAttributes(local)&FileAttributes.ReadOnly)==0,"Working edits changed");
        }
    }
    static void DrawingReferences()
    {
        var drawing = Node("model.SLDDRW", Guid.NewGuid(), 2); var model = Node("model.SLDPRT", Guid.NewGuid(), 1);
        Set(model, "Configuration", "Default"); Set(model, "CurrentRevision", "W1"); ((IList)Get(drawing, "ReferencedModels")).Add(model);
        var dto = New("DocumentReferenceNodeDto"); Set(dto, "DocumentId", Get(drawing, "DocumentId")); Set(dto, "FileName", "model.SLDDRW"); Set(dto, "Kind", 2);
        var child = New("DocumentReferenceNodeDto"); Set(child, "DocumentId", Get(model, "DocumentId")); Set(child, "Configuration", "Default");
        var rev = New("RevisionDto"); Set(rev, "Display", "W1"); Set(child, "Revision", rev); ((IList)Get(dto, "Children")).Add(child);
        var compare = T("PdmAddin").GetMethods(Flags).Single(m => m.Name == "ReferenceSnapshotMatchesTree" && m.GetParameters().Length == 2);
        Assert((bool)compare.Invoke(null, new[] { dto, drawing }), "same drawing pins mismatch");
        Set(model, "CurrentRevision", "W2"); Assert(!(bool)compare.Invoke(null, new[] { dto, drawing }), "changed model pins ignored");
    }
    static void RootAdmission()
    {
        var child = Node("sub.SLDASM", Guid.NewGuid(), 0); var tree = New("DocumentReferenceNodeDto"); Set(tree, "DocumentId", Guid.NewGuid());
        var sub = New("DocumentReferenceNodeDto"); Set(sub, "DocumentId", Get(child, "DocumentId")); ((IList)Get(tree, "Children")).Add(sub);
        Assert(!(bool)Static("PdmAddin", "IsIndependentArchiveRoot", child, tree, 0), "nested promoted to independent root");
        Set(child, "DocumentId", Guid.NewGuid()); Assert((bool)Static("PdmAddin", "IsIndependentArchiveRoot", child, tree, 0), "independent root rejected");
    }
    static void Diagnostics()
    {
        var secret = "Authorization: Bearer secret-bearer\n{\"accessToken\":\"secret-token\",\"password\":\"secret spaced password\"}\nurl?refresh_token=secret-refresh&x=1";
        var safe = (string)Static("PluginDiagnosticLog", "Redact", secret);
        Assert(!safe.Contains("secret-" ) && !safe.Contains("secret spaced"), "credential leaked");
        System.Threading.Tasks.Parallel.For(0, 400, i => Static("PluginDiagnosticLog", "Record", "进度", "测试操作" + i));
        var snapshot = (string)Static("PluginDiagnosticLog", "Snapshot");
        Assert(snapshot.Split(new[] { "【进度】" }, StringSplitOptions.None).Length - 1 == 200, "history not bounded");
        var logs = Path.Combine(directory, "log-fixtures"); Directory.CreateDirectory(logs);
        File.WriteAllText(Path.Combine(logs, "addin-errors.log"), "异常详情\n" + secret);
        File.WriteAllText(Path.Combine(logs, "addin-checkin.log.1"), "上一轮崩溃记录");
        File.WriteAllText(Path.Combine(logs, "settings.json"), "must-not-export");
        File.WriteAllText(Path.Combine(logs, "addin-scan.log"), new string('x', 1200000) + "\n末尾扫描记录");
        var report = (string)Static("PluginDiagnosticLog", "BuildReport", "test-version", logs);
        Assert(report.Contains("test-version") && report.Contains("上一轮崩溃记录") && report.Contains("末尾扫描记录"), "report missing context");
        Assert(!report.Contains("secret-") && !report.Contains("must-not-export") && report.Length < 1100000, "unsafe or unbounded report");
    }
    static void SubprojectReference()
    {
        var project = Guid.NewGuid(); var existing = Guid.NewGuid();var version=Guid.NewGuid();
        var root = Node("P700020.00.SLDASM", Guid.NewGuid(), 0);
        var part = Node("ADN-16-50-A-P-A.SLDPRT", Guid.NewGuid(), 1);
        Set(part,"DocumentId",null);Set(part,"CurrentRevision",string.Empty);
        File.WriteAllText((string)Get(part,"FullPath"),"same archived cylinder bytes");
        ((IList)Get(root,"Children")).Add(part);
        var hash=Hash((string)Get(part,"FullPath"));
        var items=Array.CreateInstance(T("BatchOperationItem"),1);
        items.SetValue(New("BatchOperationItem",root,0),0);
        using(var handler=new JsonHandler(request=>
        {
            var path=request.RequestUri.AbsolutePath;
            if(path=="/api/projects/"+project+"/documents")return new[]{new{id=existing,projectId=project,fileName="ADN-16-50-A-P-A.SLDPRT",kind=1}};
            if(path=="/api/documents/"+existing+"/versions")return new[]{new{id=version,revision=new{display="W1"},sha256=hash,propertySnapshot=new Dictionary<string,string>()}};
            throw new Exception("unexpected request "+path);
        }))
        using(var client=(IDisposable)Client(handler))
        {
            var addin=New("PdmAddin");T("PdmAddin").GetField("apiClient",Flags).SetValue(addin,client);
            Await(Call(addin,"ResolveBatchReferenceVersionsAsync",items,project,null,CancellationToken.None));
            Assert((Guid)Get(part,"DocumentId")==existing && (string)Get(part,"CurrentRevision")=="W1", "target project archive not resolved");
            var request=Static("PdmApiClient","ToRequestNode",root,true,null);
            Assert(Json.Serialize(request).Contains(existing.ToString()),"root still contains missing reference");
            File.WriteAllText((string)Get(part,"FullPath"),"different cylinder bytes");Set(part,"DocumentId",null);
            Expect<InvalidOperationException>(()=>Await(Call(addin,"ResolveBatchReferenceVersionsAsync",items,project,null,CancellationToken.None)));
        }
    }
    static void ArchivedSuppressedAssembly()
    {
        foreach(var legacy in new[]{false,true})
        {
            var root=Node("archived-parent.SLDASM",Guid.NewGuid(),0);
            var sub=Node("archived-sub-"+legacy+".SLDASM",Guid.NewGuid(),0);
            var path=(string)Get(sub,"FullPath");File.WriteAllText(path,"unchanged archived assembly");
            var id=(Guid)Get(sub,"DocumentId");var versionId=Guid.NewGuid();var childId=Guid.NewGuid();
            Set(sub,"Status",Enum.Parse(T("CadReferenceStatus"),"Suppressed"));Set(sub,"Configuration","TP900");Set(sub,"CurrentRevision","W1");Set(sub,"OpenedVersionId",versionId);Set(sub,"InstancePath","parent/sub-2");
            ((IList)Get(root,"Children")).Add(sub);
            var snapshot=new {documentId=id,fileName=Get(sub,"FileName"),kind=0,instancePath="sub",configuration=legacy?"":"TP900",children=new[]{new{documentId=childId,fileName="archived-child.SLDPRT",kind=1,instancePath="sub/child-1",configuration="默认",quantity=2,status=0,revision=new{display="W1"},children=new object[0]}}};
            using(var handler=new JsonHandler(request=>{
                if(request.Method!=HttpMethod.Get)throw new Exception("Reference reuse wrote business data");
                return new[]{new{id=versionId,sha256=Hash(path),revision=new{display="W1"},referenceSnapshot=snapshot,propertySnapshot=new Dictionary<string,string>{{"配置:TP900/制图",""}}}};
            }))
            using(var client=(IDisposable)Client(handler))
            {
                var addin=New("PdmAddin");T("PdmAddin").GetField("apiClient",Flags).SetValue(addin,client);T("PdmAddin").GetField("currentTree",Flags).SetValue(addin,root);
                Await(Call(addin,"ResolveSuppressedAssemblyReferencesAsync",root,CancellationToken.None));
                Static("PdmApiClient","ValidateArchiveReferenceCompleteness",root);
                var serialized=Json.Serialize(Static("PdmApiClient","ToRequestNode",root,true,null));
                Assert(serialized.Contains(childId.ToString())&&serialized.Contains("parent/sub-2/child-1")&&serialized.Contains("\"quantity\":2"),"archived child references lost");
                var archivedRoot=Json.Deserialize(serialized,T("DocumentReferenceNodeDto"));
                var storedRevision=New("RevisionDto");Set(storedRevision,"Display","W1");Set(((IList)Get(archivedRoot,"Children"))[0],"Revision",storedRevision);
                var compare=T("PdmAddin").GetMethods(Flags).Single(method=>method.Name=="ReferenceSnapshotMatchesTree"&&method.GetParameters().Length==2);
                Assert((bool)compare.Invoke(null,new[]{archivedRoot,root}),"suppressed loading state misclassified as structure deletion");
                Set(sub,"Quantity",2);Assert(!(bool)compare.Invoke(null,new[]{archivedRoot,root}),"real quantity change hidden");Set(sub,"Quantity",1);
                var items=Array.CreateInstance(T("BatchOperationItem"),1);items.SetValue(New("BatchOperationItem",sub,1),0);
                var plan=Await(Call(addin,"BuildBatchCheckInPlanAsync",items,null,CancellationToken.None));
                Assert(((IEnumerable)Get(plan,"Items")).Cast<object>().Count()==0 && (int)Get(plan,"SkippedFiles")==1,"unchanged suppressed assembly submitted again");
                Set(sub,"Configuration","Other");Expect<InvalidOperationException>(()=>Static("PdmApiClient","ValidateArchiveReferenceCompleteness",root));Set(sub,"Configuration","TP900");
                File.WriteAllText(path,"changed assembly");Expect<InvalidOperationException>(()=>Static("PdmApiClient","ValidateArchiveReferenceCompleteness",root));
            }
        }
    }
    static void NestedReferenceIdentity()
    {
        var project = Guid.NewGuid(); var document = Guid.NewGuid(); var version = Guid.NewGuid();
        var root = Node("nested-held-root.SLDASM", Guid.NewGuid(), 0);
        var sub = Node("nested-panel.SLDASM", Guid.NewGuid(), 0);
        Set(sub, "Revision", "W1"); Set(sub, "CurrentRevision", "W1");
        var first = Node("复位按钮标牌(正装).SLDPRT", document, 1);
        var second = Node("复位按钮标牌(正装).SLDPRT", document, 1);
        Set(first, "Configuration", "默认"); Set(second, "Configuration", "反装");
        Set(first, "CurrentRevision", "W1"); Set(second, "CurrentRevision", "W1");
        ((IList)Get(root, "Children")).Add(sub); ((IList)Get(sub, "Children")).Add(first); ((IList)Get(sub, "Children")).Add(second);
        File.WriteAllText((string)Get(first, "FullPath"), "verified archived nameplate");
        var hash = Hash((string)Get(first, "FullPath"));
        var items = Array.CreateInstance(T("BatchOperationItem"), 2);
        items.SetValue(New("BatchOperationItem", root, 0), 0); items.SetValue(New("BatchOperationItem", sub, 1), 1);
        using (var handler = new JsonHandler(request => {
            if (request.RequestUri.AbsolutePath.EndsWith("/documents")) return new[] { new { id=document, projectId=project, fileName=Get(first,"FileName"),kind=1 } };
            return new[] { new { id=version, revision=new { display="W1" },sha256=hash,propertySnapshot=new Dictionary<string,string>() } };
        }))
        using (var client = (IDisposable)Client(handler))
        {
            var addin = New("PdmAddin"); T("PdmAddin").GetField("apiClient", Flags).SetValue(addin, client);
            T("PdmAddin").GetField("currentTree", Flags).SetValue(addin, Node("unrelated-active.SLDASM",Guid.NewGuid(),0));
            foreach (var part in new[] { first, second }) { Set(part,"DocumentId",null); Set(part,"CurrentRevision", ""); }
            Await(Call(addin,"ResolveBatchReferenceVersionsAsync",items,project,null,CancellationToken.None));
            Assert((Guid)Get(first,"DocumentId")==document && (Guid)Get(second,"DocumentId")==document,"held nested instances not resolved");
            Assert((string)Get(second,"Configuration")=="反装","configuration overwritten during identity resolution");
            var body=Json.Serialize(Static("PdmApiClient","ToRequestNode",root,true,new HashSet<string>{(string)Get(sub,"FullPath")}));
            Assert(body.Split(new[]{document.ToString()},StringSplitOptions.None).Length==3,"nested serialized references missing shared identity");
        }
    }
    static void IncompleteArchivedReferences()
    {
        var root=Node("incomplete-history.SLDASM",Guid.NewGuid(),0);
        var sub=Node("incomplete-sub.SLDASM",Guid.NewGuid(),0);
        File.WriteAllText((string)Get(sub,"FullPath"),"archived incomplete assembly");
        Set(sub,"Status",Enum.Parse(T("CadReferenceStatus"),"Suppressed")); Set(sub,"Configuration","TP900"); Set(sub,"CurrentRevision","W1");
        ((IList)Get(root,"Children")).Add(sub);
        foreach(var missing in new[]{"document","revision"})
        {
            var snapshot=new {documentId=Get(sub,"DocumentId"),fileName=Get(sub,"FileName"),kind=0,configuration="TP900",children=new[]{new{documentId=missing=="document"?(Guid?)null:Guid.NewGuid(),fileName="复位按钮标牌(正装).SLDPRT",kind=1,status=0,revision=missing=="revision"?null:new{display="W1"},children=new object[0]}}};
            var saved=Json.Deserialize(Json.Serialize(new{id=Guid.NewGuid(),revision=new{display="W1"},referenceSnapshot=snapshot}),T("DocumentVersionDto"));
            Set(sub,"SuppressedArchiveVersion",saved);Set(sub,"SuppressedArchiveSha256",Hash((string)Get(sub,"FullPath")));
            Assert(!(bool)Static("SuppressedAssemblyArchive","CanReuse",sub),"incomplete historical references reused");
        }
        var missingPart=Node("unbound-reference.SLDPRT",Guid.NewGuid(),1); File.WriteAllText((string)Get(missingPart,"FullPath"),"part"); Set(missingPart,"DocumentId",null);
        var liveRoot=Node("unbound-root.SLDASM",Guid.NewGuid(),0); ((IList)Get(liveRoot,"Children")).Add(missingPart);
        Expect<InvalidOperationException>(()=>Static("PdmApiClient","ValidateCheckInReferences",liveRoot,null));
        Static("PdmApiClient","ValidateCheckInReferences",liveRoot,new HashSet<string>{(string)Get(missingPart,"FullPath")});
    }
    static void NestedDrawingReferenceIdentity()
    {
        var project = Guid.NewGuid(); var document = Guid.NewGuid();
        var root = Node("drawing-reference-root.SLDASM",Guid.NewGuid(),0);
        var model = Node("drawing-model.SLDPRT",document,1);
        File.WriteAllText((string)Get(model,"FullPath"),"archived model file");
        var drawing = Node("drawing-model.SLDDRW",Guid.NewGuid(),2);
        var alias = Node("drawing-model.SLDPRT",Guid.NewGuid(),1); Set(alias,"DocumentId",null); Set(alias,"FullPath",Get(model,"FullPath")); Set(alias,"CurrentRevision","");
        ((IList)Get(root,"Children")).Add(model); ((IList)Get(model,"Children")).Add(drawing);
        ((IList)Get(drawing,"ReferencedModels")).Add(alias); ((IList)Get(drawing,"ReferencedModels")).Add(model);
        var items=Array.CreateInstance(T("BatchOperationItem"),1); items.SetValue(New("BatchOperationItem",root,0),0);
        Static("PdmAddin","SynchronizeBatchFileIdentities",items);
        Assert((Guid)Get(alias,"DocumentId")==document,"drawing alias lost model identity");
        using(var handler=new JsonHandler(request => new[]{new{id=Guid.NewGuid(),revision=new{display="W1"},sha256=Hash((string)Get(model,"FullPath")),propertySnapshot=new Dictionary<string,string>()}}))
        using(var client=(IDisposable)Client(handler))
        {
            var plugin=New("PdmAddin"); T("PdmAddin").GetField("apiClient",Flags).SetValue(plugin,client);
            Await(Call(plugin,"ResolveBatchReferenceVersionsAsync",items,project,null,CancellationToken.None));
            Assert((string)Get(alias,"CurrentRevision")=="W1","nested drawing reference version unresolved");
            Static("PdmApiClient","ValidateCheckInReferences",root,null);
            Static("PdmApiClient","ValidateCheckInReferences",drawing,null);
        }
    }
    static void PendingRegistrationIdentity()
    {
        var project=Guid.NewGuid(); var node=Node("pending-registration.SLDDRW",Guid.NewGuid(),2);
        File.WriteAllText((string)Get(node,"FullPath"),"drawing source");
        var doc=New("DocumentDto"); Set(doc,"Id",Get(node,"DocumentId"));Set(doc,"ProjectId",project);
        var plugin=New("PdmAddin");
        using(var pane=(IDisposable)New("PdmTaskPaneControl","事务测试"))
        {
            T("PdmAddin").GetField("taskPaneControl",Flags).SetValue(plugin,pane);
            Call(plugin,"ApplyPreparedDocumentToMatchingInstances",node,doc,false);
        }
        var bindingArguments = new object[]{Get(node,"FullPath"),null};
        Assert(!(bool)Static("PdmDocumentIdentityStore","TryReadLocalMetadata",bindingArguments),"pending registration wrote a persistent binding");
        Assert((bool)Static("PdmDocumentIdentityStore","TryWrite",Get(node,"FullPath"),Get(node,"DocumentId"),project),"registration-only binding fixture failed");
        var docs=Array.CreateInstance(T("DocumentDto"),0);
        Static("PdmAddin","ResetRemovedRegistrationBindings",node,project,docs);
        Assert(Get(node,"DocumentId")==null,"removed empty registration retained invalid ID");
        var stored=Guid.NewGuid();Set(node,"DocumentId",stored);
        Static("PdmDocumentIdentityStore","TryWriteControlledVersion",Get(node,"FullPath"),stored,project,Guid.NewGuid(),"W1",Hash((string)Get(node,"FullPath")),new FileInfo((string)Get(node,"FullPath")).Length);
        Static("PdmAddin","ResetRemovedRegistrationBindings",node,project,docs);
        Assert((Guid)Get(node,"DocumentId")==stored,"archived identity cleared");
        var foreign=Guid.NewGuid(); Set(node,"DocumentId",foreign);
        Static("PdmDocumentIdentityStore","TryWrite",Get(node,"FullPath"),foreign,Guid.NewGuid());
        Static("PdmAddin","ResetRemovedRegistrationBindings",node,project,docs);
        Assert((Guid)Get(node,"DocumentId")==foreign,"foreign identity cleared");
    }
    static void SelectedModelDrawings()
    {
        var part=Node("selected-part.SLDPRT",Guid.NewGuid(),1);
        var alias=Node("selected-part.SLDPRT",(Guid)Get(part,"DocumentId"),1); Set(alias,"FullPath",Path.Combine(directory,"old-folder","selected-part.SLDPRT"));
        var other=Node("unrelated-part.SLDPRT",Guid.NewGuid(),1);
        var first=Node("different-name.SLDDRW",Guid.NewGuid(),2); ((IList)Get(first,"ReferencedModels")).Add(alias);
        var second=Node("multi-model.SLDDRW",Guid.NewGuid(),2); ((IList)Get(second,"ReferencedModels")).Add(part); ((IList)Get(second,"ReferencedModels")).Add(other);
        var unrelated=Node("unrelated.SLDDRW",Guid.NewGuid(),2); ((IList)Get(unrelated,"ReferencedModels")).Add(other);
        var selected=Array.CreateInstance(T("BatchOperationItem"),1);selected.SetValue(New("BatchOperationItem",part,1),0);
        var all=Array.CreateInstance(T("BatchOperationItem"),4); all.SetValue(selected.GetValue(0),0);
        all.SetValue(New("BatchOperationItem",first,2),1);all.SetValue(New("BatchOperationItem",second,2),2);all.SetValue(New("BatchOperationItem",unrelated,2),3);
        var items=((IEnumerable)Static("PdmAddin","IncludeSelectedModelDrawings",selected,all)).Cast<object>().ToArray();
        Assert(items.Length==3 && items.Any(item=>Get(item,"Node")==first) && items.Any(item=>Get(item,"Node")==second),"required drawing scope lost or unrelated drawing added");
        var parent=Node("selected-root.SLDASM",Guid.NewGuid(),0);
        ((IList)Get(parent,"Children")).Add(part);((IList)Get(parent,"Children")).Add(other);
        var withParent=Array.CreateInstance(T("BatchOperationItem"),2);
        withParent.SetValue(New("BatchOperationItem",parent,0),0);withParent.SetValue(selected.GetValue(0),1);
        var scoped=((IEnumerable)Static("PdmAddin","IncludeSelectedModelDrawings",withParent,all)).Cast<object>().ToArray();
        Assert(!scoped.Any(item=>Get(item,"Node")==other||Get(item,"Node")==unrelated),"selected ancestor silently added deselected model or drawing");
    }
    static void ReferenceCycle()
    {
        var model = Node("cycle.SLDPRT", Guid.NewGuid(), 1);
        var drawing = Node("cycle.SLDDRW", Guid.NewGuid(), 2);
        ((IList)Get(model, "Children")).Add(drawing);
        ((IList)Get(drawing, "ReferencedModels")).Add(model);
        Static("PdmApiClient", "ValidateArchiveReferenceCompleteness", model);
        Static("PdmApiClient", "ValidateArchiveReferenceCompleteness", drawing);
        Set(model, "Status", Enum.Parse(T("CadReferenceStatus"), "Suppressed"));
        Expect<InvalidOperationException>(() => Static("PdmApiClient", "ValidateArchiveReferenceCompleteness", drawing));
        Set(model, "Status", Enum.Parse(T("CadReferenceStatus"), "Normal"));
        var duplicate = Node("cycle.SLDPRT", Guid.NewGuid(), 1);
        Set(duplicate, "Status", Enum.Parse(T("CadReferenceStatus"), "Missing"));
        ((IList)Get(drawing, "ReferencedModels")).Add(duplicate);
        Expect<InvalidOperationException>(() => Static("PdmApiClient", "ValidateArchiveReferenceCompleteness", model));
        ((IList)Get(drawing, "ReferencedModels")).Remove(duplicate);
        foreach (var invalid in new[] { "", " ", "invalid|file.SLDPRT", "C:\\invalid|file.SLDPRT" })
            Assert((string)Static("SolidWorksReferenceTreeScanner", "NormalizeComponentFilePath", invalid) == "", "invalid path accepted");
        var valid = Path.Combine(directory, "valid.SLDPRT");
        Assert((string)Static("SolidWorksReferenceTreeScanner", "NormalizeComponentFilePath", valid) == valid, "valid full path changed");
    }
    static void SuppressedReferences()
    {
        var node = Node("suppressed.SLDPRT", Guid.NewGuid(), 1);
        var path = (string)Get(node, "FullPath"); File.WriteAllText(path, "part");
        Set(node, "Status", Enum.Parse(T("CadReferenceStatus"), "Suppressed"));
        Set(node, "CurrentRevision", "W1");
        Static("PdmApiClient", "ValidateArchiveReferenceCompleteness", node);
        var root = Node("suppressed-root.SLDASM", Guid.NewGuid(), 0);
        ((IList)Get(root, "Children")).Add(node);
        Static("PdmApiClient", "ValidateCheckInReferences", root, null);
        var snapshot = Static("PdmApiClient", "ToRequestNode", root, true, null);
        var json = Json.Serialize(snapshot);
        Assert(json.Contains("suppressed.SLDPRT") && json.Contains(Get(node, "DocumentId").ToString()), "suppressed reference omitted");
        File.Delete(path);
        Expect<InvalidOperationException>(() => Static("PdmApiClient", "ValidateCheckInReferences", root, null));
        File.WriteAllText(path, "assembly"); Set(node, "Kind", Enum.ToObject(T("CadDocumentKind"), 0));
        Expect<InvalidOperationException>(() => Static("PdmApiClient", "ValidateCheckInReferences", root, null));
        var child = Node("nested-part.SLDPRT", Guid.NewGuid(), 1); Set(child, "CurrentRevision", "W1");
        ((IList)Get(node, "Children")).Add(child);
        Static("PdmApiClient", "ValidateCheckInReferences", root, null);
    }
    static void ExternalImport()
    {
        var project = Guid.NewGuid();
        var node = Node("import-cache.SLDPRT", Guid.NewGuid(), 1);
        Set(node, "DocumentId", null);
        var path = (string)Get(node, "FullPath"); File.WriteAllText(path, "imported native part");
        Assert(!(bool)Static("PdmAddin", "RequiresExternalReferenceCopy", node, project), "unregistered cache file blocked");
        var root = Node("assembly.SLDASM", Guid.NewGuid(), 0); Set(root, "DocumentId", null);
        Set(root, "FullPath", Path.Combine(directory, "assembly", "assembly.SLDASM"));
        ((IList)Get(root, "Children")).Add(node);
        var items = Array.CreateInstance(T("BatchOperationItem"), 2);
        items.SetValue(New("BatchOperationItem", root, 0), 0); items.SetValue(New("BatchOperationItem", node, 1), 1);
        Static("PdmAddin", "ValidateNewAssemblyReferenceLocations", items, project);
        Static("PdmAddin", "ValidateBatchFileNames", items);
        Assert((bool)Static("PdmDocumentIdentityStore", "TryWriteControlledVersion", path, Guid.NewGuid(), project, Guid.NewGuid(), "W1", Hash(path), new FileInfo(path).Length), "same-project binding failed");
        Assert(!(bool)Static("PdmAddin", "RequiresExternalReferenceCopy", node, project), "same-project source blocked");
        Assert((bool)Static("PdmAddin", "RequiresExternalReferenceCopy", node, Guid.NewGuid()), "foreign project binding allowed");
        Expect<InvalidOperationException>(() => Static("PdmAddin", "ValidateNewAssemblyReferenceLocations", items, Guid.NewGuid()));
        var unbound = Path.Combine(directory, "workspace", "unknown.SLDPRT");
        Directory.CreateDirectory(Path.GetDirectoryName(unbound)); File.WriteAllText(unbound, "unknown"); Set(node, "FullPath", unbound);
        Assert((bool)Static("PdmAddin", "RequiresExternalReferenceCopy", node, project), "unbound workspace file allowed");
    }
    static object Client(HttpMessageHandler handler)
    {
        var client = New("PdmApiClient", "http://fixture.invalid/"); var field = T("PdmApiClient").GetField("httpClient", Flags);
        ((HttpClient)field.GetValue(client)).Dispose(); field.SetValue(client, new HttpClient(handler) { BaseAddress = new Uri("http://fixture.invalid/") }); return client;
    }
    sealed class JsonHandler : HttpMessageHandler
    {
        readonly Func<HttpRequestMessage, object> response;
        public JsonHandler(Func<HttpRequestMessage, object> response) { this.response = response; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { var value=response(request);return Task.FromResult(value as HttpResponseMessage??Reply(value)); }
    }
    sealed class UploadHandler : HttpMessageHandler
    {
        readonly Guid project, session = Guid.NewGuid(); readonly string hash; readonly bool lostCompletion; bool failed;
        public int Starts; public long Received; public Dictionary<int, int> Chunks = new Dictionary<int, int>(); public List<string> StoredTargets = new List<string>();
        public Action OnComplete;
        public string TargetRelativePath;
        public Dictionary<string, object> StartBody;
        public UploadHandler(Guid project, string hash, bool lostCompletion) { this.project = project; this.hash = hash; this.lostCompletion = lostCompletion; }
        object Session() => new { id = session, projectId = project, totalLength = 10, chunkSize = 4, expectedSha256 = hash, receivedLength = Received, expiresAt = DateTime.UtcNow.AddHours(1).ToString("O"), targetRelativePath = TargetRelativePath };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri.AbsolutePath;
            if (request.Method == HttpMethod.Get) return Reply(Session());
            if (path.EndsWith("/sessions")) { Starts++; StartBody = Json.Deserialize<Dictionary<string, object>>(await request.Content.ReadAsStringAsync()); return Reply(Session()); }
            if (path.Contains("/chunks/"))
            {
                var index = int.Parse(path.Substring(path.LastIndexOf('/') + 1));
                Chunks[index] = Chunks.TryGetValue(index, out var count) ? count + 1 : 1;
                if (OnComplete == null && !lostCompletion && index == 1 && !failed) { failed = true; return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("暂时不可用") }; }
                Received += (await request.Content.ReadAsByteArrayAsync()).Length; return Reply(Session());
            }
            if (path.EndsWith("/complete"))
            {
                var payload = Json.Deserialize<Dictionary<string, object>>(await request.Content.ReadAsStringAsync());
                var target = (string)payload["relativeTargetPath"]; StoredTargets.Add(target);
                if (lostCompletion && !failed) { failed = true; return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("响应丢失") }; }
                OnComplete?.Invoke();
                return Reply(new { relativePath = target, length = 10, sha256 = hash });
            }
            throw new Exception("unexpected " + path);
        }
    }
    static HttpResponseMessage Reply(object value) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Json.Serialize(value)) };
    static object Await(object result) { var task = (Task)result; task.GetAwaiter().GetResult(); return result.GetType().GetProperty("Result")?.GetValue(result); }
    static Type T(string name) => assembly.GetType("Upton.Pdm.SolidWorks." + name, true);
    static object New(string name, params object[] args)
    {
        var value = Activator.CreateInstance(T(name), Flags & ~BindingFlags.Static, null, args, null);
        var children = value.GetType().GetProperty("Children", Flags);
        if (children?.CanWrite == true && children.GetValue(value) == null) children.SetValue(value, Activator.CreateInstance(children.PropertyType));
        return value;
    }
    static object Get(object value, string name) => value.GetType().GetProperty(name, Flags).GetValue(value);
    static void Set(object value, string name, object data) => value.GetType().GetProperty(name, Flags).SetValue(value, data);
    static object Call(object value, string name, params object[] args) => value.GetType().GetMethod(name, Flags).Invoke(value, args);
    static object Static(string type, string name, params object[] args) => T(type).GetMethod(name, Flags).Invoke(null, args);
    static string Hash(string path) { using (var hash = SHA256.Create()) using (var file = File.OpenRead(path)) return BitConverter.ToString(hash.ComputeHash(file)).Replace("-", ""); }
    static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    static Exception Unwrap(Exception error) => error is TargetInvocationException && error.InnerException != null ? Unwrap(error.InnerException) : error;
    static void Expect<E>(Action action) where E : Exception { try { action(); } catch (Exception error) { if (Unwrap(error) is E) return; throw; } throw new Exception("expected " + typeof(E).Name); }
}
