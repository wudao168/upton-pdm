using System.Text.Json;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Api;

public sealed record ConnectionIndicator(string State, string Message);
public sealed record SystemConnectionSnapshot(ConnectionIndicator Pdf, ConnectionIndicator U9c, DateTimeOffset CheckedAt);

public sealed class SystemConnectionStatusCache
{
    public SemaphoreSlim Gate { get; } = new(1, 1);
    public SystemConnectionSnapshot? Snapshot { get; set; }
}

public sealed class SystemConnectionStatus(
    IPdmRepository repository, IMaterialRepository materials, IU9SecretProtector secrets,
    IU9OpenApiClient u9, IHttpClientFactory clients, SystemConnectionStatusCache cache,
    TimeProvider clock, ILogger<SystemConnectionStatus> logger)
{
    public async Task<SystemConnectionSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        await cache.Gate.WaitAsync(cancellationToken);
        try
        {
            if (cache.Snapshot is { } saved && clock.GetUtcNow() - saved.CheckedAt < TimeSpan.FromSeconds(30)) return saved;
            var pdf = ProbePdfAsync(cancellationToken);
            var u9c = ProbeU9Async(cancellationToken);
            await Task.WhenAll(pdf, u9c);
            return cache.Snapshot = new(await pdf, await u9c, clock.GetUtcNow());
        }
        finally { cache.Gate.Release(); }
    }

    private async Task<ConnectionIndicator> ProbePdfAsync(CancellationToken cancellationToken)
    {
        var settings = (await repository.GetSystemSettingsAsync(cancellationToken)).PreviewConversion;
        if (settings.Mode != PreviewConversionMode.Remote) return new("unconfigured", "PDF：使用本机转换，未连接远程转图服务器。");
        if (settings.Servers.Count == 0 && string.IsNullOrWhiteSpace(settings.AgentUrl))
            return new("unconfigured", "PDF：尚未配置转图服务器，请联系管理员完善转换设置。");
        var servers = settings.Servers.Count > 0 ? settings.Servers : [new PreviewAgentServer("转图服务器", settings.AgentUrl, settings.AgentToken)];
        var results = await Task.WhenAll(servers.Select(async server =>
        {
            if (!Uri.TryCreate(server.AgentUrl.Trim().TrimEnd('/') + PreviewAgentProtocol.HealthPath, UriKind.Absolute, out var uri)
                || (uri.Scheme != "http" && uri.Scheme != "https")) return false;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                using var client = clients.CreateClient();
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                if (!string.IsNullOrWhiteSpace(server.AgentToken)) request.Headers.Add(PreviewAgentProtocol.TokenHeader, server.AgentToken);
                using var response = await client.SendAsync(request, timeout.Token);
                response.EnsureSuccessStatusCode();
                using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
                return payload.RootElement.TryGetProperty("workerExists", out var exists) && exists.ValueKind == JsonValueKind.True;
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(exception, "转图服务器状态检查失败");
                return false;
            }
        }));
        var ready = results.Count(result => result);
        return ready == results.Length ? new("connected", $"PDF：已连接{ready}台转图服务器，转换程序就绪。")
            : ready > 0 ? new("warning", $"PDF：{ready}/{results.Length}台转图服务器就绪，请检查其余服务器。")
            : new("disconnected", "PDF：转图服务器未就绪，请检查代理服务、网络及转换程序。");
    }

    private async Task<ConnectionIndicator> ProbeU9Async(CancellationToken cancellationToken)
    {
        var configuration = await materials.GetIntegrationConfigurationAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(configuration.BaseUrl) || string.IsNullOrWhiteSpace(configuration.ClientSecretCiphertext))
            return new("unconfigured", "U9C：连接尚未配置，请联系管理员完善接口设置。");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            await u9.AuthenticateAsync(new(configuration.BaseUrl, configuration.EnterpriseCode, configuration.OrganizationCode,
                configuration.UserCode, configuration.ClientId, secrets.Unprotect(configuration.ClientSecretCiphertext)), timeout.Token);
            return new("connected", "U9C：服务器连接及身份认证正常。");
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "U9C连接状态检查失败");
            return new("disconnected", "U9C：连接或认证失败，请联系管理员检查网络及接口配置。");
        }
    }
}
