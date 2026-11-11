using System.Text.Json;
using Microsoft.Extensions.Options;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Api;

internal static class PreviewWorkbenchEndpoints
{
    internal static void MapPreviewWorkbench(this WebApplication app)
    {
        var group = app.MapGroup("/api/preview-workbench").RequireAuthorization();
        group.MapGet("", async (HttpContext context, IPdmRepository repository, IHttpClientFactory clients, CancellationToken ct) =>
        {
            var (actor, role) = PdmEndpointExtensions.CurrentUser(context.User);
            var projects = (await repository.ListProjectNavigationForUserAsync(actor, role, ct)).Where(p => p.CanReadContent).ToDictionary(p => p.Id);
            var headers = await repository.ListPreviewPackageHeadersAsync(projects.Keys.ToArray(), ct);
            var conversion = (await repository.GetSystemSettingsAsync(ct)).PreviewConversion;
            var configured = conversion.Servers.Count > 0 ? conversion.Servers : string.IsNullOrWhiteSpace(conversion.AgentUrl) ? []
                : new[] { new PreviewAgentServer("转图服务器", conversion.AgentUrl, conversion.AgentToken) };
            using var http = clients.CreateClient("preview-workbench");
            var servers = await Task.WhenAll(configured.Where(s => !s.CompanyId.HasValue || s.CompanyId == TenantContext.CompanyId)
                .Select(s => ReadServerAsync(http, s, ct)));
            return Results.Ok(new { servers, canRetry = await repository.HasUserPermissionAsync(actor, role, PermissionCodes.ReleaseManage, ct),
                packages = headers.Select(p => new { p.Id, p.ProjectId, p.Number, p.State, p.PreviewState, p.DrawingCount, projectCode = projects[p.ProjectId].Code }) });
        });
        group.MapGet("/{packageId:guid}/history", async (Guid packageId, HttpContext context, IPdmRepository repository, IOptions<PdmStorageOptions> storage, IHttpClientFactory clients, CancellationToken ct) =>
        {
            var (actor, role) = PdmEndpointExtensions.CurrentUser(context.User);
            var package = await repository.FindReleasePackageAsync(packageId, ct);
            if (package is null) return Results.NotFound();
            if (!await repository.HasProjectContentReadAccessAsync(package.ProjectId, actor, role, ct)) return Results.Forbid();
            var entries = PreviewDispatchHistory.Read(storage.Value.ConversionWorkRoot, packageId);
            var active = entries.FirstOrDefault(e => e.Finished is null && e.Started > DateTimeOffset.UtcNow.AddHours(-2));
            if (active is not null)
            {
                var conversion = (await repository.GetSystemSettingsAsync(ct)).PreviewConversion;
                var host = conversion.Servers.FirstOrDefault(s => s.Name == active.Server && (s.CompanyId == null || s.CompanyId == TenantContext.CompanyId));
                if (host is not null && !string.IsNullOrWhiteSpace(host.AgentToken))
                {
                    try
                    {
                        using var client = clients.CreateClient("preview-workbench");
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        timeout.CancelAfter(TimeSpan.FromSeconds(3));
                        using var request = new HttpRequestMessage(HttpMethod.Get, host.AgentUrl.TrimEnd('/') + "/health");
                        request.Headers.Add("X-Pdm-Agent-Token",host.AgentToken);
                        request.Headers.Add("X-UPLM-Workbench","1");
                        using var response = await client.SendAsync(request,timeout.Token);
                        response.EnsureSuccessStatusCode();
                        using var data = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
                        if (data.RootElement.TryGetProperty("dispatchId",out var key) && key.GetString() == active.Id.ToString("N")
                            && data.RootElement.TryGetProperty("progressFiles",out var files) && files.ValueKind == JsonValueKind.Array)
                        {
                            var states = new Dictionary<Guid,string>();
                            foreach (var file in files.EnumerateArray())
                                if (file.TryGetProperty("DocumentId",out var document) && document.TryGetGuid(out var id) && active.Documents.Contains(id)
                                    && file.TryGetProperty("Status",out var status)) states[id] = status.GetString() ?? "处理中";
                            entries = entries.Select(e => e.Id == active.Id ? e with { Progress = states } : e).ToArray();
                        }
                    }
                    catch (Exception e) when (!ct.IsCancellationRequested && e is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException) { }
                }
            }
            return Results.Ok(entries);
        });
    }

    private static async Task<object> ReadServerAsync(HttpClient http, PreviewAgentServer server, CancellationToken ct)
    {
        var name = server.Name;
        string state;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            using var request = new HttpRequestMessage(HttpMethod.Get, server.AgentUrl.TrimEnd('/') + "/health");
            using var response = await http.SendAsync(request, timeout.Token);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            var data = json.RootElement;
            bool Flag(string key, bool fallback = false) => data.TryGetProperty(key, out var value) ? value.ValueKind == JsonValueKind.True : fallback;
            state = PreviewAgentPool.IsCooling(server.AgentUrl) ? "故障隔离" : !Flag("workerExists") ? "缺少转换程序" :
                !Flag("accepting", true) ? "暂停接单" : Flag("busy") ? "处理中" : "空闲";
        }
        catch (Exception e) when (!ct.IsCancellationRequested && e is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException)
        { state = "离线"; }
        return new { name, state };
    }
}
