using System.Collections.Concurrent;
using System.Text.Json;
using Upton.Pdm.Domain;
using Upton.Pdm.Application;

namespace Upton.Pdm.Infrastructure;

public static class PreviewAgentPool
{
    private static readonly ConcurrentDictionary<string, string> Active = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, DateTimeOffset> Cooling = new(StringComparer.OrdinalIgnoreCase);
    public static bool IsCooling(string url) => Cooling.TryGetValue(url.TrimEnd('/'), out var until) && until > DateTimeOffset.UtcNow;
    internal static void ReportFault(string url) => Cooling[url.TrimEnd('/')] = DateTimeOffset.UtcNow.AddMinutes(2);

    private static readonly ConcurrentDictionary<Guid, string> Waiting = new();

    public static object Snapshot() => new { executing = Active.Select(pair => new { server = pair.Key, task = pair.Value }).ToArray(), queued = Waiting.Values.ToArray() };

    public static async Task<Lease> AcquireAsync(PreviewConversionSettings settings, string task,
        HttpClient client, CancellationToken cancellationToken, bool waitForServer = true, IReadOnlySet<string>? excluded = null)
    {
        var servers = settings.GetConfiguredServers().Where(server => !server.Disabled).ToArray();
        if (servers.Length == 0) throw new PreviewServerUnavailableException("暂时没有已启用的远程转图服务器，等待启用后重试。");
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
                    if (excluded?.Contains(url) == true || IsCooling(url)) continue;
                    if (!Active.TryAdd(url, task)) continue;
                    var leased = false;
                    try
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        timeout.CancelAfter(TimeSpan.FromSeconds(3));
                        using var response = await client.GetAsync($"{url}/health", timeout.Token);
                        if (response.IsSuccessStatusCode)
                        {
                            using var health = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
                            if (health.RootElement.TryGetProperty("workerExists", out var exists) && exists.ValueKind == JsonValueKind.True
                                && (!health.RootElement.TryGetProperty("accepting", out var accepting) || accepting.ValueKind != JsonValueKind.False)
                                && (!health.RootElement.TryGetProperty("busy", out var busy) || busy.ValueKind != JsonValueKind.True))
                            {
                                leased = true;
                                return new Lease(server.Name, url, settings with
                                {
                                    AgentUrl = url,
                                    AgentToken = server.AgentToken,
                                    TimeoutMinutes = server.TimeoutMinutes
                                });
                            }
                        }
                    }
                    catch (Exception exception) when (!cancellationToken.IsCancellationRequested &&
                        exception is (HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException)) { }
                    finally
                    {
                        if (!leased) Active.TryRemove(url, out _);
                    }
                }
                if (!waitForServer) throw new PreviewServerUnavailableException("转图服务器离线、忙碌或转换程序尚未就绪，等待恢复后自动重试。");
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
        }
        finally { Waiting.TryRemove(requestId, out _); }
    }

    public sealed class Lease(string name, string url, PreviewConversionSettings settings) : IDisposable
    {
        public string Name { get; } = name;
        public PreviewConversionSettings Settings { get; } = settings;
        private int released;
        public void Dispose() { if (Interlocked.Exchange(ref released, 1) == 0) Active.TryRemove(url, out _); }
    }
}
