using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using SolidWorks.Interop.sldworks;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    private ArchiveReadContext archiveBatchReads;
    private Dictionary<string, CadTreeNode[]> archiveCurrentInstances, archiveCachedInstances;

    private Func<string, bool> BrokenReferencePredicate(CadTreeNode node, CancellationToken token)
    {
        HashSet<string> verified = null;
        return target =>
        {
            token.ThrowIfCancellationRequested();
            if (verified == null)
            {
                verified = ArchiveReadContext.Read(node, "批量核对历史断开引用", () =>
                    new SolidWorksReferenceTreeScanner(application).ReadVerifiedBrokenContextReferences(node.FullPath));
                token.ThrowIfCancellationRequested();
            }
            return verified.Contains(target);
        };
    }

    private static Dictionary<string, CadTreeNode[]> IndexArchiveInstances(IEnumerable<CadTreeNode> nodes) =>
        nodes.Where(node => !string.IsNullOrWhiteSpace(node.FullPath)).Distinct()
            .GroupBy(node => node.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
    // One dependency pass, on the CAD STA. No COM document or mutable result is
    // retained across saves or later archive operations.
    private sealed class ArchiveReadContext
    {
        private readonly PdmAddin owner;
        private readonly Dictionary<string, IModelDoc2> loaded = new Dictionary<string, IModelDoc2>(StringComparer.OrdinalIgnoreCase);
        private int loadEpoch = -1;
        internal ArchiveReadContext(PdmAddin owner) => this.owner = owner;
        internal void Invalidate() => loadEpoch = -1;

        internal IModelDoc2 FindLoaded(string path)
            => FindLoadedCore(path, true);

        private IModelDoc2 FindLoadedCore(string path, bool retryInvalidDocument)
        {
            if (loadEpoch != owner.nativeLoadEpoch)
            {
                loaded.Clear();
                var epoch = owner.nativeLoadEpoch;
                var documents = Read<object>(null, "建立已加载文档索引", () => owner.application.GetDocuments()) as Array;
                foreach (var item in documents ?? Array.Empty<object>())
                    if (item is IModelDoc2 model)
                    {
                        try
                        {
                            var modelPath = PathKey(model.GetPathName());
                            if (modelPath != null && !loaded.ContainsKey(modelPath)) loaded.Add(modelPath, model);
                        }
                        catch (Exception error) when (error is COMException || error is InvalidComObjectException)
                        { LogDiagnostic("忽略已关闭的文档索引", error); }
                    }
                loadEpoch = epoch;
            }
            var key = PathKey(path);
            if (key == null || !loaded.TryGetValue(key, out var found)) return null;
            try { if (PathsEqual(found.GetPathName(), path)) return found; }
            catch (Exception error) when (error is COMException || error is InvalidComObjectException)
            { LogDiagnostic("文档索引已失效", error); }
            loaded.Remove(key);
            loadEpoch = -1;
            return retryInvalidDocument ? FindLoadedCore(path, false) : null;
        }

        private static string PathKey(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try { return Path.GetFullPath(path); }
            // Preserve native exact-path matching for paths the runtime cannot
            // normalize (for example a long path already opened by SolidWorks).
            catch (Exception error) when (error is ArgumentException || error is NotSupportedException || error is PathTooLongException) { return path; }
        }

        internal IModelDoc2[] FindByName(string name)
        {
            FindLoaded(null); // Refresh once after a native load event.
            return loaded.Keys.Where(path => string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase)).ToArray()
                .Select(FindLoaded).Where(model => model != null).ToArray();
        }

        internal static T Read<T>(CadTreeNode document, string step, Func<T> action)
        {
            ArchiveSession.Current?.Token.ThrowIfCancellationRequested();
            var context = "；图档=" + (document?.FullPath ?? "当前会话");
            PluginDiagnosticLog.Record("引用核对步骤", "开始：" + step + context);
            if (document != null) ArchiveSession.Current?.ReportFile(0, 0, document.FileName, "引用核对：" + step + "…");
            var timer = Stopwatch.StartNew(); var returned = false;
            try { var result = action(); returned = true; ArchiveSession.Current?.Token.ThrowIfCancellationRequested(); return result; }
            finally { PluginDiagnosticLog.Record("引用核对步骤", (returned ? "接口已返回：" : "读取中断：") + step
                + "；耗时=" + timer.ElapsedMilliseconds + "毫秒" + context); }
        }

        internal static IDisposable Measure(CadTreeNode document, string step) => new Measurement(document, step);

        private sealed class Measurement : IDisposable
        {
            private readonly string description;
            private readonly Stopwatch elapsed = Stopwatch.StartNew();
            internal Measurement(CadTreeNode document, string step)
            {
                description = step + "；图档=" + (document?.FullPath ?? "当前会话");
                PluginDiagnosticLog.Record("存档分段耗时", "开始：" + description);
            }
            public void Dispose() => PluginDiagnosticLog.Record("存档分段耗时", "结束：" + description + "；耗时=" + elapsed.ElapsedMilliseconds + "毫秒");
        }
    }
}
