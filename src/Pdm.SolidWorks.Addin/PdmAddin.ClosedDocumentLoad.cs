using System;
using System.Collections.Generic;
using System.Linq;
using SolidWorks.Interop.sldworks;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    private readonly HashSet<string> pendingNativeOpenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private int closingLoadEpoch = -1;
    private int emptySessionIdle = -1;
    private int emptySessionCommand;
    private bool checkingClosedDocumentLoad;

    private DAssemblyDocEvents_DestroyNotify2EventHandler assemblyClosing;
    private DPartDocEvents_DestroyNotify2EventHandler partClosing;
    private DDrawingDocEvents_DestroyNotify2EventHandler drawingClosing;

    // DestroyNotify2 is a pre-notification. Inspect only the cached paths here.
    // An unrelated file open must keep its protection even if the old window closes.
    private int RecordBoundDocumentClosing(int reason, string path)
    {
        if ((reason != 0 && reason != 1) || nativeFileOpenInProgress == 0
            || string.IsNullOrWhiteSpace(path) || pendingNativeOpenPaths.Count == 0
            || pendingNativeOpenPaths.Contains(path) || pendingNativeOpenPaths.Any(string.IsNullOrWhiteSpace)) return 0;
        try
        {
            var unverified = new HashSet<string>(pendingNativeOpenPaths, StringComparer.OrdinalIgnoreCase);
            if (PathsEqual(currentTree?.FullPath, path))
                unverified.ExceptWith(EnumerateTree(currentTree).Select(node => node.FullPath));
            if (unverified.Count > 0 && !controlledOpenManifests.Any(context =>
                PathsEqual(ManagedCadPaths.Resolve(context.Key, context.Value.RootRelativePath), path)
                && unverified.IsSubsetOf(context.Value.Files.Select(file => ControlledReferenceMaterializer.ManifestPath(context.Key, file)))))
                return 0;
            closingLoadEpoch = nativeLoadEpoch;
            emptySessionIdle = -1;
        }
        catch (Exception error) { LogDiagnostic("核对关闭图档的引用范围", error); }
        return 0;
    }

    private void TryCompleteClosedDocumentLoad()
    {
        if (closingLoadEpoch != nativeLoadEpoch || checkingClosedDocumentLoad) return;
        var epoch = nativeLoadEpoch;
        var command = nativeCommandSequence;
        bool SameLoad() => nativeFileOpenInProgress != 0 && nativeLoadEpoch == epoch
            && closingLoadEpoch == epoch && nativeCommandSequence == command && NativeInsertionEvidenceAllowed();
        checkingClosedDocumentLoad = true;
        try
        {
            if (!SameLoad() || application.CommandInProgress || application.ActiveDoc != null || application.GetDocumentCount() != 0)
            {
                emptySessionIdle = -1;
                return;
            }
            application.GetRunningCommandInfo(out _, out _, out var active);
            if (active || !SameLoad())
            {
                emptySessionIdle = -1;
                return;
            }
            var confirmed = emptySessionIdle >= 0 && emptySessionIdle != nativeIdleSequence && emptySessionCommand == command;
            emptySessionIdle = nativeIdleSequence;
            emptySessionCommand = command;
            if (!confirmed) return;
            // A native getter can dispatch another document event before it returns.
            if (application.ActiveDoc != null || application.GetDocumentCount() != 0 || application.CommandInProgress || !SameLoad())
            {
                emptySessionIdle = -1;
                return;
            }
            closingLoadEpoch = -1;
            CompleteNativeFileOpen(pendingOpenPath);
            taskPaneControl?.ClearCheckoutReminder(ArchiveBusyReminder);
            LogOperation("已核对最后图档关闭且无加载文档，恢复设计树和排队的打开请求");
        }
        catch (Exception error)
        {
            emptySessionIdle = -1;
            LogDiagnostic("核对最后图档关闭状态", error);
        }
        finally { checkingClosedDocumentLoad = false; }
    }
}
