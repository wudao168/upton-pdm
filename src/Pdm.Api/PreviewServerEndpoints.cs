using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Api;

internal static class PreviewServerEndpoints
{
    private static readonly SemaphoreSlim StatusProbeSlots = new(8);
    private static readonly MemoryCache StatusCache = new(new MemoryCacheOptions { SizeLimit = 512 });
    private static readonly TimeSpan StatusCacheLifetime = TimeSpan.FromSeconds(4);

    internal static void MapPreviewServerSettings(this RouteGroupBuilder api, ILogger logger)
    {
        var servers = api.MapGroup("/system-settings/preview-servers");
        servers.MapGet("", async (HttpContext context, IPdmRepository repository, CancellationToken ct) =>
        {
            if (!await CanManageAsync(context, repository, ct)) return Results.Forbid();
            var conversion = (await repository.GetSystemSettingsAsync(ct)).PreviewConversion;
            return Results.Ok(VisibleServers(conversion).Select(ToView).ToArray());
        });
        servers.MapGet("/status", async (HttpContext context, IPdmRepository repository, IHttpClientFactory clients, CancellationToken ct) =>
        {
            if (!await CanManageAsync(context, repository, ct)) return Results.Forbid();
            var conversion = (await repository.GetSystemSettingsAsync(ct)).PreviewConversion;
            context.Response.Headers.CacheControl = "no-store";
            var statuses = await Task.WhenAll(VisibleServers(conversion)
                .Select(server => ReadStatusAsync(server, clients, logger, ct)));
            return Results.Ok(statuses);
        });
        servers.MapPost("", async (SaveServerRequest request, HttpContext context, PdmWorkflowService workflow, CancellationToken ct) =>
        {
            var (actor, role) = PdmEndpointExtensions.CurrentUser(context.User);
            return Results.Ok(ToView(await workflow.SavePreviewServerAsync(null, request.Name, request.AgentUrl, request.AgentToken,
                request.TimeoutMinutes, request.Disabled, actor, role, ct)));
        });
        servers.MapPut("", async (SaveServerRequest request, HttpContext context, PdmWorkflowService workflow, CancellationToken ct) =>
        {
            var (actor, role) = PdmEndpointExtensions.CurrentUser(context.User);
            if (string.IsNullOrWhiteSpace(request.OriginalUrl)) throw new PdmRuleException("缺少原服务器地址，请刷新列表后重试。");
            return Results.Ok(ToView(await workflow.SavePreviewServerAsync(request.OriginalUrl, request.Name, request.AgentUrl, request.AgentToken,
                request.TimeoutMinutes, request.Disabled, actor, role, ct)));
        });
        servers.MapDelete("", async (string agentUrl, HttpContext context, PdmWorkflowService workflow, CancellationToken ct) =>
        {
            var (actor, role) = PdmEndpointExtensions.CurrentUser(context.User);
            await workflow.RemovePreviewServerAsync(agentUrl, actor, role, ct);
            return Results.NoContent();
        });
        servers.MapPost("/test", async (TestServerRequest request, HttpContext context, IPdmRepository repository,
            IHttpClientFactory clients, CancellationToken ct) =>
        {
            if (!await CanManageAsync(context, repository, ct)) return Results.Forbid();
            var conversion = (await repository.GetSystemSettingsAsync(ct)).PreviewConversion;
            var server = VisibleServers(conversion).FirstOrDefault(server => PdmWorkflowService.PreviewServerAddressMatches(
                server.AgentUrl, request.OriginalUrl ?? request.AgentUrl));
            if (server is null) throw new PdmNotFoundException("转图服务器不存在或已变更，请刷新列表。");
            if (!PdmWorkflowService.PreviewServerAddressMatches(server.AgentUrl, request.AgentUrl))
                throw new PdmRuleException("请保存服务器地址后再测试连接。");
            var result = await ProbeAsync(server.AgentUrl, server.AgentToken, clients, logger, ct);
            CacheStatus(server, result);
            return Results.Ok(result);
        });

        api.MapPost("/system-settings/preview-agent/test", async (PreviewAgentProbeRequest? request, HttpContext context,
            IPdmRepository repository, IHttpClientFactory clients, CancellationToken ct) =>
        {
            if (!await CanManageAsync(context, repository, ct)) return Results.Forbid();
            var saved = (await repository.GetSystemSettingsAsync(ct)).PreviewConversion;
            var requestedUrl = request?.AgentUrl;
            var mode = request?.Mode ?? (string.IsNullOrWhiteSpace(requestedUrl) ? saved.Mode : PreviewConversionMode.Remote);
            if (mode != PreviewConversionMode.Remote)
                return Results.Ok(new PreviewAgentProbeResult(true, "当前为本机转换，无需连接远程转图服务器。", null, null, null));
            var visible = VisibleServers(saved);
            var server = string.IsNullOrWhiteSpace(requestedUrl) ? visible.FirstOrDefault()
                : visible.FirstOrDefault(server => PdmWorkflowService.PreviewServerAddressMatches(server.AgentUrl, requestedUrl));
            return Results.Ok(await ProbeAsync(server?.AgentUrl ?? requestedUrl, request?.AgentToken ?? server?.AgentToken,
                clients, logger, ct));
        });
    }

    internal static PdmSystemSettings RedactSettings(PdmSystemSettings settings, bool localExecutionAllowed)
    {
        var servers = VisibleServers(settings.PreviewConversion);
        var primary = servers.FirstOrDefault(server => !server.Disabled) ?? servers.FirstOrDefault();
        return settings with
        {
            PreviewConversion = settings.PreviewConversion with
            {
                AgentUrl = primary?.AgentUrl ?? string.Empty,
                AgentToken = string.Empty,
                TimeoutMinutes = primary?.TimeoutMinutes ?? PreviewConversionSettings.Default.TimeoutMinutes,
                Servers = settings.PreviewConversion.Servers.Count == 0 ? [] : servers.Select(server => server with { AgentToken = string.Empty }).ToArray(),
                LocalExecutionAllowed = localExecutionAllowed
            }
        };
    }

    private static PreviewAgentServer[] VisibleServers(PreviewConversionSettings settings) =>
        settings.GetConfiguredServers().Where(server => server.IsAvailableForCompany(TenantContext.CompanyId)).ToArray();

    private static async Task<bool> CanManageAsync(HttpContext context, IPdmRepository repository, CancellationToken ct)
    {
        var (actor, role) = PdmEndpointExtensions.CurrentUser(context.User);
        return await repository.HasUserPermissionAsync(actor, role, PermissionCodes.StorageSettingsManage, ct);
    }

    private static PreviewServerView ToView(PreviewAgentServer server) =>
        new(server.Name, server.AgentUrl, !string.IsNullOrEmpty(server.AgentToken), server.TimeoutMinutes, server.Disabled);

    private static async Task<ConnectionStatus> ReadStatusAsync(PreviewAgentServer server, IHttpClientFactory clients,
        ILogger logger, CancellationToken ct)
    {
        var key = (server.AgentUrl, server.AgentToken);
        if (StatusCache.TryGetValue<ConnectionStatus>(key, out var cached) && cached is not null) return cached;
        await StatusProbeSlots.WaitAsync(ct);
        try
        {
            if (StatusCache.TryGetValue(key, out cached) && cached is not null) return cached;
            var result = await ProbeAsync(server.AgentUrl, server.AgentToken, clients, logger, ct, automatic: true);
            return CacheStatus(server, result);
        }
        finally { StatusProbeSlots.Release(); }
    }

    private static ConnectionStatus CacheStatus(PreviewAgentServer server, PreviewAgentProbeResult result)
    {
        var status = new ConnectionStatus(server.AgentUrl, result.WorkerExists.HasValue, result.WorkerExists == true,
            DateTimeOffset.UtcNow, result.Message);
        StatusCache.Set((server.AgentUrl, server.AgentToken), status,
            new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = StatusCacheLifetime, Size = 1 });
        return status;
    }

    private static async Task<PreviewAgentProbeResult> ProbeAsync(string? agentUrl, string? token, IHttpClientFactory clients,
        ILogger logger, CancellationToken ct, bool automatic = false)
    {
        string url;
        try { url = PdmWorkflowService.NormalizePreviewServerAddress(agentUrl); }
        catch (PdmRuleException exception) { return new(false, exception.Message, null, null, null); }
        var agentToken = PdmWorkflowService.NormalizePreviewServerToken(token);
        var target = new Uri(url);
        try
        {
            using var client = clients.CreateClient("preview-workbench");
            client.Timeout = TimeSpan.FromSeconds(automatic ? 4 : 20);
            using var request = new HttpRequestMessage(HttpMethod.Get, url + PreviewAgentProtocol.HealthPath);
            if (!string.IsNullOrEmpty(agentToken)) request.Headers.Add(PreviewAgentProtocol.TokenHeader, agentToken);
            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                var advice = status is 401 or 403 ? "请核对两端的访问令牌。" : "请检查该端口是否运行转图代理。";
                return new(false, $"无法连接转图服务器 {target.Authority}（HTTP {status}）。{advice}", null, null, null);
            }

            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var data = payload.RootElement;
            var machine = data.TryGetProperty("machine", out var machineElement) ? machineElement.GetString() : null;
            if (!data.TryGetProperty("workerExists", out var existsElement) || existsElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return new(false, "该端口未返回有效的转图代理状态，请核对端口对应的程序。", null, null, null);
            var workerExists = existsElement.GetBoolean();
            var message = workerExists
                ? "转图服务器已连接，SolidWorks转换程序就绪。"
                : "转图服务器已连接，转换程序未就绪，请检查转图代理配置。";
            return new(workerExists, message, machine, workerExists, null);
        }
        catch (OperationCanceledException exception) when (!ct.IsCancellationRequested)
        {
            if (!automatic) logger.LogWarning(exception, "转图代理健康检查超时 {AgentHost}", target.Authority);
            return new(false, $"转图服务器 {target.Authority} 在{(automatic ? 4 : 20)}秒内未响应，请检查代理和网络连接。", null, null, null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (!automatic) logger.LogWarning(exception, "转图代理健康检查失败 {AgentHost}", target.Authority);
            var advice = exception is JsonException or InvalidOperationException
                ? "该端口未返回有效的转图代理状态，请核对端口对应的程序。"
                : "请核对代理地址、监听端口和防火墙后重试。";
            return new(false, $"无法连接转图服务器 {target.Authority}。{advice}", null, null, null);
        }
    }

    private sealed record SaveServerRequest(string Name, string AgentUrl, int TimeoutMinutes, bool Disabled,
        string? AgentToken = null, string? OriginalUrl = null);

    private sealed record TestServerRequest(string AgentUrl, string? OriginalUrl = null);

    private sealed record PreviewServerView(string Name, string AgentUrl, bool HasToken, int TimeoutMinutes, bool Disabled);
    private sealed record ConnectionStatus(string AgentUrl, bool Connected, bool WorkerReady, DateTimeOffset CheckedAt, string Message);
}
