using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Upton.Pdm.SolidWorks.PreviewWorker;

internal static class Program
{
    private const int OpenOptionsCommon = (int)swOpenDocOptions_e.swOpenDocOptions_Silent | (int)swOpenDocOptions_e.swOpenDocOptions_ReadOnly;
    // 装配体按轻化方式打开：引用件不立即全部载入内存，总装打开时间可大幅下降；需要几何导出时 SolidWorks 会自行载入。
    private const int OpenOptionsLightweight = (int)swOpenDocOptions_e.swOpenDocOptions_LoadLightweight;

    [STAThread]
    private static int Main(string[] args)
    {
        var flags = args.Where(argument => argument.StartsWith("--", StringComparison.Ordinal))
            .Select(argument => argument.ToLowerInvariant())
            .ToArray();
        var paths = args.Where(argument => !argument.StartsWith("--", StringComparison.Ordinal)).ToArray();
        var useLightweight = !flags.Contains("--no-lightweight");
        if (flags.Contains("--serve")) return Serve(useLightweight);
        if (paths.Length != 2)
        {
            Console.Error.WriteLine("Usage: Upton.Pdm.SolidWorks.PreviewWorker <manifest.json> <result.json> [--no-lightweight] | --serve [--no-lightweight]");
            return 2;
        }

        try
        {
            var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var manifest = serializer.Deserialize<PreviewWorkerManifest>(File.ReadAllText(paths[0], Encoding.UTF8));
            if (manifest?.Jobs == null || manifest.Jobs.Count == 0)
                throw new InvalidDataException("发布转换清单为空。");
            using (var session = new ConversionSession(useLightweight))
            {
                var result = session.Convert(manifest.Jobs);
                File.WriteAllText(paths[1], serializer.Serialize(result), new UTF8Encoding(false));
            }
            return 0;
        }
        catch (Exception exception)
        {
            try
            {
                var serializer = new JavaScriptSerializer();
                File.WriteAllText(paths[1], serializer.Serialize(new PreviewWorkerResult { Success = false, Error = exception.Message }), new UTF8Encoding(false));
            }
            catch
            {
            }
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    /// <summary>
    /// 常驻模式：SolidWorks 只启动一次并跨请求复用（启动一次约十几秒），代理通过标准输入输出逐次下发转换请求。
    /// </summary>
    private static int Serve(bool lightweightAssemblies = true)
    {
        var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        var session = new ConversionSession(lightweightAssemblies);
        try
        {
            Console.Out.WriteLine("{\"ready\":true}");
            Console.Out.Flush();
            string line;
            while ((line = Console.In.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var request = serializer.Deserialize<ServeRequest>(line);
                PreviewWorkerResult result;
                try
                {
                    if (request == null || string.IsNullOrWhiteSpace(request.Manifest) || string.IsNullOrWhiteSpace(request.Result))
                        throw new InvalidDataException("常驻转换请求字段不完整。");
                    var manifest = serializer.Deserialize<PreviewWorkerManifest>(File.ReadAllText(request.Manifest, Encoding.UTF8));
                    if (manifest?.Jobs == null || manifest.Jobs.Count == 0)
                        throw new InvalidDataException("发布转换清单为空。");
                    result = session.Convert(manifest.Jobs, (stage, completed, total, solidWorksPid) => { Console.Out.WriteLine(serializer.Serialize(new { Progress = true, Stage = stage, Completed = completed, Total = total, SolidWorksPid = solidWorksPid })); Console.Out.Flush(); });
                }
                catch (Exception exception)
                {
                    // 一次失败可能让 SolidWorks 处于异常状态，直接重启实例，下一次请求会重新创建。
                    result = new PreviewWorkerResult { Success = false, Error = exception.Message };
                    session.Reset();
                    Console.Error.WriteLine(exception.Message);
                }
                if (!string.IsNullOrWhiteSpace(request?.Result))
                    File.WriteAllText(request.Result, serializer.Serialize(result), new UTF8Encoding(false));
                Console.Out.WriteLine(serializer.Serialize(new { result.Success, result.Error }));
                Console.Out.Flush();
            }
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
        finally
        {
            session.Dispose();
        }
    }

    /// <summary>一次转换会话：持有一个 SolidWorks 实例，串行转换清单里所有需要转出的任务。</summary>
    private sealed class ConversionSession : IDisposable
    {
        private readonly bool lightweightAssemblies;
        private SldWorks application;

        public ConversionSession(bool lightweightAssemblies) => this.lightweightAssemblies = lightweightAssemblies;

        public PreviewWorkerResult Convert(IReadOnlyList<PreviewWorkerJob> jobs, Action<string, int, int, int> progress = null)
        {
            var total = jobs.Count(job => job.Convert); var completed = 0;
            progress?.Invoke("启动或连接SolidWorks", completed, total, 0);
            var items = new List<PreviewWorkerItem>();
            foreach (var job in jobs)
            {
                // Convert=false 的任务只作为引用文件下发（SolidWorks 解析装配体引用时需要），不需要转出。
                if (!job.Convert) continue;
                var solidWorksPid = 0;
                // 工程图/装配体会留下已加载的引用模型；下一任务必须使用本次清单中的源文件，避免同名文档冲突。
                try
                {
                    var current = EnsureApplication();
                    solidWorksPid = current.GetProcessID();
                    progress?.Invoke("转换文件：" + Path.GetFileName(job.SourcePath), completed, total, solidWorksPid);
                    current.CloseAllDocuments(true);
                    ConvertOne(current, job, lightweightAssemblies);
                    items.Add(new PreviewWorkerItem { DocumentId = job.DocumentId, Success = true });
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine($"转换文件 {job.SourcePath} 失败：{exception}");
                    if (!string.IsNullOrWhiteSpace(job.ReleaseRevision)) throw;
                    try { if (File.Exists(job.OutputPath)) File.Delete(job.OutputPath); } catch (Exception cleanupError) { Console.Error.WriteLine(cleanupError); }
                    items.Add(new PreviewWorkerItem { DocumentId = job.DocumentId, Error = exception is InvalidOperationException
                        ? exception.Message : "SolidWorks转换异常，请在转图电脑打开该文件检查引用和弹窗后，单独重试；详细原因已记录到诊断日志。" });
                    Reset();
                    solidWorksPid = 0;
                }
                progress?.Invoke("已处理文件：" + Path.GetFileName(job.SourcePath), ++completed, total, solidWorksPid);
            }
            try { application?.CloseAllDocuments(true); } catch (Exception) { }
            var failed = items.Count(item => !item.Success);
            return new PreviewWorkerResult { Success = failed == 0, Items = items,
                Error = failed == 0 ? null : $"{failed}项图档转换失败，其余{items.Count - failed}项已完成，可单独重试失败项。" };
        }

        private SldWorks EnsureApplication()
        {
            if (application == null)
            {
                application = new SldWorks
                {
                    Visible = false,
                    UserControl = false,
                    CommandInProgress = true
                };
            }
            return application;
        }

        public void Reset()
        {
            if (application == null) return;
            try { application.CommandInProgress = false; } catch { }
            try { application.ExitApp(); } catch { }
            try { Marshal.FinalReleaseComObject(application); } catch { }
            application = null;
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        public void Dispose() => Reset();
    }

    private static void ConvertOne(ISldWorks application, PreviewWorkerJob job, bool lightweightAssemblies)
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
            var formalDrawing = !string.IsNullOrWhiteSpace(job.ReleaseRevision);
            if (formalDrawing && (documentType != (int)swDocumentTypes_e.swDocDRAWING
                || string.IsNullOrWhiteSpace(job.QrContent)
                || !string.Equals(Path.GetExtension(job.OutputPath), ".slddrw", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("正式图纸任务缺少版次、二维码内容或输出格式不正确。");
            var openOptions = (formalDrawing ? (int)swOpenDocOptions_e.swOpenDocOptions_Silent : OpenOptionsCommon)
                | (lightweightAssemblies && documentType == (int)swDocumentTypes_e.swDocASSEMBLY ? OpenOptionsLightweight : 0);
            document = application.OpenDoc6(
                job.SourcePath,
                documentType,
                openOptions,
                string.Empty,
                ref openErrors,
                ref openWarnings) as IModelDoc2;
            if (document == null || openErrors != 0)
                throw new InvalidOperationException($"SolidWorks打开{Path.GetFileName(job.SourcePath)}失败，错误码{openErrors}，警告码{openWarnings}。");

            if (formalDrawing) FormalDrawingStamp.Apply(application, document, job.ReleaseRevision, job.QrContent);

            var saveErrors = 0;
            var saveWarnings = 0;
            var saved = document.Extension.SaveAs(
                job.OutputPath,
                (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                (int)swSaveAsOptions_e.swSaveAsOptions_Silent,
                null,
                ref saveErrors,
                ref saveWarnings);
            if ((saveErrors & (int)swFileSaveError_e.swFileSaveAsNotSupported) != 0 && !formalDrawing)
            {
                Console.Error.WriteLine($"图档 {job.SourcePath} 保存错误码{saveErrors}、警告码{saveWarnings}，切换可见窗口激活文档后重试一次。");
                var wasVisible = application.Visible;
                try
                {
                    application.Visible = true;
                    var activateErrors = 0;
                    var active = application.ActivateDoc3(document.GetTitle(), false,
                        (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref activateErrors);
                    if (active == null || activateErrors != 0)
                        throw new InvalidOperationException("SolidWorks自动恢复时无法激活图档，请在转图电脑打开该文件并检查弹窗后单独重试。");
                    if (File.Exists(job.OutputPath)) File.Delete(job.OutputPath);
                    saveErrors = 0;
                    saveWarnings = 0;
                    saved = document.Extension.SaveAs(job.OutputPath, (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                        (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null, ref saveErrors, ref saveWarnings);
                }
                finally { try { application.Visible = wasVisible; } catch (Exception exception) { Console.Error.WriteLine(exception); } }
            }
            if (!saved || saveErrors != 0 || !File.Exists(job.OutputPath) || new FileInfo(job.OutputPath).Length == 0)
                throw new InvalidOperationException($"图档{Path.GetFileName(job.SourcePath)}导出失败（错误码{saveErrors}，警告码{saveWarnings}）。"
                    + ((saveErrors & 4096) != 0 ? "已尝试可见窗口自动恢复，请在转图电脑手动打开该图档并试导出。" : "请在转图电脑检查该图档后单独重试。")
                    + ((saveWarnings & 512) != 0 ? "检测到缺失的嵌入对象，请检查图纸中的嵌入表格、图片或链接对象。" : ""));
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

    private sealed class ServeRequest
    {
        public string Manifest { get; set; }
        public string Result { get; set; }
    }

    private sealed class PreviewWorkerJob
    {
        public Guid DocumentId { get; set; }
        public string SourcePath { get; set; }
        public string OutputPath { get; set; }
        public string Kind { get; set; }
        // 老版本服务端不带该字段，缺省按“需要转出”处理。
        public bool Convert { get; set; } = true;
        public string ReleaseRevision { get; set; }
        public string QrContent { get; set; }
    }

    private sealed class PreviewWorkerResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        public List<PreviewWorkerItem> Items { get; set; }
    }

    private sealed class PreviewWorkerItem
    {
        public Guid DocumentId { get; set; }
        public bool Success { get; set; }
        public string Error { get; set; }
    }
}
