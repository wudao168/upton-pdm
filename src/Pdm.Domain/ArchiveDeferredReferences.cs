namespace Upton.Pdm.Domain;

// Unclassified file links are retained as evidence, not asserted to be assembly
// members, contexts or proven feature dependencies. Versions are bound after commit.
public static class ArchiveDeferredReferences
{
    public static bool IsUnverified(DocumentReferenceNode node)
    {
        if (!node.ReferenceTypeUnverified) return false;
        if (node.IsContextReference || node.Role != ReferenceRole.ExternalFileDependency
            || node.Children.Count != 0 || node.Status == ReferenceNodeStatus.Virtual)
            throw new InvalidOperationException("待核对引用必须独立保留，不能替代已确认装配结构或上下文：" + node.FileName);
        return true;
    }

    public static bool IsDeferred(DocumentReferenceNode node) => IsUnverified(node) || ArchiveContextReferences.IsContext(node);
}
