using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

internal static partial class Program
{
    static async Task ControlledDownloadRetryScenario(string output)
    {
        var reference = Path.Combine(output, "reference");
        Directory.CreateDirectory(reference);
        var source = Path.Combine(reference, "TEST.SLDASM");
        var bytes = System.Text.Encoding.UTF8.GetBytes("verified assembly bytes");
        File.WriteAllBytes(source, bytes);
        foreach (var mode in new[] { "timeout-then-success", "503-then-success", "exhausted", "forbidden", "invalid-hash", "caller-cancels" })
        {
            var file = New("ControlledOpenFileDto");
            Set(file, "DocumentId", Guid.NewGuid());
            Set(file, "VersionId", Guid.NewGuid());
            Set(file, "FileName", "TEST.SLDASM");
            Set(file, "Sha256", WorkingReuseHash(source));
            Set(file, "FileLength", (long)bytes.Length);
            var path = Path.Combine(output, mode);
            var calls = 0;
            using (var token = new CancellationTokenSource())
            using (var client = (IDisposable)New("PdmApiClient", "http://download.invalid/"))
            using (var handler = new ReferenceRecoveryHandler(request => {
                calls++;
                if (request.Method != HttpMethod.Get || !request.RequestUri.AbsolutePath.EndsWith("/file"))
                    throw new Exception("受控下载应复用原鉴权读取接口。");
                if (mode == "caller-cancels") { token.Cancel(); throw new TaskCanceledException(); }
                if (mode == "timeout-then-success" && calls == 1) throw new TaskCanceledException();
                if (mode == "exhausted" || mode == "503-then-success" && calls == 1)
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                if (mode == "forbidden") return new HttpResponseMessage(HttpStatusCode.Forbidden);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(mode == "invalid-hash" ? new byte[] { 0 } : bytes) };
            }))
            using (var http = new HttpClient(handler) { BaseAddress = new Uri("http://download.invalid/") })
            {
                SetField(client, "controlledOpenHttpClient", http);
                Exception failure = null;
                try { await (Task)TreeCall(client, "DownloadControlledOpenFileAsync", file, path, token.Token); }
                catch (Exception error) { failure = error; }
                var success = mode.EndsWith("-then-success");
                var target = Path.Combine(path, "TEST.SLDASM");
                if ((failure == null) != success || calls != (success ? 2 : mode == "exhausted" ? 3 : 1)
                    || File.Exists(target) != success || File.Exists(target + ".download")
                    || success && WorkingReuseHash(target) != WorkingReuseHash(source))
                    throw new Exception("下载重试、完整性核对或临时文件清理结果错误：" + mode, failure);
                if (mode == "exhausted")
                {
                    var message = (string)Type("PdmAddin").GetMethod("OpenDocumentErrorMessage", Flags).Invoke(null, new object[] { failure });
                    if (!message.Contains("TEST.SLDASM") || !message.Contains("PLM服务暂不可用") || message.Contains("插件已加载"))
                        throw new Exception("下载失败提示没有指出图档及PLM服务原因。");
                }
                if (mode == "caller-cancels" && !(failure is OperationCanceledException))
                    throw new Exception("主动取消应直接结束下载。", failure);
                Console.WriteLine("PASS: controlled download " + mode);
            }
        }
    }
}
