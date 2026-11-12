using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Upton.Pdm.Application;

namespace Upton.Pdm.Api;

public sealed record AiWebPage(string Title, string Url, string Content, string? Date = null);
public interface IAiWebClient
{
    Task<AiWebPage[]> SearchAsync(AiProvider provider, string query, CancellationToken ct);
    Task<AiWebPage> FetchAsync(AiProvider provider, string url, CancellationToken ct);
}

// The search service always uses the official origin; provider configuration supplies only its protected key.
public sealed class AiWebClient(IDataProtectionProvider protection) : IAiWebClient
{
    public async Task<AiWebPage[]> SearchAsync(AiProvider provider, string query, CancellationToken ct)
    {
        using var doc = await PostAsync(provider, "search", new { query, count = 5, need_content = false }, ct);
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            throw new PdmRuleException("联网搜索返回的数据格式异常，请稍后重试。");
        return results.EnumerateArray().Take(5).Where(r => AiPublicUrl.TryParse(Text(r, "url"), out _))
            .Select(r => new AiWebPage(Clip(Text(r, "title"), 160), Text(r, "url"),
                Clip(Text(r, "snippet") + "\n" + Text(r, "summary"), 1200), Clip(Text(r, "date"), 40))).ToArray();
    }
    public async Task<AiWebPage> FetchAsync(AiProvider provider, string url, CancellationToken ct)
    {
        using var doc = await PostAsync(provider, "fetch", new { url }, ct);
        var content = Text(doc.RootElement, "content");
        if (string.IsNullOrWhiteSpace(content)) throw new PdmRuleException("该网页未返回可读取的正文，可能需要登录或限制自动访问。请换用公开页面或粘贴内容。");
        return new(Clip(Text(doc.RootElement, "title"), 160), url, Clip(content, 7000));
    }
    private async Task<JsonDocument> PostAsync(AiProvider provider, string action, object body, CancellationToken ct)
    {
        if (!AiWebPolicy.IsOfficial(provider)) throw new PdmRuleException("联网服务须选择已启用并保存密钥的DeepSeek官方云端配置。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(40));
        using var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(12),
            ConnectCallback = async (context, token) => {
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token);
                if (addresses.Length == 0 || addresses.Any(ip => !AiPublicUrl.IsPublic(ip))) throw new PdmRuleException("联网服务地址解析异常，请检查网络设置。");
                Exception? last = null;
                foreach (var ip in addresses) {
                    var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    try { await socket.ConnectAsync(new IPEndPoint(ip, context.DnsEndPoint.Port), token); return new NetworkStream(socket, true); }
                    catch (Exception ex) { socket.Dispose(); last = ex; }
                }
                throw new HttpRequestException("联网服务连接失败。", last);
            } };
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.deepseek.com/v1/tools/" + action);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", protection.CreateProtector("UPLM.AI.Keys.v1").Unprotect(provider.ProtectedKey!));
        request.Content = JsonContent.Create(body);
        try {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode) throw new PdmRuleException((int)response.StatusCode switch {
                401 => "联网服务认证失败，请管理员检查DeepSeek密钥。",
                402 => "DeepSeek账户余额不足，请充值后重试。",
                403 => "联网服务拒绝访问，请检查账户权限或换用允许访问的公开网页。",
                404 => "联网接口或网页不存在，请核对公开网址及服务可用性。",
                429 => "联网服务请求过多，请稍后重试。",
                400 or 413 => "搜索条件或网页内容超出服务限制，请缩短关键词或更换网页。",
                408 or 504 => "联网服务等待超时，请稍后重试。",
                _ => "联网服务暂时不可用，请稍后重试。"
            });
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var data = new MemoryStream(); var buffer = new byte[8192]; int read;
            while ((read = await stream.ReadAsync(buffer, timeout.Token)) > 0) {
                if (data.Length + read > 512 * 1024) throw new PdmRuleException("网页返回内容过大，请更换范围更小的公开页面。");
                data.Write(buffer, 0, read);
            }
            return JsonDocument.Parse(data.ToArray());
        } catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new PdmRuleException("联网服务等待超时，请稍后重试。"); }
        catch (HttpRequestException) { throw new PdmRuleException("无法连接DeepSeek联网服务，请检查服务器外网连接。"); }
        catch (JsonException) { throw new PdmRuleException("联网服务返回的数据格式异常，请稍后重试。"); }
    }
    private static string Text(JsonElement value, string key) => value.TryGetProperty(key, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() ?? "" : "";
    private static string Clip(string value, int length) => value.Length <= length ? value : value[..length] + "（内容已截断）";
}

public static class AiWebPolicy
{
    public static bool IsOfficial(AiProvider provider) => provider.Enabled && provider.Network == "cloud" && provider.Adapter == "deepseek"
        && !string.IsNullOrWhiteSpace(provider.ProtectedKey) && Uri.TryCreate(provider.BaseUrl, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.Host.Equals("api.deepseek.com", StringComparison.OrdinalIgnoreCase) && uri.Port == 443
        && uri.AbsolutePath.TrimEnd('/') is "" or "/v1" && uri.UserInfo == "" && uri.Query == "" && uri.Fragment == "";
    public static AiProvider Provider(AiSettings settings) => settings.Web is { Enabled: true } web
        && settings.Providers.FirstOrDefault(p => p.Id == web.ProviderId && IsOfficial(p)) is { } provider ? provider
        : throw new PdmRuleException("当前公司尚未启用联网搜索，请在AI服务配置中选择DeepSeek官方服务并启用。");
}

public static class AiPublicUrl
{
    public static bool TryParse(string? value, out Uri? uri)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 8192 || !Uri.TryCreate(value, UriKind.Absolute, out uri) || uri.Scheme is not ("https" or "http")
            || uri.UserInfo != "" || uri.Port is not (80 or 443) || uri.HostNameType == UriHostNameType.Unknown) { uri = null; return false; }
        var host = uri.IdnHost.Trim('[', ']').TrimEnd('.').ToLowerInvariant();
        return IPAddress.TryParse(host, out var ip) ? IsPublic(ip)
            : host.Contains('.') && !new[] { ".localhost", ".local", ".lan", ".internal", ".test", ".invalid", ".home", ".onion" }.Any(host.EndsWith);
    }
    public static async Task<string> ValidateAsync(string value, CancellationToken ct)
    {
        if (!TryParse(value, out var uri)) throw new PdmRuleException("只能读取公开的HTTP或HTTPS网页，不支持内网地址、登录凭据及非标准端口。");
        try {
            var addresses = await Dns.GetHostAddressesAsync(uri!.DnsSafeHost, ct);
            if (addresses.Length == 0 || addresses.Any(ip => !IsPublic(ip))) throw new PdmRuleException("网页地址解析到非公网地址，不能发送至联网服务。");
        } catch (SocketException) { throw new PdmRuleException("网页域名无法解析，请检查公开网址。"); }
        return uri!.AbsoluteUri;
    }
    public static bool IsPublic(IPAddress value)
    {
        var ip = value.IsIPv4MappedToIPv6 ? value.MapToIPv4() : value;
        if (!AiEndpointPolicy.Allowed(ip, "cloud")) return false;
        var b = ip.GetAddressBytes();
        // Reject reserved/test ranges and IPv6 transition addresses, in addition to private/link-local addresses.
        return b.Length == 4 ? !(b[0] == 192 && b[1] == 0 || b[0] == 198 && b[1] is 18 or 19 || b[0] == 198 && b[1] == 51 && b[2] == 100 || b[0] == 203 && b[1] == 0 && b[2] == 113)
            : (b[0] & 0xe0) == 0x20 && !(b[0] == 0x20 && b[1] == 0x02) && !(b[0] == 0x20 && b[1] == 0x01 && (b[2] < 2 || b[2] == 0x0d && b[3] == 0xb8));
    }
}
