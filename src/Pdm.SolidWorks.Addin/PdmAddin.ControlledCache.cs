using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    private string[] CaptureLoadedCachePaths()
    {
        var paths = new List<string>();
        foreach (var model in ((application?.GetDocuments() as Array)?.OfType<IModelDoc2>() ?? Enumerable.Empty<IModelDoc2>()))
        { var path = model.GetPathName(); if (!string.IsNullOrWhiteSpace(path)) paths.Add(path); }
        return paths.ToArray();
    }
    private string[] CaptureProtectedCachePaths()
    {
        var paths = new List<string>(CaptureLoadedCachePaths());
        paths.AddRange(localEdits.Values.Where(draft => !draft.Closed || draft.RecoveryRequired || draft.RestoreOnOpen || draft.DiscardPending)
            .Select(draft => draft.SourcePath).Where(path => !string.IsNullOrWhiteSpace(path)));
        if (editJournal != null)
            paths.AddRange(editJournal.ReadAll().Where(draft => !draft.Closed || draft.RecoveryRequired || draft.RestoreOnOpen || draft.DiscardPending)
                .Select(draft => draft.SourcePath).Where(path => !string.IsNullOrWhiteSpace(path)));
        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private Guid[] CaptureCheckedOutCacheDocuments()
    { lock (checkoutDocumentSync) return activeCheckoutDocumentIds.ToArray(); }

    private DateTime nextCacheProtectionUtc;
    private void PublishCacheProtection(bool busy = false, string openingPath = null)
    {
        if (!busy && DateTime.UtcNow < nextCacheProtectionUtc) return;
        try
        {
            var root = controlledWorkspace?.WorkspaceRoot;
            if (root == null) return;
            var paths = busy ? (string.IsNullOrWhiteSpace(openingPath) ? Array.Empty<string>() : new[] { openingPath }) : CaptureProtectedCachePaths();
            var ids = CaptureCheckedOutCacheDocuments();
            if (busy) CacheInstanceRegistry.Publish(root, true, paths, ids);
            else CacheInstanceRegistry.TryPublish(root, ArchiveTemporaryCopyCleanupBusy(), paths, ids);
            nextCacheProtectionUtc = DateTime.UtcNow.AddSeconds(2);
        }
        catch (Exception error) { LogDiagnostic("更新缓存保护状态", error); }
    }

    private int MarkCacheOperationBusy(ref int operation)
    {
        var previous = System.Threading.Interlocked.CompareExchange(ref operation, 1, 0);
        PublishCacheProtection(true);
        return previous;
    }

    private ControlledCacheUsage MaintainControlledCache(string root, string[] paths, Guid[] ids, long limit, bool manual) =>
        CacheInstanceRegistry.WithGate(root, () => MaintainControlledCacheCore(root, paths, ids, limit, manual));
    private ControlledCacheUsage MaintainControlledCacheCore(string root, string[] paths, Guid[] ids, long limit, bool manual)
    {
        var other = CacheInstanceRegistry.Read(root);
        paths = paths.Concat(other.Paths).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        ids = ids.Concat(other.CheckedOut).Distinct().ToArray();
        if (other.UnconfirmedProcesses.Count != 0)
        {
            var kept = ControlledCacheMaintenance.Inspect(root, paths, ids);
            kept.ReclaimableBytes = 0;
            kept.DeferredItems = kept.ManagedItems;
            kept.Notice = other.Notice;
            return kept;
        }
        return ControlledCacheMaintenance.Clean(root, paths, ids, limit, manual, () =>
            ArchiveTemporaryCopyCleanupBusy() || CacheInstanceRegistry.Read(root).Revision != other.Revision);
    }

    private Task<ControlledCacheUsage> ReadControlledCacheUsageAsync()
    {
        var root = controlledWorkspace?.WorkspaceRoot ?? Upton.Pdm.LocalSettings.WorkspaceSettingsStore.GetWorkspaceRoot();
        var paths = CaptureProtectedCachePaths(); var ids = CaptureCheckedOutCacheDocuments();
        return Task.Run(() =>
        {
            var other = CacheInstanceRegistry.Read(root);
            var usage = ControlledCacheMaintenance.Inspect(root, paths.Concat(other.Paths).ToArray(), ids.Concat(other.CheckedOut).ToArray());
            if (other.UnconfirmedProcesses.Count != 0)
            { usage.ReclaimableBytes = 0; usage.DeferredItems = usage.ManagedItems; usage.Notice = other.Notice; }
            return usage;
        });
    }
    private Task<ControlledCacheUsage> CleanControlledCacheAsync()
    {
        var root = controlledWorkspace?.WorkspaceRoot ?? Upton.Pdm.LocalSettings.WorkspaceSettingsStore.GetWorkspaceRoot();
        var paths = CaptureProtectedCachePaths(); var ids = CaptureCheckedOutCacheDocuments();
        var limit = (pluginSettings?.CacheLimitGigabytes ?? 20) * 1073741824L;
        return Task.Run(() => MaintainControlledCache(root, paths, ids, limit, true));
    }
    private void ScheduleControlledCacheMaintenance()
    {
        try
        {
            var root = controlledWorkspace?.WorkspaceRoot; if (root == null) return;
            var paths = CaptureProtectedCachePaths(); var ids = CaptureCheckedOutCacheDocuments();
            var limit = (pluginSettings?.CacheLimitGigabytes ?? 20) * 1073741824L;
            _ = Task.Run(() =>
            {
                try
                {
                    var result = MaintainControlledCache(root, paths, ids, limit, false);
                    PluginDiagnosticLog.Record("缓存容量维护", result.Message);
                    if (result.UsedBytes > limit) PluginDiagnosticLog.Record("缓存容量维护",
                        "缓存仍超过设置上限；正在使用、含修改及归属未确认的文件已保留，请完成编辑后在设置中清理。");
                }
                catch (Exception error) { PluginDiagnosticLog.Record("缓存容量维护", "本次未清理，原文件已保留：" + error); }
            });
        }
        catch (Exception error) { LogDiagnostic("缓存容量维护未启动，文件已保留", error); }
    }
}
