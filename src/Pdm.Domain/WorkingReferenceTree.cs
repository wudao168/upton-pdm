using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Upton.Pdm.Domain;

// Current working references follow each file's current design. Version and release
// snapshots remain immutable and must never pass through this resolver.
public static class WorkingReferenceTree
{
    public static DocumentReferenceNode Resolve(DocumentReferenceNode root, IReadOnlyDictionary<Guid, DocumentReferenceNode> current)
        => Resolve(root, current, []);

    public static DocumentReferenceNode Resolve(DocumentReferenceNode root, IReadOnlyDictionary<Guid, DocumentReferenceNode> current,
        IReadOnlyList<DocumentReferenceNode> nativeIndexes)
    {
        // New native inventories can carry a verified occurrence index for an older
        // file version whose own immutable snapshot was empty. Keep that index by
        // exact document/configuration/revision, including when opening that file alone.
        var legacyIndexes = new Dictionary<(Guid, string, string), DocumentReferenceNode>();
        var ambiguousIndexes = new HashSet<(Guid, string, string)>();
        var ownIndexes = new HashSet<(Guid, string, string)>();
        var nativeInventories = new Dictionary<(Guid, string, string), DocumentReferenceNode>();
        var completedNativeIndexes = nativeIndexes.Where(node => node.UsesNativeFileInventory
            && !node.HasPendingStructureIndex && node.DocumentId.HasValue && node.Revision.HasValue)
            .GroupBy(node => (node.DocumentId!.Value, node.Configuration, node.Revision!.Value.Display))
            .ToDictionary(group => group.Key, group => group.First());
        bool Complete(DocumentReferenceNode node) => node.Kind == DocumentKind.Drawing
            || (node.Status == ReferenceNodeStatus.Virtual || node.DocumentId.HasValue && node.Revision.HasValue)
                && node.Children.All(Complete);
        void Collect(DocumentReferenceNode node)
        {
            if (node.Kind == DocumentKind.Drawing) return;
            if (node.Kind == DocumentKind.Assembly && node.DocumentId is Guid id && node.Revision is not null
                && !node.HasPendingStructureIndex && node.Children.Any(child => child.Role == ReferenceRole.Component
                    && child.Kind != DocumentKind.Drawing) && Complete(node))
            {
                var key = (id, node.Configuration, node.Revision.Value.Display);
                if (node.UsesNativeFileInventory)
                {
                    ownIndexes.Add(key); ambiguousIndexes.Remove(key); legacyIndexes[key] = node;
                }
                else if (!ownIndexes.Contains(key))
                {
                    if (legacyIndexes.TryGetValue(key, out var previous) && !ArchiveSafety.SameOwnDesignReferences(previous, node))
                        ambiguousIndexes.Add(key);
                    else legacyIndexes[key] = node with { UsesNativeFileInventory = true };
                }
            }
            foreach (var child in node.Children.Where(child => child.Role == ReferenceRole.Component)) Collect(child);
        }
        foreach (var inventory in new[] { root }.Concat(current.Values).Concat(nativeIndexes).Where(node => node.UsesNativeFileInventory))
        {
            Collect(inventory);
            if (inventory.DocumentId is Guid id && inventory.Revision.HasValue
                && inventory.Children.Any(child => ArchiveSafety.IsNativeInventoryEntry(inventory, child)))
                nativeInventories[(id, inventory.Configuration, inventory.Revision.Value.Display)] = inventory;
        }
        DocumentReferenceNode Visit(DocumentReferenceNode occurrence, HashSet<Guid> ancestors)
        {
            var children = occurrence.Children;
            var revision = occurrence.Revision;
            var indexPending = occurrence.StructureIndexPending;
            var usesNativeInventory = occurrence.UsesNativeFileInventory;
            if (occurrence.DocumentId is Guid id && occurrence.Status != ReferenceNodeStatus.Virtual)
            {
                if (!ancestors.Add(id)) throw new InvalidOperationException("当前工作结构存在循环引用，请核对装配关系。");
                if (current.TryGetValue(id, out var source))
                {
                    var key = (id, source.Configuration, source.Revision?.Display ?? string.Empty);
                    if (source.StructureIndexPending && completedNativeIndexes.TryGetValue(key, out var nativeIndex)
                        && string.Equals(source.FileName, nativeIndex.FileName, StringComparison.OrdinalIgnoreCase)
                        && ArchiveSafety.IsNativeIndexOnlyUpdate(source, nativeIndex))
                        source = source with { Children = nativeIndex.Children.Select(child => Rebase(child, nativeIndex.InstancePath, source.InstancePath)).ToArray(),
                            StructureIndexPending = false, NativeIsolation = nativeIndex.NativeIsolation,
                            NativeIsolationVerified = nativeIndex.NativeIsolationVerified };
                    if (!ambiguousIndexes.Contains(key) && legacyIndexes.TryGetValue(key, out var completed)
                        && (ArchiveSafety.SameOwnDesignReferences(source, completed)
                            || source.Children.Count == 0 && ArchiveSafety.IsNativeIndexOnlyUpdate(source, completed)
                            || ownIndexes.Contains(key) && ArchiveSafety.IsNativeIndexOnlyUpdate(source, completed)))
                        source = source with { Children = completed.Children.Select(child => Rebase(child, completed.InstancePath, source.InstancePath)).ToArray(),
                            StructureIndexPending = completed.StructureIndexPending };
                    revision = source.Revision;
                    usesNativeInventory = source.UsesNativeFileInventory;
                    // Keep configuration-specific structure when only another configuration
                    // was captured. The physical file still contains all saved configurations.
                    // A complete occurrence captured from the exact same legacy file
                    // version can repair an older empty index without revising that file.
                    // A later version (including a deliberately emptied assembly) wins.
                    var completedLegacyIndex = source.Kind == DocumentKind.Assembly && !source.UsesNativeFileInventory
                        && source.Revision == occurrence.Revision && source.Children.Count == 0
                        && !occurrence.StructureIndexPending
                        && occurrence.Children.Any(child => child.Role == ReferenceRole.Component && child.Kind != DocumentKind.Drawing);
                    if (!completedLegacyIndex && (source.Kind != DocumentKind.Assembly || source.Configuration == occurrence.Configuration))
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
                    if (nativeInventories.TryGetValue(key, out var inventory)
                        && string.Equals(inventory.FileName, source.FileName, StringComparison.OrdinalIgnoreCase))
                    {
                        var nativeOwner = occurrence with { UsesNativeFileInventory = true };
                        children = children.Where(child => !ArchiveSafety.IsNativeInventoryEntry(nativeOwner, child))
                            .Concat(inventory.Children.Where(child => ArchiveSafety.IsNativeInventoryEntry(inventory, child))
                                .Select(child => Rebase(child, inventory.InstancePath, occurrence.InstancePath))).ToArray();
                        usesNativeInventory = true; indexPending |= inventory.StructureIndexPending;
                    }
                }
            }
            var resolved = occurrence with { Revision = revision, CheckedOutBy = null, StructureIndexPending = indexPending,
                UsesNativeFileInventory = usesNativeInventory,
                Children = children
                    .Select(child => child.Role == ReferenceRole.ExternalFileDependency
                    ? usesNativeInventory && child.DocumentId is Guid dependency && current.TryGetValue(dependency, out var dependencyDesign)
                        ? child with { Revision = dependencyDesign.Revision } : child
                    : child.Kind == DocumentKind.Drawing && occurrence.Kind != DocumentKind.Drawing
                    ? child with { Children = [] } : Visit(child, new HashSet<Guid>(ancestors))).ToArray() };
            return resolved;
        }
        return Visit(root, []);
    }

    public static CadReferenceSnapshot Refresh(CadReferenceSnapshot snapshot,
        IReadOnlyDictionary<Guid, DocumentReferenceNode> current, DateTimeOffset at, string actor)
        => Refresh(snapshot, current, at, actor, []);

    public static CadReferenceSnapshot Refresh(CadReferenceSnapshot snapshot,
        IReadOnlyDictionary<Guid, DocumentReferenceNode> current, DateTimeOffset at, string actor,
        IReadOnlyList<DocumentReferenceNode> nativeIndexes)
    {
        var root = Resolve(snapshot.Root, current, nativeIndexes);
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
