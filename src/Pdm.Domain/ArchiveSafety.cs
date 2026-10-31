using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Upton.Pdm.Domain;

public static class ArchiveSafety
{
    public static NativeFileIsolation? NativeCopy(IEnumerable<DocumentReferenceNode> indexes, Guid documentId, DocumentVersion version) =>
        indexes.Where(index => index.DocumentId == documentId).Select(index => index.NativeIsolation)
            .FirstOrDefault(copy => copy != null && copy.PolicyVersion == 1 && copy.ReferenceSnapshotId != Guid.Empty
                && copy.SourceVersionId == version.Id && string.Equals(copy.SourceSha256, version.Sha256, StringComparison.OrdinalIgnoreCase));

    public static CadReferenceSnapshot Rehash(CadReferenceSnapshot snapshot) => snapshot with
    {
        Sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(snapshot.Root, new JsonSerializerOptions(JsonSerializerDefaults.Web)))))
    };

    public static string NativeIsolationScope(DocumentReferenceNode root)
    {
        var files = new SortedSet<string>(StringComparer.Ordinal);
        void Add(DocumentReferenceNode node)
        {
            if (node.DocumentId.HasValue && node.Status != ReferenceNodeStatus.Virtual)
                files.Add(node.DocumentId.Value.ToString("N") + "|" + node.Configuration + "|" + node.Revision?.Display);
            foreach (var child in node.Children) Add(child);
        }
        Add(root);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", files))));
    }

    public static NativeFileIsolation ValidateNativeIsolation(CadReferenceSnapshot snapshot,
        Guid sourceVersionId, string sourceSha256, string relativePath, long length, string sha256)
    {
        var copy = snapshot.Root.NativeIsolation ?? throw new InvalidOperationException("缺少原生文件固化校验记录。");
        static bool Hash(string value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
        if (copy.PolicyVersion != 1 || copy.SourceVersionId != sourceVersionId
            || !string.Equals(copy.SourceSha256, sourceSha256, StringComparison.OrdinalIgnoreCase)
            || copy.RelativePath != relativePath || copy.Length != length || length <= 0
            || !string.Equals(copy.Sha256, sha256, StringComparison.OrdinalIgnoreCase)
            || !Hash(copy.GeometryEvidenceSha256) || !Hash(sha256))
            throw new InvalidOperationException("原生文件固化副本与来源版本或上传文件不一致，未提交任何变更。");
        return copy with { ReferenceSnapshotId = snapshot.SnapshotId, ReferenceScopeSha256 = NativeIsolationScope(snapshot.Root) };
    }

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
            || previous.IsContextReference != current.IsContextReference
            || previous.ReferenceTypeUnverified != current.ReferenceTypeUnverified
            || !string.Equals(previous.FileName, current.FileName, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(previous.Configuration, current.Configuration, StringComparison.Ordinal)
            || previous.Quantity != current.Quantity
            || NormalizeStatus(previous.Status) != NormalizeStatus(current.Status)
            || previous.StructureIndexPending != current.StructureIndexPending
            || root && previous.UsesNativeFileInventory != current.UsesNativeFileInventory
            || (previous.IsSuppressed || previous.Status == ReferenceNodeStatus.Suppressed)
                != (current.IsSuppressed || current.Status == ReferenceNodeStatus.Suppressed)
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
        if (current.UsesNativeFileInventory)
        {
            if (!FileClosure(previous).SetEquals(FileClosure(current))) return false;
        }
        // Verification metadata alone is not a new native file version. Verified file-closure
        // changes are compared above; real incomplete indexes still participate below.
        previous = previous with { UsesNativeFileInventory = false };
        current = current with { UsesNativeFileInventory = false };
        if (current.Kind is DocumentKind.Part or DocumentKind.Assembly)
        {
            // A file's selected occurrence is not its own quantity or suppression state.
            // Keep every child occurrence intact and preserve real missing/virtual states.
            DocumentReferenceNode FileRoot(DocumentReferenceNode node) => node with
            {
                Quantity = 1, IsSuppressed = false,
                Status = node.Status is ReferenceNodeStatus.Suppressed or ReferenceNodeStatus.Hidden or ReferenceNodeStatus.Lightweight
                    ? ReferenceNodeStatus.Normal : node.Status,
                Configuration = node.Kind == DocumentKind.Part ? "" : node.Configuration
            };
            previous = FileRoot(previous);
            current = FileRoot(current);
        }
        if (current.Kind != DocumentKind.Assembly) return SameReferences(previous, current);
        var sameConfiguration = string.Equals(previous.Configuration, current.Configuration, StringComparison.OrdinalIgnoreCase);
        DocumentReferenceNode Own(DocumentReferenceNode node) => StructureOnly(node) with
        {
            Configuration = "", // The active configuration is an opening choice, not a file edit.
            Children = node.Children.Where(child => sameConfiguration && child.Kind != DocumentKind.Drawing && child.Role != ReferenceRole.ExternalFileDependency)
                .Select(child => child with { Revision = null, Children = child.Status == ReferenceNodeStatus.Virtual
                    ? child.Children : [] }).ToArray()
        };
        return SameReferences(Own(previous), Own(current));
    }

    internal static bool IsNativeInventoryEntry(DocumentReferenceNode owner, DocumentReferenceNode child) =>
        owner.UsesNativeFileInventory && child.Role == ReferenceRole.ExternalFileDependency && child.DocumentId is Guid id
        && string.Equals(child.InstancePath, owner.InstancePath.TrimEnd('/') + "/native-file/" + id.ToString("N"), StringComparison.OrdinalIgnoreCase);

    private static HashSet<string> FileClosure(DocumentReferenceNode node) => (node.UsesNativeFileInventory ? node.Children : [])
        .Where(child => child.Role == ReferenceRole.ExternalFileDependency && !IsNativeInventoryEntry(node, child))
        .Select(child => $"{child.DocumentId:N}|{child.FileName}|{child.Kind}|{child.Revision?.Display}|{child.StructureIndexPending}")
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    // Callers must also prove identical native bytes/properties and the exact base version.
    public static bool IsNativeIndexOnlyUpdate(DocumentReferenceNode previous, DocumentReferenceNode current) =>
        current.UsesNativeFileInventory && (current.Kind is DocumentKind.Part or DocumentKind.Assembly)
        && previous.DocumentId == current.DocumentId && previous.Kind == current.Kind
        && string.Equals(previous.FileName, current.FileName, StringComparison.OrdinalIgnoreCase)
        && string.Equals(previous.Configuration, current.Configuration, StringComparison.Ordinal)
        && FileClosure(previous).SetEquals(FileClosure(current));

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
