using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static partial class Program
{
    // --performance <desktop.exe> <original.sldasm> <result.json>
    private static int CheckPreviewPerformance(string[] args)
    {
        var desktop = Path.GetFullPath(args[1]);
        var source = Path.GetFullPath(args[2]);
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) => {
            var file = Path.Combine(Path.GetDirectoryName(desktop), new AssemblyName(e.Name).Name + ".dll");
            return File.Exists(file) ? Assembly.LoadFrom(file) : null;
        };
        var type = Assembly.LoadFrom(desktop).GetType("Upton.Pdm.Desktop.EDrawingsPreviewControl", true);
        var preview = (Control)Activator.CreateInstance(type, true);
        var results = new List<object>();
        var failed = false;
        using (var host = new Form { Left = -20000, Top = -20000, Width = 1200, Height = 900,
            StartPosition = FormStartPosition.Manual, ShowInTaskbar = false })
        using (var pulse = new System.Windows.Forms.Timer { Interval = 50 })
        {
            host.Controls.Add(preview);
            host.Shown += async (_, __) => {
                try {
                    await Task.Delay(200);
                    for (var round = 0; round < 2; round++) {
                        var viewer = type.GetField("viewer", Flags).GetValue(preview);
                        dynamic control = viewer.GetType().GetProperty("ActiveControl", Flags).GetValue(viewer);
                        var deferredUi = args.Length > 4 && args[4] == "--deferred-ui";
                        if (deferredUi) {
                            viewer.GetType().GetField("interfacePrepared", Flags).SetValue(viewer, true);
                            control.FullUI = 0;
                            control.ShowToolbar(false);
                        }
                        var clock = Stopwatch.StartNew();
                        var cpu = Process.GetCurrentProcess().TotalProcessorTime;
                        long last = 0, maximumGap = 0;
                        EventHandler tick = (_, ___) => { var now = clock.ElapsedMilliseconds; maximumGap = Math.Max(maximumGap, now - last); last = now; };
                        pulse.Tick += tick; pulse.Start();
                        var opening = (Task)type.GetMethod("OpenDocumentAsync", Flags).Invoke(preview, new object[] { source, null });
                        var acceptedMs = clock.ElapsedMilliseconds;
                        await opening;
                        var graphicMs = clock.ElapsedMilliseconds;
                        if (deferredUi) { control.FullUI = -1; control.ShowToolbar(true); }
                        var totalMs = clock.ElapsedMilliseconds;
                        await Task.Delay(150);
                        pulse.Stop(); pulse.Tick -= tick;
                        if (!(bool)type.GetField("documentReady", Flags).GetValue(preview)) throw new Exception("原图未完成加载。");
                        if ((int)control.FullUI != -1) throw new Exception("完整预览界面发生变化。");
                        control.ViewOrientation = 6;
                        await Task.Delay(150);
                        control.ViewOrientation = 7;
                        results.Add(new { round, acceptedMs, graphicMs, totalMs, cpuMs = (Process.GetCurrentProcess().TotalProcessorTime - cpu).TotalMilliseconds, maximumUiGapMs = maximumGap, fullUi = true });
                        Console.WriteLine("原图性能：轮次=" + round + "，接受=" + acceptedMs + "毫秒，总计=" + totalMs + "毫秒，最大界面间隔=" + maximumGap + "毫秒");
                    }
                    File.WriteAllText(args[3], new JavaScriptSerializer().Serialize(new { status = "passed", fileLength = new FileInfo(source).Length, results }));
                } catch (Exception error) { failed = true; Console.WriteLine(error); }
                finally { host.Close(); }
            };
            Application.Run(host);
        }
        return failed ? 1 : 0;
    }
}
