using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static partial class Program
{
    // --assembly <desktop.exe> <archived.sldasm> <rebuilt.easm> <evidence directory>
    private static int CheckAssemblyPreview(string[] args)
    {
        var assembly = Path.GetFullPath(args[1]);
        var type = Assembly.LoadFrom(assembly).GetType("Upton.Pdm.Desktop.EDrawingsPreviewControl", true);
        var preview = (Control)Activator.CreateInstance(type, true);
        var failed = false;
        var evidence = Path.GetFullPath(args[4]);
        Directory.CreateDirectory(evidence);
        using (var host = new Form { Left = -20000, Top = -20000, Width = 1200, Height = 900,
            StartPosition = FormStartPosition.Manual, ShowInTaskbar = false })
        {
            host.Controls.Add(preview);
            host.Shown += async (_, __) =>
            {
                try
                {
                    for (var index = 2; index <= 3; index++)
                    {
                        var path = Path.GetFullPath(args[index]);
                        await (Task)type.GetMethod("OpenDocumentAsync", Flags).Invoke(preview, new object[] { path, null });
                        if (!(bool)type.GetField("documentReady", Flags).GetValue(preview)) throw new Exception("预览未完成加载。");
                        var viewer = type.GetField("viewer", Flags).GetValue(preview);
                        dynamic control = viewer.GetType().GetProperty("ActiveControl", Flags).GetValue(viewer);
                        control.ViewOrientation = 6;
                        await Task.Delay(500);
                        control.ViewOrientation = 7;
                        await Task.Delay(500);
                        var image = Path.Combine(evidence, index == 2 ? "archived.bmp" : "latest.bmp");
                        control.EnableFeatures = (int)control.EnableFeatures & ~64;
                        control.Save(image, false, "");
                        var saved = false;
                        for (var attempt = 0; attempt < 100; attempt++)
                        {
                            await Task.Delay(100);
                            try { using (var stream = File.Open(image, FileMode.Open, FileAccess.Read, FileShare.None)) saved = stream.Length > 0; }
                            catch (IOException) { }
                            if (saved) break;
                        }
                        if (!saved) throw new Exception("eDrawings预览图像未能导出。");
                        Console.WriteLine("PASS: eDrawings加载并导出 " + path + " → " + image);
                    }
                }
                catch (Exception error) { failed = true; Console.WriteLine(error); }
                finally { host.Close(); }
            };
            Application.Run(host);
        }
        return failed ? 1 : 0;
    }
}
