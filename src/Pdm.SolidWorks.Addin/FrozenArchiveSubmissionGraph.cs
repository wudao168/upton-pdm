using System;
using System.Collections.Generic;
using System.Linq;
using Upton.Pdm.Domain;

namespace Upton.Pdm.SolidWorks;

internal static class FrozenArchiveSubmissionGraph
{
    internal static void Validate(IReadOnlyDictionary<Guid, PdmApiClient.ArchiveReferenceSnapshot> snapshots)
    {
        var roots = snapshots.ToDictionary(pair => pair.Key, pair => (Dictionary<string, object>)pair.Value.CreateRoot());
        foreach (var pair in roots)
            if (Id(pair.Value) != pair.Key) throw new InvalidOperationException("提交快照的图档身份不一致，尚未上传：" + Name(pair.Value));
        // Match ArchiveBatchRules.Order on the actual wire graph, including native
        // inventory entries added during capture. The SCC core is shared with it.
        var edges = roots.SelectMany(owner => References(owner.Value).Where(node => !IsDeferred(node))
            .Select(Id).Where(id => id.HasValue && roots.ContainsKey(id.Value))
            .Select(id => new KeyValuePair<Guid, Guid>(owner.Key, id.Value)));
        var groups = ReferenceCycleGroups.Find(edges, EqualityComparer<Guid>.Default);
        if (groups.Count == 0) return;
        var detail = string.Join("；", groups.Select(group => string.Join("、", group.Select(id => Name(roots[id])))));
        PluginDiagnosticLog.Record("上传前整批关系校验", detail);
        throw new InvalidOperationException("最终提交快照存在" + groups.Count + "组循环引用，尚未上传任何文件：" + detail);
    }

    private static string Name(Dictionary<string, object> node) => Convert.ToString(node["fileName"]);
    private static Guid? Id(Dictionary<string, object> node) =>
        node.TryGetValue("documentId", out var value) && Guid.TryParse(Convert.ToString(value), out var id) ? id : (Guid?)null;
    private static IEnumerable<Dictionary<string, object>> Children(Dictionary<string, object> node) =>
        ((object[])node["children"]).Cast<Dictionary<string, object>>();
    private static IEnumerable<Dictionary<string, object>> References(Dictionary<string, object> node)
    {
        foreach (var child in Children(node))
        {
            if (Convert.ToInt32(child["kind"]) == (int)CadDocumentKind.Drawing) continue;
            yield return child;
            foreach (var reference in References(child)) yield return reference;
        }
    }
    private static bool Flag(Dictionary<string, object> node, string name) => node.TryGetValue(name, out var value) && Convert.ToBoolean(value);
    private static bool IsDeferred(Dictionary<string, object> node)
    {
        var context = Flag(node, "isContextReference");
        var unknown = Flag(node, "referenceTypeUnverified");
        if (!context && !unknown) return false;
        if (context && unknown || Convert.ToInt32(node["role"]) != (int)CadReferenceRole.ExternalFileDependency
            || Children(node).Any() || Convert.ToInt32(node["status"]) == (int)CadReferenceStatus.Virtual
            || context && Convert.ToInt32(node["kind"]) != (int)CadDocumentKind.Assembly)
            throw new InvalidOperationException("提交快照的引用类型记录不完整，尚未上传：" + Name(node));
        return true;
    }
}
