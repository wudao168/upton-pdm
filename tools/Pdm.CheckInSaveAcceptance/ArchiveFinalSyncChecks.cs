using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Diagnostics;
using System.Threading;
using System.Web.Script.Serialization;
using System.Threading.Tasks;

internal static partial class Program
{
    static void TargetedEditJournalScenario(string output)
    {
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        void Check(bool ok, string text) { if (!ok) throw new Exception(text); Console.WriteLine("PASS: " + text); }
        var journal = New("EditDraftJournal", output, "isolated-scope"); var id = Guid.NewGuid();
        for (var index = 0; index < 2; index++)
        {
            var entry = New("LocalEditDraft"); Set(entry, "DocumentId", id); Set(entry, "SessionId", Guid.NewGuid());
            Set(entry, "DraftPath", "preserved-recovery-copy"); Set(entry, "PendingClose", true); Set(entry, "RestoreOnOpen", true);
            TreeCall(journal, "Save", entry);
        }
        var directory = (string)Field(journal, "directory");
        var unrelated = Path.Combine(directory, Guid.NewGuid().ToString("N") + "-" + Guid.NewGuid().ToString("N") + ".xml");
        File.WriteAllText(unrelated, "unrelated record must not be opened");
        using (var locked = new FileStream(unrelated, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var rows = ((IEnumerable)TreeCall(journal, "ReadDocument", id)).Cast<object>().ToArray();
            Check(rows.Length == 2 && rows.All(row => (Guid)Get(row, "DocumentId") == id), "按图档读取全部会话记录，不读取被独占的其他图档记录");
            var plugin = New("PdmAddin");
            using (var client = (IDisposable)New("PdmApiClient", "http://fixture.invalid/"))
            {
                ((HttpClient)Field(client, "httpClient")).DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "fixture");
                SetField(plugin, "apiClient", client); SetField(plugin, "authenticatedUsername", "tester"); SetField(plugin, "editJournal", journal);
                SetField(plugin, "editJournalScope", Get(client, "EditJournalScope") + "|tester");
                TreeCall(plugin, "FinishLocalEdit", id);
                rows = ((IEnumerable)TreeCall(journal, "ReadDocument", id)).Cast<object>().ToArray();
                Check(rows.All(row => (bool)Get(row, "Closed") && !(bool)Get(row, "PendingClose") && !(bool)Get(row, "RestoreOnOpen")
                    && (string)Get(row, "DraftPath") == "preserved-recovery-copy"), "真实结束编辑流程保持原状态转换并保留恢复副本记录");
            }
        }
        Check(File.ReadAllText(unrelated) == "unrelated record must not be opened" && Directory.GetFiles(directory, "*.xml").Length == 3, "其他图档记录及历史文件不删除、不改写");
        var added = New("LocalEditDraft"); Set(added, "DocumentId", id); Set(added, "SessionId", Guid.NewGuid()); TreeCall(journal, "Save", added);
        Check(((IEnumerable)TreeCall(journal, "ReadDocument", id)).Cast<object>().Count() == 3, "后续新增会话可见，不沿用陈旧的批次记录缓存");
    }

    static void CommittedCopyCacheScenario(string output)
    {
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        void Check(bool ok, string text) { if (!ok) throw new Exception(text); Console.WriteLine("PASS: " + text); }
        var identity = Type("PdmDocumentIdentityStore"); var bindings = identity.GetField("bindingDirectory", Flags); var resolver = identity.GetField("workspaceRootResolver", Flags);
        var oldBindings = bindings.GetValue(null); var oldRoot = resolver.GetValue(null);
        var workspace = Path.Combine(output, "workspace"); Directory.CreateDirectory(workspace);
        bindings.SetValue(null, Path.Combine(output, "bindings")); resolver.SetValue(null, new Func<string>(() => workspace));
        try
        {
            var original = Path.Combine(output, "PART.SLDPRT"); var copy = Path.Combine(output, "fixed-upload.SLDPRT");
            File.WriteAllText(original, "source contains later local edits"); File.WriteAllText(copy, "exact committed bytes");
            var version = New("DocumentVersionDto"); Set(version, "Id", Guid.NewGuid()); Set(version, "Sha256", WorkingReuseHash(copy));
            Set(version, "FileLength", new FileInfo(copy).Length); Set(version, "Revision", LinkedRevision("V2"));
            var doc = Guid.NewGuid(); var project = Guid.NewGuid();
            string Cache(string upload) => (string)Type("PdmAddin").GetMethod("CacheCheckedInVersionInProjectViewCore", Flags).Invoke(null,
                new object[] { workspace, "P-CACHE", original, doc, project, version, upload });
            string target;
            using (var lockedSource = new FileStream(original, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) target = Cache(copy);
            Check(!string.IsNullOrWhiteSpace(target) && File.ReadAllText(target) == "exact committed bytes", "缓存使用已提交固定副本，不再次读取被占用的原始源文件");
            Check(File.ReadAllText(original) == "source contains later local edits" && Path.GetFileName(target) == Path.GetFileName(original), "源文件后续修改与原文件名保持不变");
            Check(Cache(copy) == target, "已有相同内容缓存复用同一路径");
            File.SetAttributes(target, FileAttributes.Normal); File.WriteAllText(target, "existing local changes");
            Check(Cache(copy) == "" && File.ReadAllText(target) == "existing local changes", "已有不同内容缓存不覆盖");
            File.WriteAllText(copy, "tampered upload bytes");
            Check(Cache(copy) == "" && File.ReadAllText(target) == "existing local changes", "上传副本不匹配服务器回执时拒绝缓存，不覆盖已有内容");
            Check(!Directory.GetFiles(output, "*.pdm-cache", SearchOption.AllDirectories).Any(), "同步完成或拒绝后不遗留半成品缓存");
        }
        finally { bindings.SetValue(null, oldBindings); resolver.SetValue(null, oldRoot); }
    }

    static async Task BackgroundArchiveSyncScenario(string output)
    {
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        void Check(bool ok, string text) { if (!ok) throw new Exception(text); Console.WriteLine("PASS: " + text); }
        var identity = Type("PdmDocumentIdentityStore"); var bindings = identity.GetField("bindingDirectory", Flags); var resolver = identity.GetField("workspaceRootResolver", Flags);
        var settingsPath = assembly.GetType("Upton.Pdm.LocalSettings.WorkspaceSettingsStore", true).GetField("SettingsPath", Flags);
        var oldBindings = bindings.GetValue(null); var oldRoot = resolver.GetValue(null); var oldSettings = settingsPath.GetValue(null);
        var workspace = Path.Combine(output, "workspace"); Directory.CreateDirectory(workspace);
        var settingsFile = Path.Combine(output, "settings.json"); File.WriteAllText(settingsFile, new JavaScriptSerializer().Serialize(new { WorkspaceRoot = workspace }));
        bindings.SetValue(null, Path.Combine(output, "bindings")); resolver.SetValue(null, new Func<string>(() => workspace)); settingsPath.SetValue(null, settingsFile);
        try
        {
            var source = Path.Combine(output, "MODEL.SLDPRT"); var copy = Path.Combine(output, "upload.SLDPRT");
            File.WriteAllText(source, "committed bytes"); File.Copy(source, copy);
            var version = New("DocumentVersionDto"); Set(version, "Id", Guid.NewGuid()); Set(version, "Sha256", WorkingReuseHash(source));
            Set(version, "FileLength", new FileInfo(source).Length); Set(version, "Revision", LinkedRevision("V2"));
            var documentId = Guid.NewGuid(); var projectId = Guid.NewGuid(); var plugin = New("PdmAddin");
            SetField(plugin, "authenticatedUsername", "tester");
            var project = New("ProjectDto"); Set(project, "Id", projectId); Set(project, "Code", "P-SYNC");
            var projects = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(Type("ProjectDto"))); projects.Add(project); SetField(plugin, "availableProjects", projects);
            Task Sync() => (Task)TreeCall(plugin, "RememberControlledVersionIdentityAndManifestAsync", source, documentId, projectId, version, copy);
            var bindingFile = (string)identity.GetMethod("BindingPath", Flags).Invoke(null, new object[] { source });
            using (var gate = new Mutex(false, @"Local\UPTON-PDM-Binding-" + Path.GetFileNameWithoutExtension(bindingFile)))
            {
                gate.WaitOne(); Task work; var elapsed = Stopwatch.StartNew();
                try
                {
                    work = Sync();
                    await Task.Delay(150);
                    Check(elapsed.ElapsedMilliseconds < 2000 && !work.IsCompleted, "后台身份写入等待磁盘锁时，界面线程仍可继续响应");
                }
                finally { gate.ReleaseMutex(); }
                await work; Check(string.IsNullOrEmpty((string)Field(Get(work, "Result"), "Warning")), "释放锁后完成身份与缓存同步，不提前宣布完成");
            }
            var cached = Directory.GetFiles(workspace, "MODEL.SLDPRT", SearchOption.AllDirectories).Single();
            Check(File.ReadAllText(cached) == "committed bytes" && ((IDictionary)Field(plugin, "completedWorkingArchives")).Count == 1, "后台完成后更新工作副本版本记录及正确缓存");
            File.SetAttributes(cached, FileAttributes.Normal); File.WriteAllText(cached, "keep existing local edits");
            var failed = Sync(); await failed; var result = Get(failed, "Result");
            Check(!string.IsNullOrEmpty((string)Field(result, "Warning")) && (bool)Field(result, "PreserveWarning") && File.ReadAllText(cached) == "keep existing local edits", "缓存冲突返回持续提醒，不能被后续版本基准推进掩盖");
            File.WriteAllText(source, "later source edits"); var changed = Sync(); await changed;
            Check(!string.IsNullOrEmpty((string)Field(Get(changed, "Result"), "Warning")) && File.ReadAllText(source) == "later source edits", "提交后新增源文件修改不误报为已存档、不覆盖");
            File.WriteAllText(source, "committed bytes");
            var switched = New("PdmAddin"); SetField(switched, "authenticatedUsername", "tester"); SetField(switched, "availableProjects", projects);
            using (var gate = new Mutex(false, @"Local\UPTON-PDM-Binding-" + Path.GetFileNameWithoutExtension(bindingFile)))
            {
                gate.WaitOne(); Task work;
                try
                {
                    work = (Task)TreeCall(switched, "RememberControlledVersionIdentityAndManifestAsync", source, documentId, projectId, version, copy);
                    await Task.Delay(100); SetField(switched, "authenticatedUsername", "other-user");
                }
                finally { gate.ReleaseMutex(); }
                await work;
                Check((bool)Field(Get(work, "Result"), "PreserveWarning") && ((IDictionary)Field(switched, "completedWorkingArchives")).Count == 0,
                    "等待后台读写时账号变化，不把旧账号结果写入新账号内存状态");
            }
            var jobType = Type("PdmAddin").GetNestedType("ArchiveLocalSyncJob", Flags);
            var jobs = Array.CreateInstance(jobType, 16);
            for (var i = 0; i < jobs.Length; i++)
            {
                var path = Path.Combine(output, "BATCH-" + i + ".SLDPRT"); var upload = Path.Combine(output, "COPY-" + i + ".SLDPRT");
                File.WriteAllText(path, "batch committed " + i); File.Copy(path, upload);
                var receipt = New("DocumentVersionDto"); Set(receipt, "Id", Guid.NewGuid()); Set(receipt, "Sha256", WorkingReuseHash(path));
                Set(receipt, "FileLength", new FileInfo(path).Length); Set(receipt, "Revision", LinkedRevision("V2"));
                jobs.SetValue(TreeCall(plugin, "CreateArchiveLocalSyncJob", path, Guid.NewGuid(), projectId, receipt, upload, WorkingReuseHash(path), true, null), i);
            }
            var batch = (Task)Type("PdmAddin").GetMethod("RunArchiveLocalSyncJobsAsync", Flags).Invoke(null, new object[] { jobs });
            Check(batch.Wait(10000), "主线程不处理回调时，16个文件的全部磁盘步骤仍能完成");
            var outcomes = (Array)Get(batch, "Result");
            Check(outcomes.Cast<object>().All(item => string.IsNullOrEmpty((string)Field(item, "Warning"))), "批量后台同步没有逐文件主线程依赖");
            var firstJob = jobs.GetValue(0); var firstResult = outcomes.GetValue(0); var changedPath = (string)Field(firstJob, "Path");
            File.AppendAllText(changedPath, " changed while queued");
            Check((bool)Type("PdmAddin").GetMethod("ArchiveSyncSourceChanged", Flags).Invoke(null, new[] { firstJob, firstResult }), "主线程排队期间的文件变化触发重新核对");
            await Task.Run(() => Type("PdmAddin").GetMethod("RefreshArchiveSyncSource", Flags).Invoke(null, new[] { firstJob, firstResult }));
            Check((bool)Field(firstResult, "LaterDiskChanges") && File.ReadAllText(changedPath).EndsWith(" changed while queued"), "重新核对保留排队期间新增修改，不复用过期结果");
            var memorySource = Path.Combine(workspace, "READONLY-MEMORY.SLDPRT"); var memoryUpload = Path.Combine(output, "memory-upload.SLDPRT");
            File.WriteAllText(memorySource, "older original bytes"); File.WriteAllText(memoryUpload, "current memory content");
            File.SetAttributes(memorySource, FileAttributes.ReadOnly);
            var memoryVersion = New("DocumentVersionDto"); var memoryId = Guid.NewGuid();
            Set(memoryVersion, "Id", Guid.NewGuid()); Set(memoryVersion, "Sha256", WorkingReuseHash(memoryUpload)); Set(memoryVersion, "FileLength", new FileInfo(memoryUpload).Length); Set(memoryVersion, "Revision", LinkedRevision("V2"));
            var memoryJob = TreeCall(plugin, "CreateArchiveLocalSyncJob", memorySource, memoryId, projectId, memoryVersion, memoryUpload, WorkingReuseHash(memorySource), true, null);
            SetField(memoryJob, "MemorySnapshot", true); var memoryJobs = Array.CreateInstance(jobType, 1); memoryJobs.SetValue(memoryJob, 0);
            var memoryWork = (Task)Type("PdmAddin").GetMethod("RunArchiveLocalSyncJobsAsync", Flags).Invoke(null, new object[] { memoryJobs }); await memoryWork;
            var memoryOutcome = ((Array)Get(memoryWork, "Result")).GetValue(0);
            var metadataArgs = new object[] { memorySource, null }; var readMetadata = (bool)identity.GetMethod("TryReadLocalMetadata", Flags).Invoke(null, metadataArgs);
            Check(readMetadata && (Guid)Get(metadataArgs[1], "DocumentId") == memoryId && Get(metadataArgs[1], "VersionId") == null,
                "内存副本提交只记本机图档身份，不把未改写原文件登记为新版本");
            var streamArgs = new object[] { memorySource, Guid.Empty };
            Check(!(bool)identity.GetMethod("TryReadUnverifiedIdentity", Flags).Invoke(null, streamArgs)
                && File.ReadAllText(memorySource) == "older original bytes" && (File.GetAttributes(memorySource) & FileAttributes.ReadOnly) != 0,
                "身份关联仅写本机记录，不向只读原文件写入数据流或改变属性");
            var memoryCached = Directory.GetFiles(Path.Combine(workspace, "View"), "READONLY-MEMORY.SLDPRT", SearchOption.AllDirectories).Single();
            Check(string.IsNullOrEmpty((string)Field(memoryOutcome, "Warning")) && File.ReadAllText(memoryCached) == "current memory content",
                "缓存保存服务器已确认的内存副本，不缓存旧原文件内容");
            File.SetAttributes(memorySource, FileAttributes.Normal);
        }
        finally { bindings.SetValue(null, oldBindings); resolver.SetValue(null, oldRoot); settingsPath.SetValue(null, oldSettings); }
    }
}
