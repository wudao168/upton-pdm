using System;
using System.Collections.Generic;

namespace Upton.Pdm.Domain;

// Shared graph operation only. Callers retain the evidence and reference type of
// every edge; membership in a group does not by itself prove a design error.
public static class ReferenceCycleGroups
{
    public static IReadOnlyList<IReadOnlyList<T>> Find<T>(
        IEnumerable<KeyValuePair<T, T>> edges, IEqualityComparer<T> comparer) where T : notnull
    {
        var forward = new Dictionary<T, HashSet<T>>(comparer);
        var reverse = new Dictionary<T, HashSet<T>>(comparer);
        void Add(T node)
        {
            if (forward.ContainsKey(node)) return;
            forward.Add(node, new HashSet<T>(comparer));
            reverse.Add(node, new HashSet<T>(comparer));
        }
        foreach (var edge in edges)
        {
            Add(edge.Key); Add(edge.Value);
            forward[edge.Key].Add(edge.Value);
            reverse[edge.Value].Add(edge.Key);
        }
        var visited = new HashSet<T>(comparer);
        var finished = new List<T>();
        // Iterative traversal avoids using the process call stack for large CAD graphs.
        foreach (var start in forward.Keys)
        {
            if (visited.Contains(start)) continue;
            var pending = new Stack<KeyValuePair<T, bool>>();
            pending.Push(new KeyValuePair<T, bool>(start, false));
            while (pending.Count != 0)
            {
                var frame = pending.Pop();
                if (frame.Value) { finished.Add(frame.Key); continue; }
                if (!visited.Add(frame.Key)) continue;
                pending.Push(new KeyValuePair<T, bool>(frame.Key, true));
                foreach (var child in forward[frame.Key])
                    if (!visited.Contains(child)) pending.Push(new KeyValuePair<T, bool>(child, false));
            }
        }
        visited.Clear();
        var result = new List<IReadOnlyList<T>>();
        for (var index = finished.Count - 1; index >= 0; index--)
        {
            var start = finished[index];
            if (!visited.Add(start)) continue;
            var group = new List<T>();
            var pending = new Stack<T>(); pending.Push(start);
            while (pending.Count != 0)
            {
                var node = pending.Pop(); group.Add(node);
                foreach (var parent in reverse[node]) if (visited.Add(parent)) pending.Push(parent);
            }
            if (group.Count > 1 || forward[start].Contains(start)) result.Add(group);
        }
        return result;
    }
}
