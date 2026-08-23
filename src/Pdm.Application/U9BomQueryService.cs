using System.Text.Json;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed class U9BomQueryService(
    IMaterialRepository materials,
    IPdmRepository repository,
    IU9BomQueryClient client,
    IU9SecretProtector secretProtector,
    TimeProvider timeProvider)
{
    public async Task<U9BomQueryExecution> QueryAsync(
        U9BomQueryCommand command,
        string actor,
        UserRole role,
        CancellationToken cancellationToken)
    {
        if (!await repository.HasUserPermissionAsync(actor, role, PermissionCodes.StorageSettingsManage, cancellationToken))
            throw new UnauthorizedAccessException("当前角色无权查询U9C BOM。");

        var itemCode = Required(command.ItemCode, "母件料号");
        var versionCode = Normalize(command.BomVersionCode);
        var productUomCode = Normalize(command.ProductUomCode);
        if (command.Lot is < 0) throw new PdmRuleException("批量不能小于0。");

        var configuration = await materials.GetIntegrationConfigurationAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(configuration.ClientSecretCiphertext))
            throw new PdmRuleException("请先保存U9C应用密钥，再查询BOM。");

        var payload = BuildPayload(
            Required(configuration.OrganizationCode, "U9C组织编码"),
            itemCode,
            versionCode,
            command.Lot,
            productUomCode);
        var authentication = await client.AuthenticateAsync(new(
            configuration.BaseUrl,
            configuration.EnterpriseCode,
            configuration.OrganizationCode,
            configuration.UserCode,
            configuration.ClientId,
            secretProtector.Unprotect(configuration.ClientSecretCiphertext)), cancellationToken);
        var result = await client.QueryBomsAsync(configuration.BaseUrl, configuration.BomQueryPath, authentication.Token, payload, cancellationToken);
        if (result.ResponseCode != 0)
            throw new PdmRuleException($"U9C BOM查询失败（ResCode={result.ResponseCode}）：{result.ResponseMessage ?? "未返回错误说明"}。");

        var queriedAt = timeProvider.GetUtcNow();
        await repository.AppendAuditAsync(new AuditEntry(
            Guid.NewGuid(), queriedAt, actor, "u9.bom.query", nameof(U9BomReference), itemCode,
            $"只读查询U9C BOM：母件{itemCode}；版本{versionCode ?? "全部"}；结果{result.Boms.Count}条；未执行U9C写入。"), cancellationToken);
        return new U9BomQueryExecution(configuration.BomQueryPath, payload, queriedAt, result);
    }

    private static string BuildPayload(
        string organizationCode,
        string itemCode,
        string? versionCode,
        int? lot,
        string? productUomCode)
    {
        var row = new Dictionary<string, object?>
        {
            ["Org"] = Archive(organizationCode),
            ["ItemMaster"] = Archive(itemCode)
        };
        if (versionCode is not null) row["BOMVersionCode"] = versionCode;
        if (lot.HasValue) row["Lot"] = lot.Value;
        if (productUomCode is not null) row["ProductUOM"] = Archive(productUomCode);
        return JsonSerializer.Serialize(new[] { row });
    }

    private static Dictionary<string, string> Archive(string code) => new() { ["Code"] = code };

    private static string Required(string? value, string field) =>
        Normalize(value) ?? throw new PdmRuleException($"{field}不能为空。");

    private static string? Normalize(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
