using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Upton.Pdm.Domain;

public static class ArchiveSafety
{
    public static bool SameProperties(IReadOnlyDictionary<string, string?> previous, IReadOnlyDictionary<string, string?> current)
    {
        static bool Meaningful(string key) => key is not ("FileName" or "Extension" or "LastWriteTimeUtc" or "SourceFileSha256")
            && !key.Split('/').Last().StartsWith("UPLM_QR_", StringComparison.OrdinalIgnoreCase);
        var before = previous.Where(pair => Meaningful(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value ?? "", StringComparer.OrdinalIgnoreCase);
        var after = current.Where(pair => Meaningful(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value ?? "", StringComparer.OrdinalIgnoreCase);
        return before.Count == after.Count && before.All(pair => after.TryGetValue(pair.Key, out var value) && value == pair.Value);
    }

    public static bool SameReferences(DocumentReferenceNode? previous, DocumentReferenceNode current, bool root = true)
    {
        if (previous is null) return false;
        if (root && current.TreeScopeOnly)
        {
            previous = StructureOnly(previous);
            current = StructureOnly(current);
            if (current.Kind == DocumentKind.Drawing && current.Children.Count > 0)
            {
                var models = current.Children.Select(node => node.DocumentId).ToHashSet();
                previous = previous with { Children = previous.Children.Where(node => models.Contains(node.DocumentId)).ToArray() };
            }
        }
        if (previous.DocumentId != current.DocumentId || previous.Kind != current.Kind || previous.Role != current.Role
            || !string.Equals(previous.FileName, current.FileName, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(previous.Configuration, current.Configuration, StringComparison.Ordinal)
            || previous.Quantity != current.Quantity
            || NormalizeStatus(previous.Status) != NormalizeStatus(current.Status)
            || (!root && previous.Revision?.Display != current.Revision?.Display)) return false;
        // Attaching a drawing does not change the model's assembly structure.
        var before = previous.Children.Where(node => current.Kind == DocumentKind.Drawing || node.Kind != DocumentKind.Drawing)
            .OrderBy(node => node.InstancePath, StringComparer.OrdinalIgnoreCase).ToArray();
        var after = current.Children.Where(node => current.Kind == DocumentKind.Drawing || node.Kind != DocumentKind.Drawing)
            .OrderBy(node => node.InstancePath, StringComparer.OrdinalIgnoreCase).ToArray();
        return before.Length == after.Length && before.Zip(after).All(pair => SameReferences(pair.First, pair.Second, false));
    }

    public static DocumentReferenceNode StructureOnly(DocumentReferenceNode node) => node with
    {
        Children = node.Children.Where(child => child.Role != ReferenceRole.ExternalFileDependency)
            .Select(StructureOnly).ToArray()
    };

    public static bool SameOwnDesignReferences(DocumentReferenceNode? previous, DocumentReferenceNode current)
    {
        if (previous is null) return false;
        if (current.Kind != DocumentKind.Assembly) return SameReferences(previous, current);
        var sameConfiguration = string.Equals(previous.Configuration, current.Configuration, StringComparison.OrdinalIgnoreCase);
        DocumentReferenceNode Own(DocumentReferenceNode node) => StructureOnly(node) with
        {
            Configuration = "", // The active configuration is an opening choice, not a file edit.
            Quantity = 1, Status = ReferenceNodeStatus.Normal,
            Children = node.Children.Where(child => sameConfiguration && child.Kind != DocumentKind.Drawing && child.Role != ReferenceRole.ExternalFileDependency)
                .Select(child => child with { Revision = null, Children = child.Status == ReferenceNodeStatus.Virtual
                    ? child.Children : [] }).ToArray()
        };
        return SameReferences(Own(previous), Own(current));
    }

    private static ReferenceNodeStatus NormalizeStatus(ReferenceNodeStatus status) =>
        status == ReferenceNodeStatus.Lightweight ? ReferenceNodeStatus.Normal : status;
}

public static class ProjectReferenceForest
{
    public static IReadOnlyList<DocumentReferenceNode> Roots(CadReferenceSnapshot snapshot) =>
        snapshot.Root.DocumentId.HasValue ? [snapshot.Root] : snapshot.Root.Children;

    public static CadReferenceSnapshot Merge(CadReferenceSnapshot? previous, CadReferenceSnapshot incoming)
    {
        if (previous is null) return incoming;
        var roots = Roots(previous).Where(node => node.DocumentId != incoming.RootDocumentId)
            .Append(incoming.Root).OrderBy(node => node.FileName, StringComparer.OrdinalIgnoreCase).ToArray();
        if (roots.Length == 1) return incoming;
        var children = roots.Select(node => Prefix(node, $"roots/{node.DocumentId:N}/")).ToArray();
        var root = new DocumentReferenceNode(incoming.ProjectId, null, "project", "", "项目装配结构",
            DocumentKind.Assembly, "", 1, ReferenceNodeStatus.Normal, null, null, children);
        var json = JsonSerializer.Serialize(root, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return incoming with { SnapshotId = Guid.NewGuid(), RootDocumentId = children[0].DocumentId!.Value,
            Root = root, Sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))) };
    }

    private static DocumentReferenceNode Prefix(DocumentReferenceNode node, string prefix) => node with
    {
        InstancePath = node.InstancePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? node.InstancePath : prefix + node.InstancePath,
        Children = node.Children.Select(child => Prefix(child, prefix)).ToArray()
    };
}
