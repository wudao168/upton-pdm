using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed partial class PdmWorkflowService
{
    // Workflow services are request-scoped: one settings read for a whole batch.
    private Task<ArchiveWarningPolicy>? archiveWarningPolicyRead;
    public async Task<ArchiveWarningPolicy> SaveArchiveWarningPolicyAsync(ArchiveWarningPolicy input,
        string actor, UserRole role, CancellationToken ct)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.StorageSettingsManage, ct);
        try { ArchiveWarningPolicy.Validate(input); }
        catch (ArgumentException error) { throw new PdmRuleException(error.Message); }
        var result = await repository.SaveArchiveWarningPolicyAsync(input, ct);
        await AuditAsync(actor, "system.archive-warning-policy.update", nameof(ArchiveWarningPolicy), "global",
            $"策略版本{result.Revision}；全部警告阻止={result.BlockAll}；阻止项目={string.Join("、", result.BlockingCodes)}", ct);
        return result;
    }

    private async Task ValidateArchiveWarningsAsync(IEnumerable<string>? warnings, CancellationToken ct)
    {
        var policy = await (archiveWarningPolicyRead ??= repository.GetArchiveWarningPolicyAsync(ct));
        var blocked = (warnings ?? []).Where(message => policy.Blocks(ArchiveWarningPolicy.CodeOf(message))).Distinct().ToArray();
        if (blocked.Length != 0)
            throw new PdmRuleException($"管理员存档警告策略（版本{policy.Revision}）阻止提交：\n" + string.Join("\n", blocked));
    }

    private Task ValidateArchiveReferenceWarningsAsync(DocumentReferenceNode root, CancellationToken ct)
    {
        var warnings = (root.ReferenceWarnings ?? []).ToList();
        foreach (var node in ArchiveBatchRules.References(root).Prepend(root))
            if (ArchiveDeferredReferences.IsUnverified(node))
                warnings.Add(ArchiveWarningPolicy.Format("ReferenceTypeUnknown", "引用类型待核对：" + node.FileName));
        return ValidateArchiveWarningsAsync(warnings, ct);
    }
}
