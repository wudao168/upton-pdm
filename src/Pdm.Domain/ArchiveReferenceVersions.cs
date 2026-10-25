using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Upton.Pdm.Domain;

public static class ArchiveReferenceVersions
{
    // Repair metadata lost by the client from the exact archived parent baseline.
    // Do not infer "latest", change an explicit version, or bind a pending batch child.
    public static CadReferenceSnapshot RestoreMissing(CadReferenceSnapshot incoming,
        DocumentReferenceNode? baseline, IReadOnlyCollection<Guid>? plannedDocumentIds = null)
    {
        if (baseline is null || baseline.DocumentId != incoming.Root.DocumentId) return incoming;
        var planned = (plannedDocumentIds ?? []).ToHashSet();
        IEnumerable<DocumentReferenceNode> Flatten(DocumentReferenceNode node) =>
            new[] { node }.Concat(node.Children.SelectMany(Flatten));
        var archived = Flatten(baseline).Where(node => node.DocumentId.HasValue && node.Revision.HasValue
            && node.Status != ReferenceNodeStatus.Virtual).ToLookup(node => node.DocumentId!.Value);
        DocumentReferenceNode Restore(DocumentReferenceNode node, bool root = false)
        {
            var revision = node.Revision;
            if (!root && revision is null && node.Status != ReferenceNodeStatus.Virtual
                && node.DocumentId is Guid id && !planned.Contains(id))
            {
                var candidates = archived[id].Where(old => old.Kind == node.Kind && old.Role == node.Role
                    && string.Equals(old.Configuration, node.Configuration, StringComparison.Ordinal)).ToArray();
                var exact = candidates.Where(old => string.Equals(old.InstancePath, node.InstancePath, StringComparison.OrdinalIgnoreCase)).ToArray();
                var revisions = (exact.Length > 0 ? exact : candidates).Select(old => old.Revision!.Value).Distinct().Take(2).ToArray();
                if (revisions.Length == 1) revision = revisions[0];
            }
            var children = node.Children.Select(child => Restore(child)).ToArray();
            return revision == node.Revision && children.Zip(node.Children).All(pair => ReferenceEquals(pair.First, pair.Second))
                ? node : node with { Revision = revision, Children = children };
        }
        var restored = Restore(incoming.Root, true);
        if (ReferenceEquals(restored, incoming.Root)) return incoming;
        return incoming with { Root = restored, Sha256 = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(restored, new JsonSerializerOptions(JsonSerializerDefaults.Web))))) };
    }
}
