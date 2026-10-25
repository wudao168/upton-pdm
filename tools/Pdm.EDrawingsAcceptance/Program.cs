using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static partial class Program
{
    const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    // Desktop assembly, readable drawing, display-data failure, repaired drawing, readable model.
    [STAThread] static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--assembly") return CheckAssemblyPreview(args);
        var dll = Path.GetFullPath(args[0]);
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
        {
            var path = Path.Combine(Path.GetDirectoryName(dll), new AssemblyName(e.Name).Name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
        var type = Assembly.LoadFrom(dll).GetType("Upton.Pdm.Desktop.EDrawingsPreviewControl", true);
        var preview = (Control)Activator.CreateInstance(type, true);
        var failures = 0; var checks = 0;
        using (var host = new Form { Left = -20000, Top = -20000, Width = 900, Height = 700, StartPosition = FormStartPosition.Manual, ShowInTaskbar = false })
        {
            host.Controls.Add(preview);
            host.Shown += async (_, __) =>
            {
                try
                {
                    for (var repeat = 0; repeat < 3; repeat++)
                    foreach (var index in new[] { 1, 2, 3, 4, 1 })
                    {
                        var path = Path.GetFullPath(args[index]);
                        var opening = (Task)type.GetMethod("OpenDocumentAsync", Flags).Invoke(preview, new object[] { path, null });
                        if (!opening.IsCompleted)
                        {
                            var otherVersion = Path.Combine(Path.GetDirectoryName(path), "other-version", Path.GetFileName(path));
                            type.GetMethod("OnDocumentLoaded", Flags).Invoke(preview, new object[] { otherVersion });
                            if (opening.IsCompleted || (bool)type.GetField("documentReady", Flags).GetValue(preview))
                                throw new Exception("Stale same-name version event acknowledged the current preview");
                        }
                        Exception error = null;
                        try { await opening; } catch (Exception e) { error = e; }
                        var ready = (bool)type.GetField("documentReady", Flags).GetValue(preview);
                        if (index == 2)
                        {
                            if (!(error is InvalidOperationException) || !error.Message.Contains("无法读取此版本工程图") || ready)
                                throw new Exception("Native failure not reported in Chinese or marked ready");
                        }
                        else if (error != null || !ready) throw new Exception("Valid native preview failed: " + path, error);
                        checks++;
                        Console.WriteLine("PASS " + repeat + " " + (index == 2 ? "failure reported" : "native loaded") + " " + path);
                    }
                    type.GetMethod("CloseDocument", Flags).Invoke(preview, null);
                    if ((bool)type.GetField("documentReady", Flags).GetValue(preview)) throw new Exception("Closed preview stayed ready");
                }
                catch (Exception error) { failures++; Console.WriteLine("FAIL " + error); }
                finally { host.Close(); }
            };
            Application.Run(host);
        }
        Console.WriteLine("Native preview checks=" + checks + "; failures=" + failures);
        return failures == 0 ? 0 : 1;
    }
}
