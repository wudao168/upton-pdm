namespace Upton.Pdm.Domain;

// A context locates a feature's assembly environment. Actual component/feature
// sources remain ordinary dependencies and retain cycle checks and fixed versions.
public static class ArchiveContextReferences
{
    public static bool IsContext(DocumentReferenceNode node)
    {
        if (!node.IsContextReference) return false;
        if (node.Role != ReferenceRole.ExternalFileDependency || node.Kind != DocumentKind.Assembly
            || node.Children.Count != 0 || node.Status == ReferenceNodeStatus.Virtual)
            throw new InvalidOperationException("上下文关联必须是独立记录的装配环境，不能代替装配成员或特征来源：" + node.FileName);
        return true;
    }
}
