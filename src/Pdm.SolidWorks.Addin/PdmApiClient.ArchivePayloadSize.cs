using System;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Upton.Pdm.SolidWorks;

internal sealed partial class PdmApiClient
{
    internal long ArchiveBatchEnvelopeBytes(Guid batchId, Guid sessionId)
        => Encoding.UTF8.GetByteCount(serializer.Serialize(new { batchId, checkoutSessionId = sessionId, files = Array.Empty<object>() }));

    internal long ArchiveBatchFileBytes(object file) => Encoding.UTF8.GetByteCount(serializer.Serialize(file));

    internal async Task ValidateArchivePayloadSizeAsync(long bytes, int fileCount, bool beforeUpload, CancellationToken token)
    {
        var maximum = await RecoverArchiveConnectionAsync("整批提交容量设置", async (retryToken, retry) =>
        {
            using var response = await httpClient.GetAsync("api/archive-batch-limits", retryToken).ConfigureAwait(false);
            // An older server still has the Kestrel default. Do not assume the new limit.
            if (response.StatusCode == HttpStatusCode.NotFound) return 30000000L;
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                ThrowAuthenticationExpiredIfNeeded(response); ThrowServiceUnavailableIfNeeded(response);
                throw new InvalidOperationException(GetErrorMessage(response.StatusCode.ToString(), body));
            }
            var settings = serializer.Deserialize<ArchiveBatchLimits>(body);
            if (settings == null || settings.MaxRequestBytes <= 0 || settings.MaxRequestBytes > 512L * 1024 * 1024)
                throw new InvalidOperationException("服务器返回的整批提交容量设置无效，未继续存档。");
            return settings.MaxRequestBytes;
        }, token).ConfigureAwait(false);
        PluginDiagnosticLog.Record("整批提交容量", "文件数=" + fileCount + "；" + (beforeUpload ? "上传前预算字节=" : "实际请求字节=")
            + bytes + "；服务器上限字节=" + maximum);
        if (bytes <= maximum) return;
        var error = new InvalidOperationException("整批提交数据" + (beforeUpload ? "大小预算" : "大小") + "为"
            + (bytes / 1048576d).ToString("F2") + " MiB，超过服务器上限" + (maximum / 1048576d).ToString("F2")
            + " MiB。" + (beforeUpload ? "本次尚未上传文件；" : "本批尚未提交；")
            + "请联系管理员在存档警告策略中调整“整批提交数据上限”后重试。");
        error.Data["ArchiveBatchNotCommitted"] = true;
        throw error;
    }

    private sealed class ArchiveBatchLimits { public long MaxRequestBytes { get; set; } }
}
