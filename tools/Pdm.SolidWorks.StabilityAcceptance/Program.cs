using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Win32;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

internal static class Program
{
    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private const string StartupKey = @"Software\SolidWorks\AddInsStartup\{BCFD8A8A-472B-42E2-AC62-58BC17773650}";
    private static readonly Dictionary<string, object> Report = new Dictionary<string, object>();
    private static readonly List<object> Scans = new List<object>();

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length < 3 || args.Length > 5 || args.Length == 5 && args[4] != "--structure-only") { Console.Error.WriteLine("Usage: <latest-addin-dll> <source-fixture-directory> <new-qa-directory> [root-assembly-file-name] [--structure-only]"); return 2; }
        Report["structureOnly"] = args.Length == 5;
        var addinPath = Path.GetFullPath(args[0]);
        var source = Path.GetFullPath(args[1]);
        var qa = Path.GetFullPath(args[2]);
        if (Process.GetProcessesByName("SLDWORKS").Length > 0) { Console.Error.WriteLine("已有SolidWorks进程，拒绝接管用户会话。"); return 2; }
        if (Directory.Exists(qa) || qa.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("验收目录必须是独立的新目录。");
        var before = HashFiles(source);
        Directory.CreateDirectory(qa);
        var copies = Path.Combine(qa, "sources");
        CopyTree(source, copies);
        var reportPath = Path.Combine(qa, "result.json");
        Report["source"] = source; Report["copies"] = copies; Report["addin"] = addinPath;
        Report["addinSha256"] = Hash(addinPath); Report["startedAt"] = DateTimeOffset.Now.ToString("O"); Report["scans"] = Scans;
        var plugin = Assembly.LoadFrom(addinPath);
        AppDomain.CurrentDomain.AssemblyResolve += (_, eventArgs) =>
        {
            var path = Path.Combine(Path.GetDirectoryName(addinPath), new AssemblyName(eventArgs.Name).Name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
        if (Equals(Report["structureOnly"], true))
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
            if (Process.GetProcessesByName("SLDWORKS").Length != 0) throw new InvalidOperationException("启动前检测到新的用户会话，已停止验收。");
            Console.WriteLine("NATIVE: starting isolated hidden SolidWorks; UPLM autoload disabled");
            app = (SldWorks)Activator.CreateInstance(System.Type.GetTypeFromProgID("SldWorks.Application", true));
            app.Visible = false; app.UserControl = false;
            ownedPid = app.GetProcessID();
            ownedStarted = Process.GetProcessById(ownedPid).StartTime.ToUniversalTime();
            Report["processId"] = ownedPid; Report["solidWorksVersion"] = app.RevisionNumber();
            Console.WriteLine("NATIVE: version=" + Report["solidWorksVersion"] + " pid=" + ownedPid);
            searchFolders = app.GetSearchFolders((int)swSearchFolderTypes_e.swDocumentType); workingDirectory = app.GetCurrentWorkingDirectory();
            searchRules = app.GetUserPreferenceToggle((int)swUserPreferenceToggle_e.swUseFolderSearchRules);
            if (!app.SetSearchFolders((int)swSearchFolderTypes_e.swDocumentType, copies)) throw new InvalidOperationException("无法设置独立样本搜索目录。");
            app.SetCurrentWorkingDirectory(copies);
            app.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swUseFolderSearchRules, true);
            if (args.Length >= 4 && Path.GetFileName(args[3]) != args[3]) throw new InvalidOperationException("根装配参数只能是样本内的文件名。");
            var assemblies = args.Length >= 4 ? new[] { Path.Combine(copies, args[3]) } : Directory.GetFiles(copies, "*.SLDASM", SearchOption.TopDirectoryOnly);
            if (assemblies.Length != 1) throw new InvalidOperationException("验收样本必须有唯一根装配体。");
            var expectedDrawings = Directory.GetFiles(copies, "*.SLDDRW", SearchOption.AllDirectories).Length;
            Report["expectedDrawingCount"] = expectedDrawings;
            int errors = 0, warnings = 0;
            var model = app.OpenDoc6(assemblies[0], (int)swDocumentTypes_e.swDocASSEMBLY,
                (int)(swOpenDocOptions_e.swOpenDocOptions_Silent | swOpenDocOptions_e.swOpenDocOptions_ReadOnly), "", ref errors, ref warnings);
            Report["openErrors"] = errors; Report["openWarnings"] = warnings;
            if (model == null || errors != 0) throw new InvalidOperationException("只读打开失败，错误=" + errors + "，警告=" + warnings);
            Console.WriteLine("NATIVE: read-only root opened " + DateTimeOffset.Now.ToString("O"));
            if (!model.IsOpenedReadOnly()) throw new InvalidOperationException("样本未以只读方式打开。");
            var outside = (app.GetDocuments() as Array)?.Cast<object>().OfType<IModelDoc2>()
                .Select(document => document.GetPathName()).Where(path => !string.IsNullOrWhiteSpace(path)
                    && !Path.GetFullPath(path).StartsWith(copies + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToArray() ?? new string[0];
            Report["loadedOutsideQa"] = outside;
            if (outside.Length > 0) throw new InvalidOperationException("样本引用加载到了验收目录之外，已停止内存状态验证。");
            var assembly = (IAssemblyDoc)model;
            var resolveResult = assembly.ResolveAllLightWeightComponents(false);
            Report["resolveResult"] = resolveResult;
            Console.WriteLine("NATIVE: lightweight resolve finished " + DateTimeOffset.Now.ToString("O"));
            Scan(plugin, app, model, "resolved", expectedDrawings);
            var components = (assembly.GetComponents(false) as Array)?.Cast<object>().OfType<IComponent2>().ToArray() ?? new IComponent2[0];
            var part = components.FirstOrDefault(component => !component.IsVirtual && component.GetSuppression2() != (int)swComponentSuppressionState_e.swComponentSuppressed
                && component.GetPathName().EndsWith(".SLDPRT", StringComparison.OrdinalIgnoreCase));
            if (part == null) throw new InvalidOperationException("样本没有可验证状态切换的零件。");
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
            passed = true;
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
            Report["passed"] = passed && unchanged && copiesUnchanged;
            File.WriteAllText(reportPath, new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(Report));
            Console.WriteLine("NATIVE REPORT: " + reportPath);
        }
        return Equals(Report["passed"], true) ? 0 : 1;
    }

    private static void Scan(Assembly plugin, SldWorks app, IModelDoc2 model, string mode, int expectedDrawings)
    {
        Console.WriteLine("NATIVE: scan start " + mode + " " + DateTimeOffset.Now.ToString("O"));
        var type = plugin.GetType("Upton.Pdm.SolidWorks.SolidWorksReferenceTreeScanner", true);
        var scanner = Activator.CreateInstance(type, Members & ~BindingFlags.Static, null, new object[] { app }, null);
        var root = type.GetMethod("ScanDocument", Members).Invoke(scanner, new object[] { model, true });
        Console.WriteLine("NATIVE: component scan finished " + mode + " " + DateTimeOffset.Now.ToString("O"));
        if (Equals(Report["structureOnly"], true))
            typeof(Program).GetMethod("ExpandSuppressed", BindingFlags.NonPublic | BindingFlags.Static).MakeGenericMethod(root.GetType()).Invoke(null, new object[] { plugin, app, root });
        else type.GetMethod("AddNativeAssociatedDrawings", Members).Invoke(scanner, new object[] { model, root });
        var nodes = Flatten(root).ToArray();
        var drawings = nodes.Where(node => Get(node, "Kind").ToString() == "Drawing")
            .Select(node => (string)Get(node, "FullPath")).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path).ToArray();
        var statuses = nodes.GroupBy(node => Get(node, "Status").ToString()).ToDictionary(group => group.Key, group => group.Count());
        Scans.Add(new { mode, nodeCount = nodes.Length, drawings, statuses });
        if (!Equals(Report["structureOnly"], true) && drawings.Length != expectedDrawings) throw new InvalidOperationException("真实关联工程图未全部发现：" + drawings.Length + "/" + expectedDrawings);
        Console.WriteLine("NATIVE PASS: " + mode + " nodes=" + nodes.Length + " drawings=" + drawings.Length);
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
        using (var stream = File.OpenRead(path)) using (var hash = SHA256.Create())
            return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "");
    }
    private static Dictionary<string, string> HashFiles(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
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
