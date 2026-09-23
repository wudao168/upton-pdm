using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed record ProductionDrawingProject(Guid Id, string Code, string Name)
{
    public string? Division { get; init; }
    public string? ProjectManager { get; init; }
}

public sealed record ProductionDrawingItem(
    Guid ProjectId,
    string ProjectCode,
    string ProjectName,
    Guid DocumentId,
    Guid VersionId,
    Guid ReleasePackageId,
    string ReleasePackageNumber,
    string DrawingNumber,
    string Model,
    string Name,
    string Revision,
    DateTimeOffset PublishedAt,
    string Priority,
    DateOnly? RequiredOn,
    bool IsCurrent,
    bool PdfReady,
    bool LegacyUnverified,
    DateTimeOffset? SupersededAt)
{
    public IReadOnlyList<BomItem> BomItems { get; init; } = [];
    public string PublishedBy { get; init; } = string.Empty;
    public string? Division { get; init; }
    public string? ProjectManager { get; init; }
}

/// <summary>按不可变正式版本列出生产图纸；工作版永远不影响当前正式版的选择。</summary>
public sealed class ProductionDrawingService(IPdmRepository repository)
{
    public async Task<IReadOnlyList<ProductionDrawingItem>> ListAsync(
        string actor, UserRole role, bool includeHistory, Guid? projectId, CancellationToken cancellationToken)
    {
        var projects = await repository.ListProductionDrawingProjectsAsync(actor, role, cancellationToken);
        var results = new List<ProductionDrawingItem>();
        foreach (var project in projects.Where(item => !projectId.HasValue || item.Id == projectId.Value))
        {
            var documents = (await repository.ListDocumentsAsync(project.Id, cancellationToken))
                .Where(document => document.Kind == DocumentKind.Drawing)
                .ToDictionary(document => document.Id);
            if (documents.Count == 0) continue;
            var packages = (await repository.ListReleasePackagesAsync(project.Id, cancellationToken))
                .Where(package => package.State == ReleasePackageState.Published
                    && package.Scope is (ReleaseScope.NonStandardWithDrawing or ReleaseScope.NonStandardSupplement or ReleaseScope.LegacyCombined))
                .ToDictionary(package => package.Id);
            var relatedModels = (await repository.ListDocumentRelationsAsync(project.Id, cancellationToken))
                .GroupBy(relation => relation.DrawingDocumentId)
                .ToDictionary(group => group.Key, group => group.Select(relation => relation.ModelDocumentId).ToHashSet());
            var packageBomItems = packages.ToDictionary(entry => entry.Key, entry => entry.Value.NonStandardBomSnapshot
                .Where(item => item.SourceDocumentId.HasValue)
                .GroupBy(item => item.SourceDocumentId!.Value)
                .ToDictionary(group => group.Key, group => group.OrderBy(item => item.Sequence).ToArray()));
            var allVersions = await repository.ListProjectDocumentVersionsAsync(project.Id, cancellationToken);
            var versionsById = allVersions.ToDictionary(version => version.Id);
            var versions = allVersions
                .Where(version => version.Status == DocumentVersionStatus.Released
                    && version.ReleasePackageId.HasValue
                    && packages.ContainsKey(version.ReleasePackageId.Value)
                    && documents.ContainsKey(version.DocumentId))
                .GroupBy(version => version.DocumentId);
            foreach (var group in versions)
            {
                var ordered = group.OrderByDescending(version => version.Revision.BaseRevision)
                    .ThenByDescending(version => version.CreatedAt).ThenByDescending(version => version.Id).ToArray();
                var current = ordered[0];
                foreach (var version in includeHistory ? ordered : [current])
                {
                    var document = documents[version.DocumentId];
                    var package = packages[version.ReleasePackageId!.Value];
                    package.DrawingDeliveryOverrides.TryGetValue(document.Id, out var delivery);
                    var source = version.SourceVersionId.HasValue
                        && versionsById.TryGetValue(version.SourceVersionId.Value, out var foundSource)
                        && foundSource.DocumentId == document.Id ? foundSource : null;
                    var legacy = source is null || string.Equals(source.StorageRelativePath, version.StorageRelativePath, StringComparison.OrdinalIgnoreCase);
                    var bomItems = relatedModels.TryGetValue(document.Id, out var modelIds)
                        ? modelIds.SelectMany(modelId => packageBomItems[package.Id].GetValueOrDefault(modelId, [])).ToArray()
                        : [];
                    if (bomItems.Length == 0)
                        bomItems = package.NonStandardBomSnapshot
                            .Where(item => string.Equals(item.DrawingNumber, document.DrawingNumber, StringComparison.OrdinalIgnoreCase))
                            .OrderBy(item => item.Sequence).ToArray();
                    results.Add(new ProductionDrawingItem(
                        project.Id, project.Code, project.Name, document.Id, version.Id, package.Id, package.Number,
                        document.DrawingNumber, Model(version.PropertySnapshot, document), document.Name, version.Revision.Display,
                        package.PublishedAt ?? version.CreatedAt,
                        delivery?.Priority ?? package.DrawingPriority,
                        delivery?.RequiredOn ?? package.DrawingRequiredOn,
                        version.Id == current.Id,
                        version.Preview?.Format == DocumentPreviewFormat.Pdf
                            && (legacy || string.Equals(version.Preview.SourceSha256, version.Sha256, StringComparison.OrdinalIgnoreCase)),
                        legacy,
                        version.Id == current.Id ? null : current.CreatedAt)
                    { BomItems = bomItems, PublishedBy = version.CreatedBy, Division = project.Division, ProjectManager = project.ProjectManager });
                }
            }
        }
        return results
            .OrderBy(item => item.IsCurrent ? 0 : 1)
            .ThenBy(item => item.Priority == "Urgent" ? 0 : item.Priority == "Priority" ? 1 : 2)
            .ThenBy(item => item.RequiredOn ?? DateOnly.MaxValue)
            .ThenByDescending(item => item.PublishedAt)
            .ToArray();
    }

    public async Task<ReleasePackage> UpdateDeliveryAsync(Guid releasePackageId, Guid documentId,
        DrawingDeliveryOverride delivery, string actor, UserRole role, CancellationToken cancellationToken)
    {
        var package = await repository.FindReleasePackageAsync(releasePackageId, cancellationToken)
            ?? throw new PdmNotFoundException("发布包不存在。");
        if (!await repository.HasProjectContentReadAccessAsync(package.ProjectId, actor, role, cancellationToken)
            || !await repository.HasUserPermissionAsync(actor, role, PermissionCodes.ProductionDrawingManage, cancellationToken))
            throw new UnauthorizedAccessException("没有调整生产图纸发图信息的权限。");
        if (delivery.Priority is not ("Normal" or "Priority" or "Urgent"))
            throw new PdmRuleException("图纸紧急程度只能是普通、优先或紧急。");
        return await repository.UpdatePublishedDrawingDeliveryAsync(releasePackageId, documentId, delivery, actor, cancellationToken);
    }

    private static string Model(IReadOnlyDictionary<string, string?> properties, PdmDocument document)
    {
        var found = properties.FirstOrDefault(item => item.Key.Split('/').Last().Equals("型号", StringComparison.OrdinalIgnoreCase)).Value;
        return string.IsNullOrWhiteSpace(found) ? document.DrawingNumber : found.Trim();
    }
}
