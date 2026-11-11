using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed partial class PdmWorkflowService
{
    private static bool IsDrawingBomRelease(ReleaseScope scope) =>
        scope is ReleaseScope.NonStandardWithDrawing or ReleaseScope.NonStandardSupplement;

    private async Task ValidateRemovedBomBaselineAsync(ReleaseScope scope, IReadOnlyList<Guid>? confirmedRemovedIds,
        Guid projectId, CancellationToken ct, Guid? boundBomVersionId = null)
    {
        if (!IsDrawingBomRelease(scope) || confirmedRemovedIds is not { Count: > 0 }) return;
        var kind = ReleaseScopeBomKind(scope);
        BomVersion? baseline;
        if (boundBomVersionId.HasValue)
        {
            var bound = await repository.FindBomVersionAsync(projectId, boundBomVersionId.Value, ct);
            baseline = bound?.BaseVersionId is Guid baseId
                ? await repository.FindBomVersionAsync(projectId, baseId, ct) : null;
        }
        else baseline = await repository.FindLatestBomVersionAsync(projectId, kind, BomVersionState.Released, ct);
        var ids = confirmedRemovedIds.ToHashSet();
        var removed = baseline is not null && baseline.ProjectId == projectId && baseline.Kind == kind && baseline.State == BomVersionState.Released
            ? baseline.Items.Where(item => ids.Contains(item.Id)).ToArray() : [];
        if (removed.Select(item => item.Id).Distinct().Count() != ids.Count)
            throw new PdmRuleException("确认删除项的CAD来源无法从本次绑定的正式BOM基线核实，请刷新变更范围后重试。");
    }

    private async Task RequireCompletePackageReleaseIndexAsync(ReleasePackage package, CadReferenceSnapshot? snapshot, CancellationToken ct)
    {
        if (package.Scope == ReleaseScope.LegacyCombined) { RequireCompleteReleaseIndex(snapshot); return; }
        if (IsLongLeadScope(package.Scope) || ReleaseScopeBomKind(package.Scope) != BomKind.NonStandard) return;

        var kind = ReleaseScopeBomKind(package.Scope);
        var ids = package.SelectedBomItemIds.ToHashSet();
        var selected = PackageBomSnapshot(package, kind).Where(item => ids.Contains(item.Id)).ToArray();
        if (selected.Select(item => item.Id).Distinct().Count() != ids.Count)
            throw new PdmRuleException("所选物料未完整保存在发布快照中，请刷新发布草稿后重试。");
        var versionId = package.NonStandardBomVersionId;
        await ValidateRemovedBomBaselineAsync(package.Scope, package.ConfirmedRemovedBomItemIds, package.ProjectId, ct, versionId);
    }
}
