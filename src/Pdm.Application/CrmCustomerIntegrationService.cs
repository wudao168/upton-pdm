using System.Text.Json;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed class CrmCustomerIntegrationService(
    IPdmRepository repository,
    IMaterialRepository materials,
    IU9OpenApiClient u9Client,
    IU9SecretProtector secretProtector,
    TimeProvider timeProvider)
{
    private static readonly SemaphoreSlim SyncGate = new(1, 1);
    private const int CustomerPageSize = 1000;
    private const int MaximumCustomerPages = 100;

    public async Task<CrmIntegrationSettings> GetSettingsAsync(string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, cancellationToken);
        var schedule = await repository.GetCrmIntegrationConfigurationAsync(cancellationToken);
        var u9Configuration = await materials.GetIntegrationConfigurationAsync(cancellationToken);
        return ToSettings(schedule, u9Configuration);
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
        await RequirePermissionAsync(actor, role, cancellationToken);
        var u9Configuration = await RequireU9ConfigurationAsync(cancellationToken);
        var existing = await repository.GetCrmIntegrationConfigurationAsync(cancellationToken);
        if (autoSyncIntervalMinutes is < 5 or > 10_080)
            throw new PdmRuleException("U9C客户自动同步间隔必须在5分钟到7天之间。");

        var saved = await repository.SaveCrmIntegrationConfigurationAsync(
            existing with
            {
                BaseUrl = u9Configuration.BaseUrl,
                Username = u9Configuration.UserCode,
                PasswordCiphertext = string.Empty,
                AutoSyncEnabled = autoSyncEnabled,
                AutoSyncIntervalMinutes = autoSyncIntervalMinutes
            },
            actor,
            cancellationToken);
        await AuditAsync(actor, "u9.customer-schedule.update", "U9CustomerIntegration", "u9c", $"复用U9C配置：{u9Configuration.BaseUrl}；账号：{u9Configuration.UserCode}；自动同步：{(saved.AutoSyncEnabled ? "启用" : "关闭")}；间隔：{saved.AutoSyncIntervalMinutes}分钟", cancellationToken);
        return ToSettings(saved, u9Configuration);
    }

    public async Task<CrmConnectionTestResult> TestConnectionAsync(
        string actor,
        UserRole role,
        CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, cancellationToken);
        var batch = await LoadCustomersAsync(cancellationToken);
        var testedAt = timeProvider.GetUtcNow();
        await AuditAsync(actor, "u9.customer.connection.test", "U9CustomerIntegration", "u9c", $"U9C客户参照连接成功；可读取客户{batch.Customers.Count}个；跳过无效数据{batch.SkippedCount}条", cancellationToken);
        return new(batch.Customers.Count, batch.SkippedCount, testedAt);
    }

    public async Task<CrmCustomerSyncResult> SyncCustomersAsync(
        string actor,
        UserRole role,
        CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, cancellationToken);
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
                var result = await SyncCustomersUnlockedAsync("system:u9-customer-auto-sync", cancellationToken, configuration);
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
        var u9Configuration = await materials.GetIntegrationConfigurationAsync(cancellationToken);
        var settings = ToSettings(await repository.GetCrmIntegrationConfigurationAsync(cancellationToken), u9Configuration);
        await AuditAsync(actor, "u9.customer.sync", nameof(PdmCustomer), "u9c", $"从U9C同步客户{batch.Customers.Count}个；跳过无效数据{batch.SkippedCount}条", cancellationToken);
        return new(batch.Customers.Count, batch.SkippedCount, syncedAt, settings, savedCustomers);
    }

    private async Task<CrmCustomerBatch> LoadCustomersAsync(CancellationToken cancellationToken) =>
        await LoadCustomersAsync(null, cancellationToken);

    private async Task<CrmCustomerBatch> LoadCustomersAsync(CrmIntegrationConfiguration? _, CancellationToken cancellationToken)
    {
        var configuration = await RequireU9ConfigurationAsync(cancellationToken);
        string clientSecret;
        try
        {
            clientSecret = secretProtector.Unprotect(configuration.ClientSecretCiphertext);
        }
        catch (Exception exception) when (exception is not PdmRuleException)
        {
            throw new PdmRuleException("U9C应用密钥无法解密，请到料品管理的U9C配置中重新保存。");
        }
        var authentication = await u9Client.AuthenticateAsync(new(
            configuration.BaseUrl,
            configuration.EnterpriseCode,
            configuration.OrganizationCode,
            configuration.UserCode,
            configuration.ClientId,
            clientSecret), cancellationToken);

        var customers = new Dictionary<string, CrmCustomerRecord>(StringComparer.OrdinalIgnoreCase);
        var skippedCount = 0;
        for (var pageIndex = 0; pageIndex < MaximumCustomerPages; pageIndex++)
        {
            var payload = JsonSerializer.Serialize(new
            {
                ReferenceCode = "Customer",
                ReferenceEntityFullName = "UFIDA.U9.CBO.SCM.Customer.Customer",
                ReferenceDefaultFilter = string.Empty,
                Transclude = string.Empty,
                TargetOrgCode = configuration.OrganizationCode,
                PageIndex = pageIndex,
                PageSize = CustomerPageSize,
                Filter = string.Empty,
                FilterObjectXML = string.Empty
            });
            var page = await u9Client.QueryCustomerReferencesAsync(
                configuration.BaseUrl,
                authentication.Token,
                payload,
                cancellationToken);
            if (page.ResponseCode != 0)
                throw new PdmRuleException($"U9C客户参照查询失败（ResCode={page.ResponseCode}）：{page.ResponseMessage ?? "未返回错误说明"}。");

            skippedCount += Math.Max(0, page.RawCount - page.Customers.Count);
            var countBeforePage = customers.Count;
            foreach (var customer in page.Customers)
            {
                var code = customer.Code.Trim();
                var name = customer.Name.Trim();
                if (!customers.TryAdd(code, new(code, name))) skippedCount++;
            }
            if (page.RawCount == 0 || page.RawCount < CustomerPageSize || customers.Count == countBeforePage) break;
        }
        if (customers.Count == 0)
            throw new PdmRuleException("U9C客户参照未返回任何有效的客户编码和名称，本次未更新PDM客户目录。");
        return new(customers.Values.OrderBy(customer => customer.Code, StringComparer.OrdinalIgnoreCase).ToArray(), skippedCount);
    }

    private async Task<U9MaterialIntegrationConfiguration> RequireU9ConfigurationAsync(CancellationToken cancellationToken)
    {
        var configuration = await materials.GetIntegrationConfigurationAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(configuration.BaseUrl)
            || string.IsNullOrWhiteSpace(configuration.EnterpriseCode)
            || string.IsNullOrWhiteSpace(configuration.OrganizationCode)
            || string.IsNullOrWhiteSpace(configuration.UserCode)
            || string.IsNullOrWhiteSpace(configuration.ClientId)
            || string.IsNullOrWhiteSpace(configuration.ClientSecretCiphertext))
            throw new PdmRuleException("请先到料品管理的U9C配置中保存完整的OAuth连接参数。");
        return configuration;
    }

    private async Task RequirePermissionAsync(string actor, UserRole role, CancellationToken cancellationToken)
    {
        if (!await repository.HasUserPermissionAsync(actor, role, PermissionCodes.CustomerSettingsManage, cancellationToken))
            throw new UnauthorizedAccessException("当前用户没有配置U9C客户同步的权限。");
    }

    private Task AuditAsync(string actor, string action, string entityType, string entityId, string detail, CancellationToken cancellationToken) =>
        repository.AppendAuditAsync(new AuditEntry(Guid.NewGuid(), timeProvider.GetUtcNow(), actor, action, entityType, entityId, detail), cancellationToken);

    private static CrmIntegrationSettings ToSettings(CrmIntegrationConfiguration schedule, U9MaterialIntegrationConfiguration configuration) => new(
        configuration.BaseUrl,
        configuration.UserCode,
        !string.IsNullOrWhiteSpace(configuration.ClientSecretCiphertext),
        schedule.AutoSyncEnabled,
        schedule.AutoSyncIntervalMinutes,
        schedule.LastSyncAt,
        schedule.LastSyncCount,
        schedule.LastAutoSyncAttemptAt,
        schedule.LastAutoSyncError);
}
