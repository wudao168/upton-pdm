using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length is < 1 or > 3 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("Usage: Pdm.SolidWorks.Acceptance.exe <assembly-path> [cycles] [--exit]");
            return 2;
        }

        var cycles = args.Length == 2 && int.TryParse(args[1], out var parsedCycles) ? parsedCycles : 1;
        if (args.Length >= 2 && int.TryParse(args[1], out parsedCycles)) cycles = parsedCycles;
        var exitAfter = Array.IndexOf(args, "--exit") >= 0;
        if (cycles is < 1 or > 10) throw new ArgumentOutOfRangeException(nameof(cycles));

        SldWorks? application = null;
        ModelDoc2? document = null;
        try
        {
            application = (SldWorks)Marshal.GetActiveObject("SldWorks.Application");
            var addin = application.GetAddInObject("Upton.Pdm.SolidWorks.Addin");
            var stopwatch = Stopwatch.StartNew();
            var componentCount = 0;
            var errors = 0;
            var warnings = 0;
            for (var cycle = 1; cycle <= cycles; cycle++)
            {
                errors = 0;
                warnings = 0;
                document = application.OpenDoc6(
                    args[0],
                    (int)swDocumentTypes_e.swDocASSEMBLY,
                    (int)(swOpenDocOptions_e.swOpenDocOptions_Silent | swOpenDocOptions_e.swOpenDocOptions_ReadOnly),
                    string.Empty,
                    ref errors,
                    ref warnings);
                if (document is null)
                {
                    throw new InvalidOperationException($"OpenDoc6 cycle {cycle} returned null (errors={errors}, warnings={warnings}).");
                }

                var openedAssembly = document as AssemblyDoc ?? throw new InvalidOperationException("The opened document is not an assembly.");
                componentCount = openedAssembly.GetComponentCount(false);
                if (!Process.GetProcessesByName("SLDWORKS")[0].Responding) throw new InvalidOperationException($"SolidWorks stopped responding during cycle {cycle}.");
                if (cycle < cycles)
                {
                    var title = document.GetTitle();
                    Marshal.FinalReleaseComObject(document);
                    document = null;
                    application.CloseDoc(title);
                    System.Threading.Thread.Sleep(750);
                }
            }
            stopwatch.Stop();
            if (document is null)
            {
                throw new InvalidOperationException($"OpenDoc6 returned null (errors={errors}, warnings={warnings}).");
            }

            var process = Process.GetProcessesByName("SLDWORKS")[0];
            var report = new
            {
                status = "passed",
                solidWorksVersion = application.RevisionNumber(),
                addinLoaded = addin is not null,
                title = document.GetTitle(),
                path = document.GetPathName(),
                readOnly = document.IsOpenedReadOnly(),
                componentCount,
                cycles,
                errors,
                warnings,
                elapsedSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 3),
                processId = process.Id,
                responding = process.Responding,
            };
            Console.WriteLine(new JavaScriptSerializer().Serialize(report));
            var succeeded = addin is not null && process.Responding;
            if (exitAfter)
            {
                var title = document.GetTitle();
                Marshal.FinalReleaseComObject(document);
                document = null;
                application.CloseDoc(title);
                application.ExitApp();
                System.Threading.Thread.Sleep(2_000);
            }
            return succeeded ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally
        {
            if (document is not null) Marshal.FinalReleaseComObject(document);
            if (application is not null) Marshal.FinalReleaseComObject(application);
        }
    }
}
