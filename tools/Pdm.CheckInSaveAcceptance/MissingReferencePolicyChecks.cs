using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Collections.Generic;
using System.Runtime.Remoting.Messaging;
using System.Reflection;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static partial class Program
{
    static object capturedWarningSettings;
    static Task<TResult> CaptureWarningSettings<TSettings, TResult>(TSettings settings)
    { capturedWarningSettings = settings; return Task.FromResult(default(TResult)); }

    static async Task MissingReferencePolicyScenario(Form host, string output)
    {
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        void Check(bool value, string message) { TreeCheck(value, message); Console.WriteLine("PASS: " + message); }
        var policy = Type("ArchiveMissingReferencePolicy");
        var block = policy.GetProperty("BlockMissingReferences", Flags); var previous = block.GetValue(null);
        var settings = New("PluginSettings");
        Check(!(bool)Get(settings, "BlockArchiveOnMissingReferences"), "首次使用默认仅提示，不阻止缺失引用存档");
        var json = new JavaScriptSerializer();
        var legacy = json.Deserialize("{\"SchemaVersion\":3}", Type("PluginSettings"));
        Check(!(bool)Get(legacy, "BlockArchiveOnMissingReferences"), "已有设置缺少新字段时仍默认不阻止");
        Set(settings, "BlockArchiveOnMissingReferences", true);
        Check((bool)Get(json.Deserialize(json.Serialize(settings), Type("PluginSettings")), "BlockArchiveOnMissingReferences"), "阻止选项可按现有设置格式持久化");
        var root = LinkedNode(output, "总装.SLDASM", "Assembly"); var part = LinkedNode(output, "已勾选零件.SLDPRT", "Part");
        Set(root, "Revision", "A"); Set(root, "CurrentRevision", "A"); Set(part, "Revision", "A"); Set(part, "CurrentRevision", "A");
        ((IList)Get(root, "Children")).Add(part);
        var missing = New("CadTreeNode"); Set(missing, "FullPath", Path.Combine(output, "old-user", "缺失零件.SLDPRT"));
        Set(missing, "FileName", "缺失零件.SLDPRT"); Set(missing, "Kind", Enum.Parse(Type("CadDocumentKind"), "Part"));
        Set(missing, "Status", Enum.Parse(Type("CadReferenceStatus"), "Missing")); ((IList)Get(root, "Children")).Add(missing);
        var uncheckedFile = LinkedNode(output, "未勾选新零件.SLDPRT", "Part"); Set(uncheckedFile, "DocumentId", null); ((IList)Get(root, "Children")).Add(uncheckedFile);
        var selected = Array.CreateInstance(Type("CadTreeNode"), 2); selected.SetValue(root, 0); selected.SetValue(part, 1);
        var nativeFiles = new[] { (string)Get(root, "FullPath"), (string)Get(part, "FullPath"), (string)Get(missing, "FullPath"), (string)Get(uncheckedFile, "FullPath") };
        var before = WorkingReuseHash((string)Get(root, "FullPath"));
        try
        {
            block.SetValue(null, false);
            policy.GetMethod("Prepare", Flags).Invoke(null, new object[] { root, selected });
            Check((bool)Get(missing, "IsOutsideNativeFileScope") && ((IList)Get(root, "ReferenceWarnings")).Count == 2, "缺失和未勾选新增引用均提示，不扩大上传清单");
            Check(Get(missing, "DocumentId") == null && !File.Exists((string)Get(missing, "FullPath")), "不伪造缺失文件、图档身份或存档版本");
            Type("NativeArchiveManifest").GetMethod("ConfirmFileScope", Flags).Invoke(null, new object[] { root, nativeFiles });
            Type("NativeArchiveManifest").GetMethod("Validate", Flags).Invoke(null, new object[] { root, nativeFiles });
            Check((bool)Get(missing, "IsOutsideNativeFileScope"), "原生清单包含旧地址时也不撤销已确认的仅提示策略");
            var snapshot = Type("PdmApiClient").GetMethod("CaptureArchiveSnapshot", Flags).Invoke(null, new object[] { root, nativeFiles.Take(2).ToArray() });
            var captured = (System.Collections.Generic.Dictionary<string, object>)snapshot.GetType().GetMethod("CreateRoot", Flags).Invoke(snapshot, null);
            var serialized = json.Serialize(captured);
            Check(serialized.Contains("referenceWarnings") && serialized.Contains("缺失零件.SLDPRT") && Equals(captured["structureIndexPending"], true), "提示和结构待补全记录进入真实版本快照");
            var children = ((IEnumerable)captured["children"]).Cast<System.Collections.Generic.Dictionary<string, object>>().ToArray();
            Check(children.All(child => (string)child["fileName"] == "已勾选零件.SLDPRT"), "未勾选新文件与缺失引用不混入受控快照身份清单");
            block.SetValue(null, true); Exception strict = null;
            try { policy.GetMethod("Prepare", Flags).Invoke(null, new object[] { root, selected }); } catch (TargetInvocationException error) { strict = error.InnerException; }
            Check(strict is InvalidOperationException && strict.Message.Contains("已启用"), "启用阻止选项时在扫描、保存或上传前明确拒绝");
            var partOnly = Array.CreateInstance(Type("CadTreeNode"), 1); partOnly.SetValue(part, 0);
            policy.GetMethod("Prepare", Flags).Invoke(null, new object[] { root, partOnly });
            Check(true, "只选独立零件时不因无关总装的缺失引用阻止");
            block.SetValue(null, false); var missingSelected = Array.CreateInstance(Type("CadTreeNode"), 1); missingSelected.SetValue(missing, 0);
            Exception absent = null; try { policy.GetMethod("Prepare", Flags).Invoke(null, new object[] { root, missingSelected }); } catch (TargetInvocationException error) { absent = error.InnerException; }
            Check(absent is IOException && absent.Message.Contains("已勾选"), "选中文件本身没有可上传字节时始终报错，不冒充存档成功");
            Check(WorkingReuseHash((string)Get(root, "FullPath")) == before && File.Exists((string)Get(uncheckedFile, "FullPath")), "引用提示处理不改写总装或未勾选的真实文件");
        }
        finally { block.SetValue(null, previous); }

        var appType = Type("PdmAddin").GetField("application", Flags).FieldType; var interop = appType.Assembly;
        object model = null; bool? includeDrawings = null; var calls = new List<string>();
        var pack = new Proxy(interop.GetType("SolidWorks.Interop.sldworks.PackAndGo", true), call =>
        {
            calls.Add(call.MethodName);
            if (call.MethodName == "set_IncludeDrawings") { includeDrawings = (bool)call.Args[0]; return null; }
            if (call.MethodName == "set_IncludeSuppressed") return null;
            if (call.MethodName == "GetDocumentNames") return new ReturnMessage(true, new object[] { nativeFiles.Take(2).ToArray() }, 1, call.LogicalCallContext, call);
            throw new Exception("清单不得保存：" + call.MethodName);
        }).GetTransparentProxy();
        var extension = new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDocExtension", true), call =>
            call.MethodName == "GetPackAndGo" ? pack : throw new Exception("不得改动原模型：" + call.MethodName)).GetTransparentProxy();
        var modelType = interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2", true);
        model = new Proxy(modelType, call => call.MethodName == "GetType" ? modelType
            : call.MethodName == "GetPathName" ? nativeFiles[0] : call.MethodName == "GetTitle" ? Path.GetFileName(nativeFiles[0])
            : call.MethodName == "get_Extension" ? extension : throw new Exception("禁止的模型调用：" + call.MethodName)).GetTransparentProxy();
        var app = new Proxy(appType, call => call.MethodName == "GetType" ? appType
            : call.MethodName == "get_ActiveDoc" || call.MethodName == "ActivateDoc3" ? model
            : throw new Exception("不得打开额外图纸：" + call.MethodName)).GetTransparentProxy();
        var scanner = New("SolidWorksReferenceTreeScanner", app);
        SetField(scanner, "importedStorage", New("ImportedComponentStorage", app));
        var probe = New("CadTreeNode"); Set(probe, "FullPath", nativeFiles[0]); Set(probe, "FileName", Path.GetFileName(nativeFiles[0]));
        Set(probe, "Kind", Enum.Parse(Type("CadDocumentKind"), "Assembly"));
        Set(probe, "ConfirmedArchivePaths", new HashSet<string>(nativeFiles.Take(2), StringComparer.OrdinalIgnoreCase));
        TreeCall(scanner, "AddNativeAssociatedDrawings", model, probe);
        Check(includeDrawings == null, "确认勾选后不启动完整Pack and Go清单");
        Check(calls.Count == 0, "已确认文件范围直接收集，不调用打包、保存或修改模型");

        var constructor = Type("PluginSettingsDialog").GetConstructors(Flags).Single();
        var arguments = constructor.GetParameters().Select(parameter =>
        {
            if (!typeof(Delegate).IsAssignableFrom(parameter.ParameterType)) return Activator.CreateInstance(parameter.ParameterType, true);
            var invoke = parameter.ParameterType.GetMethod("Invoke");
            if (parameter.Name == "saveSettings")
                return (object)Delegate.CreateDelegate(parameter.ParameterType, typeof(Program).GetMethod("CaptureWarningSettings", Flags)
                    .MakeGenericMethod(invoke.GetParameters()[0].ParameterType, invoke.ReturnType.GetGenericArguments()[0]));
            object value = invoke.ReturnType.IsGenericType && invoke.ReturnType.GetGenericTypeDefinition() == typeof(Task<>)
                ? typeof(Task).GetMethod("FromResult").MakeGenericMethod(invoke.ReturnType.GetGenericArguments()[0])
                    .Invoke(null, new[] { Activator.CreateInstance(invoke.ReturnType.GetGenericArguments()[0], true) })
                : Activator.CreateInstance(invoke.ReturnType, true);
            return (object)Expression.Lambda(parameter.ParameterType, Expression.Constant(value, invoke.ReturnType), invoke.GetParameters().Select(arg => Expression.Parameter(arg.ParameterType))).Compile();
        }).ToArray();
        using (var dialog = (Form)constructor.Invoke(arguments))
        {
            dialog.Opacity = 0; dialog.ShowInTaskbar = false; dialog.Show(host);
            var option = (CheckBox)Field(dialog, "blockMissingReferences");
            Check(!option.Enabled && option.Text.Contains("管理员"), "本地开关已退役，策略由管理员统一配置");
            Check(option.ClientRectangle.Width > 300 && option.Height >= 20, "中文警告设置在现有弹窗内完整布局");
            Check(dialog.ClientRectangle.Contains(dialog.RectangleToClient(option.RectangleToScreen(option.ClientRectangle))), "警告选项完整显示在设置页内");
            using (var bitmap = new System.Drawing.Bitmap(dialog.Width, dialog.Height))
            { dialog.DrawToBitmap(bitmap, new System.Drawing.Rectangle(System.Drawing.Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(output, "missing-reference-settings.png")); }
            option.Checked = false;
            await (Task)TreeCall(dialog, "SaveAsync");
            Check(capturedWarningSettings != null && !option.Enabled, "普通设置保存不会开放本地策略开关");
        }
    }
}
