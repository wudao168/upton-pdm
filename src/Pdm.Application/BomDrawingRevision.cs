using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public static class BomDrawingRevision
{
    public static bool SamePublishedSource(BomItem item, BomItem frozen) => !frozen.IsManuallyExcluded && !frozen.IsReleaseExcluded && !frozen.IsPendingClassification
        && BomReleaseAggregation.MaterialKey(item) == BomReleaseAggregation.MaterialKey(frozen)
        && (item.Id==frozen.Id || item.ReleaseTrackingId!=Guid.Empty && (item.ReleaseTrackingId==frozen.Id || item.ReleaseTrackingId==frozen.ReleaseTrackingId)
            || frozen.ReleaseTrackingId!=Guid.Empty && item.Id==frozen.ReleaseTrackingId
            || (item.SummaryItemIds ?? []).Append(item.Id).Intersect((frozen.SummaryItemIds ?? []).Append(frozen.Id)).Any());

    public static string? PublishedBomVersion(BomItem item, IReadOnlyList<ReleasePackage> packages, IReadOnlyList<BomVersion> versions)
    {
        if(item.Kind is not (BomKind.Standard or BomKind.NonStandard or BomKind.Electrical))return null;
        static string Label(string value)=>System.Text.RegularExpressions.Regex.Replace(value.Trim(),@"-B(?=\d+$)","-V");
        foreach(var package in packages.Where(package=>package.State==ReleasePackageState.Published).OrderByDescending(package=>package.PublishedAt??package.CreatedAt))
        {
            var snapshot=item.Kind==BomKind.Standard?package.StandardBomSnapshot:item.Kind==BomKind.Electrical?package.ElectricalBomSnapshot:package.NonStandardBomSnapshot;
            if(!snapshot.Any(frozen=>SamePublishedSource(item,frozen)))continue;
            var id=item.Kind==BomKind.Standard?package.StandardBomVersionId:item.Kind==BomKind.Electrical?package.ElectricalBomVersionId:package.NonStandardBomVersionId;
            var revision=item.Kind==BomKind.Standard?package.StandardBomRevision:item.Kind==BomKind.Electrical?package.ElectricalBomRevision:package.NonStandardBomRevision;
            return !string.IsNullOrWhiteSpace(revision)?Label(revision):versions.FirstOrDefault(version=>version.Id==id) is {} version?Label(version.Label):"待核对:"+package.Id;
        }
        var released=versions.Where(version=>version.State==BomVersionState.Released && version.Kind==item.Kind).OrderByDescending(version=>version.VersionNumber)
            .FirstOrDefault(version=>version.Items.Any(frozen=>SamePublishedSource(item,frozen)));
        return released is null?null:Label(released.Label);
    }
    public static void ValidateDrawingProperties(DocumentVersion version, string targetRevision, string fileName)
    {
        foreach (var property in version.PropertySnapshot.Where(property =>
                     !property.Key.StartsWith(CadPropertyCardSnapshot.ScopePrefix, StringComparison.Ordinal)
                     && property.Key.Split('/').Last() is "版次" or "版本"))
            if (!string.IsNullOrWhiteSpace(property.Value)
                // 历史图框的Rev01等数字标记属于图框版本，不参与UPLM的A/B发布版次校验。
                && !System.Text.RegularExpressions.Regex.IsMatch(property.Value.Trim(), @"^REV[0-9]+$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)
                && !string.Equals(property.Value.Trim(), targetRevision, StringComparison.OrdinalIgnoreCase))
                throw new PdmConflictException($"工程图{fileName}的标题栏版次属性为{property.Value}，与本次发布版本{targetRevision}不一致。请核对历史发布记录，在SolidWorks中修正后重新存档并审核，再创建发布包。");
    }

    public static string ForPackage(BomItem item, ReleasePackage package, bool preparingPublication = false)
    {
        var standard = item.Kind == BomKind.Standard || package.Scope is ReleaseScope.StandardLongLead or ReleaseScope.StandardFormal or ReleaseScope.StandardSupplement;
        var material = standard || item.Kind == BomKind.Electrical || package.Scope is ReleaseScope.ElectricalLongLead or ReleaseScope.ElectricalFormal or ReleaseScope.ElectricalSupplement;
        var bomRevision = standard ? package.StandardBomRevision : package.ElectricalBomRevision;
        var unavailable = material
            ? !string.IsNullOrWhiteSpace(bomRevision)
                ? System.Text.RegularExpressions.Regex.Replace(bomRevision.Trim(), @"-B(?=\d+$)", "-V")
                : package.State == ReleasePackageState.Published || preparingPublication ? "BOM发布版本待核对" : "待发布"
            : "未发布";
        var binding = item.SourceDocumentId.HasValue
            ? package.VersionBindings?.SingleOrDefault(value => value.DocumentId == item.SourceDocumentId)
            : null;
        if (binding is not null)
        {
            if ((package.State == ReleasePackageState.Published || preparingPublication)
                && binding.Publish)
                return DrawingRevision.Display(string.IsNullOrWhiteSpace(binding.TargetRevision)
                    ? DrawingRevision.Letter(binding.Revision) : binding.TargetRevision);
            return DrawingRevision.IsWork(binding.Revision)
                ? string.IsNullOrWhiteSpace(binding.PreviousReleasedRevision) ? unavailable : binding.PreviousReleasedRevision
                : binding.Revision;
        }
        if (!material && item.SourceDocumentId.HasValue && DrawingRevision.IsWork(item.Revision)
            && package.State == ReleasePackageState.Published
            && package.Scope is ReleaseScope.NonStandardWithDrawing or ReleaseScope.NonStandardSupplement or ReleaseScope.LegacyCombined)
            return "历史版次待核对";
        return DrawingRevision.IsWork(item.Revision) || material && string.IsNullOrWhiteSpace(item.Revision)
            ? unavailable : DrawingRevision.Display(item.Revision);
    }

    public static IReadOnlyList<BomItem> ForPackage(IEnumerable<BomItem> items, ReleasePackage package, bool preparingPublication = false) =>
        items.Select(item => item with { DisplayRevision = ForPackage(item, package, preparingPublication) }).ToArray();

    public static async Task<IReadOnlyList<BomItem>> CurrentAsync(IPdmRepository repository,
        IReadOnlyList<BomItem> items, CancellationToken ct)
    {
        var publishedIds = new HashSet<Guid>();
        var packages = new Dictionary<Guid, ReleasePackage[]>();
        var versions = new Dictionary<Guid, BomVersion[]>();
        foreach (var projectId in items.Select(item => item.ProjectId).Distinct())
        {
            packages[projectId] = (await repository.ListReleasePackagesAsync(projectId, ct))
                .Where(package => package.State == ReleasePackageState.Published)
                .OrderByDescending(package => package.PublishedAt ?? package.CreatedAt).ToArray();
            foreach (var package in packages[projectId]) publishedIds.Add(package.Id);
            versions[projectId] = (await repository.ListBomVersionsAsync(projectId, null, ct))
                    .Where(version => version.State == BomVersionState.Released).OrderByDescending(version => version.VersionNumber).ToArray();
        }
        var documentIds = items.Where(item => item.SourceDocumentId.HasValue).Select(item => item.SourceDocumentId!.Value).Distinct().ToArray();
        var revisions = await repository.PublishedDocumentRevisionsAsync(documentIds, publishedIds, ct);
        return items.Select(item =>
        {
            var publication=PublishedBomVersion(item,packages[item.ProjectId],versions[item.ProjectId]);
            item = item with { IsPublishedInBom = publication is not null, BomPublishedVersion=publication };
            var label = item.SourceDocumentId is Guid id ? revisions.GetValueOrDefault(id) ?? "未发布" : ForHistory(item, []);
            if (item.Kind is not (BomKind.Standard or BomKind.Electrical) || !string.IsNullOrWhiteSpace(label) && label is not ("未发布" or "—"))
                return item with { DisplayRevision = label };
            bool Matches(BomItem frozen) => SamePublishedSource(item, frozen);
            var releasedVersions = versions.GetValueOrDefault(item.ProjectId) ?? [];
            foreach (var package in packages[item.ProjectId])
            {
                var snapshot = item.Kind == BomKind.Standard ? package.StandardBomSnapshot : package.ElectricalBomSnapshot;
                var frozen = snapshot.FirstOrDefault(Matches);
                if (frozen is null) continue;
                var versionId = item.Kind == BomKind.Standard ? package.StandardBomVersionId : package.ElectricalBomVersionId;
                var versionLabel = releasedVersions.FirstOrDefault(version => version.Id == versionId)?.Label;
                var resolved = package with { StandardBomRevision = !string.IsNullOrWhiteSpace(package.StandardBomRevision) ? package.StandardBomRevision : item.Kind == BomKind.Standard ? versionLabel : null,
                    ElectricalBomRevision = !string.IsNullOrWhiteSpace(package.ElectricalBomRevision) ? package.ElectricalBomRevision : item.Kind == BomKind.Electrical ? versionLabel ?? "" : "" };
                return item with { DisplayRevision = ForPackage(frozen with { Kind = item.Kind }, resolved) };
            }
            var version = releasedVersions.FirstOrDefault(version => version.Kind == item.Kind && version.Items.Any(Matches));
            if (version is not null)
            {
                var frozen = version.Items.First(Matches);
                label = DrawingRevision.IsWork(frozen.Revision) || string.IsNullOrWhiteSpace(frozen.Revision)
                    ? System.Text.RegularExpressions.Regex.Replace(version.Label, @"-B(?=\d+$)", "-V") : DrawingRevision.Display(frozen.Revision);
            }
            return item with { DisplayRevision = label };
        }).ToArray();
    }

    public static string ForHistory(BomItem item, IReadOnlyList<DocumentVersion> history)
    {
        if (!item.SourceDocumentId.HasValue) return DrawingRevision.IsWork(item.Revision) ? "未发布" : item.Revision;
        var released = history.Where(version => version.DocumentId == item.SourceDocumentId
                && version.Status == DocumentVersionStatus.Released)
            .OrderByDescending(version => version.Revision.BaseRevision).FirstOrDefault();
        return released?.Revision.Display ?? "未发布";
    }
}
