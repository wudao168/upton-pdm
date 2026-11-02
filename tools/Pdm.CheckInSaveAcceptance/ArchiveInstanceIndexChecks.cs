using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;

internal static partial class Program
{
    static void ArchiveInstanceIndexScenario(string output)
    {
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        void Check(bool ok, string text) { if (!ok) throw new Exception(text); Console.WriteLine("PASS: " + text); }
        var plugin = New("PdmAddin"); var id = Guid.NewGuid(); var otherId = Guid.NewGuid();
        object Node(string path, Guid? identity, bool historical = false)
        {
            var n = New("CadTreeNode"); Set(n, "FullPath", path); Set(n, "DocumentId", identity); Set(n, "IsHistoricalPreview", historical); return n;
        }
        var path = Path.Combine(output, "PART.SLDPRT"); var root = Node(Path.Combine(output, "ROOT.SLDASM"), Guid.NewGuid());
        var direct = new[] { Node(path, id), Node(Path.Combine(output, "different", "PART.SLDPRT"), id), Node(path.ToUpperInvariant(), null), Node(Path.Combine(output, "other", "PART.SLDPRT"), otherId) };
        foreach (var node in direct) ((IList)Get(root, "Children")).Add(node);
        var refs = new[] { Node(path, id), Node(path, otherId), Node(Path.Combine(output, "drawing-model", "PART.SLDPRT"), id) };
        foreach (var node in refs) ((IList)Get(root, "ReferencedModels")).Add(node);
        var cached = new[] { Node(path, otherId), Node(path, id, true), Node(Path.Combine(output, "cached-other", "PART.SLDPRT"), id) };
        var cacheRoot = Node(Path.Combine(output, "CACHE.SLDASM"), Guid.NewGuid()); foreach (var node in cached) ((IList)Get(cacheRoot, "Children")).Add(node);
        var entries = (IDictionary)Field(plugin, "activeTreeCache"); var entryType = entries.GetType().GetGenericArguments()[1];
        entries["test"] = Activator.CreateInstance(entryType, Flags, null, new object[] { cacheRoot, null, DateTime.UtcNow }, null);
        SetField(plugin, "currentTree", root);
        Array Nodes(IEnumerable<object> values) { var items = values.ToArray(); var array = Array.CreateInstance(Type("CadTreeNode"), items.Length); for (var i = 0; i < items.Length; i++) array.SetValue(items[i], i); return array; }
        var operations = direct.Concat(refs).Concat(cached).ToArray();
        var indexType = Type("PdmAddin").GetNestedType("ArchiveCommittedInstanceIndex", Flags);
        var index = Activator.CreateInstance(indexType, Flags, null, new object[] { root, Nodes(new[] { cacheRoot }.Concat(cached)), Nodes(operations) }, null);
        foreach (var source in new[] { Node(path, id), Node(Path.Combine(output, "sub", "..", "PART.SLDPRT"), id), Node(path, otherId) })
        {
            var documentId = (Guid)Get(source, "DocumentId");
            var old = ((IEnumerable)TreeCall(plugin, "FindMatchingDocumentInstances", source, documentId)).Cast<object>().ToArray();
            var current = ((IEnumerable)TreeCall(index, "Matches", source, documentId)).Cast<object>().ToArray();
            Check(old.Length == current.Length && old.All(current.Contains), "索引与原遍历匹配集合一致：" + Get(source, "FullPath") + " / " + documentId);
        }
        var matches = ((IEnumerable)TreeCall(index, "Matches", direct[0], id)).Cast<object>().ToArray();
        Check(!matches.Contains(cached[1]) && !matches.Contains(cached[2]) && !matches.Contains(refs[1]) && !matches.Contains(refs[2]), "历史预览、其他目录缓存及非匹配工程图引用保持排除");
        var operationMatches = ((IEnumerable)TreeCall(index, "Operations", direct[0])).Cast<object>().ToArray();
        var expected = operations.Where(node => (bool)Type("PdmAddin").GetMethod("PathsEqual", Flags).Invoke(null, new[] { Get(node, "FullPath"), Get(direct[0], "FullPath") })).Distinct().ToArray();
        Check(operationMatches.Length == expected.Length && expected.All(operationMatches.Contains), "批次实例索引保持原有路径匹配范围");
    }
}
