using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Upton.Pdm.Desktop;

internal static class SolidWorksPipeSender
{
    // Retries are allowed only before sending; an absent reply does not mean rejection.
    internal static async Task<bool> SendAsync(string pipeName, string json, CancellationToken cancellationToken,
        int connectTimeoutMs = 3000, int replyTimeoutMs = 5000)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                try
                {
                    await pipe.ConnectAsync(connectTimeoutMs, cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    if (attempt == 2) return false;
                    await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                catch (UnauthorizedAccessException exception)
                {
                    throw new InvalidOperationException("无法连接SolidWorks插件：访问被拒绝。请使用同一Windows账号运行客户端和SolidWorks，并保持两者的管理员权限一致。", exception);
                }
                catch (IOException exception)
                {
                    if (attempt == 2)
                        throw new InvalidOperationException("与SolidWorks插件建立连接失败。请检查插件是否已启用，待其加载完成后重试。", exception);
                    await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                using (timeout.Token.Register(pipe.Dispose))
                {
                    timeout.CancelAfter(replyTimeoutMs);
                    try
                    {
                        using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true })
                        using (var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true))
                        {
                            await writer.WriteLineAsync(json).ConfigureAwait(false);
                            var reply = await reader.ReadLineAsync().ConfigureAwait(false);
                            cancellationToken.ThrowIfCancellationRequested();
                            if (reply == "accepted") return true;
                            if (reply == "rejected")
                                throw new InvalidOperationException("SolidWorks插件拒绝了打开请求。请刷新图档后重试，并确认客户端和插件均已更新。");
                            throw new IOException("Unexpected or missing SolidWorks acknowledgement.");
                        }
                    }
                    catch (Exception exception) when (exception is IOException || exception is ObjectDisposedException || exception is OperationCanceledException)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        throw new InvalidOperationException(timeout.IsCancellationRequested
                            ? "等待SolidWorks插件确认超时。请求可能已接收，请先查看SolidWorks中的处理状态，避免重复点击打开。"
                            : "打开请求发送后通信中断，暂时无法确认是否已接收。请先查看SolidWorks中的处理状态，避免重复点击打开。", exception);
                    }
                }
            }
        }
        return false;
    }
}
