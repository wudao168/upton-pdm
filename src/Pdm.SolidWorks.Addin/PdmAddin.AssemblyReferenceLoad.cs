using System;
using System.Collections.Generic;
using System.Linq;
using SolidWorks.Interop.sldworks;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    private AssemblyReferenceLoad assemblyReferenceLoad;
    private AssemblyReferenceLoad savedAssemblyActivation;

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
        internal bool DraftSnapshot;
        internal int FinishedIdleSequence;
        internal bool NativeSave, ActivationObserved;
        internal bool OwnerReferenceProbeFailed;
        internal IModelDoc2 ActivatedPart;
        internal int PartStamp;

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

    private AssemblyReferenceLoad BeginAssemblyDraftReferenceLoad(IModelDoc2 owner, LocalEditDraft entry)
    {
        if (owner is not IAssemblyDoc || nativeFileOpenInProgress != 0 || assemblyReferenceLoad != null
            || !ReferenceEquals(owner, assemblyEventDocument) || currentTree?.DocumentId != entry.DocumentId
            || !PathsEqual(currentTree.FullPath, entry.SourcePath) || !PathsEqual(assemblyEventDocumentPath, entry.SourcePath)) return null;
        var load = new AssemblyReferenceLoad(owner, entry.SourcePath, forReuse: false)
        { DraftSnapshot = true, CommandSequence = nativeCommandSequence };
        var context = FindBoundAssemblyLoadContext(entry.ProjectId, entry.DocumentId, entry.SourcePath);
        if (context.Value != null)
            load.References.UnionWith(context.Value.Files.Where(file => !file.IsRoot && IsNativeModelReference(file.FileName))
                .Select(file => ControlledReferenceMaterializer.ManifestPath(context.Key, file)));
        load.References.UnionWith(EnumerateCadNodes(currentTree).Where(node => node != currentTree && IsNativeModelReference(node.FullPath))
            .Select(node => node.FullPath));
        assemblyReferenceLoad = load;
        return load;
    }

    private KeyValuePair<string, ControlledOpenManifestDto> FindBoundAssemblyLoadContext(Guid? projectId, Guid? documentId, string path) =>
        controlledOpenManifests.FirstOrDefault(pair => pair.Value.ProjectId == projectId
            && pair.Value.RootDocumentId == documentId && pair.Value.Files.Any(file => file.IsRoot
                && PathsEqual(ControlledReferenceMaterializer.ManifestPath(pair.Key, file), path)));

    private void ObserveAssemblyReferenceLoad(string path)
    {
        var load = assemblyReferenceLoad;
        if (load == null) return;
        if (load.Completed || string.IsNullOrWhiteSpace(path) || (load.DraftSnapshot
            ? draftSnapshotInProgress == 0 || !load.References.Contains(path) || load.CommandSequence != nativeCommandSequence
            : load.ForReuse
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
            && load.Observed.IsSubsetOf(load.References)
            && (!load.DraftSnapshot || load.CommandSequence == nativeCommandSequence
                && load.Observed.SetEquals(pendingNativeOpenPaths));
        load.FinishedIdleSequence = nativeIdleSequence;
        if (!load.Completed) assemblyReferenceLoad = null;
    }

    // A native assembly save can resolve lightweight children without a matching
    // FileOpenPostNotify. Save-post precedes the subsequent switch to a resident part.
    private void ObserveSavedAssemblyReferenceLoads(string path)
    {
        if (managedSaveInProgress || assemblyReferenceLoad != null || nativeFileOpenCommandId != 0 || nativeFileOpenInProgress == 0
            || !NativeInsertionEvidenceAllowed() || unknownLoadOwner == null
            || !ReferenceEquals(unknownLoadOwner, assemblyEventDocument) || !PathsEqual(path, unknownLoadOwnerPath)
            || !PathsEqual(path, currentTree?.FullPath) || !PathsEqual(path, assemblyEventDocumentPath)
            || pendingNativeOpenPaths.Count == 0 || pendingNativeOpenPaths.Contains(path)
            || FindBoundAssemblyLoadContext(currentProjectId, currentTree?.DocumentId, path).Value == null) return;
        var load = new AssemblyReferenceLoad(unknownLoadOwner, path, false)
        { NativeSave = true, Completed = true, Epoch = nativeLoadEpoch, CommandSequence = nativeCommandSequence };
        load.Observed.UnionWith(pendingNativeOpenPaths);
        savedAssemblyActivation = load;
    }

    private bool SavedAssemblyContextReady(AssemblyReferenceLoad load, IModelDoc2 active)
    {
        if (pendingOpenIdleEpoch != load.Epoch || active == null
            || nativeCommandSequence != load.CommandSequence || !ReferenceEquals(load.Owner, assemblyEventDocument)
            || !PathsEqual(load.OwnerPath, currentDocumentIdentity) || load.Owner.GetSaveFlag()
            || !load.Observed.SetEquals(pendingNativeOpenPaths)) return false;
        var stillInOwner = ReferenceEquals(active, load.Owner);
        var path = active.GetPathName();
        if (stillInOwner)
        {
            if (load.ActivationObserved || load.Owner.IsOpenedViewOnly() || active.GetBlockingState() != 0) return false;
        }
        else if (!load.ActivationObserved || unknownRemovalObserved || string.IsNullOrEmpty(path) || DocumentKindFromPath(path) != CadDocumentKind.Part
            || load.Observed.Contains(path) || active.GetBlockingState() != 0 || !ReferenceEquals(active, application.GetOpenDocumentByName(path))) return false;
        var context = FindBoundAssemblyLoadContext(currentProjectId, currentTree?.DocumentId, load.OwnerPath);
        if (context.Value == null) return false;
        var references = new HashSet<string>(context.Value.Files.Where(file => !file.IsRoot && IsNativeModelReference(file.FileName))
            .Select(file => ControlledReferenceMaterializer.ManifestPath(context.Key, file)), StringComparer.OrdinalIgnoreCase);
        return (stillInOwner || references.Contains(path)) && load.Observed.IsSubsetOf(references);
    }

    // A native Save3 after removing a component may load an already-resident
    // sibling without opening its own window. Verify the surviving native
    // occurrence itself. Never resolve, rebuild or activate a model as evidence.
    private bool SavedOwnerReferencesAreResident(AssemblyReferenceLoad load)
    {
        if (!(load.Owner is IAssemblyDoc assembly)) return false;
        var pending = new HashSet<string>(load.Observed, StringComparer.OrdinalIgnoreCase);
        // SOLIDWORKS documents this API for drawings only. The assembly and
        // its model references are proven through their native blocking state.
        if (application.EnableBackgroundProcessing)
        {
            var documents = application.GetDocuments() as Array;
            if (documents == null || documents.OfType<IModelDoc2>().Select(model => model.GetPathName())
                .Any(path => DocumentKindFromPath(path) == CadDocumentKind.Drawing && !application.IsBackgroundProcessingCompleted(path))) return false;
        }
        var treePaths = EnumerateCadNodes(currentTree)
            .Where(node => node.Role == CadReferenceRole.Component && pending.Contains(node.FullPath)
                && !string.IsNullOrWhiteSpace(node.ComponentSelectionName));
        void Match(IComponent2 component)
        {
            var path = component?.GetPathName();
            if (string.IsNullOrWhiteSpace(path) || !pending.Contains(path)) return;
            var suppression = component.GetSuppression2();
            if (suppression != 2 && suppression != 3) return;
            var model = component.GetModelDoc2() as IModelDoc2;
            if (model != null && PathsEqual(model.GetPathName(), path) && model.GetBlockingState() == 0
                && ReferenceEquals(model, application.GetOpenDocumentByName(path))) pending.Remove(path);
        }
        foreach (var node in treePaths)
        {
            Match(FindAssemblyComponent(assembly, node.ComponentSelectionName));
            if (pending.Count == 0) return true;
        }
        // A tree drawn from the PLM manifest may lack native selection names.
        // Check only the owner's direct instances; never load or resolve children.
        foreach (var component in (assembly.GetComponents(true) as Array)?.OfType<IComponent2>() ?? Enumerable.Empty<IComponent2>())
        {
            Match(component);
            if (pending.Count == 0) return true;
        }
        return pending.Count == 0;
    }

    private bool TryCompleteAssemblyReferenceLoad()
    {
        var load = assemblyReferenceLoad ?? savedAssemblyActivation;
        var commandSequence = nativeCommandSequence;
        bool SameLoad() => load != null && load.Completed && ReferenceEquals(load, load.NativeSave ? savedAssemblyActivation : assemblyReferenceLoad)
            && load.Epoch == nativeLoadEpoch && nativeFileOpenInProgress != 0 && !managedSaveInProgress
            && commandSequence == nativeCommandSequence && NativeInsertionEvidenceAllowed()
            && (!load.DraftSnapshot || load.Observed.SetEquals(pendingNativeOpenPaths)
                && ReferenceEquals(load.Owner, assemblyEventDocument) && PathsEqual(load.OwnerPath, assemblyEventDocumentPath));
        if (!SameLoad()) return false;
        try
        {
            if (application == null || !application.StartupProcessCompleted || application.CommandInProgress) return false;
            application.GetRunningCommandInfo(out _, out _, out var active);
            if (active) return false;
            var activeModel = application.ActiveDoc as IModelDoc2;
            if ((load.NativeSave ? !SavedAssemblyContextReady(load, activeModel) : !ReferenceEquals(activeModel, load.Owner))
                || !PathsEqual(load.Owner.GetPathName(), load.OwnerPath) || load.Owner.GetBlockingState() != 0) return false;
            // Reuse can await server data, and a draft SaveAs can pump Idle inside
            // SolidWorks. Verify two distinct idle turns after the draft returns.
            if (load.ForReuse || load.NativeSave || load.DraftSnapshot)
            {
                if (pendingOpenIdleEpoch != load.Epoch) return false;
                if (load.DraftSnapshot && nativeIdleSequence == load.FinishedIdleSequence) return false;
                var stamp = load.Owner.GetUpdateStamp();
                var count = application.GetDocumentCount();
                var partStamp = load.NativeSave ? activeModel.GetUpdateStamp() : 0;
                if (!SameLoad()) return false;
                if (load.IdleSequence < 0 || count != load.DocumentCount || stamp != load.OwnerStamp
                    || commandSequence != load.CommandSequence || load.NativeSave
                    && (partStamp != load.PartStamp || !ReferenceEquals(load.ActivatedPart, activeModel)))
                {
                    load.IdleSequence = nativeIdleSequence;
                    load.OwnerStamp = stamp;
                    load.DocumentCount = count;
                    load.CommandSequence = commandSequence;
                    load.ActivatedPart = activeModel;
                    load.PartStamp = partStamp;
                    load.OwnerReferenceProbeFailed = false;
                    return false;
                }
                if (load.IdleSequence == nativeIdleSequence || count < 1) return false;
                if (load.NativeSave && ReferenceEquals(activeModel, load.Owner))
                {
                    if (load.OwnerReferenceProbeFailed) return false;
                    load.OwnerReferenceProbeFailed = !SavedOwnerReferencesAreResident(load);
                    if (load.OwnerReferenceProbeFailed) return false;
                }
                if (load.ForReuse || load.DraftSnapshot)
                {
                    // Verify each unresolved notification without loading anything.
                    foreach (var path in load.Observed.Where(path => load.DraftSnapshot || !load.Loaded.Contains(path)).ToArray())
                    {
                        var reference = application.GetOpenDocumentByName(path) as IModelDoc2;
                        var ready = reference == null ? load.Loaded.Contains(path)
                            : PathsEqual(reference.GetPathName(), path) && reference.GetBlockingState() == 0;
                        if (!ready || !SameLoad()) return false;
                    }
                }
                if (load.NativeSave && ((!ReferenceEquals(activeModel, load.Owner) && count < 2) || load.Owner.GetSaveFlag() || load.Owner.GetUpdateStamp() != stamp
                    || activeModel.GetBlockingState() != 0 || activeModel.GetUpdateStamp() != partStamp
                    || application.GetDocumentCount() != count)) return false;
                if (load.DraftSnapshot && (load.Owner.GetUpdateStamp() != stamp || load.Owner.GetBlockingState() != 0
                    || application.GetDocumentCount() != count)) return false;
            }
            application.GetRunningCommandInfo(out _, out _, out active);
            if (active || application.CommandInProgress || !ReferenceEquals(application.ActiveDoc, activeModel)
                || !SameLoad()) return false;
            if (!load.NativeSave) assemblyReferenceLoad = null;
            CompleteNativeFileOpen(pendingOpenPath);
            LogOperation("已核对总装" + (load.DraftSnapshot ? "草稿备份" : load.ForReuse ? "复用" : load.NativeSave && !ReferenceEquals(activeModel, load.Owner) ? "保存后切换至受控零件" : "保存") + "及本次引用加载完成，恢复保存、存档和设计树："
                + load.OwnerPath + "；引用数=" + load.Observed.Count);
            return true;
        }
        catch (Exception error) { LogDiagnostic("核对总装引用加载状态", error); return false; }
    }
}
