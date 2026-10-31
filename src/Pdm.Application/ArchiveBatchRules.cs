using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public static class ArchiveBatchRules
{
    public static void ValidateRestoreSession(PdmDocument document, string actor, Guid? expectedSessionId)
    {
        if (document.DeletedAt.HasValue || document.PurgedAt.HasValue || document.State is DocumentLifecycleState.InReview or DocumentLifecycleState.Obsolete)
            throw new PdmConflictException("图档已删除、作废或正在审批，不能恢复历史版本。");
        if (document.CheckoutSessionId != expectedSessionId
            || (document.CheckedOutBy is not null && !string.Equals(document.CheckedOutBy, actor, StringComparison.OrdinalIgnoreCase))
            || (document.CheckedOutBy is not null && expectedSessionId is null))
            throw new PdmConflictException("图档的编辑会话已变化，不能恢复历史版本。请刷新后通过当前编辑会话重试。");
    }

    public static IReadOnlyList<ArchiveBatchFile> Order(IReadOnlyList<ArchiveBatchFile> files)
    {
        var byId = files.ToDictionary(file => file.DocumentId);
        var edges = files.SelectMany(file => References(file.Commit.ReferenceSnapshot.Root)
            .Where(reference => !ArchiveDeferredReferences.IsDeferred(reference)
                && reference.DocumentId.HasValue && byId.ContainsKey(reference.DocumentId.Value))
            .Select(reference => new KeyValuePair<Guid, Guid>(file.DocumentId, reference.DocumentId!.Value)));
        var groups = ReferenceCycleGroups.Find(edges, EqualityComparer<Guid>.Default);
        if (groups.Count != 0)
            throw new PdmRuleException("整批存档存在" + groups.Count + "组循环引用，尚未提交任何图档："
                + string.Join("；", groups.Select(group => string.Join("、", group.Select(id => byId[id].Commit.ReferenceSnapshot.Root.FileName)))));
        var ordered = new List<ArchiveBatchFile>(); var visited = new HashSet<Guid>(); var visiting = new HashSet<Guid>();
        var chain = new List<string>();
        void Visit(ArchiveBatchFile file)
        {
            if (visited.Contains(file.DocumentId)) return;
            if (!visiting.Add(file.DocumentId)) throw new PdmRuleException("整批存档存在循环引用，尚未提交任何图档："
                + string.Join(" → ", chain.Append(file.Commit.ReferenceSnapshot.Root.FileName)));
            chain.Add(file.Commit.ReferenceSnapshot.Root.FileName);
            foreach (var reference in References(file.Commit.ReferenceSnapshot.Root))
            {
                if (ArchiveDeferredReferences.IsDeferred(reference)) continue;
                if (reference.DocumentId is Guid id && byId.TryGetValue(id, out var child)) Visit(child);
            }
            visiting.Remove(file.DocumentId); visited.Add(file.DocumentId); ordered.Add(file);
            chain.RemoveAt(chain.Count - 1);
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

    // Context and unclassified links can commit after their owners. Bind those links
    // to actual receipts before the transaction becomes visible; never guess a version.
    public static DocumentReferenceNode BindContextReferences(DocumentReferenceNode root,
        IReadOnlyDictionary<Guid, DocumentCheckInResult> results) => root with
    {
        Revision = ArchiveDeferredReferences.IsDeferred(root) && root.DocumentId is Guid id && results.TryGetValue(id, out var target)
            ? target.Document.Revision : root.Revision,
        Children = root.Children.Select(child => BindContextReferences(child, results)).ToArray()
    };

    public static ArchiveBatchReceipt RequireMatchingReceipt(ArchiveBatchReceipt receipt, string actor, string requestHash)
    {
        if (!string.Equals(receipt.Actor, actor, StringComparison.OrdinalIgnoreCase) || receipt.RequestHash != requestHash)
            throw new PdmConflictException("本批次编号与原提交内容或用户不一致，已停止提交。");
        return receipt;
    }
}
