using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.AI;
using Upton.Pdm.Application;

namespace Upton.Pdm.Api;

public interface IAiModelClientFactory
{
    IChatClient Create(AiProvider provider, AiModel model);
}

public sealed class AiModelClientFactory(IDataProtectionProvider protection) : IAiModelClientFactory
{
    public IChatClient Create(AiProvider provider, AiModel model)
    {
        var endpoint = AiEndpointPolicy.Validate(provider.BaseUrl, provider.Network);
        var sockets = new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(15),
            ConnectCallback = async (context, ct) => {
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
                if (addresses.Length == 0 || addresses.Any(ip => !AiEndpointPolicy.Allowed(ip, provider.Network)))
                    throw new PdmRuleException("模型地址不符合所选网络范围，请核对内网地址或云端配置。");
                Exception? last = null;
                foreach (var ip in addresses) {
                    var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    try { await socket.ConnectAsync(new IPEndPoint(ip, context.DnsEndPoint.Port), ct); return new NetworkStream(socket, ownsSocket: true); }
                    catch (Exception ex) { socket.Dispose(); last = ex; }
                }
                throw new HttpRequestException("模型连接失败。", last);
            } };
        var http = new HttpClient(new AiCompatibilityHandler(provider.Adapter, model) { InnerHandler = sockets }) { Timeout = Timeout.InfiniteTimeSpan };
        var key = provider.ProtectedKey is null ? "local-no-key" : protection.CreateProtector("UPLM.AI.Keys.v1").Unprotect(provider.ProtectedKey);
        var options = new OpenAI.OpenAIClientOptions { Endpoint = endpoint, Transport = new HttpClientPipelineTransport(http), RetryPolicy = new ClientRetryPolicy(0) };
        var client = new OpenAI.Chat.ChatClient(model.Id, new ApiKeyCredential(key), options).AsIChatClient();
        IChatClient owned = new OwnedClient(client, http);
        return provider.Adapter == "deepseek" && model.Thinking ? new AiDeepSeekThinkingClient(owned, provider, model, protection) : owned;
    }
    private sealed class OwnedClient(IChatClient inner, HttpClient http) : DelegatingChatClient(inner)
    {
        protected override void Dispose(bool disposing) { base.Dispose(disposing); if (disposing) http.Dispose(); }
    }
}

public static class AiEndpointPolicy
{
    public static Uri Validate(string address, string network)
    {
        if (network is not ("intranet" or "cloud") || !Uri.TryCreate(address.TrimEnd('/') + "/", UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment) || address.Length > 300 || network == "cloud" && uri.Scheme != "https")
            throw new PdmRuleException("请填写有效的API基础地址；云端服务必须使用HTTPS，地址不能包含账号、查询参数或片段。");
        if (uri.AbsolutePath.TrimEnd('/').EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            throw new PdmRuleException("请填写API基础地址，不要包含 /chat/completions。");
        if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && !Allowed(ip, network))
            throw new PdmRuleException("模型地址不符合所选网络范围，内网模式只允许回环和私有地址。");
        return uri;
    }
    public static bool Allowed(IPAddress value, string network)
    {
        var ip = value.IsIPv4MappedToIPv6 ? value.MapToIPv4() : value;
        var bytes = ip.GetAddressBytes();
        var privateAddress = IPAddress.IsLoopback(ip) || (bytes.Length == 4
            ? bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 || bytes[0] == 192 && bytes[1] == 168
            : (bytes[0] & 0xfe) == 0xfc);
        if (network == "intranet") return privateAddress;
        if (privateAddress || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.IsIPv6LinkLocal || ip.IsIPv6Multicast) return false;
        return bytes.Length != 4 || bytes[0] is > 0 and < 224 && !(bytes[0] == 169 && bytes[1] == 254) && !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127);
    }
}

public sealed class AiCompatibilityHandler(string adapter, AiModel model) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.Content is not null && adapter == "deepseek") {
            var body = JsonNode.Parse(await request.Content.ReadAsStringAsync(ct))!.AsObject();
            body["thinking"] = new JsonObject { ["type"] = model.Thinking ? "enabled" : "disabled" };
            if (model.Thinking) body["reasoning_effort"] = model.ReasoningEffort;
            else body.Remove("reasoning_effort");
            if (body.Remove("max_completion_tokens", out var limit)) body["max_tokens"] = limit;
            request.Content.Dispose(); request.Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
        }
        return await base.SendAsync(request, ct);
    }
}
