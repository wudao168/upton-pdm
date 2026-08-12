namespace Upton.Pdm.Domain;

public sealed record DocumentReferenceNode(
    Guid NodeId,
    Guid? DocumentId,
    string InstancePath,
    string FileName,
    string DisplayName,
    DocumentKind Kind,
    string Configuration,
    int Quantity,
    ReferenceNodeStatus Status,
    RevisionLabel? Revision,
    string? CheckedOutBy,
    IReadOnlyList<DocumentReferenceNode> Children)
{
    public bool HasBlockingIssue => Status == ReferenceNodeStatus.Missing || Children.Any(child => child.HasBlockingIssue);
}

public sealed record CadReferenceSnapshot(
    Guid SnapshotId,
    Guid ProjectId,
    Guid RootDocumentId,
    DateTimeOffset CapturedAt,
    string CapturedBy,
    DocumentReferenceNode Root,
    string Sha256);

public sealed record ReferenceTreeChange(
    ReferenceChangeKind Kind,
    string InstancePath,
    string? PreviousValue,
    string? CurrentValue);

public static class ReferenceTreeDiff
{
    public static IReadOnlyList<ReferenceTreeChange> Compare(DocumentReferenceNode previous, DocumentReferenceNode current)
    {
        var before = Flatten(previous);
        var after = Flatten(current);
        var changes = new List<ReferenceTreeChange>();

        foreach (var (path, node) in before)
        {
            if (!after.TryGetValue(path, out var currentNode))
            {
                changes.Add(new ReferenceTreeChange(ReferenceChangeKind.Removed, path, node.FileName, null));
                continue;
            }

            if (!string.Equals(node.FileName, currentNode.FileName, StringComparison.OrdinalIgnoreCase))
            {
                changes.Add(new ReferenceTreeChange(ReferenceChangeKind.Replaced, path, node.FileName, currentNode.FileName));
            }

            if (!string.Equals(node.Configuration, currentNode.Configuration, StringComparison.Ordinal))
            {
                changes.Add(new ReferenceTreeChange(ReferenceChangeKind.ConfigurationChanged, path, node.Configuration, currentNode.Configuration));
            }

            if (node.Quantity != currentNode.Quantity)
            {
                changes.Add(new ReferenceTreeChange(ReferenceChangeKind.QuantityChanged, path, node.Quantity.ToString(), currentNode.Quantity.ToString()));
            }

            if (node.Status != currentNode.Status)
            {
                changes.Add(new ReferenceTreeChange(ReferenceChangeKind.StatusChanged, path, node.Status.ToString(), currentNode.Status.ToString()));
            }
        }

        foreach (var (path, node) in after.Where(item => !before.ContainsKey(item.Key)))
        {
            changes.Add(new ReferenceTreeChange(ReferenceChangeKind.Added, path, null, node.FileName));
        }

        return changes
            .OrderBy(change => change.InstancePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(change => change.Kind)
            .ToArray();
    }

    private static Dictionary<string, DocumentReferenceNode> Flatten(DocumentReferenceNode root)
    {
        var result = new Dictionary<string, DocumentReferenceNode>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<DocumentReferenceNode>();
        pending.Push(root);
        while (pending.TryPop(out var node))
        {
            result[node.InstancePath] = node;
            for (var index = node.Children.Count - 1; index >= 0; index--)
            {
                pending.Push(node.Children[index]);
            }
        }

        return result;
    }
}
