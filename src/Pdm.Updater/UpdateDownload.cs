using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Upton.Pdm.ClientShared;

namespace Upton.Pdm.Updater;

internal static class UpdateDownload
{
    private const long MaxPackageBytes = 1024L * 1024 * 1024;

    public static void VerifySignature(ClientUpdateState state)
    {
        if (!ClientUpdateState.TryVersion(state.Version, out _) || state.Sha256?.Length != 64
            || string.IsNullOrWhiteSpace(state.Signature))
            throw new InvalidDataException("更新包缺少签名或校验信息，请联系管理员重新发布。");
        using var key = Assembly.GetExecutingAssembly().GetManifestResourceStream("UPLM.Update.PublicKey");
        if (key == null)
            throw new InvalidDataException("本机未配置更新签名公钥，请联系管理员修复 UPLM 安装。");
        using var reader = new StreamReader(key, Encoding.UTF8);
        using var rsa = new RSACryptoServiceProvider { PersistKeyInCsp = false };
        rsa.FromXmlString(reader.ReadToEnd());
        if (!rsa.VerifyData(ClientUpdateState.SignedPayload(state.Component, state.Version, state.Sha256),
            CryptoConfig.MapNameToOID("SHA256"), Convert.FromBase64String(state.Signature)))
            throw new InvalidDataException("更新包签名校验失败，请联系管理员重新发布。");
    }

    public static bool HasExpectedHash(string path, string expected)
    {
        if (!File.Exists(path)) return false;
        using var file = File.OpenRead(path);
        using var sha = SHA256.Create();
        return string.Equals(BitConverter.ToString(sha.ComputeHash(file)).Replace("-", ""), expected, StringComparison.OrdinalIgnoreCase);
    }

    public static void Fetch(ClientUpdateState state, string package, Action<int?> report)
    {
        if (HasExpectedHash(package, state.Sha256)) return;
        var partial = package + ".part";
        if (!Uri.TryCreate(state.PackageUrl, UriKind.Absolute, out var url)
            || (url.Scheme != "http" && url.Scheme != "https") || url.UserInfo.Length != 0)
            throw new InvalidDataException("更新下载地址无效，请联系管理员核对发布清单。");

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        var offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
        using var response = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).GetAwaiter().GetResult();
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && offset > 0)
        {
            if (HasExpectedHash(partial, state.Sha256))
            {
                File.Delete(package);
                File.Move(partial, package);
                return;
            }
            File.Delete(partial);
        }
        response.EnsureSuccessStatusCode();
        var content = response.Content.Headers;
        var append = response.StatusCode == HttpStatusCode.PartialContent;
        if (append && (offset == 0 || content.ContentRange?.From != offset || !content.ContentRange.HasLength))
        {
            File.Delete(partial);
            throw new InvalidDataException("服务器返回的续传位置无效，下次检查时将重新下载。");
        }
        var received = append ? offset : 0;
        var total = append ? content.ContentRange.Length : content.ContentLength;
        if (total > MaxPackageBytes || received > MaxPackageBytes) throw new InvalidDataException("更新包超过大小限制。");
        using (var source = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
        using (var output = new FileStream(partial, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[128 * 1024];
            var lastProgress = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                var count = source.ReadAsync(buffer, 0, buffer.Length, timeout.Token).GetAwaiter().GetResult();
                if (count == 0) break;
                received += count;
                if (received > MaxPackageBytes) throw new InvalidDataException("更新包超过大小限制。");
                output.Write(buffer, 0, count);
                if (lastProgress.ElapsedMilliseconds >= 1000)
                {
                    lastProgress.Restart();
                    report(total > 0 ? (int?)Math.Min(99, received * 100 / total.Value) : null);
                }
            }
            output.Flush(true);
        }
        if (total.HasValue && received != total.Value) throw new IOException("更新包未完整下载，将继续下载。");
        if (!HasExpectedHash(partial, state.Sha256))
        {
            File.Delete(partial);
            throw new InvalidDataException("更新包文件校验失败，下次检查时将重新下载。");
        }
        File.Delete(package);
        File.Move(partial, package);
    }

}
