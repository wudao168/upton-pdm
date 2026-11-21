using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

internal static partial class Program
{
    static async Task UnchangedPartDiscardScenario()
    {
        var root = Path.Combine(Path.GetTempPath(), "pdm-unchanged-part-discard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var identity = Type("PdmDocumentIdentityStore");
        var bindingField = identity.GetField("bindingDirectory", Flags);
        var resolverField = identity.GetField("workspaceRootResolver", Flags);
        var oldBinding = bindingField.GetValue(null);
        var oldResolver = resolverField.GetValue(null);
        bindingField.SetValue(null, Path.Combine(root, "bindings"));
        resolverField.SetValue(null, new Func<string>(() => root));
        try
        {
            foreach (var scenario in new[] { "verified-unmodified", "saved-edit", "memory-edit", "wrong-configuration",
                "foreign-driving-reference", "wrong-driving-version" })
                await CheckUnchangedPartDiscardAsync(root, scenario);
        }
        finally
        {
            bindingField.SetValue(null, oldBinding);
            resolverField.SetValue(null, oldResolver);
            foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, true);
        }
    }

    private static async Task CheckUnchangedPartDiscardAsync(string root, string scenario)
    {
        var folder = Path.Combine(root, "View", scenario);
        var outside = Path.Combine(root, "history", scenario);
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(outside);
        var bytes = Encoding.UTF8.GetBytes("Verified W2 working bytes");
        var manifest = SpeedManifest("TARGET.SLDPRT", bytes);
        var files = (IList)Get(manifest, "Files");
        while (files.Count > 3) files.RemoveAt(files.Count - 1);
        var part = files[0];
        var environment = files[1];
        var driver = files[2];
        Set(environment, "FileName", "ASSEMBLY-ENVIRONMENT.SLDASM");
        Set(environment, "RelativePath", Get(environment, "FileName"));
        Set(driver, "FileName", "DRIVER.SLDPRT");
        Set(driver, "RelativePath", Get(driver, "FileName"));
        Set(part, "Configuration", "AssemblyOccurrence");
        var project = (Guid)Get(manifest, "ProjectId");
        foreach (var file in files.Cast<object>())
        {
            var path = Path.Combine(folder, (string)Get(file, "FileName"));
            File.WriteAllBytes(path, bytes);
            var version = ReferenceEquals(file, driver) && scenario == "wrong-driving-version"
                ? Guid.NewGuid() : (Guid)Get(file, "VersionId");
            TreeCheck((bool)TreeCallStatic("PdmDocumentIdentityStore", "TryWriteControlledVersion", path,
                Get(file, "DocumentId"), project, version, Get(file, "Revision"), Get(file, "Sha256"), (long)bytes.Length),
                "登记隔离测试工作副本");
        }
        var pathToPart = Path.Combine(folder, (string)Get(part, "FileName"));
        var historicalAddress = Path.Combine(outside, "OldUnlisted.SLDPRT");
        var environmentPath = Path.Combine(outside, (string)Get(environment, "FileName"));
        var driverPath = Path.Combine(scenario == "foreign-driving-reference" ? outside : folder, (string)Get(driver, "FileName"));
        File.WriteAllText(historicalAddress, "Existing file at the stored historical address");
        File.WriteAllText(environmentPath, "Open assembly environment outside this operation");
        if (scenario == "foreign-driving-reference") File.WriteAllText(driverPath, "Unexpected actual reference");
        var plugin = New("PdmAddin");
        var appType = Type("PdmAddin").GetField("application", Flags).FieldType;
        var interop = appType.Assembly;
        var modelType = interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2", true);
        var diskAttributes = File.GetAttributes(pathToPart);
        var actualConfiguration = scenario == "wrong-configuration" ? "Wrong" : "Default";
        var configuration = new Proxy(interop.GetType("SolidWorks.Interop.sldworks.Configuration", true),
            call => call.MethodName == "get_Name" ? actualConfiguration : throw new Exception(call.MethodName)).GetTransparentProxy();
        var manager = new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ConfigurationManager", true),
            call => call.MethodName == "get_ActiveConfiguration" ? configuration : throw new Exception(call.MethodName)).GetTransparentProxy();
        var extension = new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDocExtension", true),
            call => call.MethodName == "ListExternalFileReferences2" ? null : throw new Exception(call.MethodName)).GetTransparentProxy();
        var readOnly = false;
        var directDependencyReads = 0;
        object ModelFor(string path, int kind) => new SheetReferenceModelProxy(modelType, false, call =>
        {
            switch (call.MethodName)
            {
                case "GetPathName": return path;
                case "GetTitle": return Path.GetFileName(path);
                case "GetType": return kind;
                case "GetSaveFlag": return path == pathToPart && scenario == "memory-edit";
                case "IsOpenedReadOnly": return readOnly;
                case "SetReadOnlyState":
                    TreeCheck(path == pathToPart, "放弃零件未改变其他图档权限");
                    readOnly = (bool)call.Args[0];
                    return true;
                case "get_ConfigurationManager": return manager;
                case "get_Extension": return extension;
                case "GetEquationMgr":
                case "GetDesignTable": return null;
                default: throw new Exception("不应调用 SolidWorks 方法 " + call.MethodName);
            }
        }).GetTransparentProxy();
        var loaded = ModelFor(pathToPart, 1);
        var loadedModels = new[] { loaded, ModelFor(environmentPath, 2), ModelFor(driverPath, 1) };
        SetField(plugin, "application", new Proxy(appType, call =>
        {
            switch (call.MethodName)
            {
                case "GetDocuments": return loadedModels;
                case "GetDocumentDependencies2":
                    directDependencyReads++;
                    return new[] { Path.GetFileName(historicalAddress), historicalAddress };
                default: throw new Exception("不应关闭、重开或改写图档 " + call.MethodName);
            }
        }).GetTransparentProxy());

        var versionsRead = new List<Guid>();
        var json = new JavaScriptSerializer();
        object Reference(object file, int kind, bool context) => new
        {
            documentId = Get(file, "DocumentId"), fileName = Get(file, "FileName"), kind,
            role = 1, isContextReference = context, configuration = "Default", children = Array.Empty<object>()
        };
        using var client = (IDisposable)New("PdmApiClient", "http://unchanged-part.invalid/");
        using var lifetime = new CancellationTokenSource();
        using var handler = new ReferenceRecoveryHandler(request =>
        {
            var url = request.RequestUri.AbsolutePath;
            TreeCheck(request.Method == HttpMethod.Get && url.EndsWith("/versions", StringComparison.Ordinal),
                "只读取存档版本凭据，不下载文件或写服务端");
            var id = Guid.Parse(url.Split('/').Reverse().Skip(1).First());
            versionsRead.Add(id);
            var file = files.Cast<object>().Single(candidate => Get(candidate, "DocumentId").Equals(id));
            var revision = new
            {
                id = Get(file, "VersionId"), documentId = id, sha256 = Get(file, "Sha256"), fileLength = (long)bytes.Length,
                referenceSnapshot = new
                {
                    documentId = id, fileName = Get(file, "FileName"), kind = 1, configuration = "Default",
                    children = ReferenceEquals(file, part) ? new[] { Reference(environment, 0, true), Reference(driver, 1, false) } : Array.Empty<object>()
                }
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json.Serialize(new[] { revision })) };
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://unchanged-part.invalid/") };
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "fixture");
        SetField(client, "httpClient", http);
        SetField(client, "controlledOpenHttpClient", http);
        SetField(plugin, "apiClient", client);
        SetField(plugin, "lifetime", lifetime);
        SetField(plugin, "authenticatedUsername", "tester");
        var scope = (string)Get(client, "EditJournalScope") + "|tester";
        var journal = New("EditDraftJournal", Path.Combine(folder, "journal"), scope);
        SetField(plugin, "editJournal", journal);
        SetField(plugin, "editJournalScope", scope);
        var draft = New("LocalEditDraft");
        Set(draft, "DocumentId", Get(part, "DocumentId"));
        Set(draft, "ProjectId", project);
        Set(draft, "SessionId", Field(plugin, "checkoutSessionId"));
        Set(draft, "SourcePath", pathToPart);
        Set(draft, "ObservedOpen", true);
        Set(draft, "BaseVersionId", Get(part, "VersionId"));
        Set(draft, "BaseSha256", Get(part, "Sha256"));
        DiscardCall(journal, "MarkDiscarded", draft);

        if (scenario == "verified-unmodified")
        {
            await (Task)DiscardCall(plugin, "RestoreDiscardedWorkingPathsAsync", manifest, new[] { pathToPart });
            var record = ((IEnumerable)DiscardCall(journal, "ReadAll")).Cast<object>().Single();
            TreeCheck(readOnly && File.GetAttributes(pathToPart).HasFlag(FileAttributes.ReadOnly)
                && !(bool)Get(record, "DiscardPending") && File.ReadAllBytes(pathToPart).SequenceEqual(bytes),
                "无改动零件在原窗口变为只读并完成待恢复标记，保留原字节");
            TreeCheck(versionsRead.SequenceEqual(new[] { (Guid)Get(part, "DocumentId"), (Guid)Get(driver, "DocumentId") }),
                "已验证的装配环境不需读取版本，真实设计依赖仍需读取");
        }
        else
        {
            if (scenario == "saved-edit") File.AppendAllText(pathToPart, " actual edit");
            var observed = File.ReadAllBytes(pathToPart);
            var binding = TreeCallStatic("PdmAddin", "ValidateDiscardBinding", draft);
            var protectedState = false;
            var verification = (Task)DiscardCall(plugin, "KeepUnchangedPartWorkingReferencesAsync", loaded, draft, part,
                manifest, Get(part, "Sha256"), binding);
            try
            {
                await verification;
                protectedState = !(bool)Get(verification, "Result");
            }
            catch (Exception error) when (scenario == "foreign-driving-reference" && error is InvalidOperationException
                && error.Message.Contains("另一目录的同名图档") || scenario == "wrong-driving-version" && error is IOException
                && error.Message.Contains("版本关联需要核对"))
            {
                protectedState = true;
            }
            TreeCheck(protectedState && !readOnly && File.GetAttributes(pathToPart) == diskAttributes
                && File.ReadAllBytes(pathToPart).SequenceEqual(observed) && (bool)Get(draft, "DiscardPending"),
                "真实变更、配置差异或实际依赖冲突仍保留文件及待恢复标记 " + scenario);
        }
        TreeCheck(directDependencyReads == 0, "原位恢复不按过往的序列化引用路径探测或改写");
        TreeCheck(File.ReadAllText(historicalAddress) == "Existing file at the stored historical address"
            && File.ReadAllText(environmentPath) == "Open assembly environment outside this operation",
            "旧引用地址中的文件和外部环境都保留原样");
        Console.WriteLine("PASS: unmodified part discard " + scenario);
    }
}
