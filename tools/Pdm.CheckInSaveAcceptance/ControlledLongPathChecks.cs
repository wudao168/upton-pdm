using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

internal static partial class Program
{
    private static async Task ControlledLongPathScenario(string output)
    {
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        var identity = Type("PdmDocumentIdentityStore");
        var bindings = identity.GetField("bindingDirectory", Flags);
        var resolver = identity.GetField("workspaceRootResolver", Flags);
        var oldBindings = bindings.GetValue(null);
        var oldResolver = resolver.GetValue(null);

        const string projectCode = "P-QA-LONG-PATH-001";
        const string rootName = "ROOT.SLDASM";
        var workingRoot = Path.Combine(output, "w");
        var snapshotRoot = Path.Combine(output, "r");
        var downloadRoot = Path.Combine(workingRoot, ".uplm", "Staging", "working-" + new string('a', 32));
        var longNameLength = 258 - downloadRoot.Length - 1;
        const string drawingExtension = ".SLDDRW";
        if (longNameLength < 120 || longNameLength > 248)
            throw new ArgumentException("长路径验收请使用短的隔离输出目录。");

        var title = string.Concat(Enumerable.Repeat("设备夹具 ", longNameLength));
        var longName = title.Substring(0, longNameLength - drawingExtension.Length) + drawingExtension;
        var oldDownloadTarget = Path.Combine(downloadRoot, longName);
        var oldWorkingTarget = Path.Combine(workingRoot, "View", projectCode, "装配工作区", new string('a', 32), longName);
        var oldSnapshotTarget = Path.Combine(snapshotRoot, ".uplm", "Snapshots", projectCode,
            new string('a', 32), "Latest", new string('a', 32), longName);
        Require(oldWorkingTarget.Length >= 260 && oldSnapshotTarget.Length >= 260
            && oldDownloadTarget.Length == 258 && (oldDownloadTarget + ".download").Length >= 260,
            "验收文件必须同时复现旧受控目录超长和下载临时后缀超长");
        var rejectsLongPath = false;
        try { _ = new FileInfo(oldDownloadTarget + ".download"); }
        catch (PathTooLongException) { rejectsLongPath = true; }
        Require(rejectsLongPath, "验收进程应强制启用与报错一致的 .NET Framework 旧路径限制");

        var source = Path.Combine(output, "source");
        Directory.CreateDirectory(source);
        var sourceRoot = Path.Combine(source, rootName);
        var sourceDrawing = Path.Combine(source, longName);
        File.WriteAllText(sourceRoot, "已存档装配体原始内容");
        File.WriteAllText(sourceDrawing, "已存档工程图原始内容");
        var sourceHash = WorkingReuseHash(sourceDrawing);
        var sourceTime = File.GetLastWriteTimeUtc(sourceDrawing);

        using (var client = (IDisposable)New("PdmApiClient", "http://long-path-fixture.invalid/"))
        using (var handler = new CacheDownloadHandler())
        using (var http = new HttpClient(handler) { BaseAddress = new Uri("http://long-path-fixture.invalid/") })
        {
            SetField(client, "httpClient", http);
            SetField(client, "controlledOpenHttpClient", http);
            object Manager(string path) => New("ControlledWorkspaceManager", client, path);
            object Manifest(params string[] paths)
            {
                var manifest = New("ControlledOpenManifestDto");
                Set(manifest, "Id", Guid.NewGuid());
                Set(manifest, "ProjectId", Guid.NewGuid());
                Set(manifest, "ProjectCode", projectCode);
                var entries = (IList)Activator.CreateInstance(Property(manifest, "Files").PropertyType);
                for (var index = 0; index < paths.Length; index++)
                {
                    var path = paths[index];
                    var entry = New("ControlledOpenFileDto");
                    var id = Guid.NewGuid();
                    Set(entry, "DocumentId", id);
                    Set(entry, "VersionId", Guid.NewGuid());
                    Set(entry, "FileName", Path.GetFileName(path));
                    Set(entry, "RelativePath", Path.GetFileName(path));
                    Set(entry, "Revision", "W1");
                    Set(entry, "Sha256", WorkingReuseHash(path));
                    Set(entry, "FileLength", new FileInfo(path).Length);
                    Set(entry, "IsRoot", index == 0);
                    entries.Add(entry);
                    handler.Bytes.Add(id, File.ReadAllBytes(path));
                }
                Set(manifest, "Files", entries);
                Set(manifest, "RootRelativePath", Get(entries[0], "RelativePath"));
                Set(manifest, "RootDocumentId", Get(entries[0], "DocumentId"));
                Set(manifest, "RootVersionId", Get(entries[0], "VersionId"));
                return manifest;
            }
            async Task<string> Prepare(object manager, object manifest, bool readOnly)
            {
                var operation = readOnly ? "PrepareReadOnlyAsync" : "PrepareWorkingCopyAsync";
                var task = (Task)TreeCall(manager, operation, manifest, readOnly ? (object)false : Array.Empty<Guid>(), CancellationToken.None);
                await task;
                return (string)Get(task, "Result");
            }
            void CheckDrawing(string path, string scenario)
            {
                Require(path.Length < 260 && Path.GetDirectoryName(path).Length < 248, scenario + "的路径满足旧版 SolidWorks 环境");
                Require(Path.GetFileName(path) == longName && WorkingReuseHash(path) == sourceHash,
                    scenario + "完整保留中文、空格文件名及图档内容");
            }

            try
            {
                bindings.SetValue(null, Path.Combine(output, "bindings"));
                resolver.SetValue(null, new Func<string>(() => output));
                var manifest = Manifest(sourceRoot, sourceDrawing);
                var drawing = ((IList)Get(manifest, "Files"))[1];
                var manager = Manager(workingRoot);
                var working = await Prepare(manager, manifest, false);
                Require(handler.Requests == 2 && WorkingReuseHash(working) == WorkingReuseHash(sourceRoot),
                    "首次获取装配体时从服务器取回选中的两个文件");
                var workingDrawing = Path.Combine(Path.GetDirectoryName(working), longName);
                CheckDrawing(workingDrawing, "装配体中的长文件名工程图");
                var metadata = new object[] { workingDrawing, null };
                Require((bool)identity.GetMethod("TryReadLocalMetadata", Flags).Invoke(null, metadata)
                    && (Guid)Get(metadata[1], "VersionId") == (Guid)Get(drawing, "VersionId"),
                    "短路径工作副本绑定实际工程图归档版本");
                Require((bool)identity.GetMethod("IsControlledWorkspacePath", Flags).Invoke(null, new object[] { workingDrawing })
                    && (bool)identity.GetMethod("IsProjectWorkspacePath", Flags).Invoke(null, new object[] { workingRoot, projectCode, workingDrawing }),
                    "短路径属于原项目受控工作区，可被后续获取及放弃流程识别");
                var cached = (string[])Type("ControlledCacheMaintenance").GetMethod("FindDirectories", Flags).Invoke(null, new object[] {
                    workingRoot, Get(manifest, "ProjectId"), Get(drawing, "DocumentId") });
                Require(cached.Contains(Path.GetDirectoryName(working), StringComparer.OrdinalIgnoreCase),
                    "短路径工作区进入原缓存索引，无需重复下载或留下不受管副本");
                Require((string)Type("ControlledReferenceMaterializer").GetMethod("LocalPath", Flags).Invoke(null, new object[] {
                    Path.GetDirectoryName(working), drawing }) == workingDrawing, "参考图档解析沿用相同受控路径");
                var reopened = Manager(workingRoot);
                Require(await Prepare(reopened, manifest, false) == working && handler.Requests == 2
                    && (string)TreeCall(reopened, "GetWorkingFilePath", manifest, Get(drawing, "DocumentId")) == workingDrawing,
                    "重建管理器后仍获取同一受控工作区，无需再次下载");
                Require((string)TreeCall(reopened, "ApplyWorkingPermissions", manifest,
                        new[] { (Guid)Get(drawing, "DocumentId") }, false) == working
                    && (File.GetAttributes(workingDrawing) & FileAttributes.ReadOnly) == 0
                    && (File.GetAttributes(working) & FileAttributes.ReadOnly) != 0,
                    "获取编辑权限复用受控路径，只改变选中文件的本地可写属性");

                var drawingManifest = Manifest(sourceDrawing);
                var readOnlyDrawing = await Prepare(Manager(snapshotRoot), drawingManifest, true);
                CheckDrawing(readOnlyDrawing, "直接只读打开长文件名工程图");
                Require((File.GetAttributes(readOnlyDrawing) & FileAttributes.ReadOnly) != 0
                    && handler.Requests == 3 && await Prepare(Manager(snapshotRoot), drawingManifest, true) == readOnlyDrawing
                    && handler.Requests == 3,
                    "独立只读打开及重开复用同一快照，保持只读属性");

                var download = (Task)TreeCall(client, "DownloadControlledOpenFileAsync", drawing, downloadRoot, CancellationToken.None);
                await download;
                Require((string)Get(download, "Result") == oldDownloadTarget && handler.Requests == 4
                    && Directory.EnumerateFiles(downloadRoot).Single() == oldDownloadTarget,
                    "可用的原路径无需缩短，下载临时文件路径单独缩短并清理");
                CheckDrawing(oldDownloadTarget, "260 字符边界附近的受控下载");

                var ordinary = Manifest(sourceRoot);
                var ordinaryRoot = Path.Combine(output, "regular");
                var legacy = Path.Combine(ordinaryRoot, "View", projectCode, "装配工作区", ((Guid)Get(ordinary, "RootDocumentId")).ToString("N"));
                Directory.CreateDirectory(legacy);
                var existing = Path.Combine(legacy, rootName);
                File.Copy(sourceRoot, existing);
                Require((bool)identity.GetMethod("TryWriteControlledVersion", Flags).Invoke(null, new object[] {
                    existing, Get(ordinary, "RootDocumentId"), Get(ordinary, "ProjectId"), Get(ordinary, "RootVersionId"), "W1",
                    WorkingReuseHash(existing), new FileInfo(existing).Length }), "预先建立普通旧布局受控缓存");
                Require(await Prepare(Manager(ordinaryRoot), ordinary, false) == existing && handler.Requests == 4,
                    "旧布局里长度允许的已验证副本继续沿用，保持原路径且无需下载");

                var outside = Path.Combine(output, "escape.SLDDRW");
                File.WriteAllText(outside, "受控工作区外的文件");
                var outsideHash = WorkingReuseHash(outside);
                var invalid = Manifest(outside);
                Set(((IList)Get(invalid, "Files"))[0], "RelativePath", @"..\escape.SLDDRW");
                Exception failure = null;
                try { await Prepare(Manager(Path.Combine(output, "invalid")), invalid, false); }
                catch (Exception error) { failure = error is TargetInvocationException ? error.InnerException : error; }
                Require(failure is InvalidDataException && handler.Requests == 4 && WorkingReuseHash(outside) == outsideHash,
                    "受控清单越界路径在下载前拒绝，外部文件保持原样");

                var resolve = Type("ManagedCadPaths").GetMethod("Resolve", Flags);
                var issue = assembly.GetType("Upton.Pdm.Domain.DocumentMetadataLimits", true).GetMethod("GetArchiveFileNameIssue");
                var rename = Type("BatchRenameRule").GetMethod("NormalizeFileBaseName", Flags);
                bool Rejects(System.Reflection.MethodInfo method, params object[] arguments)
                {
                    try { method.Invoke(null, arguments); return false; }
                    catch (TargetInvocationException error) when (error.InnerException.Message.Contains("保留名称")) { return true; }
                }
                foreach (var name in new[] { "CON.SLDDRW", "nul.tar.gz", "COM¹.SLDPRT", "lpt9.SLDASM" })
                {
                    Require(Rejects(resolve, ordinaryRoot, name) && Rejects(rename, name, Path.GetExtension(name))
                        && (issue.Invoke(null, new object[] { name }) as string)?.Contains("保留名称") == true,
                        "存档、受控打开和重命名都能提前拒绝 Windows 保留名称 " + name);
                }
                Require(Rejects(resolve, ordinaryRoot, @"PRN\模型.SLDPRT") && handler.Requests == 4,
                    "保留目录名称在取回图档前被拦截");
                foreach (var name in new[] { "COM10.SLDPRT", "CONveyor.SLDASM", "设备 夹具.SLDDRW" })
                    Require((string)resolve.Invoke(null, new object[] { ordinaryRoot, name }) == Path.Combine(ordinaryRoot, name)
                        && issue.Invoke(null, new object[] { name }) == null,
                        "相似普通文件名和中文空格保持原名 " + name);

                var previewWorkspace = Path.Combine(output, "preview");
                resolver.SetValue(null, new Func<string>(() => previewWorkspace));
                var compactDirectory = Type("ManagedCadPaths").GetMethod("CompactDirectory", Flags);
                var pluginType = Type("PdmAddin");
                var plugin = New("PdmAddin");
                foreach (var kind in new[] { "r", "l", "d", "w" })
                {
                    var directory = (string)compactDirectory.Invoke(null, new object[] { previewWorkspace, kind, "归档身份", "测试项目 有空格" });
                    var nested = Path.Combine(directory, "引用目录", rootName);
                    bool HasKind(string classifier, string path) => (bool)pluginType.GetMethod(classifier, Flags).Invoke(null, new object[] { path });
                    foreach (var path in new[] { directory, nested })
                    {
                        Require(HasKind("IsHistoricalPreviewPath", path) == (kind == "r")
                            && HasKind("IsLatestReadOnlyPreviewPath", path) == (kind == "l")
                            && HasKind("IsHistoricalDerivedPath", path) == (kind == "d"),
                            "受控短目录及其子图档识别同一操作范围 " + kind + " " + (path == directory ? "目录" : "文件"));
                    }
                    var node = New("CadTreeNode");
                    Set(node, "Kind", Enum.Parse(Type("CadDocumentKind"), "Assembly"));
                    Set(node, "FullPath", nested);
                    TreeCall(plugin, "MarkReadOnlyPreview", node, false, false);
                    Require((bool)Get(node, "IsHistoricalPreview") == (kind == "r")
                        && (bool)Get(node, "IsLatestReadOnlyPreview") == (kind == "l"),
                        "设计树标记与受控短目录权限语义一致 " + kind);
                }

                Require(WorkingReuseHash(sourceDrawing) == sourceHash && File.GetLastWriteTimeUtc(sourceDrawing) == sourceTime,
                    "获取、只读打开和编辑权限变更均未改写原图档");
            }
            finally
            {
                bindings.SetValue(null, oldBindings);
                resolver.SetValue(null, oldResolver);
            }
        }

        void Require(bool success, string label)
        {
            if (!success) throw new InvalidOperationException(label);
            Console.WriteLine("PASS: " + label);
        }
    }
}
