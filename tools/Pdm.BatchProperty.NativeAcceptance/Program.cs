using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

internal static class Program
{
    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private static Assembly plugin;

    [STAThread]
    private static int Main(string[] args)
    {
        if ((args.Length != 2 && !(args.Length == 3 && args[2] == "--separate-instance")) || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("Usage: Pdm.BatchProperty.NativeAcceptance.exe <candidate-addin-dll> <scratch-directory> [--separate-instance]");
            return 2;
        }
        SldWorks app = null;
        var ownedApplication = false;
        var ownedPid = 0;
        var ownedStarted = DateTime.MinValue;
        var created = new List<IModelDoc2>();
        var scratch = Path.Combine(Path.GetFullPath(args[1]), "native-" + Guid.NewGuid().ToString("N"));
        try
        {
            AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
            {
                var path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[0])), new AssemblyName(e.Name).Name + ".dll");
                return File.Exists(path) ? Assembly.LoadFrom(path) : null;
            };
            plugin = Assembly.LoadFrom(Path.GetFullPath(args[0]));
            Directory.CreateDirectory(scratch);
            if (args.Length != 3)
            {
                try { app = (SldWorks)Marshal.GetActiveObject("SldWorks.Application"); }
                catch (COMException error) when (error.ErrorCode == unchecked((int)0x800401E3)) { }
            }
            if (app == null)
            {
                var existingIds = Process.GetProcessesByName("SLDWORKS").Select(process => process.Id).ToArray();
                app = (SldWorks)Activator.CreateInstance(System.Type.GetTypeFromProgID("SldWorks.Application", true));
                var id = app.GetProcessID();
                if (existingIds.Contains(id)) { Marshal.ReleaseComObject(app); app = null; throw new InvalidOperationException("独立验收实例复用了已有进程，未操作原会话。"); }
                using (var owned = Process.GetProcessById(id))
                {
                    if (!owned.ProcessName.Equals("SLDWORKS", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("无法确认独立验收进程身份。");
                    ownedPid = id; ownedStarted = owned.StartTime.ToUniversalTime();
                }
                ownedApplication = true;
                File.WriteAllText(Path.Combine(scratch, "owned-process.txt"), id + "\n" + ownedStarted.ToString("O"));
                app.Visible = false;
                var interactive = app.GetAddInObject("{BCFD8A8A-472B-42E2-AC62-58BC17773650}");
                if (interactive != null) { if (Marshal.IsComObject(interactive)) Marshal.ReleaseComObject(interactive); throw new InvalidOperationException("独立后台验收实例加载了交互插件，未操作图档或修改启动设置。"); }
                Console.WriteLine("Owned native test process=" + id);
            }
            var originalDocuments = ((app.GetDocuments() as object[]) ?? new object[0]).OfType<IModelDoc2>().ToArray();
            foreach (var configurationSpecific in new[] { false, true })
            {
                var model = (IModelDoc2)app.NewDocument(@"C:\ProgramData\SOLIDWORKS\SOLIDWORKS 2022\templates\gb_part.prtdot", 0, 0, 0);
                if (model == null) throw new InvalidOperationException("未能创建独立零件验收样本。");
                created.Add(model);
                var configuration = model.ConfigurationManager.ActiveConfiguration.Name;
                var file = Path.Combine(scratch, configurationSpecific ? "配置属性.SLDPRT" : "全局属性.SLDPRT");
                var card = Path.Combine(scratch, configurationSpecific ? "配置.prtprp" : "全局.prtprp");
                var scope = configurationSpecific ? " ApplyTo=\"Config\"" : "";
                File.WriteAllText(card, "<Root><Control PropName=\"型号\"" + scope + "/><Control PropName=\"未编辑字段\"" + scope + "/></Root>");
                var target = model.Extension.CustomPropertyManager[configurationSpecific ? configuration : ""];
                var other = model.Extension.CustomPropertyManager[configurationSpecific ? "" : configuration];
                Add(target, "型号", "原型号");
                Add(other, "型号", "另一范围原型号");
                Add(target, "关联原文", "$PRP:\"型号\"");
                var rawExpression = Read(target, "关联原文");
                var errors = 0; var warnings = 0;
                if (!model.Extension.SaveAs3(file, 0, (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null, null, ref errors, ref warnings) || errors != 0)
                    throw new IOException("独立验收样本保存失败：" + errors);
                var node = New("CadTreeNode");
                Set(node, "FullPath", file); Set(node, "FileName", Path.GetFileName(file));
                Set(node, "Kind", Enum.ToObject(T("CadDocumentKind"), 1)); Set(node, "Configuration", configuration);
                var item = New("BatchPropertyEditItem", New("BatchOperationItem", node, 1), "", "", configuration,
                    new Dictionary<string, string> { { "型号", "原型号" } }, new Dictionary<string, string>(), new[] { "型号" }, "", "");
                var template = T("NativePropertyCardTemplate").GetMethod("Load", Members).Invoke(null, new object[] { card });
                T("BatchPropertyEditItem").GetMethod("ApplyPropertyCard", Members).Invoke(item, new[] { template });
                Set(item, "Model", "新型号");
                var addin = New("PdmAddin");
                T("PdmAddin").GetField("application", Members).SetValue(addin, app);
                T("PdmAddin").GetField("currentTree", Members).SetValue(addin, node);
                T("PdmAddin").GetMethod("ApplyBatchPropertyEdit", Members).Invoke(addin, new[] { item });
                Verify(model, configuration, configurationSpecific, rawExpression);
                app.CloseDoc(model.GetTitle());
                created.Remove(model);
                model = (IModelDoc2)app.OpenDoc6(file, (int)swDocumentTypes_e.swDocPART,
                    (int)(swOpenDocOptions_e.swOpenDocOptions_Silent | swOpenDocOptions_e.swOpenDocOptions_ReadOnly), configuration, ref errors, ref warnings);
                if (model == null || errors != 0 || !string.Equals(model.GetPathName(), file, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("保存后重开验收样本失败：" + errors);
                created.Add(model);
                Verify(model, configuration, configurationSpecific, rawExpression);
                app.CloseDoc(model.GetTitle());
                created.Remove(model);
                Console.WriteLine("PASS: native " + (configurationSpecific ? "configuration" : "global") + " edit persists after reopen and preserves other scope, missing fields and expression");
            }
            var after = ((app.GetDocuments() as object[]) ?? new object[0]).OfType<IModelDoc2>().ToArray();
            if (originalDocuments.Any(document => !after.Contains(document)))
                throw new InvalidOperationException("原先打开的文档集合发生变化。");
            Console.WriteLine("PASS: all pre-existing open documents remain open; scratch=" + scratch);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        finally
        {
            // These COM objects come exclusively from this invocation's NewDocument/OpenDoc6.
            foreach (var model in created)
            {
                try { app?.CloseDoc(model.GetTitle()); }
                catch (Exception error) { Console.Error.WriteLine("验收样本关闭失败：" + error.Message); }
            }
            if (ownedApplication)
            {
                try
                {
                    using (var owned = Process.GetProcessById(ownedPid))
                    {
                        if (!owned.ProcessName.Equals("SLDWORKS", StringComparison.OrdinalIgnoreCase) || owned.StartTime.ToUniversalTime() != ownedStarted)
                            throw new InvalidOperationException("验收进程身份已变化，未关闭该会话。");
                        app?.ExitApp();
                        if (!owned.WaitForExit(20000)) { owned.Kill(); owned.WaitForExit(10000); }
                    }
                }
                catch (ArgumentException) { }
                finally { if (app != null) Marshal.FinalReleaseComObject(app); }
            }
        }
    }

    private static void Verify(IModelDoc2 model, string configuration, bool configurationSpecific, string expression)
    {
        var target = model.Extension.CustomPropertyManager[configurationSpecific ? configuration : ""];
        var other = model.Extension.CustomPropertyManager[configurationSpecific ? "" : configuration];
        if (Read(target, "型号") != "新型号" || Read(other, "型号") != "另一范围原型号")
            throw new InvalidOperationException("目标属性修改错误，或另一作用域的值被覆盖/删除。");
        if (((target.GetNames() as string[]) ?? new string[0]).Contains("未编辑字段")
            || ((other.GetNames() as string[]) ?? new string[0]).Contains("未编辑字段"))
            throw new InvalidOperationException("批量属性修改意外创建了未编辑字段。");
        if (Read(target, "关联原文") != expression) throw new InvalidOperationException("未编辑属性的表达式被破坏。");
    }

    private static string Read(CustomPropertyManager manager, string name)
    {
        var result = manager.Get6(name, false, out var raw, out _, out _, out _);
        if (result == (int)swCustomInfoGetResult_e.swCustomInfoGetResult_NotPresent) throw new InvalidOperationException("缺少属性：" + name);
        return raw;
    }

    private static void Add(CustomPropertyManager manager, string name, string value)
    {
        if (manager.Add3(name, (int)swCustomInfoType_e.swCustomInfoText, value, (int)swCustomPropertyAddOption_e.swCustomPropertyDeleteAndAdd) != 0)
            throw new InvalidOperationException("未能建立验收属性：" + name);
    }

    private static System.Type T(string name) => plugin.GetType("Upton.Pdm.SolidWorks." + name, true);
    private static object New(string name, params object[] args) => Activator.CreateInstance(T(name), Members & ~BindingFlags.Static, null, args, null);
    private static void Set(object instance, string name, object value) => instance.GetType().GetProperty(name, Members).SetValue(instance, value);
}
