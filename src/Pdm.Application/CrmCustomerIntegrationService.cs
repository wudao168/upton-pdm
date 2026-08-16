using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed class CrmCustomerIntegrationService(
    IPdmRepository repository,
    ICrmCustomerClient crmClient,
    ICrmCredentialProtector credentialProtector,
    TimeProvider timeProvider)
{
    private static readonly SemaphoreSlim SyncGate = new(1, 1);

    public async Task<CrmIntegrationSettings> GetSettingsAsync(UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(role, cancellationToken);
        return ToSettings(await repository.GetCrmIntegrationConfigurationAsync(cancellationToken));
    }

    public async Task<CrmIntegrationSettings> UpdateSettingsAsync(
        string baseUrl,
        string username,
        string? password,
        bool autoSyncEnabled,
        int autoSyncIntervalMinutes,
        string actor,
        UserRole role,
        CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(role, cancellationToken);
        var normalizedBaseUrl = NormalizeBaseUrl(baseUrl);
        var normalizedUsername = username?.Trim() ?? string.Empty;
        if (normalizedUsername.Length is < 1 or > 100)
            throw new PdmRuleException("CRM集成账号不能为空且不能超过100个字符。");

        var existing = await repository.GetCrmIntegrationConfigurationAsync(cancellationToken);
        var passwordCiphertext = existing.PasswordCiphertext;
        if (!string.IsNullOrEmpty(password))
        {
            if (password.Length > 500) throw new PdmRuleException("CRM集成密码不能超过500个字符。");
            passwordCiphertext = credentialProtector.Protect(password);
        }
        if (string.IsNullOrWhiteSpace(passwordCiphertext))
            throw new PdmRuleException("首次配置CRM连接时必须填写集成账号密码。");
        if (autoSyncIntervalMinutes is < 5 or > 10_080)
            throw new PdmRuleException("CRM自动同步间隔必须在5分钟到7天之间。");

        var saved = await repository.SaveCrmIntegrationConfigurationAsync(
            existing with
            {
                BaseUrl = normalizedBaseUrl,
                Username = normalizedUsername,
                PasswordCiphertext = passwordCiphertext,
                AutoSyncEnabled = autoSyncEnabled,
                AutoSyncIntervalMinutes = autoSyncIntervalMinutes
            },
            actor,
            cancellationToken);
        await AuditAsync(actor, "crm.integration.update", "CrmIntegration", "crm", $"地址：{saved.BaseUrl}；账号：{saved.Username}；自动同步：{(saved.AutoSyncEnabled ? "启用" : "关闭")}；间隔：{saved.AutoSyncIntervalMinutes}分钟", cancellationToken);
        return ToSettings(saved);
    }

    public async Task<CrmConnectionTestResult> TestConnectionAsync(
        string actor,
        UserRole role,
        CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(role, cancellationToken);
        var batch = await LoadCustomersAsync(cancellationToken);
        var testedAt = timeProvider.GetUtcNow();
        await AuditAsync(actor, "crm.connection.test", "CrmIntegration", "crm", $"连接成功；可读取客户{batch.Customers.Count}个；跳过无效数据{batch.SkippedCount}条", cancellationToken);
        return new(batch.Customers.Count, batch.SkippedCount, testedAt);
    }

    public async Task<CrmCustomerSyncResult> SyncCustomersAsync(
        string actor,
        UserRole role,
        CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(role, cancellationToken);
        await SyncGate.WaitAsync(cancellationToken);
        try
        {
            return await SyncCustomersUnlockedAsync(actor, cancellationToken);
        }
        finally
        {
            SyncGate.Release();
        }
    }

    public async Task<CrmCustomerSyncResult?> TrySyncAutomaticallyAsync(CancellationToken cancellationToken)
    {
        await SyncGate.WaitAsync(cancellationToken);
        try
        {
            var configuration = await repository.GetCrmIntegrationConfigurationAsync(cancellationToken);
            if (!configuration.AutoSyncEnabled) return null;

            var lastActivity = new[] { configuration.LastSyncAt, configuration.LastAutoSyncAttemptAt }
                .Where(value => value.HasValue)
                .Select(value => value!.Value)
                .DefaultIfEmpty(DateTimeOffset.MinValue)
                .Max();
            var now = timeProvider.GetUtcNow();
            if (lastActivity != DateTimeOffset.MinValue
                && now < lastActivity.AddMinutes(configuration.AutoSyncIntervalMinutes)) return null;

            try
            {
                var result = await SyncCustomersUnlockedAsync("system:crm-auto-sync", cancellationToken, configuration);
                await repository.RecordCrmAutomaticSyncAttemptAsync(now, null, cancellationToken);
                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                var error = exception.Message.Length > 1000 ? exception.Message[..1000] : exception.Message;
                await repository.RecordCrmAutomaticSyncAttemptAsync(now, error, cancellationToken);
                throw;
            }
        }
        finally
        {
            SyncGate.Release();
        }
    }

    private async Task<CrmCustomerSyncResult> SyncCustomersUnlockedAsync(
        string actor,
        CancellationToken cancellationToken,
        CrmIntegrationConfiguration? configuration = null)
    {
        var batch = await LoadCustomersAsync(configuration, cancellationToken);
        var syncedAt = timeProvider.GetUtcNow();
        var savedCustomers = await repository.ApplyCrmCustomerSyncAsync(batch.Customers, syncedAt, cancellationToken);
        var settings = ToSettings(await repository.GetCrmIntegrationConfigurationAsync(cancellationToken));
        await AuditAsync(actor, "crm.customer.sync", nameof(PdmCustomer), "crm", $"从CRM同步客户{batch.Customers.Count}个；跳过无效数据{batch.SkippedCount}条", cancellationToken);
        return new(batch.Customers.Count, batch.SkippedCount, syncedAt, settings, savedCustomers);
    }

    private async Task<CrmCustomerBatch> LoadCustomersAsync(CancellationToken cancellationToken) =>
        await LoadCustomersAsync(null, cancellationToken);

    private async Task<CrmCustomerBatch> LoadCustomersAsync(CrmIntegrationConfiguration? savedConfiguration, CancellationToken cancellationToken)
    {
        var configuration = savedConfiguration ?? await repository.GetCrmIntegrationConfigurationAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(configuration.BaseUrl)
            || string.IsNullOrWhiteSpace(configuration.Username)
            || string.IsNullOrWhiteSpace(configuration.PasswordCiphertext))
            throw new PdmRuleException("请先保存完整的CRM连接配置。");

        string password;
        try
        {
            password = credentialProtector.Unprotect(configuration.PasswordCiphertext);
        }
        catch (Exception exception) when (exception is not PdmRuleException)
        {
            throw new PdmRuleException("CRM连接密码无法解密，请重新输入并保存密码。");
        }
        return await crmClient.ListCustomersAsync(configuration.BaseUrl, configuration.Username, password, cancellationToken);
    }

    private async Task RequirePermissionAsync(UserRole role, CancellationToken cancellationToken)
    {
        if (!await repository.HasRolePermissionAsync(role, PermissionCodes.CustomerSettingsManage, cancellationToken))
            throw new UnauthorizedAccessException("当前用户没有配置CRM客户同步的权限。");
    }

    private Task AuditAsync(string actor, string action, string entityType, string entityId, string detail, CancellationToken cancellationToken) =>
        repository.AppendAuditAsync(new AuditEntry(Guid.NewGuid(), timeProvider.GetUtcNow(), actor, action, entityType, entityId, detail), cancellationToken);

    private static CrmIntegrationSettings ToSettings(CrmIntegrationConfiguration configuration) => new(
        configuration.BaseUrl,
        configuration.Username,
        !string.IsNullOrWhiteSpace(configuration.PasswordCiphertext),
        configuration.AutoSyncEnabled,
        configuration.AutoSyncIntervalMinutes,
        configuration.LastSyncAt,
        configuration.LastSyncCount,
        configuration.LastAutoSyncAttemptAt,
        configuration.LastAutoSyncError);

    private static string NormalizeBaseUrl(string value)
    {
        var normalized = value?.Trim().TrimEnd('/') ?? string.Empty;
        if (normalized.Length is < 1 or > 500
            || !Uri.TryCreate(normalized, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
            throw new PdmRuleException("CRM服务地址必须是有效的HTTP或HTTPS地址，例如 http://127.0.0.1:8080。");
        return normalized;
    }
}
