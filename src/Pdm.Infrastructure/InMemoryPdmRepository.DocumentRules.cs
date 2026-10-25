using Upton.Pdm.Domain;
using Upton.Pdm.Application;

namespace Upton.Pdm.Infrastructure;

public sealed partial class InMemoryPdmRepository
{
    private readonly object documentRuleSettingsSync = new();
    private DocumentOperationRuleSettings documentRuleSettings = DocumentOperationRuleSettings.Default;

    public Task<DocumentOperationRuleSettings> GetDocumentOperationRulesAsync(CancellationToken cancellationToken)
    {
        lock (documentRuleSettingsSync) return Task.FromResult(documentRuleSettings.Normalize());
    }

    public Task<DocumentOperationRuleSettings> SetDocumentOperationRuleAsync(string ruleId, bool enabled, long expectedRevision, CancellationToken cancellationToken)
    {
        if (!DocumentOperationRuleIds.Contains(ruleId)) throw new PdmRuleException("图档规则编号不存在，请刷新规则列表。");
        if (enabled && !DocumentOperationRuleCatalog.CanBlock(ruleId)) throw new PdmRuleException("该项已改为自动处理和提示，不再提供拦截开关。");
        lock (documentRuleSettingsSync)
        {
            if (documentRuleSettings.Revision != expectedRevision) throw new PdmConflictException("规则设置已被其他人修改，请刷新后重试。");
            var disabled = documentRuleSettings.Normalize().DisabledRuleIds.ToHashSet(StringComparer.Ordinal);
            if (enabled) disabled.Remove(ruleId); else disabled.Add(ruleId);
            documentRuleSettings = new(documentRuleSettings.Revision + 1, disabled.OrderBy(id => id, StringComparer.Ordinal).ToArray());
            return Task.FromResult(documentRuleSettings with { DisabledRuleIds = documentRuleSettings.DisabledRuleIds.ToArray() });
        }
    }
}
