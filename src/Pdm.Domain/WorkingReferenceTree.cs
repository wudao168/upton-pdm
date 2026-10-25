using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Upton.Pdm.Domain;

// Current working references follow each file's current design. Version and release
// snapshots remain immutable and must never pass through this resolver.
public static class WorkingReferenceTree
{
    public static DocumentReferenceNode Resolve(DocumentReferenceNode root, IReadOnlyDictionary<Guid, DocumentReferenceNode> current)
    {
        DocumentReferenceNode Visit(DocumentReferenceNode occurrence, HashSet<Guid> ancestors)
        {
            var children = occurrence.Children;
            var revision = occurrence.Revision;
            var indexPending = occurrence.StructureIndexPending;
            if (occurrence.DocumentId is Guid id && occurrence.Status != ReferenceNodeStatus.Virtual)
            {
                if (!ancestors.Add(id)) throw new InvalidOperationException("当前工作结构存在循环引用，请核对装配关系。");
                if (current.TryGetValue(id, out var source))
                {
                    revision = source.Revision;
                    // Keep configuration-specific structure when only another configuration
                    // was captured. The physical file still contains all saved configurations.
                    if (source.Kind != DocumentKind.Assembly || source.Configuration == occurrence.Configuration)
                    {
                        // Legacy design dependencies used an ancestor's bare file name and
                        // no instance configuration. An enclosing source may already have
                        // copied that context into this occurrence; revisions cannot identify it.
                        bool LegacyParentContext(DocumentReferenceNode child) => source.Kind == DocumentKind.Assembly
                            && !source.UsesNativeFileInventory && child.Role == ReferenceRole.Component
                            && child.Kind == DocumentKind.Assembly && child.Status != ReferenceNodeStatus.Virtual
                            && child.DocumentId is Guid parent && parent != id && ancestors.Contains(parent)
                            && string.IsNullOrWhiteSpace(child.Configuration)
                            && string.Equals(child.InstancePath.Replace('\\', '/').TrimEnd('/').Split('/').Last(),
                                Path.GetFileNameWithoutExtension(child.FileName), StringComparison.OrdinalIgnoreCase);
                        children = source.Children.Where(child => !LegacyParentContext(child))
                            .Select(child => Rebase(child, source.InstancePath, occurrence.InstancePath)).ToArray();
                        indexPending = source.StructureIndexPending;
                    }
                }
            }
            var resolved = occurrence with { Revision = revision, CheckedOutBy = null, StructureIndexPending = indexPending,
                Children = children
                    .Select(child => child.Role == ReferenceRole.ExternalFileDependency ? child
                    : child.Kind == DocumentKind.Drawing && occurrence.Kind != DocumentKind.Drawing
                    ? child with { Children = [] } : Visit(child, new HashSet<Guid>(ancestors))).ToArray() };
            return resolved;
        }
        return Visit(root, []);
    }

    public static CadReferenceSnapshot Refresh(CadReferenceSnapshot snapshot,
        IReadOnlyDictionary<Guid, DocumentReferenceNode> current, DateTimeOffset at, string actor)
    {
        var root = Resolve(snapshot.Root, current);
        if (root.Revision == snapshot.Root.Revision && ArchiveSafety.SameReferences(snapshot.Root, root)) return snapshot;
        var json = JsonSerializer.Serialize(root, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return snapshot with { SnapshotId = Guid.NewGuid(), Root = root, CapturedAt = at, CapturedBy = actor,
            Sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))) };
    }

    private static DocumentReferenceNode Rebase(DocumentReferenceNode node, string? oldRoot, string newRoot)
    {
        var prefix = (oldRoot ?? "").TrimEnd('/') + "/";
        var relative = node.InstancePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? node.InstancePath[prefix.Length..] : node.InstancePath.Split('/').Last();
        return node with { InstancePath = newRoot.TrimEnd('/') + "/" + relative,
            Children = node.Children.Select(child => Rebase(child, oldRoot, newRoot)).ToArray() };
    }
}
