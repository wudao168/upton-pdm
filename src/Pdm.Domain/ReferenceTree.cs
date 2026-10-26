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
    public ReferenceRole Role { get; init; } = ReferenceRole.Component;

    public bool TreeScopeOnly { get; init; }
    public bool IsSuppressed { get; init; }
    public bool IncludesDesignDependencies { get; init; }
    public bool UsesNativeFileInventory { get; init; }
    public bool StructureIndexPending { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? ReferenceWarnings { get; init; }
    public NativeFileIsolation? NativeIsolation { get; init; }
    public bool NativeIsolationVerified { get; init; }
    public bool HasPendingStructureIndex => StructureIndexPending || Children.Any(child => child.HasPendingStructureIndex);

    public bool HasBlockingIssue => Status == ReferenceNodeStatus.Missing || Children.Any(child => child.HasBlockingIssue);
}

// A verified, independent native copy is a technical projection of an immutable
// source version. It does not create an engineering version or replace its bytes.
public sealed record NativeFileIsolation(Guid SourceVersionId, string SourceSha256,
    string RelativePath, long Length, string Sha256, string GeometryEvidenceSha256)
{
    public int PolicyVersion { get; init; } = 1;
    public Guid ReferenceSnapshotId { get; init; }
    public string ReferenceScopeSha256 { get; init; } = "";
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
        if (current.TreeScopeOnly)
        {
            previous = ArchiveSafety.StructureOnly(previous);
            current = ArchiveSafety.StructureOnly(current);
        }
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
            else if (node.Status == ReferenceNodeStatus.Virtual && node.IsSuppressed != currentNode.IsSuppressed)
            {
                changes.Add(new ReferenceTreeChange(ReferenceChangeKind.StatusChanged, path,
                    node.IsSuppressed ? "嵌入，压缩" : "嵌入，未压缩",
                    currentNode.IsSuppressed ? "嵌入，压缩" : "嵌入，未压缩"));
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
