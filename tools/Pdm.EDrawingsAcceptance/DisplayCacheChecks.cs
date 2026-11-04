using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static partial class Program
{
    // Isolated experiment only; it never changes the native source or production preview format.
    private static int CheckDisplayCache(string[] args)
    {
        var desktop = Path.GetFullPath(args[1]);
        var source = Path.GetFullPath(args[2]);
        var cache = Path.GetFullPath(args[3]);
        Directory.CreateDirectory(Path.GetDirectoryName(cache));
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) => {
            var path = Path.Combine(Path.GetDirectoryName(desktop), new AssemblyName(e.Name).Name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
        var type = Assembly.LoadFrom(desktop).GetType("Upton.Pdm.Desktop.EDrawingsPreviewControl", true);
        var preview = (Control)Activator.CreateInstance(type, true);
        var failed = false;
        using (var host = new Form { Left = -20000, Top = -20000, Width = 1200, Height = 900, StartPosition = FormStartPosition.Manual, ShowInTaskbar = false })
        {
            host.Controls.Add(preview);
            host.Shown += async (_, __) => {
                try
                {
                    var timer = Stopwatch.StartNew();
                    await (Task)type.GetMethod("OpenDocumentAsync", Flags).Invoke(preview, new object[] { source, null });
                    var originalMs = timer.ElapsedMilliseconds;
                    var viewer = type.GetField("viewer", Flags).GetValue(preview);
                    dynamic control = viewer.GetType().GetProperty("ActiveControl", Flags).GetValue(viewer);
                    var originalConfigurations = (int)control.ConfigurationCount;
                    string originalConfiguration = control.ConfigurationName[(int)control.CurrentConfigurationIndex];
                    var originalComponents = (int)control.ComponentCount[originalConfiguration];
                    var originalMeasure = (bool)control.IsMeasureEnabled;
                    var saved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    Action done = () => saved.TrySetResult(true);
                    Action<string, int, string> error = (_, code, message) => saved.TrySetException(new Exception("图形缓存导出失败：" + code));
                    var events = new Guid("2EA4AE0F-494C-4554-97AE-F02F3076A90D");
                    ComEventsHelper.Combine((object)control, events, 4, done);
                    ComEventsHelper.Combine((object)control, events, 6, error);
                    timer.Restart();
                    control.EnableFeatures = (int)control.EnableFeatures & ~64;
                    control.Save(cache, false, "");
                    if (await Task.WhenAny(saved.Task, Task.Delay(30000)) != saved.Task) throw new TimeoutException("缓存导出未完成。");
                    await saved.Task;
                    var exportMs = timer.ElapsedMilliseconds;
                    ComEventsHelper.Remove((object)control, events, 4, done);
                    ComEventsHelper.Remove((object)control, events, 6, error);
                    timer.Restart();
                    await (Task)type.GetMethod("OpenDocumentAsync", Flags).Invoke(preview, new object[] { cache, null });
                    var cachedMs = timer.ElapsedMilliseconds;
                    control = viewer.GetType().GetProperty("ActiveControl", Flags).GetValue(viewer);
                    var cachedConfigurations = (int)control.ConfigurationCount;
                    string cachedConfiguration = control.ConfigurationName[(int)control.CurrentConfigurationIndex];
                    var cachedComponents = (int)control.ComponentCount[cachedConfiguration];
                    var cachedMeasure = (bool)control.IsMeasureEnabled;
                    File.WriteAllText(args[4], new JavaScriptSerializer().Serialize(new { status = "passed", originalMs, exportMs, cachedMs, originalBytes = new FileInfo(source).Length, cacheBytes = new FileInfo(cache).Length, originalComponents, cachedComponents, originalConfigurations, cachedConfigurations, originalMeasure, cachedMeasure }));
                    Console.WriteLine(File.ReadAllText(args[4]));
                }
                catch (Exception error) { failed = true; Console.WriteLine(error); }
                finally { host.Close(); }
            };
            Application.Run(host);
        }
        return failed ? 1 : 0;
    }
}
