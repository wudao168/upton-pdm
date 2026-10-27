using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static partial class Program
{
    static void NativeDrawingQrRemovalScenario(string output)
    {
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        foreach (var name in new[] { "DrawingQrCodeService", "DrawingQrLayoutRule", "DrawingQrDeletionRule", "DrawingQrPolicyDto" })
            TreeCheck(assembly.GetType("Upton.Pdm.SolidWorks." + name) == null, "仍编译了原生工程图二维码组件：" + name);
        TreeCheck(!assembly.GetReferencedAssemblies().Any(item => item.Name == "QRCoder"), "插件仍依赖二维码生成库");
        Console.WriteLine("PASS: Native QR renderer, layout/deletion rules, policy DTO and QRCoder dependency are absent");

        foreach (var name in new[] { "OnDrawingFileSaving", "TrySynchronizeSavedDrawingQrCode", "TryRefreshSavedDrawingQrGraphics", "SynchronizeDrawingQrCode", "BeginAutomaticDrawingSave", "EndAutomaticDrawingSave" })
            TreeCheck(Type("PdmAddin").GetMethod(name, Flags) == null, "仍存在二维码保存处理：" + name);
        TreeCheck(Type("PdmApiClient").GetMethod("GetDrawingQrPolicyAsync", Flags) == null, "插件仍读取原生二维码策略");
        Console.WriteLine("PASS: Save/idle QR synchronization and legacy policy requests are removed");

        var serializer = new JavaScriptSerializer();
        var settings = serializer.Deserialize("{\"SchemaVersion\":3,\"ServerAddress\":\"http://192.168.2.8:5173\",\"AutomaticUpdatesEnabled\":false,\"PlannedTaskScrollIntervalSeconds\":60,\"UseCustomDrawingQrPosition\":true,\"DrawingQrXMillimeters\":100,\"DrawingQrYMillimeters\":100}", Type("PluginSettings"));
        TreeCall(settings, "Normalize");
        TreeCheck((int)Get(settings, "PlannedTaskScrollIntervalSeconds") == 60 && !(bool)Get(settings, "AutomaticUpdatesEnabled"), "旧设置中的有效偏好未保留");
        TreeCheck(!serializer.Serialize(settings).Contains("DrawingQr"), "保存设置仍包含已删除的二维码选项");
        Console.WriteLine("PASS: Legacy QR settings are ignored while connection, update and planned-task preferences remain usable");

        var plugin = New("PdmAddin");
        var nativeWrites = 0;
        var appType = plugin.GetType().GetField("application", Flags).FieldType;
        SetField(plugin, "application", new Proxy(appType, call => { if (call.MethodName == "get_ActiveDoc") return null; nativeWrites++; throw new Exception("Unexpected native call: " + call.MethodName); }).GetTransparentProxy());
        TreeCheck((int)TreeCall(plugin, "OnDrawingFileSaved", 0, "test.SLDDRW") == 0 && nativeWrites == 0
            && (int)Field(plugin, "pendingTreeRefresh") == 1, "工程图保存回调仍触发原生修改，或丢失设计树刷新");
        Console.WriteLine("PASS: Drawing save retains ordinary tree refresh without opening, rewriting or saving a native document again");

        var snapshot = New("PluginUpdateSnapshot");
        var constructor = Type("PluginSettingsDialog").GetConstructors(Flags).Single();
        var arguments = new object[constructor.GetParameters().Length]; arguments[0] = settings; arguments[1] = snapshot;
        for (var index = 2; index < arguments.Length; index++)
        {
            var delegateType = constructor.GetParameters()[index].ParameterType;
            var invoke = delegateType.GetMethod("Invoke");
            object value;
            if (invoke.ReturnType == Type("PluginUpdateSnapshot")) value = snapshot;
            else
            {
                var resultType = invoke.ReturnType.GetGenericArguments()[0];
                var result = resultType == Type("PluginUpdateSnapshot") ? snapshot : New("PluginConnectionResult");
                value = typeof(Task).GetMethod("FromResult").MakeGenericMethod(resultType).Invoke(null, new[] { result });
            }
            arguments[index] = Expression.Lambda(delegateType, Expression.Constant(value, invoke.ReturnType),
                invoke.GetParameters().Select(parameter => Expression.Parameter(parameter.ParameterType)).ToArray()).Compile();
        }
        using (var dialog = (Form)constructor.Invoke(arguments))
        {
            var controls = ArchiveControls(dialog).ToArray();
            TreeCheck(!controls.Any(control => control.Text.Contains("二维码")) && !controls.OfType<NumericUpDown>().Any(), "设置页仍显示二维码选项");
            TreeCheck(controls.OfType<GroupBox>().Select(control => control.Text).OrderBy(text => text).SequenceEqual(
                new[] { "服务器连接", "任务计划显示", "版本与更新", "存档临时副本" }.OrderBy(text => text)), "设置页丢失其他功能或仍有空白二维码分组");
            dialog.Show(); dialog.Update();
            using (var bitmap = new Bitmap(dialog.Width, dialog.Height))
            {
                dialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                bitmap.Save(Path.Combine(output, "plugin-settings.png"), ImageFormat.Png);
            }
            dialog.Close();
        }
        Console.WriteLine("PASS: Rendered settings dialog preserves connection, planned-task display, updates and temporary-copy cleanup with no QR controls");
    }
}
