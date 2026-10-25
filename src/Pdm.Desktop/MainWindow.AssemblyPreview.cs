using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Upton.Pdm.ClientShared;

namespace Upton.Pdm.Desktop;

public partial class MainWindow
{
    private CancellationTokenSource? assemblyPreviewCancellation;

    private async Task<(VersionResponse Version, string Path)> DownloadLatestAssemblyPreviewAsync(Guid documentId)
    {
        using var request = CreateApiRequest(HttpMethod.Post, $"/api/documents/{documentId}/assembly-preview");
        // Conversion can take longer than an ordinary API read; keep the lifetime cancellation active.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(bootstrapLifetime.Token);
        assemblyPreviewCancellation = timeout;
        timeout.CancelAfter(TimeSpan.FromMinutes(120));
        using var client = new HttpClient(new ClientDiagnosticHandler()) { Timeout = Timeout.InfiniteTimeSpan };
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                var detail = await response.Content.ReadAsStringAsync();
                AppendClientDiagnostic($"最新装配预览失败：document={documentId} status={response.StatusCode} detail={detail}");
                throw new InvalidOperationException("无法生成最新装配预览，请检查转图服务是否已更新并可用，随后重新加载预览；原有存档不受影响。");
            }
            if (!response.Headers.TryGetValues("X-Pdm-Preview-Version", out var versions)
                || !Guid.TryParse(versions.SingleOrDefault(), out var versionId)
                || !response.Headers.TryGetValues("X-Pdm-Preview-Sha256", out var hashes))
                throw new InvalidDataException("装配预览缺少版本校验信息，请更新服务器后重试。");
            var hash = hashes.SingleOrDefault() ?? "";
            if (hash.Length != 64 || hash.Any(character => !Uri.IsHexDigit(character)) || response.Content.Headers.ContentLength is not long length || length <= 0)
                throw new InvalidDataException("装配预览校验信息无效，请重新加载预览。");
            var directory = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "UPTON", "PDM", "Preview", documentId.ToString("N"), versionId.ToString("N"), hash);
            Directory.CreateDirectory(directory);
            var path = System.IO.Path.Combine(directory, "preview.easm");
            if (!await IsValidCacheAsync(path, length, hash))
            {
                var temporary = path + "." + Guid.NewGuid().ToString("N") + ".download";
                try
                {
                    using (var input = await response.Content.ReadAsStreamAsync())
                    using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 256 * 1024, true))
                        await input.CopyToAsync(output, 256 * 1024, timeout.Token);
                    if (!await IsValidCacheAsync(temporary, length, hash))
                        throw new InvalidDataException("最新装配预览文件校验失败，请重新加载预览。");
                    if (File.Exists(path)) { File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); }
                    File.Move(temporary, path);
                    File.SetAttributes(path, FileAttributes.ReadOnly);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            return (new VersionResponse { Id = versionId, FileLength = length, Sha256 = hash }, path);
        }
        catch (HttpRequestException error)
        {
            AppendClientDiagnostic($"最新装配预览连接失败：document={documentId} error={error}");
            throw new InvalidOperationException("无法连接装配预览服务，请检查网络和服务器状态后重新加载预览。", error);
        }
        catch (TaskCanceledException error)
        {
            throw new InvalidOperationException("装配预览生成已中断或等待超时，请检查转图服务状态后重新加载预览。", error);
        }
        finally { assemblyPreviewCancellation = null; }
    }
}
