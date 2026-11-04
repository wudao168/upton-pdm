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
using System.Windows.Forms;

internal static partial class Program
{
    static async Task RegistrationWordingScenario(Form host, string output)
    {
        Directory.CreateDirectory(output);
        var store = Type("PdmDocumentIdentityStore"); var bindingField = store.GetField("bindingDirectory", Flags); var resolver = store.GetField("workspaceRootResolver", Flags);
        var oldBinding = bindingField.GetValue(null); var oldRoot = resolver.GetValue(null);
        bindingField.SetValue(null, Path.Combine(output, "bindings")); resolver.SetValue(null, (Func<string>)(() => Path.Combine(output, "managed")));
        try
        {
            var project = Guid.NewGuid(); var documentId = Guid.NewGuid(); var versionId = Guid.NewGuid();
            var source = LinkedNode(output, "标签机钣金.SLDPRT", "Part"); Set(source, "DocumentId", documentId);
            var originalHash = WorkingReuseHash((string)Get(source, "FullPath")); var originalVersion = LinkedVersion(source, LinkedSnapshot(source)); Set(originalVersion, "Id", versionId);
            if (!(bool)store.GetMethod("TryWriteControlledVersion", Flags).Invoke(null, new object[] { Get(source, "FullPath"), documentId, project, versionId, "A", originalHash, new FileInfo((string)Get(source, "FullPath")).Length })) throw new Exception("Could not record successful source archive");
            File.AppendAllText((string)Get(source, "FullPath"), "new saved geometry"); var changedHash = WorkingReuseHash((string)Get(source, "FullPath"));
            // Reproduce native scanning clearing identity after the selection analysis.
            Set(source, "DocumentId", null); Set(source, "OpenedVersionId", null);
            var plugin = New("PdmAddin"); SetField(plugin, "authenticatedUsername", "tester"); SetField(plugin, "currentTree", source);
            using var lifetime = new CancellationTokenSource(); SetField(plugin, "lifetime", lifetime);
            using var pane = (Control)New("PdmTaskPaneControl", "隔离身份验证"); pane.CreateControl(); SetField(plugin, "taskPaneControl", pane);
            var appType = Type("PdmAddin").GetField("application", Flags).FieldType;
            SetField(plugin, "application", new Proxy(appType, call => call.MethodName == "GetDocuments" ? Array.Empty<object>() : throw new Exception(call.MethodName)).GetTransparentProxy());
            var requests = new List<string>(); var json = new JavaScriptSerializer(); var existingStepId = Guid.NewGuid();
            using (var client = (IDisposable)New("PdmApiClient", "http://fixture.invalid/"))
            using (var http = new HttpClient(new ReferenceRecoveryHandler(request =>
            {
                var path = request.RequestUri.AbsolutePath; requests.Add(path);
                if (path.EndsWith("/registration-preflight"))
                {
                    var payload = (Dictionary<string, object>)json.DeserializeObject(request.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                    var candidates = (object[])payload["candidates"];
                    if (candidates.Length != 1) throw new Exception("原路径已有图档被当成新文件送去登记预检");
                    var candidate = (Dictionary<string, object>)candidates[0];
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json.Serialize(new[] { new { candidateKey = candidate["CandidateKey"], matchKind = Convert.ToInt32(Enum.Parse(Type("PdmAddin+RegistrationMatchKind"), "SameNameSameContent")), existingDocumentId = existingStepId, existingFileName = "SOURCE.STEP" } })) };
                }
                if (path == "/api/projects/" + project + "/documents") return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json.Serialize(new[] { new { id = existingStepId, projectId = project, fileName = "SOURCE.STEP", kind = 5, revision = new { display = "A" } } })) };
                if (path == "/api/documents/" + documentId + "/versions") return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json.Serialize(new[] { originalVersion })) };
                throw new Exception("不得新建登记、提交或额外获取权限：" + path);
            })) { BaseAddress = new Uri("http://fixture.invalid/") })
            {
                SetField(client, "httpClient", http); SetField(plugin, "apiClient", client);
                var review = (Task)TreeCall(plugin, "ReviewRegistrationDuplicatesAsync", LinkedItems(source), project); await review;
                if (((IDictionary)Get(review, "Result")).Count != 0 || requests.Count != 0 || !Equals(Get(source, "DocumentId"), documentId) || !Equals(Get(source, "OpenedVersionId"), versionId)) throw new Exception("原路径继续编辑没有恢复成功存档的同一图档身份");
                Console.WriteLine("PASS: 原路径重扫描清空身份后，提交边界恢复原图档和基准版本，不进入新建预检或增加确认");
                var planTask = (Task)TreeCall(plugin, "BuildBatchCheckInPlanAsync", LinkedItems(source), null, CancellationToken.None); await planTask;
                var planned = ((IEnumerable)Get(Get(planTask, "Result"), "Items")).Cast<object>().ToArray();
                if (planned.Length != 1 || !Equals(Get(Get(planned[0], "Node"), "DocumentId"), documentId) || requests.Any(path => path.Contains("registration"))) throw new Exception("真实修改未按原图档进入版本提交计划");
                Console.WriteLine("PASS: 修改后的源文件按同一图档进入版本提交计划，不新建图档");
                var auxiliary = LinkedNode(output, "SOURCE.STEP", "Other"); Set(auxiliary, "DocumentId", null);
                review = (Task)TreeCall(plugin, "ReviewRegistrationDuplicatesAsync", LinkedItems(auxiliary), project); await review;
                if (((IDictionary)Get(review, "Result")).Count != 0 || !Equals(Get(auxiliary, "DocumentId"), existingStepId)) throw new Exception("同名同内容辅助文件没有自动沿用目标项目已有图档");
                if (WorkingReuseHash((string)Get(source, "FullPath")) != changedHash) throw new Exception("身份恢复改写了本地源文件");
                Console.WriteLine("PASS: 同名同内容辅助文件自动沿用既有图档，无二选一弹窗，原图内容不变");
                var modelType = appType.Assembly.GetType("SolidWorks.Interop.sldworks.IModelDoc2", true);
                var native = new Model(modelType, (string)Get(source, "FullPath"), true, new List<string>());
                var memory = New("PdmAddin+ArchiveMemoryCopy", native.GetTransparentProxy(), Get(source, "FullPath"));
                var committed = LinkedVersion(source, LinkedSnapshot(source)); var committedId = Guid.NewGuid(); Set(committed, "Id", committedId); Set(committed, "Revision", LinkedRevision("W2"));
                SetField(memory, "CommittedVersion", committed); SetField(memory, "CommittedDocumentId", documentId); SetField(memory, "CommittedUser", "tester"); SetField(memory, "CommittedScope", Get(client, "EditJournalScope"));
                SetField(memory, "SourceHash", changedHash); SetField(memory, "CopyHash", Get(committed, "Sha256"));
                ((IDictionary)Field(plugin, "archiveMemoryCopies"))[(string)Get(source, "FullPath")] = memory;
                Set(source, "DocumentId", null); Set(source, "OpenedVersionId", null);
                review = (Task)TreeCall(plugin, "ReviewRegistrationDuplicatesAsync", LinkedItems(source), project); await review;
                if (!Equals(Get(source, "DocumentId"), documentId) || !Equals(Get(source, "OpenedVersionId"), committedId)) throw new Exception("原文件旧回执覆盖了当前会话已提交的W2内存基准");
                TreeCallStatic("ArchiveSourceIdentity", "RestoreForProject", source, project);
                var knownVersions = Array.CreateInstance(Type("DocumentVersionDto"), 1); knownVersions.SetValue(committed, 0);
                var baseline = (Task)TreeCall(client, "BaseVersionFromHistoryAsync", source, documentId, CancellationToken.None, knownVersions); await baseline;
                if (!Equals(Get(baseline, "Result"), committedId)) throw new Exception("下一次原图档提交未使用已确认的W2基准");
                Console.WriteLine("PASS: 多次不关图继续编辑保留最新已提交内存基准，不被原文件旧W1回执降级");
                native.Destroy(); Set(source, "DocumentId", null); Set(source, "OpenedVersionId", null);
                review = (Task)TreeCall(plugin, "ReviewRegistrationDuplicatesAsync", LinkedItems(source), project); await review;
                if (!Equals(Get(source, "OpenedVersionId"), versionId)) throw new Exception("已关闭的旧内存会话仍被作为当前基准使用");
                Console.WriteLine("PASS: 已关闭的内存会话不复用成功基准，不凭同名窗口或旧对象冒用图档");
                ((IDisposable)memory).Dispose(); ((IDictionary)Field(plugin, "archiveMemoryCopies")).Clear();
                var association = LinkedNode(output, "首次内存存档.SLDPRT", "Part"); var associationId = (Guid)Get(association, "DocumentId");
                if (!(bool)store.GetMethod("TryWriteAssociationOnly", Flags).Invoke(null, new object[] { Get(association, "FullPath"), associationId, project })) throw new Exception("首次内存存档关联未建立");
                var associationModel = new Model(modelType, (string)Get(association, "FullPath"), true, new List<string>());
                var firstMemory = New("PdmAddin+ArchiveMemoryCopy", associationModel.GetTransparentProxy(), Get(association, "FullPath"));
                var firstVersion = LinkedVersion(association, LinkedSnapshot(association));
                SetField(firstMemory, "CommittedVersion", firstVersion); SetField(firstMemory, "CommittedDocumentId", associationId); SetField(firstMemory, "CommittedUser", "tester"); SetField(firstMemory, "CommittedScope", Get(client, "EditJournalScope"));
                SetField(firstMemory, "SourceHash", WorkingReuseHash((string)Get(association, "FullPath"))); SetField(firstMemory, "CopyHash", Get(firstVersion, "Sha256"));
                ((IDictionary)Field(plugin, "archiveMemoryCopies"))[(string)Get(association, "FullPath")] = firstMemory; Set(association, "DocumentId", null);
                review = (Task)TreeCall(plugin, "ReviewRegistrationDuplicatesAsync", LinkedItems(association), project); await review;
                if (!Equals(Get(association, "DocumentId"), associationId) || !Equals(Get(association, "OpenedVersionId"), Get(firstVersion, "Id"))) throw new Exception("首次内存提交成功后被当成尚未登记的新文件");
                Console.WriteLine("PASS: 首次内存存档仅有路径关联回执时，仍凭当前会话成功收据沿用原图档与版本");
                ((IDisposable)firstMemory).Dispose(); ((IDictionary)Field(plugin, "archiveMemoryCopies")).Clear();
            }
            var session = Type("ArchiveSession").GetMethod("Start", Flags).Invoke(null, new object[] { host.Handle, CancellationToken.None }); await (Task)Get(session, "Ready");
            try
            {
                TreeCall(session, "CancelBeforeSubmission"); TreeCall(session, "FinishIfRunning");
                ArchiveUi(session, () => { var window = ArchiveWindow(session); if (((Label)Field(window, "resultState")).Text != "已取消" || !((TextBox)Field(window, "history")).Text.Contains("尚未上传或升版")) throw new Exception("提交前取消被误报为失败或未知服务器结果"); return true; });
                Console.WriteLine("PASS: 提交前取消显示中性已取消状态，明确未上传、未升版并保留本地修改");
            }
            finally { ArchiveClose(session); await (Task)Get(session, "Closed"); ((IDisposable)session).Dispose(); }
        }
        finally { bindingField.SetValue(null, oldBinding); resolver.SetValue(null, oldRoot); }
    }

    static async Task LinkedDrawingScopeScenario(string output)
    {
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        void Check(bool ok, string text) { if (!ok) throw new Exception(text); Console.WriteLine("PASS: " + text); }
        var store = Type("PdmDocumentIdentityStore"); var bindingField = store.GetField("bindingDirectory", Flags);
        var previous = bindingField.GetValue(null); bindingField.SetValue(null, Path.Combine(output, "bindings"));
        try
        {
            foreach (var mode in new[] { "correct", "stale", "explicit", "unreadable", "partial", "malformed-path", "missing", "same-name-copy", "source-path" })
            {
                var folder = Path.Combine(output, mode); Directory.CreateDirectory(folder);
                var plugin = New("PdmAddin"); var project = Guid.NewGuid(); var session = (Guid)Field(plugin, "checkoutSessionId");
                var model = LinkedNode(folder, "PART.SLDPRT", "Part"); var drawing = LinkedNode(folder, "DRAWING.SLDDRW", "Drawing");
                ((IList)Get(model, "Children")).Add(drawing); ((IList)Get(drawing, "ReferencedModels")).Add(model);
                SetField(plugin, "currentTree", model); SetField(plugin, "authenticatedUsername", "tester");
                Set(model, "CheckedOutBy", "tester"); Set(model, "CheckoutSessionId", session);
                var versions = new Dictionary<Guid, object> { [(Guid)Get(model, "DocumentId")] = LinkedVersion(model, LinkedSnapshot(model)),
                    [(Guid)Get(drawing, "DocumentId")] = LinkedVersion(drawing, LinkedSnapshot(drawing)) };
                File.AppendAllText((string)Get(model, "FullPath"), "changed geometry");
                var otherFolder = Path.Combine(folder, "other"); Directory.CreateDirectory(otherFolder);
                var actual = mode == "correct" ? (string)Get(model, "FullPath")
                    : Path.Combine(otherFolder, mode == "same-name-copy" ? "PART.SLDPRT" : "OTHER.SLDASM");
                if (mode != "missing" && mode != "correct") File.WriteAllText(actual, "actual unrelated saved model");
                if (mode == "source-path") Set(model, "SourceReferencePath", actual);
                if (mode == "explicit") Set(drawing, "RequiresModelUpdate", true);
                var calls = new List<string>(); var appType = Type("PdmAddin").GetField("application", Flags).FieldType;
                var app = new Proxy(appType, call =>
                {
                    calls.Add(call.MethodName);
                    switch (call.MethodName)
                    {
                        case "GetType": return appType;
                        case "GetDocuments": return new object[0];
                        case "GetOpenDocumentByName": return null;
                        case "GetCurrentWorkingDirectory": return folder;
                        case "SetCurrentWorkingDirectory": return true;
                        case "GetDocumentDependencies2":
                            if (mode == "unreadable") throw new IOException("fixture cannot read dependency inventory");
                            return mode == "partial" ? new[] { "OTHER", actual, "unpaired" }
                                : mode == "malformed-path" ? new[] { "OTHER", actual, "invalid", "\0" } : new[] { Path.GetFileName(actual), actual };
                        default: throw new Exception("禁止完整打开、保存或改图：" + call.MethodName);
                    }
                }).GetTransparentProxy();
                SetField(plugin, "application", app); SetField(plugin, "scanner", New("SolidWorksReferenceTreeScanner", app));
                using (var handler = new LinkedArchiveHandler(new[] { model, drawing }, versions, project, session, new List<string>(), false))
                using (var client = (IDisposable)New("PdmApiClient", "http://linked-qa.invalid/"))
                {
                    LinkedClient(plugin, client, handler);
                    var task = (Task)TreeCall(plugin, "BuildBatchCheckInPlanAsync", mode == "explicit" ? LinkedItems(model, drawing) : LinkedItems(model), null, CancellationToken.None);
                    await task;
                    var planned = ((IEnumerable)Get(Get(task, "Result"), "Items")).Cast<object>().Select(item => Get(item, "Node")).ToArray();
                    Check(planned.Contains(drawing) == (mode != "stale"), mode + "：实际无关工程图不自动带入；明确选择、正确配套或证据不完整仍保留检查");
                    Check(!calls.Any(call => call == "OpenDoc6" || call == "Save3" || call == "ActivateDoc3"), mode + "：计划核对不完整打开、保存或激活其他图纸");
                }
            }
            await LinkedDrawingMismatchScenario(Path.Combine(output, "mismatch"));
            await CheckoutTargetedJournalScenario(Path.Combine(output, "journal"));
        }
        finally { bindingField.SetValue(null, previous); }
    }

    static async Task CheckoutTargetedJournalScenario(string output)
    {
        var journal = New("EditDraftJournal", output, "fixture"); var id = Guid.NewGuid();
        var entry = New("LocalEditDraft"); Set(entry, "DocumentId", id); Set(entry, "SessionId", Guid.NewGuid()); TreeCall(journal, "Save", entry);
        var directory = (string)Field(journal, "directory");
        var other = Path.Combine(directory, Guid.NewGuid().ToString("N") + "-" + Guid.NewGuid().ToString("N") + ".xml");
        File.WriteAllText(other, "unrelated record must remain unread");
        var plugin = New("PdmAddin");
        using (var client = (IDisposable)New("PdmApiClient", "http://fixture.invalid/"))
        using (var locked = new FileStream(other, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            ((System.Net.Http.HttpClient)Field(client, "httpClient")).DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "fixture");
            SetField(plugin, "apiClient", client); SetField(plugin, "authenticatedUsername", "tester"); SetField(plugin, "editJournal", journal);
            SetField(plugin, "editJournalScope", Get(client, "EditJournalScope") + "|tester");
            await (Task)TreeCall(plugin, "FinishPendingCloseBeforeCheckoutAsync", id, CancellationToken.None);
            Console.WriteLine("PASS: 真实检出前置流程不读取其他图档被独占的历史记录");
            Set(entry, "DiscardPending", true); TreeCall(journal, "Save", entry); Exception failure = null;
            try { await (Task)TreeCall(plugin, "FinishPendingCloseBeforeCheckoutAsync", id, CancellationToken.None); }
            catch (InvalidOperationException error) { failure = error; }
            if (failure == null || !failure.Message.Contains("已放弃编辑")) throw new Exception("遗漏目标图档待恢复保护");
            Console.WriteLine("PASS: 目标图档任何会话的放弃待恢复记录仍阻止重新检出，不改写或删除历史记录");
        }
        if (File.ReadAllText(other) != "unrelated record must remain unread") throw new Exception("历史记录被修改");
    }

    static async Task LinkedDrawingMismatchScenario(string folder)
    {
        Directory.CreateDirectory(folder); var plugin = New("PdmAddin"); var session = (Guid)Field(plugin, "checkoutSessionId");
        var model = LinkedNode(folder, "EXPECTED.SLDPRT", "Part"); var drawing = LinkedNode(folder, "SELECTED.SLDDRW", "Drawing");
        var actual = LinkedNode(folder, "ACTUAL.SLDPRT", "Part");
        ((IList)Get(drawing, "ReferencedModels")).Add(model); Set(drawing, "RequiresModelUpdate", true);
        Set(drawing, "CheckedOutBy", "tester"); Set(drawing, "CheckoutSessionId", session); SetField(plugin, "authenticatedUsername", "tester");
        var appType = Type("PdmAddin").GetField("application", Flags).FieldType; var interop = appType.Assembly;
        var nativeActual = new SheetReferenceModelProxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2", true), false,
            call => call.MethodName == "GetPathName" ? Get(actual, "FullPath") : throw new Exception(call.MethodName)).GetTransparentProxy();
        var view = new AssemblyProxy(interop.GetType("SolidWorks.Interop.sldworks.View", true), call => call.MethodName == "get_ReferencedDocument" ? nativeActual
            : call.MethodName == "get_ReferencedConfiguration" ? "Default" : throw new Exception(call.MethodName)).GetTransparentProxy();
        var sheet = new Proxy(interop.GetType("SolidWorks.Interop.sldworks.Sheet", true), call => call.MethodName == "GetName" ? "SHEET" : throw new Exception(call.MethodName)).GetTransparentProxy();
        var rebuilds = 0;
        var native = new SheetReferenceModelProxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2", true), true, call =>
        {
            switch (call.MethodName)
            {
                case "GetType": return 3; case "GetPathName": return Get(drawing, "FullPath"); case "GetTitle": return "SELECTED.SLDDRW";
                case "GetSaveFlag": return false; case "GetViews": return new object[] { new object[] { null, view } };
                case "GetCurrentSheet": return sheet; case "GetSheetNames": return new[] { "SHEET" }; case "ActivateSheet": return true;
                case "ForceRebuild3": rebuilds++; return true;
                default: throw new Exception("引用不一致时不得保存或上传：" + call.MethodName);
            }
        }).GetTransparentProxy();
        var app = new Proxy(appType, call =>
        {
            switch (call.MethodName)
            {
                case "GetType": return appType;
                case "get_ActiveDoc": return null; case "GetDocuments": return new[] { native };
                case "ActivateDoc3": call.Args[3] = 0; return native;
                default: throw new Exception(call.MethodName);
            }
        }).GetTransparentProxy();
        SetField(plugin, "application", app); SetField(plugin, "scanner", New("SolidWorksReferenceTreeScanner", app));
        var plan = New("PdmAddin+BatchCheckInPlan", LinkedItems(drawing), 0, null); Exception failure = null;
        var bytes = File.ReadAllBytes((string)Get(drawing, "FullPath"));
        try { await (Task)TreeCall(plugin, "UpdateLinkedDrawingsAsync", plan, Guid.NewGuid(), CancellationToken.None); }
        catch (InvalidOperationException error) { failure = error; }
        if (failure == null || !failure.Message.Contains("实际视图") || failure.Message.Contains("被占用") || rebuilds != 1)
            throw new Exception("视图引用不一致未返回准确中文原因", failure);
        if (!bytes.SequenceEqual(File.ReadAllBytes((string)Get(drawing, "FullPath")))) throw new Exception("引用不一致时改写了原图");
        Console.WriteLine("PASS: 真正选中工程图引用不一致仍在上传前阻止，准确提示实际视图问题，原文件不变");
    }
}
