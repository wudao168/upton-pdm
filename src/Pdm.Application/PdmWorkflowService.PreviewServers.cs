using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed partial class PdmWorkflowService
{
    public Task<PreviewAgentServer> SavePreviewServerAsync(string? originalUrl, string? name, string? agentUrl,
        string? agentToken, int timeoutMinutes, bool disabled, string actor, UserRole role, CancellationToken ct) =>
        ChangePreviewServerAsync(originalUrl, name, agentUrl, agentToken, timeoutMinutes, disabled, actor, role, false, ct);

    private async Task<PreviewAgentServer> ChangePreviewServerAsync(string? originalUrl, string? name, string? agentUrl,
        string? agentToken, int timeoutMinutes, bool disabled, string actor, UserRole role, bool registering, CancellationToken ct)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.StorageSettingsManage, ct);
        var url = NormalizePreviewServerAddress(agentUrl);
        var displayName = name?.Trim() ?? string.Empty;
        if (registering && displayName.Length == 0) displayName = url;
        if (displayName.Length is 0 or > 100 || displayName.Any(char.IsControl))
            throw new PdmRuleException("转图服务器名称必须为1到100个字符，且不能包含控制字符。");
        if (timeoutMinutes is < 1 or > 120)
            throw new PdmRuleException("图纸转换超时必须设置为1到120分钟。");
        var token = NormalizePreviewServerToken(agentToken);
        var companyId = TenantContext.CompanyId;
        var existingUrl = registering ? url : originalUrl;

        var saved = await repository.UpdatePreviewConversionAsync(current =>
        {
            var servers = current.GetConfiguredServers().ToList();
            var index = existingUrl is null ? -1 : servers.FindIndex(server => PreviewServerAddressMatches(server.AgentUrl, existingUrl));
            var previous = index >= 0 ? servers[index] : null;
            if (originalUrl is not null && previous is null
                || previous is not null && !previous.IsAvailableForCompany(companyId))
                throw new PdmNotFoundException("转图服务器不存在或已变更，请刷新列表。");
            if (servers.Where((_, position) => position != index).Any(server => PreviewServerAddressMatches(server.AgentUrl, url)))
                throw new PdmConflictException("已配置相同地址的转图服务器。");
            var changed = new PreviewAgentServer(displayName, url, token ?? previous?.AgentToken ?? string.Empty,
                timeoutMinutes, previous is null ? companyId : previous.CompanyId)
            {
                Disabled = registering && previous is not null ? previous.Disabled : disabled
            };
            if (index >= 0) servers[index] = changed;
            else servers.Add(changed);
            return SetConfiguredPreviewServers(current, servers);
        }, ct);
        var result = saved.Servers.Single(server => PreviewServerAddressMatches(server.AgentUrl, url));
        await AuditAsync(actor, registering ? "system.preview-agent.register" : originalUrl is null
            ? "system.preview-agent.add" : "system.preview-agent.update", nameof(PdmSystemSettings), "preview-conversion",
            $"转图服务器：{result.Name} · {result.AgentUrl}；超时{result.TimeoutMinutes}分钟；{(result.Disabled ? "停用" : "启用")}", ct);
        return result;
    }

    /// <summary>为现有代理安装程序保留按地址登记的入口。</summary>
    public async Task<PdmSystemSettings> RegisterPreviewAgentAsync(string agentUrl, string agentToken, int timeoutMinutes,
        string actor, UserRole role, CancellationToken ct, string? name = null)
    {
        await ChangePreviewServerAsync(null, name, agentUrl, agentToken, timeoutMinutes, false, actor, role, true, ct);
        return await repository.GetSystemSettingsAsync(ct);
    }

    public async Task<PdmSystemSettings> ClearPreviewAgentAsync(string actor, UserRole role, CancellationToken ct, string? agentUrl = null)
    {
        var conversion = await RemovePreviewServerAsync(string.IsNullOrWhiteSpace(agentUrl) ? null : agentUrl,
            actor, role, ct, requireExisting: false);
        return (await repository.GetSystemSettingsAsync(ct)) with { PreviewConversion = conversion };
    }

    public async Task<PreviewConversionSettings> RemovePreviewServerAsync(string? agentUrl, string actor, UserRole role,
        CancellationToken ct, bool requireExisting = true)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.StorageSettingsManage, ct);
        var companyId = TenantContext.CompanyId;
        var saved = await repository.UpdatePreviewConversionAsync(current =>
        {
            var servers = current.GetConfiguredServers();
            var remaining = servers.Where(server => !server.IsAvailableForCompany(companyId)
                || agentUrl is not null && !PreviewServerAddressMatches(server.AgentUrl, agentUrl)).ToArray();
            if (remaining.Length < servers.Count) return SetConfiguredPreviewServers(current, remaining);
            if (requireExisting) throw new PdmNotFoundException("转图服务器不存在或已变更，请刷新列表。");
            return current;
        }, ct);
        await AuditAsync(actor, "system.preview-agent.unregister", nameof(PdmSystemSettings), "preview-conversion",
            $"已处理转图服务器移除；当前可见服务器 {saved.GetConfiguredServers().Count(server => server.IsAvailableForCompany(companyId))} 台", ct);
        return saved;
    }

    public static string NormalizePreviewServerAddress(string? value)
    {
        var address = value?.Trim().TrimEnd('/') ?? string.Empty;
        if (address.Length > 500 || address.Any(char.IsControl)
            || !Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || string.IsNullOrWhiteSpace(uri.Host) || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new PdmRuleException("请输入完整的 HTTP 或 HTTPS 转图服务器地址，地址不能包含账号、查询参数或片段。");
        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    public static bool PreviewServerAddressMatches(string? saved, string? requested)
    {
        static string Key(string? value)
        {
            var trimmed = value?.Trim().TrimEnd('/') ?? string.Empty;
            return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
                ? uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped).TrimEnd('/') : trimmed;
        }
        return string.Equals(Key(saved), Key(requested), StringComparison.OrdinalIgnoreCase);
    }

    public static string? NormalizePreviewServerToken(string? value)
    {
        var token = value?.Trim();
        if (token is not null && (token.Length > 200 || token.Any(char.IsControl)))
            throw new PdmRuleException("转图服务器访问令牌不能超过200个字符，且不能包含控制字符。");
        return token;
    }

    private static PreviewConversionSettings SetConfiguredPreviewServers(PreviewConversionSettings current, IReadOnlyList<PreviewAgentServer> servers)
    {
        // 保留旧代理安装程序和单服务器读取入口使用的兼容字段。
        var primary = servers.FirstOrDefault(server => !server.Disabled) ?? servers.FirstOrDefault();
        return current with
        {
            Mode = PreviewConversionMode.Remote,
            AgentUrl = primary?.AgentUrl ?? string.Empty,
            AgentToken = primary?.AgentToken ?? string.Empty,
            TimeoutMinutes = primary?.TimeoutMinutes ?? PreviewConversionSettings.Default.TimeoutMinutes,
            Servers = servers,
            AwaitingRegistration = primary is null,
            LocalExecutionAllowed = null
        };
    }
}
