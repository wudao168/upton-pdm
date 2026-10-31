using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public static class BomDrawingRevision
{
    public static void ValidateDrawingProperties(DocumentVersion version, string targetRevision, string fileName)
    {
        foreach (var property in version.PropertySnapshot.Where(property =>
                     !property.Key.StartsWith(CadPropertyCardSnapshot.ScopePrefix, StringComparison.Ordinal)
                     && property.Key.Split('/').Last() is "版次" or "版本"))
            if (!string.IsNullOrWhiteSpace(property.Value)
                && !string.Equals(property.Value.Trim(), targetRevision, StringComparison.OrdinalIgnoreCase))
                throw new PdmConflictException($"工程图{fileName}的标题栏版次属性为{property.Value}，与本次发布版本{targetRevision}不一致。请核对历史发布记录，在SolidWorks中修正后重新存档并审核，再创建发布包。");
    }

    public static string ForPackage(BomItem item, ReleasePackage package, bool preparingPublication = false)
    {
        var standard = item.Kind == BomKind.Standard || package.Scope is ReleaseScope.StandardLongLead or ReleaseScope.StandardFormal or ReleaseScope.StandardSupplement;
        var unavailable = standard
            ? !string.IsNullOrWhiteSpace(package.StandardBomRevision)
                ? System.Text.RegularExpressions.Regex.Replace(package.StandardBomRevision.Trim(), @"-B(?=\d+$)", "-V")
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
        if (!standard && item.SourceDocumentId.HasValue && DrawingRevision.IsWork(item.Revision)
            && package.State == ReleasePackageState.Published
            && package.Scope is ReleaseScope.NonStandardWithDrawing or ReleaseScope.NonStandardSupplement or ReleaseScope.LegacyCombined)
            return "历史版次待核对";
        return DrawingRevision.IsWork(item.Revision) || standard && string.IsNullOrWhiteSpace(item.Revision)
            ? unavailable : DrawingRevision.Display(item.Revision);
    }

    public static IReadOnlyList<BomItem> ForPackage(IEnumerable<BomItem> items, ReleasePackage package, bool preparingPublication = false) =>
        items.Select(item => item with { DisplayRevision = ForPackage(item, package, preparingPublication) }).ToArray();

    public static async Task<IReadOnlyList<BomItem>> CurrentAsync(IPdmRepository repository,
        IReadOnlyList<BomItem> items, CancellationToken ct)
    {
        var publishedIds = new HashSet<Guid>();
        foreach (var projectId in items.Where(item => item.SourceDocumentId.HasValue).Select(item => item.ProjectId).Distinct())
            foreach (var package in await repository.ListReleasePackagesAsync(projectId, ct))
                if (package.State == ReleasePackageState.Published) publishedIds.Add(package.Id);
        var histories = new Dictionary<Guid, IReadOnlyList<DocumentVersion>>();
        foreach (var id in items.Where(item => item.SourceDocumentId.HasValue)
                     .Select(item => item.SourceDocumentId!.Value).Distinct())
            histories[id] = (await repository.ListDocumentVersionsAsync(id, ct))
                .Where(version => version.Status == DocumentVersionStatus.Work
                    || version.ReleasePackageId.HasValue && publishedIds.Contains(version.ReleasePackageId.Value)).ToArray();
        return items.Select(item => item with { DisplayRevision = ForHistory(item,
            item.SourceDocumentId is Guid id && histories.TryGetValue(id, out var history) ? history : []) }).ToArray();
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
