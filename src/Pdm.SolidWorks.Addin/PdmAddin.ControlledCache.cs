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

    private Task<ControlledCacheUsage> ReadControlledCacheUsageAsync()
    {
        var root = controlledWorkspace?.WorkspaceRoot ?? Upton.Pdm.LocalSettings.WorkspaceSettingsStore.GetWorkspaceRoot();
        var paths = CaptureProtectedCachePaths(); var ids = CaptureCheckedOutCacheDocuments();
        return Task.Run(() => ControlledCacheMaintenance.Inspect(root, paths, ids));
    }
    private Task<ControlledCacheUsage> CleanControlledCacheAsync()
    {
        var root = controlledWorkspace?.WorkspaceRoot ?? Upton.Pdm.LocalSettings.WorkspaceSettingsStore.GetWorkspaceRoot();
        var paths = CaptureProtectedCachePaths(); var ids = CaptureCheckedOutCacheDocuments();
        var limit = (pluginSettings?.CacheLimitGigabytes ?? 20) * 1073741824L;
        return Task.Run(() => ControlledCacheMaintenance.Clean(root, paths, ids, limit, true, ArchiveTemporaryCopyCleanupBusy));
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
                    var result = ControlledCacheMaintenance.Clean(root, paths, ids, limit, false, ArchiveTemporaryCopyCleanupBusy);
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
