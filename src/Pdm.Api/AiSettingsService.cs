using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.AI;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api;

public sealed record AiProviderInput(Guid Id, string Name, string BaseUrl, string Network, string Adapter, string? ApiKey, bool ClearKey, bool Enabled, AiModel[] Models);
public sealed record AiSettingsInput(long Revision, AiProviderInput[] Providers, string? DefaultModel);
public sealed record AiTestInput(string ModelId, bool Tools = false);

public sealed class AiSettingsService(IAiStore store, AiAccess access, IDataProtectionProvider protection, IAiModelClientFactory clients, ILogger<AiSettingsService> log)
{
    public static string Key(AiProvider provider, AiModel model) => provider.Id + ":" + model.Id;
    public async Task<object> ReadAsync(CancellationToken ct) {
        await access.RequireAsync(AiPermissions.Settings, null, ct);
        return View(await store.SettingsAsync(access.Tenant.CompanyId, ct));
    }
    private static object View(AiSettings settings) => new { settings.Revision, settings.DefaultModel,
        providers = settings.Providers.Select(p => new { p.Id, p.Name, p.BaseUrl, p.Network, p.Adapter, p.Enabled, p.Models, hasKey = p.ProtectedKey is not null }) };
    public async Task<object> SaveAsync(AiSettingsInput input, CancellationToken ct)
    {
        await access.RequireAsync(AiPermissions.Settings, null, ct);
        if (input.Providers is null || input.Providers.Length > 20) throw new PdmRuleException("最多配置20个模型服务。");
        var old = await store.SettingsAsync(access.Tenant.CompanyId, ct);
        if (old.Revision != input.Revision) throw new PdmConflictException("AI配置已变化，请刷新后保存。");
        var rows = new List<AiProvider>(); var ids = new HashSet<Guid>();
        foreach (var p in input.Providers) {
            if (p.Id == Guid.Empty || !ids.Add(p.Id) || string.IsNullOrWhiteSpace(p.Name) || p.Name.Length > 60 || p.Adapter is not ("compatible" or "deepseek")) throw new PdmRuleException("请检查服务名称、唯一标识及接口类型。");
            AiEndpointPolicy.Validate(p.BaseUrl ?? "", p.Network);
            if (p.Models is null || p.Models.Length is < 1 or > 20 || p.Models.Select(m => m.Id).Distinct().Count() != p.Models.Length) throw new PdmRuleException("每个服务须配置1至20个模型，模型ID不能重复。");
            foreach (var model in p.Models) {
                if (string.IsNullOrWhiteSpace(model.Id) || model.Id.Length > 150 || string.IsNullOrWhiteSpace(model.Name) || model.Name.Length > 80
                    || model.ContextTokens is < 4096 or > 2000000 || model.OutputTokens is < 128 or > 8192 || model.OutputTokens > model.ContextTokens / 2)
                    throw new PdmRuleException("请检查模型ID、名称及容量；上下文至少4096，输出上限128至8192且不超过上下文的一半。");
            }
            var existing = old.Providers.FirstOrDefault(x => x.Id == p.Id);
            if (p.ApiKey?.Length > 4096 || p.ApiKey?.Any(char.IsControl) == true) throw new PdmRuleException("API密钥格式不正确，请重新填写。");
            if (existing?.ProtectedKey is not null && existing.BaseUrl.TrimEnd('/') != p.BaseUrl!.TrimEnd('/') && string.IsNullOrWhiteSpace(p.ApiKey) && !p.ClearKey)
                throw new PdmRuleException("更改API地址时请重新填写或清除密钥，避免将已有密钥发送至其他服务。");
            var key = p.ClearKey ? null : string.IsNullOrWhiteSpace(p.ApiKey) ? existing?.ProtectedKey : protection.CreateProtector("UPLM.AI.Keys.v1").Protect(p.ApiKey.Trim());
            rows.Add(new(p.Id, p.Name.Trim(), p.BaseUrl!.Trim().TrimEnd('/'), p.Network, p.Adapter, key, p.Enabled, p.Models));
        }
        if (input.DefaultModel is not null && !rows.Any(p => p.Enabled && p.Models.Any(m => m.Enabled && Key(p, m) == input.DefaultModel)))
            throw new PdmRuleException("默认模型必须是已启用的模型。");
        await store.SaveSettingsAsync(access.Tenant.CompanyId, new(input.Revision, rows.ToArray(), input.DefaultModel), ct);
        log.LogInformation("AI settings updated company {Company} user {User} revision {Revision}", access.Tenant.CompanyId, access.Tenant.UserId, input.Revision + 1);
        return View(await store.SettingsAsync(access.Tenant.CompanyId, ct));
    }
    public async Task<(AiSettings Settings, AiProvider Provider, AiModel Model)> ResolveAsync(string key, CancellationToken ct)
    {
        var settings = await store.SettingsAsync(access.Tenant.CompanyId, ct);
        foreach (var provider in settings.Providers.Where(p => p.Enabled)) foreach (var model in provider.Models.Where(m => m.Enabled))
            if (Key(provider, model) == key) {
                if (provider.Network == "cloud") await access.RequireAsync(AiPermissions.Cloud, null, ct);
                return (settings, provider, model);
            }
        throw new PdmRuleException("模型未配置、已停用或已删除，请选择其他模型。");
    }
    public async Task<object> TestAsync(Guid providerId, AiTestInput input, CancellationToken ct)
    {
        await access.RequireAsync(AiPermissions.Settings, null, ct);
        var (_, provider, model) = await ResolveAsync(providerId + ":" + input.ModelId, ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(45));
        var called = false;
        using var client = new FunctionInvokingChatClient(clients.Create(provider, model)) { MaximumIterationsPerRequest = 3 };
        var options = new ChatOptions { MaxOutputTokens = 128, Tools = input.Tools ? [AIFunctionFactory.Create(() => { called = true; return "连接验证成功"; }, "connection_check", "测试连接时必须调用的只读工具")] : null };
        var text = new System.Text.StringBuilder();
        try {
            await foreach (var update in client.GetStreamingResponseAsync([new(ChatRole.User, input.Tools ? "请先调用connection_check工具，再用中文简短回复验证结果。" : "请只回复：连接成功。")], options, timeout.Token)) text.Append(update.Text);
            if (text.Length == 0 || input.Tools && !called) throw new PdmRuleException(input.Tools ? "模型没有完成工具调用，请关闭业务工具能力或检查模型配置。" : "模型未返回文字，请核对模型名称及兼容接口。");
            return new { success = true, message = input.Tools ? "流式回复与工具调用验证通过。" : "认证与流式回复验证通过。", tools = called };
        } catch (Exception ex) when (ex is not PdmRuleException) {
            log.LogWarning("AI connection test failed provider {Provider} exception {Type}", providerId, ex.GetType().Name);
            throw new PdmRuleException(AiChatService.FriendlyError(ex));
        }
    }
}
