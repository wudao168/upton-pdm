using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed record ReleaseChangeReasonCount(string Reason, int Count);
public sealed record ReleaseChangeOverviewGroup(string Category, int Count, IReadOnlyList<ReleaseChangeReasonCount> Reasons);

public sealed class ProjectReleaseChangeOverview(IPdmRepository repository)
{
    public async Task<IReadOnlyList<ReleaseChangeOverviewGroup>> ReadAsync(Guid projectId, string actor, UserRole role, CancellationToken ct)
    {
        var visible = (await repository.ListProjectsForUserAsync(actor, role, ct)).Where(CanReadRelease).ToArray();
        var selected = visible.FirstOrDefault(project => project.Id == projectId);
        if (selected is null || !await repository.HasProjectContentReadAccessAsync(projectId, actor, role, ct))
            throw new UnauthorizedAccessException("当前账号无权查看该项目的发布记录。");

        var candidates = selected.ParentProjectId is null
            ? visible.Where(project => project.OrganizationId == selected.OrganizationId
                && (project.Id == projectId || project.RootProjectId == projectId || project.ParentProjectId == projectId)).ToArray()
            : [selected];
        var releases = new List<ReleasePackage>();
        foreach (var batch in candidates.Chunk(4))
        {
            var records = await Task.WhenAll(batch.Select(async project =>
            {
                if (project.Id != projectId && !await repository.HasProjectContentReadAccessAsync(project.Id, actor, role, ct))
                    return Array.Empty<ReleasePackage>();
                return (await repository.ListReleasePackagesAsync(project.Id, ct))
                    .Where(item => item.State == ReleasePackageState.Published && Category(item.Scope) is not null).ToArray();
            }));
            releases.AddRange(records.SelectMany(page => page));
        }

        var unique = releases.DistinctBy(item => item.Id).ToArray();
        var byCategory = unique.ToLookup(item => Category(item.Scope));
        return [
            Summarize("All", unique),
            Summarize(nameof(BomKind.Standard), byCategory[BomKind.Standard]),
            Summarize(nameof(BomKind.NonStandard), byCategory[BomKind.NonStandard]),
            Summarize(nameof(BomKind.Electrical), byCategory[BomKind.Electrical])
        ];
    }

    private static bool CanReadRelease(Project project) =>
        project.CanReadContent && project.EffectiveProjectPermissions.Contains(ProjectCenterPermissions.Tab("release"))
        && project.EffectiveProjectPermissions.Contains("release.view");

    private static BomKind? Category(ReleaseScope scope) => scope switch
    {
        ReleaseScope.StandardSupplement => BomKind.Standard,
        ReleaseScope.NonStandardSupplement => BomKind.NonStandard,
        ReleaseScope.ElectricalSupplement => BomKind.Electrical,
        _ => null
    };

    private static ReleaseChangeOverviewGroup Summarize(string category, IEnumerable<ReleasePackage> packages)
    {
        var reasons = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var count = 0;
        foreach (var package in packages)
        {
            count++;
            foreach (var reason in Reasons(package).Distinct(StringComparer.OrdinalIgnoreCase))
                reasons[reason] = reasons.GetValueOrDefault(reason) + 1;
        }
        return new(category, count, reasons.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new ReleaseChangeReasonCount(pair.Key, pair.Value)).ToArray());
    }

    private static IEnumerable<string> Reasons(ReleasePackage package)
    {
        if (package.ChangeReasonSelections.Count > 0)
            return package.ChangeReasonSelections.Where(reason => reason.CategoryCode != "FormalSupplement")
                .Select(reason => reason.Reason.Trim()).Where(reason => reason.Length > 0 && reason != "正式补充");

        return (package.ChangeReason ?? "").Split(['；', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(reason =>
            {
                var parts = reason.Split(" / ", 2, StringSplitOptions.TrimEntries);
                return parts[0] == "其他" ? parts[0] : parts[^1];
            }).Where(reason => reason.Length > 0 && reason != "正式补充");
    }
}
