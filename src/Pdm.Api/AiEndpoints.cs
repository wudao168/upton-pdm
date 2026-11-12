using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api;

public static class AiEndpoints
{
    public static IServiceCollection AddPdmAi(this IServiceCollection services) {
        services.AddScoped<IAiStore>(p => (IAiStore)p.GetRequiredService<IPdmRepository>());
        services.AddScoped<AiAccess>(); services.AddScoped<AiSettingsService>(); services.AddScoped<AiChatService>(); services.AddScoped<AiBusinessTools>();
        services.AddSingleton<IAiWebClient, AiWebClient>(); services.AddScoped<AiWebTools>();
        services.AddSingleton<IAiModelClientFactory, AiModelClientFactory>(); services.AddSingleton<AiRunLimits>(); return services;
    }
    public static void MapPdmAi(this WebApplication app) {
        var api = app.MapGroup("/api/ai").RequireAuthorization();
        api.MapGet("/bootstrap", async (AiAccess access, IAiStore store, CancellationToken ct) => {
            var chat = await access.AllowsAsync(AiPermissions.Chat, null, ct); var manage = await access.AllowsAsync(AiPermissions.Settings, null, ct);
            if (!chat && !manage) throw new UnauthorizedAccessException("当前账号没有AI助手权限，请在权限中心配置。");
            var cloud = await access.AllowsAsync(AiPermissions.Cloud, null, ct); var settings = await store.SettingsAsync(access.Tenant.CompanyId, ct);
            var canWeb = chat && cloud && await access.AllowsAsync(AiPermissions.Web, null, ct);
            return Results.Ok(new { canChat = chat, canManage = manage, canBusiness = await access.CanUseBusinessAsync(ct), canWeb, settings.DefaultModel,
                webEnabled = settings.Web is { Enabled: true } w && settings.Providers.Any(p => p.Id == w.ProviderId && AiWebPolicy.IsOfficial(p)),
                web = canWeb ? settings.Web ?? new() : null, webUsage = canWeb ? await store.WebUsageAsync(access.Tenant.CompanyId, DateTimeOffset.UtcNow, ct) : null,
                models = settings.Providers.Where(p => chat && p.Enabled && (p.Network != "cloud" || cloud)).SelectMany(p => p.Models.Where(m => m.Enabled).Select(m => new { key = AiSettingsService.Key(p, m), provider = p.Name, m.Name, m.Tools, p.Network })) });
        });
        api.MapGet("/settings", async (AiSettingsService service, CancellationToken ct) => Results.Ok(await service.ReadAsync(ct)));
        api.MapPut("/settings", async (AiSettingsInput input, AiSettingsService service, CancellationToken ct) => Results.Ok(await service.SaveAsync(input, ct)));
        api.MapPost("/providers/{id:guid}/test", async (Guid id, AiTestInput input, AiSettingsService service, CancellationToken ct) => Results.Ok(await service.TestAsync(id, input, ct)));
        api.MapGet("/conversations", async (AiChatService service, CancellationToken ct) => Results.Ok(await service.ListAsync(ct)));
        api.MapPost("/conversations", async (AiCreateInput input, AiChatService service, CancellationToken ct) => Results.Ok(await service.CreateAsync(input, ct)));
        api.MapGet("/conversations/{id:guid}", async (Guid id, AiChatService service, CancellationToken ct) => Results.Ok(await service.FindAsync(id, ct)));
        api.MapDelete("/conversations/{id:guid}", async (Guid id, AiChatService service, CancellationToken ct) => { await service.DeleteAsync(id, ct); return Results.NoContent(); });
        api.MapPost("/conversations/{id:guid}/messages", async (Guid id, AiSendInput input, AiChatService service, HttpContext context) => await service.SendAsync(id, input, context));
    }
}
