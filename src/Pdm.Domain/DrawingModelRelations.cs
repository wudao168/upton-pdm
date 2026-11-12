namespace Upton.Pdm.Domain;

/// <summary>配套工程图只关联直接模型；普通文件依赖继续保留在完整引用快照中。</summary>
public static class DrawingModelRelations
{
    public static bool IsDirectModel(DocumentReferenceNode node) =>
        node.Role == ReferenceRole.Component && !node.IsContextReference
        && node.Kind is DocumentKind.Part or DocumentKind.Assembly;

    public static IEnumerable<DocumentReferenceNode> KnownModels(DocumentReferenceNode drawing) =>
        drawing.Kind != DocumentKind.Drawing ? [] : drawing.Children.Where(node => IsDirectModel(node)
            && node.Status is not (ReferenceNodeStatus.Missing or ReferenceNodeStatus.Virtual)
            && node.DocumentId.HasValue);

    // Repair only positively classified dependencies. Unknown/missing evidence cannot
    // prove that an earlier direct relation disappeared, especially in partial drawings.
    public static IReadOnlySet<Guid> DependencyOnlyModelIds(DocumentReferenceNode drawing)
    {
        if (drawing.Kind != DocumentKind.Drawing) return new HashSet<Guid>();
        var direct = drawing.Children.Where(IsDirectModel).Where(node => node.DocumentId.HasValue)
            .Select(node => node.DocumentId!.Value).ToHashSet();
        return drawing.Children.Where(node => node.Role == ReferenceRole.ExternalFileDependency
                && node.Kind is DocumentKind.Part or DocumentKind.Assembly && node.DocumentId.HasValue)
            .Select(node => node.DocumentId!.Value).Where(id => !direct.Contains(id)).ToHashSet();
    }
}
