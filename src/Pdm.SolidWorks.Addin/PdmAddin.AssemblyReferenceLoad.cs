using System;
using System.Collections.Generic;
using System.Linq;
using SolidWorks.Interop.sldworks;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    private AssemblyReferenceLoad assemblyReferenceLoad;

    private sealed class AssemblyReferenceLoad
    {
        internal readonly IModelDoc2 Owner;
        internal readonly string OwnerPath;
        internal readonly bool ForReuse;
        internal readonly HashSet<string> References = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal readonly HashSet<string> Observed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal readonly HashSet<string> Loaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal int Epoch, IdleSequence = -1, OwnerStamp, DocumentCount, CommandSequence;
        internal bool Completed;

        internal AssemblyReferenceLoad(IModelDoc2 owner, string ownerPath, bool forReuse)
        {
            Owner = owner;
            OwnerPath = ownerPath;
            ForReuse = forReuse;
        }
    }

    private AssemblyReferenceLoad BeginAssemblyReuseReferenceLoad(Guid projectId, Guid rootId)
    {
        // Capture before enumerating native documents; enumerating a lightweight
        // assembly can itself request files. Only the bound assembly qualifies.
        if (nativeFileOpenInProgress != 0 || assemblyReferenceLoad != null || assemblyEventDocument == null
            || currentTree?.DocumentId != rootId || !PathsEqual(currentTree.FullPath, assemblyEventDocumentPath)) return null;
        var context = controlledOpenManifests.FirstOrDefault(pair => pair.Value.ProjectId == projectId
            && pair.Value.RootDocumentId == rootId && pair.Value.Files.Any(file => file.DocumentId == rootId
                && PathsEqual(ControlledReferenceMaterializer.ManifestPath(pair.Key, file), assemblyEventDocumentPath)));
        if (context.Value == null) return null;
        var load = new AssemblyReferenceLoad(assemblyEventDocument, assemblyEventDocumentPath, forReuse: true);
        load.References.UnionWith(context.Value.Files.Where(file => file.DocumentId != rootId && IsNativeModelReference(file.FileName))
            .Select(file => ControlledReferenceMaterializer.ManifestPath(context.Key, file)));
        assemblyReferenceLoad = load;
        return load;
    }

    private void ObserveAssemblyReferenceLoad(string path)
    {
        var load = assemblyReferenceLoad;
        if (load == null) return;
        if (load.Completed || string.IsNullOrWhiteSpace(path) || (load.ForReuse
            ? !load.References.Contains(path)
            : !managedSaveInProgress || nativeFileOpenCommandId != 0))
        {
            assemblyReferenceLoad = null;
            return;
        }
        load.Observed.Add(path);
        load.Loaded.Remove(path);
        load.Epoch = nativeLoadEpoch;
    }

    private void FinishAssemblyReferenceOperation(AssemblyReferenceLoad load, bool succeeded)
    {
        if (load == null || !ReferenceEquals(load, assemblyReferenceLoad)) return;
        load.Completed = succeeded && load.Observed.Count > 0 && load.Epoch == nativeLoadEpoch
            && nativeFileOpenInProgress != 0 && !load.Observed.Contains(load.OwnerPath)
            && load.Observed.IsSubsetOf(load.References);
        if (!load.Completed) assemblyReferenceLoad = null;
    }

    private bool TryCompleteAssemblyReferenceLoad()
    {
        var load = assemblyReferenceLoad;
        var commandSequence = nativeCommandSequence;
        bool SameLoad() => load != null && load.Completed && ReferenceEquals(load, assemblyReferenceLoad)
            && load.Epoch == nativeLoadEpoch && nativeFileOpenInProgress != 0 && !managedSaveInProgress
            && commandSequence == nativeCommandSequence && NativeInsertionEvidenceAllowed();
        if (!SameLoad()) return false;
        try
        {
            if (application == null || !application.StartupProcessCompleted || application.CommandInProgress) return false;
            application.GetRunningCommandInfo(out _, out _, out var active);
            if (active || !ReferenceEquals(application.ActiveDoc, load.Owner)
                || !PathsEqual(load.Owner.GetPathName(), load.OwnerPath) || load.Owner.GetBlockingState() != 0) return false;
            // Reuse may await server metadata. Confirm stable native state on two
            // distinct idle turns after successful reuse, before consuming its loads.
            if (load.ForReuse)
            {
                if (pendingOpenIdleEpoch != load.Epoch) return false;
                var stamp = load.Owner.GetUpdateStamp();
                var count = application.GetDocumentCount();
                if (!SameLoad()) return false;
                if (load.IdleSequence < 0 || count != load.DocumentCount || stamp != load.OwnerStamp
                    || commandSequence != load.CommandSequence)
                {
                    load.IdleSequence = nativeIdleSequence;
                    load.OwnerStamp = stamp;
                    load.DocumentCount = count;
                    load.CommandSequence = commandSequence;
                    return false;
                }
                if (load.IdleSequence == nativeIdleSequence || count < 1) return false;
                // Lightweight references need not expose a separate window. Accept
                // a native load notification or verify the exact resident document.
                foreach (var path in load.Observed.Where(path => !load.Loaded.Contains(path)).ToArray())
                {
                    var reference = application.GetOpenDocumentByName(path) as IModelDoc2;
                    if (reference == null || !PathsEqual(reference.GetPathName(), path)
                        || reference.GetBlockingState() != 0 || !SameLoad()) return false;
                }
            }
            application.GetRunningCommandInfo(out _, out _, out active);
            if (active || application.CommandInProgress || !ReferenceEquals(application.ActiveDoc, load.Owner)
                || !SameLoad()) return false;
            assemblyReferenceLoad = null;
            CompleteNativeFileOpen(pendingOpenPath);
            taskPaneControl?.ClearCheckoutReminder(ArchiveBusyReminder);
            LogOperation("已核对总装" + (load.ForReuse ? "复用" : "保存") + "及本次引用加载完成，恢复存档和设计树："
                + load.OwnerPath + "；引用数=" + load.Observed.Count);
            return true;
        }
        catch (Exception error) { LogDiagnostic("核对总装引用加载状态", error); return false; }
    }
}
