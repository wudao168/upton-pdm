using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed partial class PdmWorkflowService
{
    public async Task<DocumentOperationRuleSettings> SetDocumentOperationRuleAsync(string ruleId, bool enabled,
        long expectedRevision, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.StorageSettingsManage, cancellationToken);
        var settings = await repository.SetDocumentOperationRuleAsync(ruleId, enabled, expectedRevision, cancellationToken);
        await AuditAsync(actor, "system.document-operation-rule.update", nameof(DocumentOperationRuleSettings), ruleId,
            $"{(enabled ? "启用" : "停用")}规则拦截；自动处理及诊断提示保留；设置版本{settings.Revision}", cancellationToken);
        return settings;
    }
}
