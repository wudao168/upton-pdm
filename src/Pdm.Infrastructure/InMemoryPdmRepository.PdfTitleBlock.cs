using Upton.Pdm.Domain;
using Upton.Pdm.Application;

namespace Upton.Pdm.Infrastructure;

public sealed partial class InMemoryPdmRepository
{
    public Task<ReleasePackage> SaveReleasePdfTitleBlockAsync(Guid packageId, PdfTitleBlockSnapshot snapshot, CancellationToken ct)
    {
        lock (gate)
        {
            var package = packages.GetValueOrDefault(packageId) ?? throw new PdmNotFoundException("发布包不存在。");
            var restoreEmpty = ReleasePreviewPolicy.DeliversFrozenDrawings(package) && snapshot.QuantitiesFrozen && snapshot.Drawings.Count > 0
                && package.PdfTitleBlockSnapshot is { Enabled: true, Drawings.Count: 0 }
                && package.State is ReleasePackageState.Published or ReleasePackageState.Publishing;
            if (!restoreEmpty && package.State is not (ReleasePackageState.Draft or ReleasePackageState.Rejected)
                && !(package.State == ReleasePackageState.Publishing && package.PdfTitleBlockSnapshot is { QuantitiesFrozen: false } && snapshot.QuantitiesFrozen))
                throw new PdmConflictException("PDF填写快照已冻结或发布状态已变化，请刷新后重试。");
            packages[packageId] = package with { PdfTitleBlockSnapshot = snapshot };
            return Task.FromResult(packages[packageId]);
        }
    }
}
