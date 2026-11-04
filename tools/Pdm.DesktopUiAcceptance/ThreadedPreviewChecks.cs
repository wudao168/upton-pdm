using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Forms = System.Windows.Forms;

internal static partial class Program
{
    private static async Task ProbeThreadedPreviewAsync(Window window, Dictionary<string, object> results, string image)
    {
        var preview = window.GetType().GetField("embeddedPreview", Flags).GetValue(window);
        var completionSource = preview.GetType().GetField("ready", Flags).GetValue(preview);
        var ownerReady = (Task)completionSource.GetType().GetProperty("Task").GetValue(completionSource);
        var owner = (Forms.Form)ownerReady.GetType().GetProperty("Result").GetValue(ownerReady);
        var panel = (Forms.Control)preview.GetType().GetProperty("HostControl", Flags).GetValue(preview);
        var mainThread = Thread.CurrentThread.ManagedThreadId;
        var parent = panel.Handle;
        window.Width += 120;
        await Task.Delay(200);
        var expectedSize = panel.ClientSize;
        foreach (var theme in new[] { "a", "c", "p", "o", "g" }) preview.GetType().GetMethod("ApplyTheme", Flags).Invoke(preview, new object[] { theme });
        var documentPath = (string)preview.GetType().GetProperty("CurrentDocumentPath", Flags).GetValue(preview);
        foreach (var command in Path.GetExtension(documentPath).Equals(".SLDDRW", StringComparison.OrdinalIgnoreCase) ? new[] { "fit" } : new[] { "front", "isometric", "fit" })
            preview.GetType().GetMethod("ExecuteCommand", Flags).Invoke(preview, new object[] { command });
        await OnPreviewOwnerAsync(owner, () => {
            if (Thread.CurrentThread.ManagedThreadId == mainThread || Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
                throw new Exception("原生预览未使用独立STA线程。");
            results["embedding"] = new { expectedParent = parent.ToInt64(), actualParent = PreviewGetParent(owner.Handle).ToInt64(), expectedWidth = expectedSize.Width, expectedHeight = expectedSize.Height, actualWidth = owner.ClientSize.Width, actualHeight = owner.ClientSize.Height };
            if (PreviewGetParent(owner.Handle) != parent || owner.ClientSize != expectedSize || owner.ClientSize.Width < 80)
                throw new Exception("预览嵌入关系或缩放尺寸不正确。");
            var native = owner.GetType().GetProperty("Preview", Flags).GetValue(owner);
            var viewer = native.GetType().GetField("viewer", Flags).GetValue(native);
            dynamic control = viewer.GetType().GetProperty("ActiveControl", Flags).GetValue(viewer);
            if ((int)control.FullUI != -1 || ((int)control.EnableFeatures & 64) == 0) throw new Exception("完整预览界面或只读模式发生变化。");
            results["ownerStaVerified"] = true;
            results["embeddedResizeVerified"] = true;
            results["nativeThemesVerified"] = 5;
            results["measureEnabled"] = (bool)control.IsMeasureEnabled;
            control.EnableFeatures = (int)control.EnableFeatures & ~64;
            control.Save(image, false, "");
        });
        for (var attempt = 0; attempt < 100; attempt++) {
            try { using (var input = File.Open(image, FileMode.Open, FileAccess.Read, FileShare.None)) if (input.Length > 0) break; } catch (IOException) { }
            await Task.Delay(100);
        }
        if (!File.Exists(image) || new FileInfo(image).Length == 0) throw new Exception("原生图形未能导出。");
        await OnPreviewOwnerAsync(owner, () => {
            var native = owner.GetType().GetProperty("Preview", Flags).GetValue(owner);
            var viewer = native.GetType().GetField("viewer", Flags).GetValue(native);
            dynamic control = viewer.GetType().GetProperty("ActiveControl", Flags).GetValue(viewer);
            control.EnableFeatures = (int)control.EnableFeatures | 64;
        });
        results["nativeImage"] = image;
    }

    private static Task OnPreviewOwnerAsync(Forms.Control control, Action action)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        control.BeginInvoke(new Action(() => { try { action(); done.TrySetResult(true); } catch (Exception error) { done.TrySetException(error); } }));
        return done.Task;
    }

    [DllImport("user32.dll", EntryPoint = "GetParent")] private static extern IntPtr PreviewGetParent(IntPtr window);
}
