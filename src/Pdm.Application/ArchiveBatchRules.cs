using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public static class ArchiveBatchRules
{
    public static IReadOnlyList<ArchiveBatchFile> Order(IReadOnlyList<ArchiveBatchFile> files)
    {
        var byId = files.ToDictionary(file => file.DocumentId);
        var ordered = new List<ArchiveBatchFile>(); var visited = new HashSet<Guid>(); var visiting = new HashSet<Guid>();
        void Visit(ArchiveBatchFile file)
        {
            if (visited.Contains(file.DocumentId)) return;
            if (!visiting.Add(file.DocumentId)) throw new PdmRuleException("整批存档存在循环引用，尚未提交任何图档。");
            foreach (var reference in References(file.Commit.ReferenceSnapshot.Root))
                if (reference.DocumentId is Guid id && byId.TryGetValue(id, out var child)) Visit(child);
            visiting.Remove(file.DocumentId); visited.Add(file.DocumentId); ordered.Add(file);
        }
        foreach (var file in files) Visit(file);
        return ordered;
    }

    public static IEnumerable<DocumentReferenceNode> References(DocumentReferenceNode root)
    {
        foreach (var child in root.Children)
        {
            if (child.Kind == DocumentKind.Drawing) continue;
            yield return child;
            foreach (var reference in References(child)) yield return reference;
        }
    }

    public static DocumentVersionCommit BindCommittedReferences(DocumentVersionCommit commit, IReadOnlyDictionary<Guid, DocumentCheckInResult> results)
    {
        DocumentReferenceNode Bind(DocumentReferenceNode node, bool root = false) => node with
        {
            Revision = !root && node.DocumentId is Guid id && results.TryGetValue(id, out var result) ? result.Document.Revision : node.Revision,
            Children = node.Children.Select(child => Bind(child)).ToArray()
        };
        return commit with { ReferenceSnapshot = commit.ReferenceSnapshot with { Root = Bind(commit.ReferenceSnapshot.Root, true) } };
    }

    public static ArchiveBatchReceipt RequireMatchingReceipt(ArchiveBatchReceipt receipt, string actor, string requestHash)
    {
        if (!string.Equals(receipt.Actor, actor, StringComparison.OrdinalIgnoreCase) || receipt.RequestHash != requestHash)
            throw new PdmConflictException("本批次编号与原提交内容或用户不一致，已停止提交。");
        return receipt;
    }
}
