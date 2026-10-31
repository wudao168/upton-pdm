using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api;

public static class ClientLogEndpoints
{
    public static void AddClientLogs(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ClientLogOptions>(configuration.GetSection("Pdm:ClientLogs"));
        services.AddSingleton<ClientLogStore>();
        services.AddHostedService<ClientLogCleanupService>();
        services.AddRateLimiter(options =>
        {
            options.OnRejected = async (context, ct) =>
            {
                context.HttpContext.Response.StatusCode = 429;
                await context.HttpContext.Response.WriteAsJsonAsync(new { message = "日志请求过于频繁，请稍后重试。" }, ct);
            };
            options.AddPolicy("client-logs", context => RateLimitPartition.GetFixedWindowLimiter(context.User.Identity?.Name ?? "anonymous", _ =>
                new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
    }

    public static void MapClientLogs(this WebApplication app)
    {
        var group = app.MapGroup("/api/client-logs").RequireAuthorization().RequireRateLimiting("client-logs");
        // Keep all diagnostics errors Chinese, including corrupt archives and disk/database failures.
        group.AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (PdmRuleException e) { return Results.BadRequest(new { message = e.Message }); }
            catch (PdmNotFoundException e) { return Results.NotFound(new { message = e.Message }); }
            catch (Exception e) when (e is JsonException or InvalidDataException or BadHttpRequestException)
            { return Results.BadRequest(new { message = "日志文件格式无效或超过大小限制，请升级客户端后重试。" }); }
            catch (Exception e) when (!context.HttpContext.RequestAborted.IsCancellationRequested)
            {
                context.HttpContext.RequestServices.GetRequiredService<ILogger<ClientLogStore>>().LogError(e, "客户端日志处理失败");
                return Results.Json(new { message = "日志服务暂时不可用，请稍后重试或联系管理员检查存储空间。" }, statusCode: 503);
            }
        });
        group.MapPost("/preview-agent/batches", async (HttpContext context, ClientLogStore store, IPdmRepository repository, CancellationToken ct) =>
        {
            var credential = context.Request.Headers["X-Pdm-Agent-Token"].ToString();
            if (string.IsNullOrWhiteSpace(credential) || credential.Length > 512) return Results.Unauthorized();
            var settings = (await repository.GetSystemSettingsAsync(ct)).PreviewConversion;
            var servers = settings.Servers.Count > 0 ? settings.Servers
                : settings.Mode == PreviewConversionMode.Remote ? [new PreviewAgentServer(settings.AgentUrl, settings.AgentUrl, settings.AgentToken, settings.TimeoutMinutes)] : [];
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(credential));
            var matched = servers.Where(server => !string.IsNullOrWhiteSpace(server.AgentToken)
                && CryptographicOperations.FixedTimeEquals(digest, SHA256.HashData(Encoding.UTF8.GetBytes(server.AgentToken)))).ToArray();
            if (matched.Length != 1) return Results.Unauthorized();
            var agent = matched[0];
            // Older registrations predate company binding; resolve the system's default organization on the server.
            var companyId = agent.CompanyId ?? (await repository.GetProjectNumberingOptionsAsync(ct)).Organizations.FirstOrDefault(item => item.IsActive)?.Id;
            if (companyId is null || companyId == Guid.Empty) throw new PdmRuleException("转图代理尚未关联公司，请管理员重新登记该代理。");
            if (context.Request.ContentLength > 5 * 1024 * 1024) return Results.Json(new { message = "日志压缩包不能超过5MB。" }, statusCode: 413);
            using var compressed = await ReadBoundedAsync(context.Request.Body, 5 * 1024 * 1024, ct);
            using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
            using var expanded = await ReadBoundedAsync(gzip, 10 * 1024 * 1024, ct);
            var batch = await JsonSerializer.DeserializeAsync<ClientLogBatch>(expanded, new JsonSerializerOptions(JsonSerializerDefaults.Web), ct);
            if (batch is null || batch.Component != "preview-agent") throw new PdmRuleException("转图代理日志格式不正确，请升级代理后重试。");
            await store.SaveAsync(batch, companyId.Value, "转图代理：" + agent.Name, ct);
            return Results.Ok(new { batchId = batch.BatchId, accepted = true });
        }).AllowAnonymous().WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(5 * 1024 * 1024));
        group.MapPost("/batches", async (HttpContext context, ClientLogStore store, CancellationToken ct) =>
        {
            var tenant = TenantContext.Current;
            if (tenant is null) return Results.Unauthorized();
            if (context.Request.ContentLength > 5 * 1024 * 1024) return Results.Json(new { message = "日志压缩包不能超过5MB。" }, statusCode: 413);
            using var compressed = await ReadBoundedAsync(context.Request.Body, 5 * 1024 * 1024, ct);
            using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
            using var expanded = await ReadBoundedAsync(gzip, 10 * 1024 * 1024, ct);
            var batch = await JsonSerializer.DeserializeAsync<ClientLogBatch>(expanded, new JsonSerializerOptions(JsonSerializerDefaults.Web), ct);
            if (batch is null) throw new PdmRuleException("日志内容不能为空。");
            await store.SaveAsync(batch, tenant.CompanyId, tenant.Username, ct);
            return Results.Ok(new { batchId = batch.BatchId, accepted = true });
        }).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(5 * 1024 * 1024));

        group.MapGet("", async (HttpContext context, ClientLogStore store, IOptions<ClientLogOptions> options, CancellationToken ct) =>
        {
            if (!CanRead()) return Results.Forbid();
            var query = Query(context.Request);
            var result = await store.ListAsync(TenantContext.Current!.CompanyId, query, 50, ct);
            return Results.Ok(new { items = result.Items, total = result.Total, pageSize = 50, runtimeDays = options.Value.RuntimeDays, errorDays = options.Value.ErrorDays });
        });
        group.MapGet("/download", async (HttpContext context, ClientLogStore store, IPdmRepository repository, TimeProvider clock, CancellationToken ct) =>
        {
            if (!CanRead()) return Results.Forbid();
            var query = Query(context.Request);
            var tenant = TenantContext.Current!;
            var package = await store.DownloadAsync(tenant.CompanyId, query, ct);
            await repository.AppendAuditAsync(new AuditEntry(Guid.NewGuid(), clock.GetUtcNow(), tenant.Username, "client-logs.download", "ClientDiagnostics", tenant.CompanyId.ToString(), $"下载客户端诊断日志，共{package.Count}个分片；类型={query.Kind ?? "全部"}；时间={query.From:O}至{query.To:O}"), ct);
            return Results.File(package.Content, "application/zip", "UPLM-客户端诊断-" + clock.GetUtcNow().ToString("yyyyMMdd-HHmmss") + ".zip");
        });
    }

    private static bool CanRead() => TenantContext.Current is { } t && (t.IsPlatformAdministrator || t.HasRole("Administrator"));
    private static ClientLogQuery Query(HttpRequest request)
    {
        string? Value(string key) => request.Query.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.ToString() : null;
        DateTimeOffset? Date(string key)
        {
            var value = Value(key);
            if (value is null) return null;
            if (!DateTimeOffset.TryParse(value, out var date)) throw new PdmRuleException("筛选时间格式不正确。");
            return date;
        }
        var page = Value("page");
        if (page != null && !int.TryParse(page, out _)) throw new PdmRuleException("页码格式不正确。");
        return new(Value("device"), Value("user"), Value("component"), Value("kind"), Value("version"), Date("from"), Date("to"), page == null ? 1 : int.Parse(page));
    }
    private static async Task<MemoryStream> ReadBoundedAsync(Stream input, int maximum, CancellationToken ct)
    {
        var result = new MemoryStream();
        try
        {
            var buffer = new byte[81920];
            int count;
            while ((count = await input.ReadAsync(buffer, ct)) > 0)
            {
                if (result.Length + count > maximum) throw new PdmRuleException("日志内容超过大小限制，请分批上传。");
                await result.WriteAsync(buffer.AsMemory(0, count), ct);
            }
            result.Position = 0;
            return result;
        }
        catch { result.Dispose(); throw; }
    }
}
