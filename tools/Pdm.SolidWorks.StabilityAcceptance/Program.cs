using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Web.Script.Serialization;
using Microsoft.Win32;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

internal static partial class Program
{
    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private const string StartupKey = @"Software\SolidWorks\AddInsStartup\{BCFD8A8A-472B-42E2-AC62-58BC17773650}";
    private static readonly Dictionary<string, object> Report = new Dictionary<string, object>();
    private static readonly List<object> Scans = new List<object>();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string name, string title);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("ole32.dll")] private static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable table);
    [DllImport("ole32.dll")] private static extern int CreateBindCtx(int reserved, out IBindCtx context);

    private static SldWorks FindProcess(int processId)
    {
        GetRunningObjectTable(0, out var table); CreateBindCtx(0, out var context);
        table.EnumRunning(out var iterator); var monikers = new IMoniker[1];
        try
        {
            while (iterator.Next(1, monikers, IntPtr.Zero) == 0)
                try
                {
                    monikers[0].GetDisplayName(context, null, out var name);
                    if (!string.Equals(name, "SolidWorks_PID_" + processId, StringComparison.OrdinalIgnoreCase)) continue;
                    table.GetObject(monikers[0], out var value);
                    return (SldWorks)value;
                }
                finally { Marshal.ReleaseComObject(monikers[0]); }
            return null;
        }
        finally { Marshal.ReleaseComObject(iterator); Marshal.ReleaseComObject(context); Marshal.ReleaseComObject(table); }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        var options = args.Skip(3).Where(value => value.StartsWith("--", StringComparison.Ordinal)).ToArray();
        var roots = args.Skip(3).Where(value => !value.StartsWith("--", StringComparison.Ordinal)).ToArray();
        if (args.Length < 3 || args.Length > 8 || roots.Length > 1 || options.Any(value =>
            value != "--structure-only" && value != "--archive-preload" && value != "--mixed-states-only"
                && value != "--api-payload" && value != "--virtual-fixture"
                && value != "--virtual-nested-fixture" && value != "--reopen-native" && value != "--rename-fixture" && value != "--rename-existing"
                && value != "--audit-native" && value != "--repair-native-ref" && value != "--pack-repaired" && value != "--reopen-archive" && value != "--pending-child-rename" && value != "--isolated-virtual-fixture" && value != "--separate-instance" && value != "--checkout-context" && value != "--open-only" && value != "--controlled-isolation")
            || options.Contains("--structure-only") && options.Contains("--archive-preload")
            || options.Contains("--api-payload") && !options.Contains("--archive-preload")
            || options.Contains("--reopen-native") && options.Length != 1)
        { Console.Error.WriteLine("Usage: <latest-addin-dll> <source-fixture-directory> <new-qa-directory> [root-assembly-file-name] [--structure-only|--archive-preload] [--mixed-states-only] [--api-payload]"); return 2; }
        Report["structureOnly"] = options.Contains("--structure-only");
        Report["mixedStatesOnly"] = options.Contains("--mixed-states-only");
        Report["archivePreload"] = options.Contains("--archive-preload");
        Report["apiPayload"] = options.Contains("--api-payload");
        Report["virtualFixture"] = options.Contains("--virtual-fixture");
        Report["virtualNestedFixture"] = options.Contains("--virtual-nested-fixture") || options.Contains("--isolated-virtual-fixture");
        Report["isolatedVirtualFixture"] = options.Contains("--isolated-virtual-fixture");
        Report["reopenNative"] = options.Contains("--reopen-native");
        Report["packRepaired"] = options.Contains("--pack-repaired");
        Report["reopenArchive"] = options.Contains("--reopen-archive");
        Report["renameFixture"] = options.Contains("--rename-fixture") || options.Contains("--rename-existing");
        Report["addRenameDrawing"] = options.Contains("--rename-fixture");
        Report["pendingChildRenameFixture"] = options.Contains("--pending-child-rename");
        Report["auditNative"] = options.Contains("--audit-native");
        Report["repairNativeReference"] = options.Contains("--repair-native-ref");
        var addinPath = Path.GetFullPath(args[0]);
        var source = Path.GetFullPath(args[1]);
        if (Equals(Report["packRepaired"], true) && !source.StartsWith(
            Path.Combine(System.Environment.CurrentDirectory, "tmp") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("原生打包可能保存总装，只允许从仓库tmp中的隔离样本导出，不能直接打包原图目录。");
        var qa = Path.GetFullPath(args[2]);
        var separateInstance = options.Contains("--separate-instance");
        if (!separateInstance && Process.GetProcessesByName("SLDWORKS").Length > 0) { Console.Error.WriteLine("已有SolidWorks进程，拒绝接管用户会话。"); return 2; }
        if (Directory.Exists(qa) || qa.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("验收目录必须是独立的新目录。");
        var before = HashFiles(source);
        Directory.CreateDirectory(qa);
        var inPlaceReadOnly = Equals(Report["reopenNative"], true) || Equals(Report["packRepaired"], true) || Equals(Report["reopenArchive"], true);
        var copies = inPlaceReadOnly ? source : Path.Combine(qa, "sources");
        if (!inPlaceReadOnly) CopyTree(source, copies);
        var reportPath = Path.Combine(qa, "result.json");
        Report["source"] = source; Report["copies"] = copies; Report["addin"] = addinPath;
        Report["addinSha256"] = Hash(addinPath); Report["startedAt"] = DateTimeOffset.Now.ToString("O"); Report["scans"] = Scans;
        var plugin = Assembly.LoadFrom(addinPath);
        AppDomain.CurrentDomain.AssemblyResolve += (_, eventArgs) =>
        {
            var path = Path.Combine(Path.GetDirectoryName(addinPath), new AssemblyName(eventArgs.Name).Name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
        if (roots.Length == 1 && options.Length > 0)
        {
            // Archived suppressed references retain their old workstation paths. Give only
            // these verified, isolated copies a QA binding so the production resolver can
            // locate them without trusting an arbitrary same-named file or changing CAD bytes.
            var identities = plugin.GetType("Upton.Pdm.SolidWorks.PdmDocumentIdentityStore", true);
            identities.GetField("bindingDirectory", Members).SetValue(null, Path.Combine(qa, "qa-bindings"));
            identities.GetField("workspaceRootResolver", Members).SetValue(null, new Func<string>(() => Path.Combine(qa, "unbound")));
            var project = Guid.NewGuid();
            foreach (var file in Directory.GetFiles(copies, "*", SearchOption.AllDirectories))
                if (!(bool)identities.GetMethod("TryWriteControlledVersion", Members).Invoke(null,
                    new object[] { file, Guid.NewGuid(), project, Guid.NewGuid(), "W1", Hash(file), new FileInfo(file).Length }))
                    throw new InvalidOperationException("无法建立隔离样本的受控副本记录。");
            identities.GetField("workspaceRootResolver", Members).SetValue(null, new Func<string>(() => copies));
            Report["qaControlledBindings"] = true;
        }
        SldWorks app = null;
        Process launchedProcess = null;
        int ownedPid = 0;
        DateTime ownedStarted = DateTime.MinValue;
        bool startupChanged = false, keyExisted = false, valueExisted = false;
        object startupValue = null;
        RegistryValueKind startupKind = RegistryValueKind.DWord;
        string searchFolders = null, workingDirectory = null;
        bool? searchRules = null;
        var passed = false;
        try
        {
            using (var existing = Registry.CurrentUser.OpenSubKey(StartupKey))
            {
                keyExisted = existing != null;
                valueExisted = existing?.GetValueNames().Contains("") == true;
                if (valueExisted) { startupValue = existing.GetValue(""); startupKind = existing.GetValueKind(""); }
            }
            Report["startupOriginal"] = new { keyExisted, valueExisted, value = startupValue };
            using (var key = Registry.CurrentUser.CreateSubKey(StartupKey)) key.SetValue("", 0, RegistryValueKind.DWord);
            startupChanged = true;
            var existingProcesses = Process.GetProcessesByName("SLDWORKS").Select(process => process.Id).ToArray();
            if (!separateInstance && existingProcesses.Length != 0) throw new InvalidOperationException("启动前检测到新的用户会话，已停止验收。");
            Console.WriteLine("NATIVE: starting isolated hidden SolidWorks; UPLM autoload disabled");
            if (separateInstance)
            {
                launchedProcess = Process.Start(new ProcessStartInfo(Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFiles), "SOLIDWORKS Corp", "SOLIDWORKS", "SLDWORKS.exe"))
                    { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden,
                        WorkingDirectory = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFiles), "SOLIDWORKS Corp", "SOLIDWORKS") });
                Console.WriteLine("NATIVE: launched separate process pid=" + launchedProcess.Id);
                for (var attempt = 0; attempt < 90 && app == null && !launchedProcess.HasExited; attempt++)
                {
                    try
                    {
                        var candidate = FindProcess(launchedProcess.Id);
                        if (candidate != null && candidate.GetProcessID() == launchedProcess.Id) app = candidate;
                        else if (candidate != null) Marshal.ReleaseComObject(candidate);
                    }
                    catch (COMException) { }
                    if (app == null) Thread.Sleep(1000);
                }
                if (app == null) throw new InvalidOperationException("独立CAD进程未就绪，未接管已有会话。");
            }
            else app = (SldWorks)Activator.CreateInstance(System.Type.GetTypeFromProgID("SldWorks.Application", true));
            ownedPid = app.GetProcessID();
            if (existingProcesses.Contains(ownedPid))
            {
                Marshal.ReleaseComObject(app); app = null; ownedPid = 0;
                throw new InvalidOperationException("未创建独立的SolidWorks进程，未修改或接管已有会话。");
            }
            Report["separateProcessVerified"] = true;
            Report["preexistingProcessIds"] = existingProcesses;
            app.Visible = false; app.UserControl = false;
            ownedStarted = Process.GetProcessById(ownedPid).StartTime.ToUniversalTime();
            Report["processId"] = ownedPid; Report["solidWorksVersion"] = app.RevisionNumber();
            Console.WriteLine("NATIVE: version=" + Report["solidWorksVersion"] + " pid=" + ownedPid);
            searchFolders = app.GetSearchFolders((int)swSearchFolderTypes_e.swDocumentType); workingDirectory = app.GetCurrentWorkingDirectory();
            searchRules = app.GetUserPreferenceToggle((int)swUserPreferenceToggle_e.swUseFolderSearchRules);
            if (!app.SetSearchFolders((int)swSearchFolderTypes_e.swDocumentType, copies)) throw new InvalidOperationException("无法设置独立样本搜索目录。");
            app.SetCurrentWorkingDirectory(copies);
            app.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swUseFolderSearchRules, true);
            var explicitRoot = roots.Length == 1;
            if (explicitRoot && Path.GetFileName(roots[0]) != roots[0]) throw new InvalidOperationException("根装配参数只能是样本内的文件名。");
            var assemblies = explicitRoot ? new[] { Path.Combine(copies, roots[0]) } : Directory.GetFiles(copies, "*.SLDASM", SearchOption.TopDirectoryOnly);
            if (assemblies.Length != 1) throw new InvalidOperationException("验收样本必须有唯一根装配体。");
            var expectedDrawings = Directory.GetFiles(copies, "*.SLDDRW", SearchOption.AllDirectories).Length;
            var expectedDrawingList = Path.Combine(copies, "qa-expected-drawings.json");
            if (File.Exists(expectedDrawingList))
            {
                var names = new JavaScriptSerializer().Deserialize<string[]>(File.ReadAllText(expectedDrawingList));
                if (names == null || names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length
                    || names.Any(name => Path.GetFileName(name) != name || !File.Exists(Path.Combine(copies, name))))
                    throw new InvalidOperationException("显式工程图验收清单无效。");
                Report["expectedDrawingNames"] = names;
                expectedDrawings = names.Length;
            }
            Report["expectedDrawingCount"] = expectedDrawings;
            if (options.Contains("--controlled-isolation"))
            {
                ControlledIsolation(app, plugin, copies, assemblies[0], qa);
                goto NativeVerificationComplete;
            }
            int errors = 0, warnings = 0;
            if (Equals(Report["repairNativeReference"], true))
            {
                var referencing = Path.Combine(copies, "P700017-3.00.01-07.SLDPRT");
                var savedReferences = ((Array)app.GetDocumentDependencies2(referencing, false, false, false)).Cast<string>().Where((value, index) => index % 2 == 1).ToArray();
                var old = savedReferences.Single(path => Path.GetFileName(path) == "四立柱组件集合.SLDASM");
                File.SetAttributes(referencing, File.GetAttributes(referencing) & ~FileAttributes.ReadOnly);
                var replaced = app.ReplaceReferencedDocument(referencing, old, assemblies[0]);
                Console.WriteLine("NATIVE REFERENCE REPAIR result=" + replaced);
                if (!replaced) throw new InvalidOperationException("隔离样本旧上下文引用替换失败。");
                Report["repairedReferences"] = app.GetDocumentDependencies2(referencing, false, false, false);
            }
            if (Equals(Report["renameFixture"], true))
                foreach (var file in Directory.GetFiles(copies)) File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
            var model = app.OpenDoc6(assemblies[0], (int)swDocumentTypes_e.swDocASSEMBLY,
                (int)swOpenDocOptions_e.swOpenDocOptions_Silent | (Equals(Report["renameFixture"], true) ? 0 : (int)swOpenDocOptions_e.swOpenDocOptions_ReadOnly), "", ref errors, ref warnings);
            Report["openErrors"] = errors; Report["openWarnings"] = warnings;
            if (model == null || errors != 0) throw new InvalidOperationException("只读打开失败，错误=" + errors + "，警告=" + warnings);
            Console.WriteLine("NATIVE: read-only root opened " + DateTimeOffset.Now.ToString("O"));
            if (!Equals(Report["renameFixture"], true) && !model.IsOpenedReadOnly()) throw new InvalidOperationException("样本未以只读方式打开。");
            var activationErrors = 0;
            var activatedRoot = app.ActivateDoc3(model.GetTitle(), false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref activationErrors) as IModelDoc2;
            if (activatedRoot == null || activationErrors != 0 || !string.Equals(activatedRoot.GetPathName(), assemblies[0], StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("隔离根装配未进入有效的活动文档上下文。");
            var assembly = (IAssemblyDoc)model;
            var resolveResult = assembly.ResolveAllLightWeightComponents(false);
            Report["resolveResult"] = resolveResult;
            Console.WriteLine("NATIVE: lightweight resolve finished " + DateTimeOffset.Now.ToString("O"));
            if (options.Contains("--checkout-context"))
            {
                app.UserControl = true;
                CheckoutContext(plugin, app, model, copies, qa);
                passed = true;
                goto NativeFinished;
            }
            var virtualPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var importedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var importStorageType = plugin.GetType("Upton.Pdm.SolidWorks.ImportedComponentStorage", true);
            var importStorage = Activator.CreateInstance(importStorageType, Members, null, new object[] { app }, null);
            var importInputs = new List<object>(); Report["importOwnershipInputs"] = importInputs;
            foreach (var component in (assembly.GetComponents(false) as Array)?.Cast<object>().OfType<IComponent2>() ?? Enumerable.Empty<IComponent2>())
            {
                var owner = (string)importStorageType.GetMethod("FindOwner", Members).Invoke(importStorage, new object[] { component, model });
                if (component.GetPathName().IndexOf("IC~~", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var parent = component.GetParent(); var parentModel = parent?.GetModelDoc2() as IModelDoc2;
                    var importModel = component.GetModelDoc2() as IModelDoc2;
                    var importFeature = ((parentModel ?? model) as IAssemblyDoc)?.FeatureByName(component.Name2.Split('/').Last()) as IFeature;
                    var featureComponent = importFeature?.GetSpecificFeature2() as IComponent2;
                    object storagePaths = null, storageTitles = null;
                    var nativeVirtual = importModel?.Extension.IsVirtualComponent3(out storagePaths, out storageTitles);
                    importInputs.Add(new { name = component.Name2, path = component.GetPathName(), imported = component.GetImportedPath(),
                        loaded = (component.GetModelDoc2() as IModelDoc2)?.GetPathName(), suppression = component.GetSuppression2(),
                        parent = parent?.GetPathName(), parentModel = parentModel?.GetPathName(), owner, nativeVirtual, storagePaths, storageTitles,
                        featureType = importFeature?.GetTypeName2(), isInterconnectFeature = importFeature?.Is3DInterconnectFeature,
                        componentId = component.GetID(), featureComponentId = featureComponent?.GetID(), featureComponentPath = featureComponent?.GetPathName(),
                        featureComponentModel = (featureComponent?.GetModelDoc2() as IModelDoc2)?.GetPathName(), featureImported = featureComponent?.GetImportedPath() });
                }
                if (!string.IsNullOrWhiteSpace(owner) && Path.GetFullPath(owner).StartsWith(copies + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    importedPaths.Add(Path.GetFullPath(component.GetPathName()));
                if (!component.IsVirtual) continue;
                var path = component.GetPathName();
                if (!string.IsNullOrWhiteSpace(path) && path.IndexOf("VC~~", StringComparison.OrdinalIgnoreCase) >= 0)
                    virtualPaths.Add(Path.GetFullPath(path));
            }
            var outside = (app.GetDocuments() as Array)?.Cast<object>().OfType<IModelDoc2>()
                .Select(document => document.GetPathName()).Where(path => !string.IsNullOrWhiteSpace(path)
                    && !Path.GetFullPath(path).StartsWith(copies + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    && !virtualPaths.Contains(Path.GetFullPath(path)) && !importedPaths.Contains(Path.GetFullPath(path))).ToArray() ?? new string[0];
            Report["loadedOutsideQa"] = outside;
            Report["embeddedVirtualOutsideQa"] = virtualPaths.Count;
            Report["verifiedImportedRuntimePaths"] = importedPaths.ToArray();
            Report["storageInventories"] = importStorageType.GetField("inventories", Members).GetValue(importStorage);
            if (outside.Length > 0) throw new InvalidOperationException("样本引用加载到了验收目录之外，已停止内存状态验证。");
            if (Equals(Report["packRepaired"], true))
            {
                var pack = (IPackAndGo)model.Extension.GetPackAndGo();
                pack.IncludeDrawings = true; pack.IncludeSuppressed = true; pack.FlattenToSingleFolder = true;
                if (!pack.GetDocumentNames(out var values)) throw new InvalidOperationException("未读取到修复副本的完整文件清单。");
                var native = ((Array)values).Cast<string>().ToArray();
                if (native.Any(path => path.IndexOf("VC~~", StringComparison.OrdinalIgnoreCase) >= 0))
                    throw new InvalidOperationException("本验收导出仅用于已核对的外部图档样本。");
                var target = Path.Combine(qa, "图档");
                Directory.CreateDirectory(target);
                var destinations = native.Select(path => Path.Combine(target, Path.GetFileName(path))).ToArray();
                if (destinations.Distinct(StringComparer.OrdinalIgnoreCase).Count() != destinations.Length)
                    throw new InvalidOperationException("修复副本存在同名图档，不能压平目录。");
                if (!pack.SetDocumentSaveToNames(destinations)) throw new InvalidOperationException("未能设置修复副本的目标路径。");
                var statuses = model.Extension.SavePackAndGo((PackAndGo)pack) as Array;
                if (statuses == null || statuses.Length != native.Length || statuses.Cast<object>().Any(value => Convert.ToInt32(value) != 0)
                    || destinations.Any(path => !File.Exists(path) || new FileInfo(path).Length == 0))
                    throw new InvalidOperationException("SolidWorks原生打包未能完整输出修复图档。");
                Report["exportedDirectory"] = target; Report["exportedFileCount"] = destinations.Length;
                Report["exportedRoot"] = Path.Combine(target, Path.GetFileName(assemblies[0]));
                Console.WriteLine("NATIVE PACK PASS: files=" + destinations.Length + " target=" + target);
            }
            else if (Equals(Report["reopenArchive"], true))
            {
                var manifestType = plugin.GetType("Upton.Pdm.SolidWorks.ControlledOpenManifestDto", true);
                var fileType = plugin.GetType("Upton.Pdm.SolidWorks.ControlledOpenFileDto", true);
                var manifest = Activator.CreateInstance(manifestType);
                var files = (IList)Activator.CreateInstance(manifestType.GetProperty("Files").PropertyType);
                foreach (var path in Directory.GetFiles(copies).Where(path => new[] { ".SLDASM", ".SLDPRT", ".SLDDRW" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)))
                {
                    var file = Activator.CreateInstance(fileType);
                    fileType.GetProperty("FileName").SetValue(file, Path.GetFileName(path));
                    fileType.GetProperty("RelativePath").SetValue(file, Path.GetFileName(path));
                    fileType.GetProperty("IsRoot").SetValue(file, string.Equals(path, assemblies[0], StringComparison.OrdinalIgnoreCase));
                    fileType.GetProperty("FileLength").SetValue(file, new FileInfo(path).Length);
                    fileType.GetProperty("Sha256").SetValue(file, Hash(path));
                    files.Add(file);
                }
                manifestType.GetProperty("Files").SetValue(manifest, files);
                plugin.GetType("Upton.Pdm.SolidWorks.PdmAddin", true).GetMethod("ValidateHistoricalControlledOpenReferences", Members)
                    .Invoke(null, new object[] { model, assemblies[0], manifest, app });
                Report["controlledOpenValidationPassed"] = true;
                if (!options.Contains("--open-only")) Scan(plugin, app, model, "reopened-archive", expectedDrawings);
            }
            else if (Equals(Report["auditNative"], true))
            {
                var dependencies = new Dictionary<string, object>();
                foreach (var file in Directory.GetFiles(copies).Where(path => new[] { ".SLDPRT", ".SLDASM", ".SLDDRW" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)))
                    dependencies[Path.GetFileName(file)] = new {
                        saved = app.GetDocumentDependencies2(file, false, false, false),
                        resolved = app.GetDocumentDependencies2(file, false, true, false) };
                Report["fileDependencies"] = dependencies;
                Report["featureDependencies"] = AuditFeatureDependencies(app, model, copies);
                var pack = (IPackAndGo)model.Extension.GetPackAndGo();
                pack.IncludeDrawings = true; pack.IncludeSuppressed = true; pack.GetDocumentNames(out var names);
                Report["auditNativeFiles"] = names;
                File.WriteAllText(Path.Combine(qa, "dependency-audit.json"), new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(Report));
                Scan(plugin, app, model, "audit-native", expectedDrawings);
            }
            else if (Equals(Report["renameFixture"], true) && !Equals(Report["virtualFixture"], true) && !Equals(Report["virtualNestedFixture"], true))
            {
                RenameFixture(plugin, app, model, expectedDrawings);
            }
            else if (Equals(Report["virtualFixture"], true) || Equals(Report["virtualNestedFixture"], true))
            {
                var components = (assembly.GetComponents(false) as Array)?.Cast<object>().OfType<IComponent2>().ToArray()
                    ?? new IComponent2[0];
                var topLevel = (assembly.GetComponents(true) as Array)?.Cast<object>().OfType<IComponent2>().ToArray()
                    ?? new IComponent2[0];
                var nested = Equals(Report["virtualNestedFixture"], true);
                var candidate = Equals(Report["isolatedVirtualFixture"], true) ? CreateIndependentVirtualCandidate(app, model, copies) : nested
                    ? topLevel.Where(component => !component.IsVirtual
                        && component.GetSuppression2() != (int)swComponentSuppressionState_e.swComponentSuppressed
                        && component.GetPathName().EndsWith(".SLDASM", StringComparison.OrdinalIgnoreCase)
                        && component.GetChildren() is Array children && children.Length >= 2 && children.Length <= 12)
                        .OrderBy(component => ((Array)component.GetChildren()).Length).FirstOrDefault()
                    : components.FirstOrDefault(component => !component.IsVirtual
                        && component.GetSuppression2() != (int)swComponentSuppressionState_e.swComponentSuppressed
                        && component.GetPathName().EndsWith(".SLDPRT", StringComparison.OrdinalIgnoreCase));
                if (candidate == null) throw new InvalidOperationException(nested
                    ? "隔离样本没有可转为嵌入式虚拟件的两件以上子装配体。" : "隔离样本没有可转为嵌入式虚拟件的零件。");
                Console.WriteLine("NATIVE: virtualizing " + candidate.Name2 + " path=" + candidate.GetPathName()
                    + " children=" + ((candidate.GetChildren() as Array)?.Length ?? 0));
                File.SetAttributes(assemblies[0], File.GetAttributes(assemblies[0]) & ~FileAttributes.ReadOnly);
                if (model.IsOpenedReadOnly() && !model.SetReadOnlyState(false) || model.IsOpenedReadOnly()) throw new InvalidOperationException("隔离根装配无法进入可保存状态。");
                var beforeVirtual = Hash(assemblies[0]);
                var madeVirtual = candidate.MakeVirtual2(nested);
                if (!madeVirtual || !candidate.IsVirtual)
                    throw new InvalidOperationException("SolidWorks未建立嵌入式虚拟组件；API返回=" + madeVirtual + "；虚拟状态=" + candidate.IsVirtual);
                model.SetSaveFlag();
                if (!model.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref errors, ref warnings) || errors != 0)
                    throw new InvalidOperationException("隔离根装配及嵌入式虚拟零件未保存：错误=" + errors + "，警告=" + warnings);
                if (Hash(assemblies[0]) == beforeVirtual || model.GetSaveFlag())
                    throw new InvalidOperationException("虚拟零件未持久保存在父装配文件中。");
                Report["virtualizedComponent"] = candidate.Name2;
                Report["virtualizedRootSaved"] = true;
                Scan(plugin, app, model, nested ? "virtual-nested" : "virtual-fixture", expectedDrawings);
                if (Equals(Report["renameFixture"], true)) RenameFixture(plugin, app, model, expectedDrawings);
            }
            else if (Equals(Report["reopenNative"], true))
            {
                Scan(plugin, app, model, "reopened-virtual", expectedDrawings);
            }
            else if (Equals(Report["mixedStatesOnly"], true))
            {
                var mixed = (assembly.GetComponents(false) as Array)?.Cast<object>().OfType<IComponent2>().First(component => !component.IsVirtual
                    && component.GetSuppression2() != (int)swComponentSuppressionState_e.swComponentSuppressed
                    && component.GetPathName().EndsWith(".SLDPRT", StringComparison.OrdinalIgnoreCase));
                var mixedOriginal = mixed.GetSuppression2();
                try
                {
                    mixed.SetSuppression2((int)swComponentSuppressionState_e.swComponentLightweight);
                    if (mixed.GetSuppression2() != (int)swComponentSuppressionState_e.swComponentLightweight
                        && mixed.GetSuppression2() != (int)swComponentSuppressionState_e.swComponentFullyLightweight)
                        throw new InvalidOperationException("混合状态样本未进入轻化状态。");
                    Scan(plugin, app, model, "mixed-states", expectedDrawings);
                }
                finally { mixed.SetSuppression2(mixedOriginal); }
            }
            else
            {
                Scan(plugin, app, model, "resolved", expectedDrawings);
                // Native drawing preload can unload/reload reference-only models in an
                // invisible automation session. Never reuse the pre-preload COM pointer.
                model = app.GetOpenDocumentByName(assemblies[0]) as ModelDoc2
                    ?? app.OpenDoc6(assemblies[0], (int)swDocumentTypes_e.swDocASSEMBLY,
                        (int)(swOpenDocOptions_e.swOpenDocOptions_Silent | swOpenDocOptions_e.swOpenDocOptions_ReadOnly), "", ref errors, ref warnings);
                if (model == null) throw new InvalidOperationException("无法重新获取隔离根装配进行状态验证。");
                assembly = (IAssemblyDoc)model;
                var components = (assembly.GetComponents(false) as Array)?.Cast<object>().OfType<IComponent2>().ToArray() ?? new IComponent2[0];
                var part = components.FirstOrDefault(component => !component.IsVirtual && component.GetSuppression2() != (int)swComponentSuppressionState_e.swComponentSuppressed
                    && component.GetPathName().EndsWith(".SLDPRT", StringComparison.OrdinalIgnoreCase)
                    && component.GetPathName().IndexOf("IC~~", StringComparison.OrdinalIgnoreCase) < 0);
                if (part == null) { Report["stateChangesNotApplicable"] = "组件由原生导入特征管理，不支持作为独立零件切换轻化状态"; passed = true; }
                else
                {
                var originalState = part.GetSuppression2();
                Report["stateComponent"] = part.Name2;
                try
                {
                    foreach (var state in new[] { swComponentSuppressionState_e.swComponentLightweight, swComponentSuppressionState_e.swComponentSuppressed })
                    {
                        var result = part.SetSuppression2((int)state);
                        var actual = part.GetSuppression2();
                        Report[state.ToString()] = new { result, actual };
                        if (actual != (int)state && !(state == swComponentSuppressionState_e.swComponentLightweight && actual == (int)swComponentSuppressionState_e.swComponentFullyLightweight))
                            throw new InvalidOperationException("SolidWorks未进入请求的内存状态：" + state + "，实际=" + actual);
                        Scan(plugin, app, model, state.ToString(), expectedDrawings);
                        part.SetSuppression2(originalState);
                    }
                }
                finally { part.SetSuppression2(originalState); }
                }
                if (Equals(Report["archivePreload"], true) && !Report.ContainsKey("qaControlledBindings"))
                {
                    var drawingPath = Directory.GetFiles(copies, "*.SLDDRW").OrderBy(path => path).First();
                    var savedDrawingHash = Hash(drawingPath);
                    var drawing = app.OpenDoc6(drawingPath, (int)swDocumentTypes_e.swDocDRAWING,
                        (int)(swOpenDocOptions_e.swOpenDocOptions_Silent | swOpenDocOptions_e.swOpenDocOptions_ReadOnly), "", ref errors, ref warnings);
                    if (drawing == null) throw new InvalidOperationException("无法打开已有工程图窗口的验收样本。");
                    File.SetAttributes(drawingPath, File.GetAttributes(drawingPath) & ~FileAttributes.ReadOnly);
                    if (drawing.IsOpenedReadOnly() && !drawing.SetReadOnlyState(false))
                        throw new InvalidOperationException("隔离工程图样本无法进入可编辑状态。");
                    drawing.Extension.CustomPropertyManager[""].Add3("UPLM_QA_PreserveEdit", (int)swCustomInfoType_e.swCustomInfoText,
                        "仅用于隔离副本的未保存内容验证", (int)swCustomPropertyAddOption_e.swCustomPropertyReplaceValue);
                    drawing.Extension.CustomPropertyManager[""].Get6("UPLM_QA_PreserveEdit", true, out var rawEdit, out var resolvedEdit, out var resolved, out var linked);
                    if (rawEdit != "仅用于隔离副本的未保存内容验证")
                        throw new InvalidOperationException("隔离工程图样本未建立真实的属性修改。");
                    // Custom-property API writes need not set the UI save flag. Model a user
                    // editor's pending change explicitly, then exercise the real Save3 path.
                    drawing.SetSaveFlag();
                    if (!drawing.GetSaveFlag()) throw new InvalidOperationException("工程图未进入待保存状态，无法验证修改保留。");
                    Report["existingDrawingPath"] = drawingPath;
                    Scan(plugin, app, model, "existing-drawing", expectedDrawings);
                    if (app.GetOpenDocumentByName(drawingPath) == null || drawing.GetSaveFlag() || savedDrawingHash == Hash(drawingPath))
                        throw new InvalidOperationException("已有工程图窗口被关闭，或确认保存后的修改未写入隔离副本。");
                    Report["existingDrawingEditSavedAndWindowRetained"] = true;
                }
            }
        NativeVerificationComplete:
            passed = true;
        NativeFinished:;
        }
        catch (Exception exception)
        {
            Report["error"] = exception.ToString();
            Console.Error.WriteLine("NATIVE FAIL: " + exception);
        }
        finally
        {
            if (app != null)
            {
                try
                {
                    if (searchFolders != null) app.SetSearchFolders((int)swSearchFolderTypes_e.swDocumentType, searchFolders);
                    if (searchRules.HasValue) app.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swUseFolderSearchRules, searchRules.Value);
                    if (workingDirectory != null) app.SetCurrentWorkingDirectory(workingDirectory);
                    // A background session can exit as soon as its last document closes:
                    // https://help.solidworks.com/2022/english/api/sldworksapi/SolidWorks.Interop.sldworks~SolidWorks.Interop.sldworks.ISldWorks~CloseDoc.html
                    Console.WriteLine("NATIVE: closing only the owned QA session");
                    if (!app.CloseAllDocuments(true)) throw new InvalidOperationException("验收文档未能全部关闭。");
                    app.ExitApp();
                }
                catch (COMException exception) when (exception.ErrorCode == unchecked((int)0x80010108))
                { Report["backgroundSessionDisconnected"] = true; }
                catch (Exception exception) { Report["shutdownError"] = exception.Message; passed = false; }
                finally { Marshal.FinalReleaseComObject(app); }
            }
            if (ownedPid != 0)
            {
                try
                {
                    using (var process = Process.GetProcessById(ownedPid))
                    {
                        if (process.StartTime.ToUniversalTime() == ownedStarted && !process.WaitForExit(20000))
                        {
                            Report["ownedProcessNeededTermination"] = true;
                            passed = false;
                            process.Kill(); process.WaitForExit(10000);
                        }
                    }
                }
                catch (ArgumentException) { }
            }
            if (app == null && launchedProcess != null && !launchedProcess.HasExited)
            {
                launchedProcess.Kill(); launchedProcess.WaitForExit(10000);
            }
            launchedProcess?.Dispose();
            if (startupChanged)
            {
                try
                {
                    using (var key = Registry.CurrentUser.OpenSubKey(StartupKey, true))
                    {
                        if (valueExisted) key.SetValue("", startupValue, startupKind);
                        else key?.DeleteValue("", false);
                    }
                    if (!keyExisted)
                    {
                        using (var key = Registry.CurrentUser.OpenSubKey(StartupKey))
                            if (key != null && (key.GetValueNames().Length != 0 || key.GetSubKeyNames().Length != 0))
                                throw new InvalidOperationException("验收期间启动项出现其他值，已保留并停止删除。");
                        Registry.CurrentUser.DeleteSubKey(StartupKey, false);
                    }
                    using (var key = Registry.CurrentUser.OpenSubKey(StartupKey))
                    {
                        var restored = (key != null) == keyExisted && (key?.GetValueNames().Contains("") == true) == valueExisted
                            && (!valueExisted || Equals(key.GetValue(""), startupValue));
                        Report["startupRestored"] = restored; passed &= restored;
                    }
                }
                catch (Exception exception) { Report["startupRestoreError"] = exception.ToString(); passed = false; }
            }
            var unchanged = SameHashes(before, HashFiles(source));
            var copiesUnchanged = SameHashes(before, HashFiles(copies));
            Report["sourceUnchanged"] = unchanged; Report["copiesUnchanged"] = copiesUnchanged;
            Report["sourceFileCount"] = before.Count; Report["finishedAt"] = DateTimeOffset.Now.ToString("O");
            Report["qaCopiesSavedWithConsent"] = Equals(Report["archivePreload"], true) || options.Contains("--controlled-isolation");
            Report["passed"] = passed && unchanged && (copiesUnchanged || Equals(Report["archivePreload"], true) || Equals(Report["renameFixture"], true) || Equals(Report["repairNativeReference"], true) || options.Contains("--controlled-isolation"));
            File.WriteAllText(reportPath, new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(Report));
            Console.WriteLine("NATIVE REPORT: " + reportPath);
        }
        return Equals(Report["passed"], true) ? 0 : 1;
    }

    private static IComponent2 CreateIndependentVirtualCandidate(SldWorks app, IModelDoc2 root, string copies)
    {
        int errors = 0, warnings = 0;
        var part = Directory.GetFiles(copies, "LMF40LUU.SLDPRT").Single();
        var assembly = (IModelDoc2)app.NewDocument(@"C:\ProgramData\SOLIDWORKS\SOLIDWORKS 2022\templates\gb_assembly.asmdot", 0, 0, 0);
        var target = Path.Combine(copies, "QA-独立嵌入子装配.SLDASM");
        if (assembly == null || !assembly.Extension.SaveAs3(target, 0, 1, null, null, ref errors, ref warnings) || errors != 0)
            throw new InvalidOperationException("未建立独立虚拟子装配样本。");
        for (var index = 0; index < 2; index++)
        {
            var path = Path.Combine(copies, "QA-内部轴承" + index + ".SLDPRT");
            File.Copy(part, path); File.SetAttributes(path, FileAttributes.Normal);
            var child = app.OpenDoc6(path, 1, 1, "", ref errors, ref warnings);
            if (child == null || errors != 0 || !string.Equals(child.GetPathName(), path, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("独立子件样本未按指定路径打开。");
            app.ActivateDoc3(assembly.GetTitle(), false, 0, ref errors);
            if (((IAssemblyDoc)assembly).AddComponent5(path, 0, "", false, "", index * 0.2, 0, 0) == null)
                throw new InvalidOperationException("未插入独立内部子件。");
        }
        if (!assembly.Save3(1, ref errors, ref warnings) || errors != 0) throw new InvalidOperationException("独立子装配未保存。");
        File.SetAttributes(root.GetPathName(), File.GetAttributes(root.GetPathName()) & ~FileAttributes.ReadOnly);
        if (root.IsOpenedReadOnly()) root.SetReadOnlyState(false);
        app.ActivateDoc3(root.GetTitle(), false, 0, ref errors);
        return ((IAssemblyDoc)root).AddComponent5(target, 0, "", false, "", 0.5, 0, 0);
    }

    private static object AuditFeatureDependencies(SldWorks app, IModelDoc2 root, string copies)
    {
        var result = new List<object>();
        foreach (var path in Directory.GetFiles(copies, "*.SLDPRT"))
        {
            var refs = (app.GetDocumentDependencies2(path, false, false, false) as Array)?.Cast<object>()
                .Where((value, index) => index % 2 == 1).Select(Convert.ToString).ToArray() ?? new string[0];
            if (!refs.Any(value => value.EndsWith(".SLDASM", StringComparison.OrdinalIgnoreCase))) continue;
            var doc = app.GetOpenDocumentByName(path) as IModelDoc2;
            var owned = doc == null;
            int errors = 0, warnings = 0;
            if (owned) doc = app.OpenDoc6(path, (int)swDocumentTypes_e.swDocPART,
                (int)(swOpenDocOptions_e.swOpenDocOptions_Silent | swOpenDocOptions_e.swOpenDocOptions_ReadOnly), "", ref errors, ref warnings);
            if (doc == null || errors != 0 || !string.Equals(doc.GetPathName(), path, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("引用特征审计未能打开指定副本：" + path);
            var original = doc.ConfigurationManager.ActiveConfiguration.Name;
            try
            {
                foreach (var configuration in ((Array)doc.GetConfigurationNames()).Cast<string>())
                {
                    if (doc.ConfigurationManager.ActiveConfiguration.Name != configuration && !doc.ShowConfiguration2(configuration))
                        throw new InvalidOperationException("引用特征审计无法读取配置：" + path + " / " + configuration);
                    var count = doc.Extension.ListExternalFileReferencesCount();
                    doc.Extension.ListExternalFileReferences(out var models, out var components, out var features,
                        out var types, out var states, out var entities, out var owners, out var option, out var referencedConfiguration);
                    object[] Values(object value) => (value as Array)?.Cast<object>().ToArray() ?? new object[0];
                    var stateValues = Values(states);
                    result.Add(new { file = Path.GetFileName(path), configuration, count, models = Values(models),
                        components = Values(components), features = Values(features), dataTypes = Values(types),
                        statuses = stateValues.Select(value => new { value, name = Enum.GetName(typeof(swExternalReferenceStatus_e), Convert.ToInt32(value)) }).ToArray(),
                        entities = Values(entities), featureOwners = Values(owners), configurationOption = option,
                        referencedConfiguration, dirty = doc.GetSaveFlag() });
                    Console.WriteLine("FEATURE AUDIT: " + Path.GetFileName(path) + " / " + configuration + " references=" + count);
                }
            }
            finally
            {
                if (doc.ConfigurationManager.ActiveConfiguration.Name != original && !doc.ShowConfiguration2(original))
                    throw new InvalidOperationException("引用特征审计无法恢复原配置：" + path);
                if (owned) app.CloseDoc(doc.GetTitle());
            }
        }
        return result;
    }

    private static void RenameFixture(Assembly plugin, SldWorks app, IModelDoc2 model, int expectedDrawings)
    {
        var errors = 0; var warnings = 0;
        var rootTitle = model.GetTitle();
        var extraDrawing = Path.Combine((string)Report["copies"], "不同名总装工程图.SLDDRW");
        if (Equals(Report["addRenameDrawing"], true))
        {
        model.Visible = true;
        var drawing = (IModelDoc2)app.NewDocument(@"C:\ProgramData\SOLIDWORKS\SOLIDWORKS 2022\templates\gb_a4.drwdot", 0, 0, 0);
        var views = ((Array)model.GetModelViewNames()).Cast<string>().ToArray();
        Console.WriteLine("RENAME FIXTURE VIEWS: " + string.Join(";", views));
        if (drawing == null || ((IDrawingDoc)drawing).CreateDrawViewFromModelView3(model.GetPathName(), views.Skip(1).First(name => name.StartsWith("*")), 0.1, 0.1, 0) == null)
            throw new InvalidOperationException("未建立引用总装的工程图验收样本。");
        if (!drawing.Extension.SaveAs3(extraDrawing, 0, 1, null, null, ref errors, ref warnings) || errors != 0)
            throw new InvalidOperationException("总装工程图样本保存失败。");
        app.CloseDoc(drawing.GetTitle());
        model = (IModelDoc2)app.ActivateDoc3(rootTitle, false, 0, ref errors);
        expectedDrawings++;
        }
        Console.WriteLine("RENAME: scanning original root " + model.GetPathName());
        var scannerType = plugin.GetType("Upton.Pdm.SolidWorks.SolidWorksReferenceTreeScanner", true);
        var scanner = Activator.CreateInstance(scannerType, Members & ~BindingFlags.Static, null, new object[] { app }, null);
        var root = scannerType.GetMethod("ScanDocument", Members).Invoke(scanner, new object[] { model, true });
        Console.WriteLine("RENAME: original root scanned");
        app.UserControl = true;
        var type = plugin.GetType("Upton.Pdm.SolidWorks.PdmAddin", true);
        type.GetField("documentRenameRecoveryDirectory", Members)?.SetValue(null, Path.Combine(Path.GetDirectoryName((string)Report["copies"]), "rename-recovery"));
        var addin = Activator.CreateInstance(type);
        type.GetField("application", Members).SetValue(addin, app);
        type.GetField("currentTree", Members).SetValue(addin, root);
        type.GetField("scanner", Members).SetValue(addin, scanner);
        foreach (var node in Flatten(root)) node.GetType().GetProperty("DocumentId").SetValue(node, null);
        var oldPath = model.GetPathName();
        var pack = (IPackAndGo)model.Extension.GetPackAndGo();
        pack.IncludeDrawings = true; pack.IncludeSuppressed = true; pack.GetDocumentNames(out var native);
        Report["beforeRenameNativeFiles"] = ((Array)native).Cast<string>().ToArray();
        using (var pane = (Control)Activator.CreateInstance(plugin.GetType("Upton.Pdm.SolidWorks.PdmTaskPaneControl", true), Members & ~BindingFlags.Static, null, new object[] { "改名存档验收" }, null))
        {
            type.GetField("taskPaneControl", Members).SetValue(addin, pane);
            if (Equals(Report["pendingChildRenameFixture"], true))
            {
                var candidate = Flatten(root).Where(item => Get(item, "Kind").ToString() == "Part")
                    .GroupBy(item => (string)Get(item, "FullPath"), StringComparer.OrdinalIgnoreCase).Select(group => group.First())
                    .First(item => ((string[])type.GetMethod("ReadRenameReferences", Members).Invoke(addin, new object[] { Get(item, "FullPath"), true }))
                        .Any(path => string.Equals(path, oldPath, StringComparison.OrdinalIgnoreCase)));
                var beforePath = (string)Get(candidate, "FullPath");
                type.GetMethod("ApplyDocumentRenameWithLinkedDrawing", Members).Invoke(addin,
                    new object[] { candidate, "QA-待落盘子件", model, false });
                var pendingPath = (string)Get(candidate, "FullPath");
                Report["pendingChildBeforeRootRename"] = new { beforePath, pendingPath,
                    pending = Get(candidate, "IsRenamePendingSave"), targetExists = File.Exists(pendingPath) };
                if (File.Exists(pendingPath) || !(bool)Get(candidate, "IsRenamePendingSave"))
                    throw new InvalidOperationException("真实样本未形成待落盘改名状态，不能算作该回归通过。");
                Console.WriteLine("RENAME: child renamed in memory; target absent on disk; now renaming root");
            }
            Console.WriteLine("RENAME: applying production rename");
            type.GetMethod("ApplyDocumentRenameWithLinkedDrawing", Members).Invoke(addin, new object[] { root, "QA-改名后总装", model, true });
            model = (IModelDoc2)app.GetOpenDocumentByName((string)Get(root, "FullPath"));
            Report["renamedRoot"] = model.GetPathName();
            Report["oldRootRemains"] = File.Exists(oldPath);
            if (File.Exists(oldPath) || (string)Get(root, "DisplayName") != "QA-改名后总装")
                throw new InvalidOperationException("主图档改名留下重复原文件或旧显示名称。");
            if (File.Exists(extraDrawing))
            {
                var drawingReferences = ((Array)app.GetDocumentDependencies2(extraDrawing, false, false, false)).Cast<string>().Where((value, index) => index % 2 == 1).ToArray();
                if (!drawingReferences.Contains(model.GetPathName(), StringComparer.OrdinalIgnoreCase))
                    throw new InvalidOperationException("不同名总装工程图未更新到改名后的模型。");
                Report["differentlyNamedDrawingUpdated"] = true;
            }
            pack = (IPackAndGo)model.Extension.GetPackAndGo();
            pack.IncludeDrawings = true; pack.IncludeSuppressed = true; pack.GetDocumentNames(out native);
            Report["afterRenameNativeFiles"] = ((Array)native).Cast<string>().ToArray();
            Scan(plugin, app, model, "renamed-root", expectedDrawings);
        }
    }

    private static void Scan(Assembly plugin, SldWorks app, IModelDoc2 model, string mode, int expectedDrawings)
    {
        Console.WriteLine("NATIVE: scan start " + mode + " " + DateTimeOffset.Now.ToString("O"));
        var type = plugin.GetType("Upton.Pdm.SolidWorks.SolidWorksReferenceTreeScanner", true);
        var scanner = Activator.CreateInstance(type, Members & ~BindingFlags.Static, null, new object[] { app }, null);
        var root = type.GetMethod("ScanDocument", Members).Invoke(scanner, new object[] { model, true });
        Console.WriteLine("NATIVE: component scan finished " + mode + " " + DateTimeOffset.Now.ToString("O"));
        if (mode == "reopened-virtual")
            foreach (var node in Flatten(root).Where(node => Get(node, "Status").ToString() == "Virtual"))
                Console.WriteLine("NATIVE VERIFIED VIRTUAL path=" + Get(node, "FullPath") + " source=" + Get(node, "SourceReferencePath"));
        if (Report.ContainsKey("qaControlledBindings"))
            typeof(Program).GetMethod("ExpandSuppressed", BindingFlags.NonPublic | BindingFlags.Static).MakeGenericMethod(root.GetType()).Invoke(null, new object[] { plugin, app, root });
        IReadOnlyList<string> nativeFiles = null;
        if (!Equals(Report["structureOnly"], true))
        {
            nativeFiles = (IReadOnlyList<string>)type.GetMethod("AddNativeAssociatedDrawings", Members).Invoke(scanner, new object[] { model, root });
            if (mode == "reopened-virtual")
                foreach (var path in nativeFiles.Where(path => path.IndexOf("VC~~", StringComparison.OrdinalIgnoreCase) >= 0))
                    Console.WriteLine("NATIVE PACK VIRTUAL path=" + path);
            var external = (IReadOnlyList<string>)plugin.GetType("Upton.Pdm.SolidWorks.NativeArchiveManifest", true)
                .GetMethod("Validate", Members).Invoke(null, new object[] { root, nativeFiles });
            Report["validatedArchiveFiles"] = nativeFiles;
            Report["fileDependencyNodes"] = Flatten(root).Where(node => Get(node, "Role").ToString() == "ExternalFileDependency")
                .Select(node => new { path = Get(node, "FullPath"), kind = Get(node, "Kind").ToString(), configuration = Get(node, "Configuration") }).ToArray();
            Report["nativeExternalFiles"] = external;
            Console.WriteLine("NATIVE: external documents listed separately=" + external.Count);
        }
        var nodes = Flatten(root).ToArray();
        var drawings = nodes.Where(node => Get(node, "Kind").ToString() == "Drawing")
            .Select(node => (string)Get(node, "FullPath")).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path).ToArray();
        var statuses = nodes.GroupBy(node => Get(node, "Status").ToString()).ToDictionary(group => group.Key, group => group.Count());
        if (mode == "virtual-fixture" || mode == "virtual-nested" || mode == "reopened-virtual")
        {
            var embedded = nodes.Where(node => Get(node, "Status").ToString() == "Virtual")
                .Select(node => (string)Get(node, "FullPath")).Where(path => path.IndexOf("VC~~", StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
            if (embedded.Length == 0 || !nativeFiles.Any(path => path.IndexOf("VC~~", StringComparison.OrdinalIgnoreCase) >= 0))
                throw new InvalidOperationException("真实原生清单未与已扫描的嵌入式虚拟件对应。");
            if (mode == "virtual-nested" && embedded.Distinct(StringComparer.OrdinalIgnoreCase).Count() < 2)
                throw new InvalidOperationException("嵌入式子装配体的内部虚拟子件未完整扫描。");
            var batchItems = (IEnumerable)plugin.GetType("Upton.Pdm.SolidWorks.PdmAddin", true)
                .GetMethod("BuildBatchOperationItems", Members).Invoke(null, new[] { root });
            if (batchItems.Cast<object>().Any(item => ((string)Get(Get(item, "Node"), "FullPath")).IndexOf("VC~~", StringComparison.OrdinalIgnoreCase) >= 0))
                throw new InvalidOperationException("嵌入式虚拟件被作为独立文件上传。");
            Report["embeddedVirtualCount"] = embedded.Length;
        }
        var configurations = nodes.Where(node => Get(node, "Kind").ToString() != "Drawing")
            .GroupBy(node => (string)Get(node, "FullPath"), StringComparer.OrdinalIgnoreCase)
            .Select(group => new { path = group.Key, configurations = group.Select(node => (string)Get(node, "Configuration")).Distinct().ToArray() })
            .Where(group => group.configurations.Length > 1).ToArray();
        Scans.Add(new { mode, nodeCount = nodes.Length, drawings, statuses, multipleConfigurations = configurations });
        if (!Equals(Report["structureOnly"], true) && drawings.Length != expectedDrawings) throw new InvalidOperationException("真实关联工程图未全部发现：" + drawings.Length + "/" + expectedDrawings);
        if (Report.TryGetValue("expectedDrawingNames", out var expectedNames)
            && !((string[])expectedNames).OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .SequenceEqual(drawings.Select(Path.GetFileName).OrderBy(name => name, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("工程图实际名称与已核对的验收清单不一致。");
        Console.WriteLine("NATIVE PASS: " + mode + " nodes=" + nodes.Length + " drawings=" + drawings.Length);
        Console.WriteLine("NATIVE: files referenced in multiple configurations=" + configurations.Length);
        if (Equals(Report["archivePreload"], true))
        {
            ArchivePreload(plugin, app, root, mode);
            if (mode == "resolved")
            {
                var saved = HashFiles((string)Report["copies"]);
                ArchivePreload(plugin, app, root, "unchanged-repeat");
                if (!SameHashes(saved, HashFiles((string)Report["copies"]))) throw new InvalidOperationException("无修改重复准备改写了源图档，存在误升版风险。");
                Report["unchangedRepeatDidNotSaveSources"] = true;
            }
        }
    }

    private static void ArchivePreload(Assembly plugin, SldWorks app, object root, string mode)
    {
        var type = plugin.GetType("Upton.Pdm.SolidWorks.PdmAddin", true);
        var addin = Activator.CreateInstance(type);
        type.GetField("application", Members).SetValue(addin, app);
        type.GetField("currentTree", Members).SetValue(addin, root);
        var scannerType = plugin.GetType("Upton.Pdm.SolidWorks.SolidWorksReferenceTreeScanner", true);
        type.GetField("scanner", Members).SetValue(addin, Activator.CreateInstance(scannerType, Members & ~BindingFlags.Static, null, new object[] { app }, null));
        foreach (var node in Flatten(root)) node.GetType().GetProperty("DocumentId").SetValue(node, null);
        var items = type.GetMethod("BuildBatchOperationItems", Members).Invoke(null, new[] { root });
        var clientType = plugin.GetType("Upton.Pdm.SolidWorks.PdmApiClient", true);
        using (var client = (IDisposable)Activator.CreateInstance(clientType, Members & ~BindingFlags.Static, null, new object[] { "http://isolated-cad.invalid/" }, null))
        {
            var httpField = clientType.GetField("httpClient", Members);
            ((HttpClient)httpField.GetValue(client)).Dispose();
            httpField.SetValue(client, new HttpClient(new RejectReferenceLookup()) { BaseAddress = new Uri("http://isolated-cad.invalid/") });
            type.GetField("apiClient", Members).SetValue(addin, client);
            var resolve = type.GetMethod("ResolveBatchReferenceVersionsAsync", Members);
            Pump((Task)resolve.Invoke(addin, new object[] { items, Guid.NewGuid(), null, CancellationToken.None }));
            foreach (var embedded in Flatten(root).Where(node => Get(node, "Status").ToString() == "Virtual"))
            { embedded.GetType().GetProperty("DocumentId").SetValue(embedded, Guid.NewGuid()); embedded.GetType().GetProperty("CurrentRevision").SetValue(embedded, "W99"); }
            Pump((Task)resolve.Invoke(addin, new object[] { items, Guid.NewGuid(), null, CancellationToken.None }));
            Report["referenceIdentityPreflightPassed"] = true;
            Console.WriteLine("NATIVE REFERENCE PASS: embedded components require no separate registration or history queries");
            type.GetField("apiClient", Members).SetValue(addin, null);
        }
        var previous = SynchronizationContext.Current;
        var plannedPaths = ((IEnumerable)items).Cast<object>().Select(item => (string)Get(Get(item, "Node"), "FullPath")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var outsideHashes = HashFiles((string)Report["copies"]).Where(pair => !plannedPaths.Contains(Path.Combine((string)Report["copies"], pair.Key))).ToDictionary(pair => pair.Key, pair => pair.Value);
        var drawingHashes = HashFiles((string)Report["copies"]).Where(pair => pair.Key.EndsWith(".SLDDRW", StringComparison.OrdinalIgnoreCase)).ToDictionary(pair => pair.Key, pair => pair.Value);
        using (var host = new Form { ShowInTaskbar = false, Opacity = 0 })
        using (var pane = (Control)Activator.CreateInstance(plugin.GetType("Upton.Pdm.SolidWorks.PdmTaskPaneControl", true), Members & ~BindingFlags.Static, null, new object[] { "隔离存档准备验收" }, null))
        using (var click = new System.Threading.Timer(_ => {
            var window = FindWindow("#32770", "整体存档前保存文件");
            if (window != IntPtr.Zero) { GetWindowThreadProcessId(window, out var owner); if (owner == (uint)Process.GetCurrentProcess().Id) PostMessage(window, 0x111, new IntPtr(6), IntPtr.Zero); }
        }, null, 100, 100))
        {
            host.Controls.Add(pane); var handle = host.Handle; pane.CreateControl();
            type.GetField("taskPaneControl", Members).SetValue(addin, pane);
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            object opened = null;
            try
            {
                var preload = (Task)type.GetMethod("PreloadBatchCheckInDocumentsAsync", Members).Invoke(addin,
                    new object[] { items, (Action<string>)(stage => Console.WriteLine("PRELOAD: " + stage)), CancellationToken.None });
                Pump(preload); opened = preload.GetType().GetProperty("Result").GetValue(preload);
                var saves = (Task)type.GetMethod("EnsureBatchCheckInSavedStateAsync", Members).Invoke(addin, new object[] { items, Guid.NewGuid(), CancellationToken.None });
                Pump(saves);
                plugin.GetType("Upton.Pdm.SolidWorks.PdmApiClient", true).GetMethod("ValidateArchiveReferenceCompleteness", Members).Invoke(null, new[] { root });
                var cardsType = typeof(Dictionary<,>).MakeGenericType(plugin.GetType("Upton.Pdm.SolidWorks.CadDocumentKind", true), typeof(IReadOnlyList<string>));
                var cards = Activator.CreateInstance(cardsType);
                var configurations = Flatten(root).SelectMany(node => new[] { node }.Concat(((IEnumerable)Get(node, "ReferencedModels")).Cast<object>()))
                    .Where(node => Get(node, "Kind").ToString() != "Drawing" && !string.IsNullOrWhiteSpace((string)Get(node, "FullPath")))
                    .GroupBy(node => (string)Get(node, "FullPath"), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => (IReadOnlyList<string>)group.Select(node => (string)Get(node, "Configuration")).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), StringComparer.OrdinalIgnoreCase);
                var propertiesByPath = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
                var initialFlags = ((IEnumerable)items).Cast<object>().Select(item => (string)Get(Get(item, "Node"), "FullPath"))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(path => path,
                        path => (app.GetOpenDocumentByName(path) as IModelDoc2)?.GetSaveFlag() == true, StringComparer.OrdinalIgnoreCase);
                long peakBytes = Process.GetProcessById(app.GetProcessID()).PrivateMemorySize64;
                var drawingsRead = 0;
                foreach (var item in (IEnumerable)items)
                {
                    var node = Get(item, "Node");
                    propertiesByPath[(string)Get(node, "FullPath")] = (IReadOnlyDictionary<string, string>)type.GetMethod("ReadArchivePreflightProperties", Members)
                        .Invoke(addin, new object[] { node, cards, configurations });
                    if (Get(node, "Kind").ToString() == "Drawing")
                    {
                        type.GetMethod("ValidateDrawingModelsSaved", Members).Invoke(addin, new object[] { node, null });
                        var document = app.GetOpenDocumentByName((string)Get(node, "FullPath")) as IModelDoc2;
                        var owned = document == null;
                        if (owned) document = (IModelDoc2)type.GetMethod("OpenDrawingForArchive", Members).Invoke(addin, new object[] { Get(node, "FullPath") });
                        var create = type.GetMethods(Members).Single(method => method.Name == "CreateCheckInUploadCopy"
                            && method.GetParameters().Length == 3 && method.GetParameters()[0].ParameterType.Name == "IModelDoc2");
                        string copy = null;
                        try
                        {
                            copy = (string)create.Invoke(addin, new object[] { document, Get(node, "FullPath"), Guid.NewGuid() });
                            if (!File.Exists(copy) || new FileInfo(copy).Length == 0) throw new InvalidOperationException("工程图上传副本为空。");
                            type.GetMethod("ValidateDrawingModelsSaved", Members).Invoke(addin, new object[] { node, null });
                            if (Equals(Report["apiPayload"], true) && (mode == "resolved" || mode == "mixed-states" || mode == "virtual-fixture" || mode == "virtual-nested" || mode == "audit-native"))
                            {
                                var staging = Path.Combine(Path.GetDirectoryName((string)Report["copies"]), "staging");
                                Directory.CreateDirectory(staging);
                                File.Copy(copy, Path.Combine(staging, Path.GetFileName((string)Get(node, "FullPath"))));
                            }
                            drawingsRead++;
                            peakBytes = Math.Max(peakBytes, Process.GetProcessById(app.GetProcessID()).PrivateMemorySize64);
                            Console.WriteLine("DRAWING " + drawingsRead + ": " + Get(node, "FileName") + " privateBytes=" + peakBytes);
                        }
                        finally
                        {
                            if (copy != null) type.GetMethod("DeleteCheckInUploadCopy", Members).Invoke(null, new object[] { copy });
                            if (owned && document != null) type.GetMethod("CloseArchiveReadDocument", Members).Invoke(addin, new object[] { document });
                        }
                    }
                }
                type.GetMethod("ValidateBatchSavedState", Members).Invoke(addin, new object[] { items, initialFlags, false });
                Report["streamedDrawings"] = drawingsRead; Report["archivePeakPrivateBytes"] = peakBytes;
                if (!drawingHashes.Where(pair => mode != "existing-drawing" || !string.Equals(Path.Combine((string)Report["copies"], pair.Key),
                    (string)Report["existingDrawingPath"], StringComparison.OrdinalIgnoreCase))
                    .All(pair => Hash(Path.Combine((string)Report["copies"], pair.Key)) == pair.Value)) throw new InvalidOperationException("临时工程图预加载改写了源工程图。");
                if (Equals(Report["apiPayload"], true) && (mode == "resolved" || mode == "mixed-states" || mode == "virtual-fixture" || mode == "virtual-nested" || mode == "audit-native"))
                    WriteNativeApiPayload(plugin, app, addin, root, items, propertiesByPath);
                Report["sourceDrawingsUnchanged"] = true;
                if (outsideHashes.Any(pair => Hash(Path.Combine((string)Report["copies"], pair.Key)) != pair.Value))
                    throw new InvalidOperationException("存档准备改写了结构外文件，验证失败。");
                Report["outsideFilesUnchanged"] = true;
                Console.WriteLine("NATIVE PRELOAD PASS: " + mode + " models saved before drawings; final read-only preflight clean");
            }
            finally
            {
                if (opened != null) type.GetMethod("ClosePreloadedArchiveDocuments", Members).Invoke(addin, new[] { opened });
                SynchronizationContext.SetSynchronizationContext(previous);
            }
        }
    }

    private static void Pump(Task task)
    {
        while (!task.IsCompleted) { Application.DoEvents(); Thread.Sleep(10); }
        task.GetAwaiter().GetResult();
    }

    private sealed class RejectReferenceLookup : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => throw new InvalidOperationException("完整存档的嵌入式组件不应要求独立图档查询：" + request.RequestUri.AbsolutePath);
    }

    private static void WriteNativeApiPayload(Assembly plugin, SldWorks app, object addin, object root, object items,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> propertiesByPath)
    {
        var operationItems = ((IEnumerable)items).Cast<object>().ToArray();
        var paths = operationItems.Select(item => (string)Get(Get(item, "Node"), "FullPath")).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var ids = paths.ToDictionary(path => path, path => Guid.NewGuid(), StringComparer.OrdinalIgnoreCase);
        foreach (var node in Flatten(root).SelectMany(current => new[] { current }
            .Concat(((IEnumerable)Get(current, "ReferencedModels")).Cast<object>())))
            if (ids.TryGetValue((string)Get(node, "FullPath"), out var id))
                node.GetType().GetProperty("DocumentId").SetValue(node, id);
        var frozen = Path.Combine((string)Report["copies"], "..", "frozen-source");
        var staging = Path.Combine((string)Report["copies"], "..", "staging");
        Directory.CreateDirectory(frozen); Directory.CreateDirectory(staging);
        var client = plugin.GetType("Upton.Pdm.SolidWorks.PdmApiClient", true);
        var capture = client.GetMethod("CaptureArchiveSnapshot", Members);
        var fingerprint = plugin.GetType("Upton.Pdm.SolidWorks.ArchiveFileFingerprint", true).GetMethod("Read", Members);
        var files = new List<object>();
        foreach (var item in operationItems)
        {
            var node = Get(item, "Node");
            var source = (string)Get(node, "FullPath");
            var name = Path.GetFileName(source);
            var hash = (string)fingerprint.Invoke(null, new object[] { source, CancellationToken.None });
            var sourceCopy = Path.Combine(frozen, name);
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var output = new FileStream(sourceCopy, FileMode.CreateNew, FileAccess.Write, FileShare.None)) input.CopyTo(output);
            if (Hash(sourceCopy) != hash || (string)fingerprint.Invoke(null, new object[] { source, CancellationToken.None }) != hash)
                throw new InvalidOperationException("真实图档在生成隔离存档快照期间变化：" + source);
            var upload = sourceCopy;
            if (Get(node, "Kind").ToString() == "Drawing")
            {
                upload = Path.Combine(staging, name);
                if (!File.Exists(upload) || new FileInfo(upload).Length == 0)
                    throw new InvalidOperationException("工程图上传副本未从真实预检阶段生成：" + source);
            }
            var originalRevision = Get(node, "Revision");
            node.GetType().GetProperty("Revision").SetValue(node, "W1");
            object snapshot;
            try { snapshot = capture.Invoke(null, new object[] { node, new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase) }); }
            finally { node.GetType().GetProperty("Revision").SetValue(node, originalRevision); }
            var serializedRoot = snapshot.GetType().GetMethod("CreateRoot", Members).Invoke(snapshot, null);
            files.Add(new { key = ids[source], path = upload, sourcePath = sourceCopy, sourceSha256 = hash,
                isProjectRoot = ReferenceEquals(node, root), root = serializedRoot, properties = propertiesByPath[source] });
        }
        var file = Path.Combine(Path.GetDirectoryName((string)Report["copies"]), "native-api-request.json");
        File.WriteAllText(file, new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(new { files }));
        Report["apiPayloadPath"] = file; Report["apiPayloadFiles"] = files.Count;
        Console.WriteLine("NATIVE API PAYLOAD: " + file + " files=" + files.Count);
    }

    private static void ExpandSuppressed<T>(Assembly plugin, SldWorks app, T root)
    {
        var addinType = plugin.GetType("Upton.Pdm.SolidWorks.PdmAddin", true);
        var addin = Activator.CreateInstance(addinType);
        addinType.GetField("application", Members).SetValue(addin, app);
        var read = addinType.GetMethod("ReadSuppressedAssemblyStructure", Members);
        Func<T, Task<T>> reader = node => Task.FromResult((T)read.Invoke(addin, new object[] { node }));
        var task = (Task)plugin.GetType("Upton.Pdm.SolidWorks.SuppressedAssemblyLocalStructure", true)
            .GetMethod("ExpandAsync", Members).Invoke(null, new object[] { root, reader, CancellationToken.None });
        task.GetAwaiter().GetResult();
    }

    private static object Get(object value, string name) => value.GetType().GetProperty(name, Members).GetValue(value);
    private static IEnumerable<object> Flatten(object node)
    {
        yield return node;
        foreach (var child in ((IEnumerable)Get(node, "Children")).Cast<object>())
            foreach (var descendant in Flatten(child)) yield return descendant;
    }
    private static string Hash(string path)
    {
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) using (var hash = SHA256.Create())
            return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "");
    }
    private static Dictionary<string, string> HashFiles(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .Where(path => !Path.GetFileName(path).StartsWith("~$", StringComparison.Ordinal) || new FileInfo(path).Length >= 1024)
        .ToDictionary(path => path.Substring(root.TrimEnd(Path.DirectorySeparatorChar).Length + 1), Hash, StringComparer.OrdinalIgnoreCase);
    private static bool SameHashes(Dictionary<string, string> before, Dictionary<string, string> after) => before.Count == after.Count
        && before.All(pair => after.TryGetValue(pair.Key, out var hash) && pair.Value == hash);
    private static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        foreach (var directory in Directory.GetDirectories(source)) CopyTree(directory, Path.Combine(target, Path.GetFileName(directory)));
    }
}
