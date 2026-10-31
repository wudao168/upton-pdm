using System.Collections.Concurrent;
using System.Text.Json;
using Upton.Pdm.Domain;
using Upton.Pdm.Application;

namespace Upton.Pdm.Infrastructure;

public static class PreviewAgentPool
{
    private static readonly ConcurrentDictionary<string, string> Active = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<Guid, string> Waiting = new();

    public static object Snapshot() => new { executing = Active.Select(pair => new { server = pair.Key, task = pair.Value }).ToArray(), queued = Waiting.Values.ToArray() };

    public static async Task<Lease> AcquireAsync(PreviewConversionSettings settings, string task,
        HttpClient client, CancellationToken cancellationToken, bool waitForServer = true)
    {
        var servers = settings.Servers.Count > 0 ? settings.Servers
            : [new PreviewAgentServer(settings.AgentUrl, settings.AgentUrl, settings.AgentToken, settings.TimeoutMinutes)];
        var requestId = Guid.NewGuid();
        Waiting[requestId] = task;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var server in servers)
                {
                    var url = server.AgentUrl.Trim().TrimEnd('/');
                    if (!Active.TryAdd(url, task)) continue;
                    try
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        timeout.CancelAfter(TimeSpan.FromSeconds(3));
                        using var response = await client.GetAsync($"{url}/health", timeout.Token);
                        if (response.IsSuccessStatusCode)
                        {
                            using var health = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
                            if (health.RootElement.TryGetProperty("workerExists", out var exists) && exists.ValueKind == JsonValueKind.True
                                && (!health.RootElement.TryGetProperty("busy", out var busy) || busy.ValueKind != JsonValueKind.True))
                                return new Lease(url, settings with { AgentUrl = url, AgentToken = server.AgentToken, TimeoutMinutes = server.TimeoutMinutes });
                        }
                    }
                    catch (Exception exception) when (exception is HttpRequestException || exception is OperationCanceledException || exception is JsonException) { }
                    Active.TryRemove(url, out _);
                }
                if (!waitForServer) throw new PreviewServerUnavailableException("转图服务器离线、忙碌或转换程序尚未就绪，等待恢复后自动重试。");
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
        }
        finally { Waiting.TryRemove(requestId, out _); }
    }

    public sealed class Lease(string url, PreviewConversionSettings settings) : IDisposable
    {
        public PreviewConversionSettings Settings { get; } = settings;
        public void Dispose() => Active.TryRemove(url, out _);
    }
}
