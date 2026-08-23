using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Upton.Pdm.SolidWorks.PreviewWorker;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Usage: Upton.Pdm.SolidWorks.PreviewWorker <manifest.json> <result.json>");
            return 2;
        }

        try
        {
            var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var manifest = serializer.Deserialize<PreviewWorkerManifest>(File.ReadAllText(args[0], Encoding.UTF8));
            if (manifest?.Jobs == null || manifest.Jobs.Count == 0)
                throw new InvalidDataException("发布转换清单为空。");
            Convert(manifest.Jobs);
            File.WriteAllText(args[1], serializer.Serialize(new PreviewWorkerResult { Success = true }), new UTF8Encoding(false));
            return 0;
        }
        catch (Exception exception)
        {
            try
            {
                var serializer = new JavaScriptSerializer();
                File.WriteAllText(args[1], serializer.Serialize(new PreviewWorkerResult { Success = false, Error = exception.Message }), new UTF8Encoding(false));
            }
            catch
            {
            }
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static void Convert(IReadOnlyList<PreviewWorkerJob> jobs)
    {
        SldWorks application = null;
        try
        {
            application = new SldWorks
            {
                Visible = false,
                UserControl = false,
                CommandInProgress = true
            };
            foreach (var job in jobs)
            {
                ConvertOne(application, job);
            }
        }
        finally
        {
            if (application != null)
            {
                try { application.CommandInProgress = false; } catch { }
                try { application.ExitApp(); } catch { }
                try { Marshal.FinalReleaseComObject(application); } catch { }
            }
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    private static void ConvertOne(ISldWorks application, PreviewWorkerJob job)
    {
        if (job == null || string.IsNullOrWhiteSpace(job.SourcePath) || string.IsNullOrWhiteSpace(job.OutputPath))
            throw new InvalidDataException("发布转换任务字段不完整。");
        if (!File.Exists(job.SourcePath)) throw new FileNotFoundException("发布源文件不存在。", job.SourcePath);
        var documentType = DocumentType(job.Kind);
        Directory.CreateDirectory(Path.GetDirectoryName(job.OutputPath));
        if (File.Exists(job.OutputPath)) File.Delete(job.OutputPath);

        var openErrors = 0;
        var openWarnings = 0;
        IModelDoc2 document = null;
        try
        {
            document = application.OpenDoc6(
                job.SourcePath,
                documentType,
                (int)(swOpenDocOptions_e.swOpenDocOptions_Silent | swOpenDocOptions_e.swOpenDocOptions_ReadOnly),
                string.Empty,
                ref openErrors,
                ref openWarnings) as IModelDoc2;
            if (document == null || openErrors != 0)
                throw new InvalidOperationException($"SolidWorks打开{Path.GetFileName(job.SourcePath)}失败，错误码{openErrors}，警告码{openWarnings}。");

            var saveErrors = 0;
            var saveWarnings = 0;
            var saved = document.Extension.SaveAs(
                job.OutputPath,
                (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                (int)swSaveAsOptions_e.swSaveAsOptions_Silent,
                null,
                ref saveErrors,
                ref saveWarnings);
            if (!saved || saveErrors != 0 || !File.Exists(job.OutputPath) || new FileInfo(job.OutputPath).Length == 0)
                throw new InvalidOperationException($"SolidWorks生成{Path.GetFileName(job.OutputPath)}失败，错误码{saveErrors}，警告码{saveWarnings}。");
        }
        finally
        {
            if (document != null)
            {
                try { application.CloseDoc(document.GetTitle()); } catch { }
                try { Marshal.FinalReleaseComObject(document); } catch { }
            }
        }
    }

    private static int DocumentType(string kind)
    {
        if (string.Equals(kind, "Assembly", StringComparison.OrdinalIgnoreCase)) return (int)swDocumentTypes_e.swDocASSEMBLY;
        if (string.Equals(kind, "Part", StringComparison.OrdinalIgnoreCase)) return (int)swDocumentTypes_e.swDocPART;
        if (string.Equals(kind, "Drawing", StringComparison.OrdinalIgnoreCase)) return (int)swDocumentTypes_e.swDocDRAWING;
        throw new InvalidDataException($"不支持的SolidWorks图档类型：{kind}");
    }

    private sealed class PreviewWorkerManifest
    {
        public List<PreviewWorkerJob> Jobs { get; set; }
    }

    private sealed class PreviewWorkerJob
    {
        public Guid DocumentId { get; set; }
        public string SourcePath { get; set; }
        public string OutputPath { get; set; }
        public string Kind { get; set; }
    }

    private sealed class PreviewWorkerResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
    }
}
