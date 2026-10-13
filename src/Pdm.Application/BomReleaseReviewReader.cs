using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public static class BomReleaseReviewReader
{
    public static async Task<IReadOnlyList<BomItem>> AttachAsync(IPdmRepository repository, Guid projectId,
        IReadOnlyList<BomItem> items, IReadOnlyDictionary<Guid, BomReleaseConfirmation> confirmations, CancellationToken cancellationToken)
    {
        if (items.Count == 0) return items;
        var relations = await repository.ListDocumentRelationsAsync(projectId, cancellationToken);
        var latest = await repository.LatestDocumentVersionIdsAsync(projectId, cancellationToken);
        var result = new List<BomItem>();
        foreach (var item in items)
        {
            var ids = item.SourceDocumentId is Guid model
                ? relations.Where(relation => relation.ModelDocumentId == model).Select(relation => relation.DrawingDocumentId).Append(model).Distinct().ToArray()
                : [];
            var token = BomReleaseReview.Token(item, ids.ToDictionary(id => id, id => latest.GetValueOrDefault(id)));
            confirmations.TryGetValue(item.Id, out var confirmation);
            result.Add(item with { ReleaseReviewToken = token, ReleaseReviewTokens = new Dictionary<Guid, string> { [item.Id] = token },
                ReleaseConfirmation = confirmation?.Token == token ? confirmation : null });
        }
        return result;
    }
}
