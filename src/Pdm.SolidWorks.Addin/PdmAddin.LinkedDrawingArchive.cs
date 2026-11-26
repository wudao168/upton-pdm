using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    private async Task AcquireLinkedDrawingPermissionsAsync(Guid projectId, IEnumerable<Guid> modelIds, string directory, CancellationToken token)
    {
        var ids = new HashSet<Guid>(modelIds);
        if (ids.Count == 0 || string.IsNullOrWhiteSpace(directory)) return;
        var warnings = new List<string>();
        try
        {
            var relations = FilterDrawingRelations(await apiClient.GetDocumentRelationsAsync(projectId, token), directory, true, ids);
            var drawingIds = new HashSet<Guid>(relations.Where(relation => ids.Contains(relation.ModelDocumentId)).Select(relation => relation.DrawingDocumentId));
            if (drawingIds.Count == 0) return;
            var documents = await apiClient.GetDocumentsAsync(projectId, token);
            foreach (var document in documents.Where(document => drawingIds.Contains(document.Id)))
            {
                try
                {
                    var path = Path.Combine(directory, Path.GetFileName(document.FileName));
                    var node = currentTree == null ? null : EnumerateCadNodes(currentTree).FirstOrDefault(candidate => candidate.DocumentId == document.Id && PathsEqual(candidate.FullPath, path));
                    if (node == null) node = new CadTreeNode { DocumentId = document.Id, FullPath = path, FileName = document.FileName,
                        Kind = CadDocumentKind.Drawing, DrawingReviewLocked = document.DrawingReviewLocked, LifecycleState = LifecycleStateName(document.State) };
                    if (!File.Exists(path)) throw new IOException("工程图尚未取回，存档时将补齐并重试。");
                    ApplyCheckoutDocument(node, document);
                    await PrepareBatchCheckInPermissionAsync(new BatchOperationItem(node, 0), projectId, token);
                    await EnsureCheckedOutDocumentEditableAsync(path, document.Id, projectId, node.CheckedOutAt);
                    LogOperation("随模型获取关联工程图权限：" + document.FileName);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception error)
                {
                    LogDiagnostic("关联工程图权限准备：" + document.FileName, error);
                    warnings.Add(document.FileName + "：" + ArchivePreflightError(error));
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            LogDiagnostic("读取模型关联工程图", error);
            warnings.Add("暂未完成工程图权限准备，存档时将重试。");
        }
        if (warnings.Count > 0) taskPaneControl?.SetCheckoutReminder("模型可继续编辑；关联工程图权限待处理：" + string.Join("；", warnings), true);
    }

    private Task<HashSet<string>> ChangedReadOnlyAssembliesAsync(IEnumerable<BatchOperationItem> items, CancellationToken token)
        => ChangedReadOnlyAssembliesCoreAsync(items, token, new ArchiveReadContext(this));

    private async Task<HashSet<string>> ChangedReadOnlyAssembliesCoreAsync(IEnumerable<BatchOperationItem> items, CancellationToken token, ArchiveReadContext reads)
    {
        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in DistinctBatchItems(items))
        {
            var node = item.Node;
            if (node.Kind != CadDocumentKind.Assembly || !node.DocumentId.HasValue
                || node.Role == CadReferenceRole.ExternalFileDependency || IsCheckedOutByCurrentUser(node)) continue;
            var loaded = reads.FindLoaded(node.FullPath);
            if (loaded?.GetSaveFlag() != true || !loaded.IsOpenedReadOnly()) continue;
            var latest = (await apiClient.GetVersionsAsync(node.DocumentId.Value, token)).FirstOrDefault();
            // A rebuild/save flag alone is not an assembly edit. Insertion/removal/replacement is.
            if (latest?.ReferenceSnapshot != null && !OwnAssemblyStructureMatches(latest.ReferenceSnapshot, node))
                changed.Add(node.FullPath);
        }
        return changed;
    }

    private async Task<IReadOnlyList<BatchOperationItem>> IncludeChangedAssemblyOwnersAsync(
        IReadOnlyList<BatchOperationItem> selected, IReadOnlyList<BatchOperationItem> all, CancellationToken token)
    {
        var ownerPaths = new HashSet<string>(selected.SelectMany(item => item.Ancestors).Select(node => node.FullPath), StringComparer.OrdinalIgnoreCase);
        var candidates = all.Where(item => ownerPaths.Contains(item.Node.FullPath)).ToArray();
        var changed = await ChangedReadOnlyAssembliesAsync(candidates, token);
        return DistinctBatchItems(selected.Concat(candidates.Where(item => changed.Contains(item.Node.FullPath))));
    }

    private static bool DrawingUsesModel(CadTreeNode drawing, CadTreeNode model) => drawing.ReferencedModels.Any(reference =>
        PathsEqual(reference.FullPath, model.FullPath) || model.DocumentId.HasValue && reference.DocumentId == model.DocumentId);

    internal static bool DrawingUsesWorkingModel(CadTreeNode drawing, CadTreeNode model) => drawing.ReferencedModels.Any(reference =>
        PathsEqual(reference.FullPath, model.FullPath) || model.DocumentId.HasValue && reference.DocumentId == model.DocumentId
            && string.IsNullOrWhiteSpace(reference.FullPath) && PathsEqual(Path.GetDirectoryName(drawing.FullPath), Path.GetDirectoryName(model.FullPath)));

    private async Task IncludeChangedModelDrawingsAsync(IReadOnlyList<BatchOperationItem> candidates,
        List<BatchOperationItem> planned, Dictionary<string, BatchCheckInPreflight> preflights, CancellationToken token)
    {
        var modelHistories = new Dictionary<Guid, IReadOnlyList<DocumentVersionDto>>();
        var changedModels = planned.Where(item => item.Node.Kind != CadDocumentKind.Drawing
            && item.Node.Role != CadReferenceRole.ExternalFileDependency)
            .Where(item => !preflights.TryGetValue(item.Node.FullPath, out var preflight)
                || !VersionMatchesLocalFile(preflight.Versions.FirstOrDefault(), item.Node.FullPath, preflight.LocalSha256)
                || item.Node.Kind == CadDocumentKind.Assembly && !OwnAssemblyStructureMatches(preflight.Versions.FirstOrDefault()?.ReferenceSnapshot, item.Node))
            .Select(item => item.Node).ToArray();
        var visibleDrawings = (currentTree == null ? Enumerable.Empty<CadTreeNode>() : EnumerateCadNodes(currentTree))
            .Concat(changedModels.SelectMany(model => EnumerateCadNodes(model)))
            .Where(node => node.Kind == CadDocumentKind.Drawing && node.DocumentId.HasValue
                && changedModels.Any(model => DrawingUsesWorkingModel(node, model)))
            .Select(node => new BatchOperationItem(node, 1));
        foreach (var item in DistinctBatchItems(candidates.Where(item => item.Node.Kind == CadDocumentKind.Drawing && item.Node.DocumentId.HasValue)
            .Concat(visibleDrawings)))
        {
            var drawing = item.Node;
            // Candidate associations can predate a drawing's current views. Only
            // auto-added drawings may be excluded, and only after a complete read.
            if (!candidates.Any(selected => PathsEqual(selected.Node.FullPath, drawing.FullPath))
                && IsUnrelatedAutomaticDrawing(drawing, changedModels))
            {
                LogOperation("工程图实际引用不包含本次修改的模型，未自动纳入存档：" + drawing.FileName);
                continue;
            }
            if (!preflights.TryGetValue(drawing.FullPath, out var preflight))
            {
                var versions = await apiClient.GetVersionsAsync(drawing.DocumentId.Value, token);
                preflight = BatchCheckInPreflight.Create(drawing.FullPath, versions, ComputeFileHash(drawing.FullPath));
                preflights[drawing.FullPath] = preflight;
            }
            var modelChanged = changedModels.Any(model => DrawingUsesModel(drawing, model));
            var snapshot = preflight.Versions.FirstOrDefault()?.ReferenceSnapshot;
            // Native inventory context nodes have no occurrence configuration. Only
            // unchanged drawing bytes can prove their saved view configuration.
            var sameFile = VersionMatchesLocalFile(preflight.Versions.FirstOrDefault(), drawing.FullPath, ComputeFileHash(drawing.FullPath));
            var sameDrawing = sameFile && drawing.ReferencedModels.Any(model => model.Role == CadReferenceRole.ExternalFileDependency
                && string.IsNullOrWhiteSpace(model.Configuration));
            Func<DocumentReferenceNodeDto, CadTreeNode, bool> savedViewConfiguration = (before, after) => sameDrawing
                && after.Role == CadReferenceRole.ExternalFileDependency && string.IsNullOrWhiteSpace(after.Configuration)
                && !string.IsNullOrWhiteSpace(before.Configuration);
            var referencesMatch = ReferenceSnapshotMatchesTree(snapshot, drawing, true, null, savedViewConfiguration);
            if (!modelChanged && sameFile && DrawingModelIndexMissing(snapshot, drawing)) referencesMatch = true;
            if (!modelChanged && !referencesMatch && snapshot != null)
            {
                var equivalent = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                Func<DocumentReferenceNodeDto, CadTreeNode, string> key = (before, after) =>
                    before.DocumentId + "|" + before.Revision?.Display + "|" + (after.CurrentRevision ?? string.Empty).TrimEnd('*');
                foreach (var model in drawing.ReferencedModels.Where(model => model.DocumentId.HasValue))
                {
                    var revision = (model.CurrentRevision ?? string.Empty).TrimEnd('*');
                    var prior = (snapshot.Children ?? new List<DocumentReferenceNodeDto>()).Where(reference =>
                        reference.DocumentId == model.DocumentId && (string.Equals(reference.Configuration ?? string.Empty, model.Configuration ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                            || savedViewConfiguration(reference, model))
                        && !string.IsNullOrWhiteSpace(reference.Revision?.Display)
                        && !string.Equals(reference.Revision.Display, revision, StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (prior.Length == 0 || string.IsNullOrWhiteSpace(revision)) continue;
                    if (!modelHistories.TryGetValue(model.DocumentId.Value, out var history))
                        modelHistories[model.DocumentId.Value] = history = await apiClient.GetVersionsAsync(model.DocumentId.Value, token);
                    var current = history.FirstOrDefault(version => string.Equals(version.Revision?.Display, revision, StringComparison.OrdinalIgnoreCase));
                    foreach (var reference in prior)
                    {
                        var source = history.FirstOrDefault(version => string.Equals(version.Revision?.Display, reference.Revision.Display, StringComparison.OrdinalIgnoreCase));
                        if (PdmApiClient.ReleasedArchiveBaseMatches(source, current)) equivalent.Add(key(reference, model));
                    }
                }
                // A release-only promotion is not a model edit. Keep the frozen
                // drawing snapshot intact; configurations and identities still match strictly.
                referencesMatch = ReferenceSnapshotMatchesTree(snapshot, drawing, true,
                    (before, after) => equivalent.Contains(key(before, after)), savedViewConfiguration);
            }
            if (drawing.RequiresModelUpdate && !modelChanged && drawing.ReferencedModels.Any(reference =>
                (currentTree == null ? new[] { reference } : EnumerateCadNodes(currentTree).Where(model => model.Kind != CadDocumentKind.Drawing
                    && PathsEqual(model.FullPath, reference.FullPath)).DefaultIfEmpty(reference))
                .Any(model => model.IsModifiedInSolidWorks || model.WorkState == CadWorkState.PendingCheckIn || model.WorkState == CadWorkState.ModifiedUnsaved)))
            {
                RecordUnresolvedDrawingViews(drawing, "配套模型有本批以外的修改，保留当前工程图视图和模型修改。");
                continue;
            }
            drawing.RequiresModelUpdate = drawing.RequiresModelUpdate || modelChanged || !referencesMatch;
            if (drawing.RequiresModelUpdate && !planned.Any(existing => PathsEqual(existing.Node.FullPath, drawing.FullPath))) planned.Add(item);
        }
    }

    private bool IsUnrelatedAutomaticDrawing(CadTreeNode drawing, IReadOnlyList<CadTreeNode> changedModels)
    {
        if (scanner == null || changedModels.Count == 0) return false;
        var references = scanner.ReadDrawingReferences(drawing.FullPath, out var readError, true);
        if (readError != null) return false;
        var models = references.Where(path => string.Equals(Path.GetExtension(path), ".SLDPRT", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetExtension(path), ".SLDASM", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (models.Length == 0 || models.Any(path => !File.Exists(path))) return false;
        return !models.Any(path => changedModels.Any(model => PathsEqual(path, model.FullPath)
            || PathsEqual(path, model.SourceReferencePath)
            // A same-named copy without proven identity is ambiguous, not proof
            // that this drawing is unrelated. Keep the existing update checks.
            || string.Equals(Path.GetFileName(path), model.FileName, StringComparison.OrdinalIgnoreCase)
            || model.DocumentId.HasValue && PdmDocumentIdentityStore.TryReadLocalMetadata(path, out var metadata)
                && metadata.DocumentId == model.DocumentId));
    }

    private readonly Dictionary<string, IModelDoc2> archiveOpenedDrawingWindows =
        new Dictionary<string, IModelDoc2>(StringComparer.OrdinalIgnoreCase);
    private string closingCommittedArchiveDrawingPath;

    private sealed class CommittedArchiveDrawingWindow
    {
        internal string Path;
        internal IModelDoc2 Document;
        internal ArchiveSourceStamp SourceStamp;
        internal ArchiveMemoryCopy Memory;
    }

    private void CloseCommittedArchiveDrawingWindows(IReadOnlyList<CommittedArchiveDrawingWindow> windows, BatchCheckInResult result)
    {
        foreach (var window in windows)
        {
            try
            {
                var loaded = application.GetOpenDocumentByName(window.Path) as IModelDoc2;
                if (loaded == null || !ReferenceEquals(window.Document, loaded) || !PathsEqual(loaded.GetPathName(), window.Path))
                    continue;
                // A new save or memory edit can occur while other files are synchronized.
                if (!window.SourceStamp.Matches(window.Path) || (loaded.GetSaveFlag()
                    && (window.Memory == null || !ReferenceEquals(window.Memory.Model, loaded)
                        || !window.Memory.MatchesMemory() || window.Memory.CommittedVersion == null
                        || !string.Equals(window.Memory.CommittedVersion.Sha256, window.Memory.CopyHash, StringComparison.OrdinalIgnoreCase))))
                {
                    result.BomWarnings.Add(Path.GetFileName(window.Path) + "：存档后仍有本地修改，临时窗口已保留。");
                    continue;
                }
                bool closeRequested;
                closingCommittedArchiveDrawingPath = window.Path;
                try { closeRequested = CloseArchiveReadDocument(loaded); }
                finally { closingCommittedArchiveDrawingPath = null; }
                if (!closeRequested || application.GetOpenDocumentByName(window.Path) is IModelDoc2 remaining
                        && PathsEqual(remaining.GetPathName(), window.Path) && remaining.Visible)
                    throw new InvalidOperationException("临时工程图窗口仍然打开。");
                LogOperation("存档成功后已关闭临时工程图：" + window.Path);
            }
            catch (Exception error)
            {
                LogDiagnostic("存档后关闭临时工程图：" + window.Path, error);
                result.BomWarnings.Add(Path.GetFileName(window.Path) + "：服务器已存档，临时窗口关闭未确认。");
            }
        }
    }

    private async Task<BatchCheckInPlan> UpdateLinkedDrawingsAsync(BatchCheckInPlan plan, Guid projectId, CancellationToken token)
    {
        if (!plan.Items.Any(item => item.Node.RequiresModelUpdate)) return plan;
        var drawings = plan.Items.Where(item => item.Node.Kind == CadDocumentKind.Drawing && item.Node.RequiresModelUpdate).ToArray();
        ArchiveSession.Current?.SetStep(3, "正在准备配套工程图更新…", drawings.Length);
        var pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var active = (application.ActiveDoc as IModelDoc2)?.GetPathName();
        for (var index = 0; index < drawings.Length; index++)
        {
            var item = drawings[index];
            var node = item.Node;
            IModelDoc2 drawing = null;
            var opened = false;
            try
            {
                token.ThrowIfCancellationRequested();
                ArchiveSession.Current?.ReportFile(index, drawings.Length, node.FileName, "正在准备配套工程图编辑权限…");
                await RefreshLinkedDrawingBeforeArchiveAsync(node, projectId, token);
                await PrepareBatchCheckInPermissionAsync(item, projectId, token);
                ArchiveSession.Current?.ReportFile(index, drawings.Length, node.FileName, "正在读取配套工程图视图…");
                drawing = ReadLinkedDrawingForArchive(node, plan, projectId, out opened);
                if (!PathsEqual(drawing?.GetPathName(), node.FullPath))
                    throw new InvalidOperationException("未能加载对应的工程图，请核对当前窗口中的图档路径。");
                if (opened) archiveOpenedDrawingWindows[node.FullPath] = drawing;
                if (node.ConfirmedArchivePaths != null && !node.ConfirmedArchivePaths.Contains(node.FullPath))
                    node.ConfirmedArchivePaths = new HashSet<string>(node.ConfirmedArchivePaths, StringComparer.OrdinalIgnoreCase)
                    { node.FullPath };
                var expectedModels = node.ReferencedModels.ToArray();
                var expected = expectedModels.Where(model => model.DocumentId.HasValue).Select(model => model.DocumentId.Value).Distinct().ToArray();
                scanner.UpdateDrawingReferences(drawing, node);
                ArchiveSession.Current?.ReportFile(index, drawings.Length, node.FileName, "正在按本次模型更新配套工程图…");
                if (node.HasUnresolvedDrawingViews || node.ReferencedModels.Count == 0
                    || node.ReferencedModels.Any(model => !expectedModels.Any(reference => PathsEqual(reference.FullPath, model.FullPath))))
                {
                    RecordUnresolvedDrawingViews(node);
                    continue;
                }
                try
                {
                    RebuildLinkedDrawingSheets(drawing, node, expectedModels);
                    scanner.UpdateDrawingReferences(drawing, node);
                }
                catch (Exception error) when (error is InvalidOperationException || error is System.Runtime.InteropServices.COMException)
                {
                    LogDiagnostic("配套工程图视图重建待核对：" + node.FileName, error);
                    RecordUnresolvedDrawingViews(node, "视图重建未完成，按当前可保存的工程图继续存档。");
                    continue;
                }
                if (node.StructureIndexPending || expected.Any(id => !node.ReferencedModels.Any(model => model.DocumentId == id)))
                {
                    RecordUnresolvedDrawingViews(node);
                    continue;
                }
                // Loading/rebuilding drawing views can dirty a model configuration. Keep
                // the drawing open, persist only models already in this batch, then save
                // the drawing against that settled model state. Unselected references stay untouched.
                await EnsureBatchCheckInSavedStateAsync(plan.Items.Where(candidate => candidate.Node.Kind != CadDocumentKind.Drawing
                    && candidate.Node.Role != CadReferenceRole.ExternalFileDependency).ToArray(), projectId, token);
                await PrepareCurrentMemoryCopiesAsync(new[] { item }, token);
                ValidateCurrentMemoryCopy(node);
                node.WorkState = CadWorkState.PendingCheckIn;
                node.DrawingUpdatePendingReason = null;
                ArchiveSession.Current?.ReportFile(index + 1, drawings.Length, node.FileName, "配套工程图已更新并生成提交副本，原文件未改写。");
                LogOperation("关联工程图已按本次模型更新并生成提交副本，原文件未改写：" + node.FileName);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                if (PathsEqual(node.FullPath, currentTree?.FullPath)) throw;
                ThrowOnArchiveConnectionFailure(error);
                LogDiagnostic("关联工程图待更新：" + node.FileName, error);
                node.DrawingUpdatePendingReason = ArchivePreflightError(error);
                pending.Add(node.FullPath);
            }
            finally
            {
                try
                {
                    if (opened && drawing != null && PathsEqual(drawing.GetPathName(), node.FullPath)
                        && !drawing.GetSaveFlag() && CloseArchiveReadDocument(drawing))
                        archiveOpenedDrawingWindows.Remove(node.FullPath);
                }
                catch (Exception error) { LogDiagnostic("工程图临时窗口保留：" + node.FileName, error); }
            }
        }
        if (!string.IsNullOrWhiteSpace(active) && FindLoadedDocument(active) != null)
        {
            var errors = 0;
            application.ActivateDoc3(active, false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref errors);
        }
        if (pending.Count == 0) return plan;
        return new BatchCheckInPlan(plan.Items.Where(item => !pending.Contains(item.Node.FullPath)).ToArray(),
            plan.SkippedFiles, plan.Preflights)
        {
            DeferredDrawings = drawings.Where(item => pending.Contains(item.Node.FullPath)).Select(item => item.Node).ToArray()
        };
    }

    private static void RecordUnresolvedDrawingViews(CadTreeNode drawing, string reason = "部分工程图视图未能核对模型，按图纸已保存状态存档。")
    {
        drawing.RequiresModelUpdate = false;
        drawing.StructureIndexPending = true;
        drawing.DrawingUpdatePendingReason = null;
        ArchiveWarningGate.Record(drawing, "DrawingOutOfSync", reason);
    }

    private void RebuildLinkedDrawingSheets(IModelDoc2 document, CadTreeNode node, IReadOnlyList<CadTreeNode> expectedModels)
    {
        bool IsActiveDrawing() => PathsEqual((application.ActiveDoc as IModelDoc2)?.GetPathName(), node.FullPath);
        if (!IsActiveDrawing())
        {
            var errors = 0;
            application.ActivateDoc3(node.FullPath, false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref errors);
            if (!IsActiveDrawing())
                throw new InvalidOperationException("未能激活所选工程图，请核对打开窗口中的文件路径。");
        }
        var drawing = (IDrawingDoc)document;
        var original = (drawing.GetCurrentSheet() as ISheet)?.GetName();
        var names = drawing.GetSheetNames() as string[];
        if (string.IsNullOrWhiteSpace(original) || names == null || names.Length == 0 || names.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException("未能读取工程图的图纸页，请核对该工程图。");

        ISheet SelectSheet(string name)
        {
            if (!IsActiveDrawing()) throw new InvalidOperationException("活动工程图已切换，请重新核对所选图档。");
            var sheet = drawing.GetCurrentSheet() as ISheet;
            if (string.Equals(sheet?.GetName(), name, StringComparison.OrdinalIgnoreCase)) return sheet;
            var activated = drawing.ActivateSheet(name);
            sheet = drawing.GetCurrentSheet() as ISheet;
            if (!IsActiveDrawing() || !string.Equals(sheet?.GetName(), name, StringComparison.OrdinalIgnoreCase))
            {
                LogOperation("工程图切页未完成 file=" + node.FileName + " sheet=" + name + " ActivateSheet=" + activated);
                throw new InvalidOperationException("未能切换到工程图的“" + name + "”页，请核对该图纸页。");
            }
            return sheet;
        }

        var inheritedErrors = ReadLinkedModelRebuildErrors(node, expectedModels);
        string recoveredSheet = null;
        Exception rebuildFailure = null;
        try
        {
            foreach (var name in names)
            {
                SelectSheet(name);
                var rebuilt = document.ForceRebuild3(false);
                if (!IsActiveDrawing() || !string.Equals((drawing.GetCurrentSheet() as ISheet)?.GetName(), name, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("重建期间活动图纸页发生变化，请核对“" + name + "”页后重试。");
                if (rebuilt) continue;
                // A drawing can return false even with a current sheet and current model views.
                // Request the sheet-specific update, then check the resulting native state.
                var updated = document.Extension.Rebuild((int)swRebuildOptions_e.swCurrentSheetDisp);
                var sheet = drawing.GetCurrentSheet() as ISheet;
                if (!IsActiveDrawing() || !string.Equals(sheet?.GetName(), name, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("更新视图期间活动图纸页发生变化，请核对“" + name + "”页后重试。");
                VerifyRebuiltDrawingViews(drawing, name, updated, node, expectedModels);
                recoveredSheet = name;
            }
        }
        catch (Exception error) { rebuildFailure = error; throw; }
        finally
        {
            try { if (IsActiveDrawing()) SelectSheet(original); }
            catch (Exception error) when (rebuildFailure != null) { LogDiagnostic("工程图恢复原图纸页失败：" + original, error); }
        }
        if (recoveredSheet != null) VerifyLinkedDrawingRebuild(document.Extension, recoveredSheet, inheritedErrors);
    }

    private List<(IFeature Feature, int Code)> ReadLinkedModelRebuildErrors(CadTreeNode drawing, IReadOnlyList<CadTreeNode> expectedModels)
    {
        var result = new List<(IFeature Feature, int Code)>();
        var sources = drawing.ReferencedModels.Where(model => expectedModels.Any(expected => PathsEqual(expected.FullPath, model.FullPath)))
            .GroupBy(model => model.FullPath, StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            try
            {
                var model = FindLoadedDocument(source.Key);
                if (model == null || !PathsEqual(model.GetPathName(), source.Key)) continue;
                var errors = ReadNativeRebuildErrors(model.Extension);
                if (errors.Count == 0) continue;
                var configuration = model.ConfigurationManager?.ActiveConfiguration?.Name;
                if (!string.IsNullOrWhiteSpace(configuration) && source.Any(view =>
                    string.Equals(view.Configuration, configuration, StringComparison.OrdinalIgnoreCase))) result.AddRange(errors);
            }
            catch (Exception error) { LogDiagnostic("未能确认引用模型原有的重建诊断：" + source.Key, error); }
        }
        return result;
    }

    private static List<(IFeature Feature, int Code)> ReadNativeRebuildErrors(IModelDocExtension extension)
    {
        var errors = new List<(IFeature Feature, int Code)>();
        var count = extension.GetWhatsWrongCount();
        if (count == 0) return errors;
        if (count < 0 || !extension.GetWhatsWrong(out var features, out var codes, out var warnings)
            || !(warnings is bool[] flags) || flags.Length != count || !(codes is int[] numbers) || numbers.Length != count
            || !(features is object[] items) || items.Length != count)
            throw new InvalidOperationException("未能确认重建结果，请核对 SolidWorks 的重建诊断。");
        for (var i = 0; i < count; i++)
        {
            if (!flags[i]) errors.Add((items[i] as IFeature, numbers[i]));
        }
        return errors;
    }

    private void VerifyLinkedDrawingRebuild(IModelDocExtension extension, string sheet, IReadOnlyList<(IFeature Feature, int Code)> inheritedErrors)
    {
        // Drawing diagnostics can include features from the referenced model.
        // Match the actual pre-existing feature and error, including its severity.
        if (extension.NeedsRebuild2 != (int)swModelRebuildStatus_e.swModelRebuildStatus_FullyRebuilt)
            throw new InvalidOperationException("工程图仍需重建，请核对图纸页“" + sheet + "”及工程图特征。");
        foreach (var error in ReadNativeRebuildErrors(extension))
        {
            if (error.Feature != null && inheritedErrors.Any(prior => prior.Code == error.Code && prior.Feature != null
                && (ReferenceEquals(prior.Feature, error.Feature) || application.IsSame(prior.Feature, error.Feature) == (int)swObjectEquality.swObjectSame)))
                continue;
            var name = error.Feature?.Name;
            throw new InvalidOperationException("工程图含有重建错误，请核对" + (string.IsNullOrWhiteSpace(name) ? "工程图特征或视图" : "特征“" + name + "”")
                + "（SolidWorks 代码 " + error.Code + "）。");
        }
    }

    private static void VerifyRebuiltDrawingViews(IDrawingDoc drawing, string name, bool displayUpdated,
        CadTreeNode node, IReadOnlyList<CadTreeNode> expectedModels)
    {
        LogOperation("工程图视图重建核对 file=" + node.FileName + " sheet=" + name
            + " ForceRebuild3=False displayRebuild=" + displayUpdated);
        if (!(drawing.GetFirstView() is IView sheetView) || sheetView.Type != (int)swDrawingViewTypes_e.swDrawingSheet)
            throw new InvalidOperationException("未能读取工程图“" + name + "”页的视图，无法确认更新结果。");
        var views = new HashSet<IView> { sheetView };
        var item = sheetView.GetNextView();
        while (item != null)
        {
            if (!(item is IView view) || !views.Add(view))
                throw new InvalidOperationException("未能核对工程图“" + name + "”页的视图列表。");
            var path = (view.ReferencedDocument as IModelDoc2)?.GetPathName();
            var config = view.ReferencedConfiguration ?? "默认";
            if (!expectedModels.Any(model => PathsEqual(model.FullPath, path))
                || !node.ReferencedModels.Any(model => PathsEqual(model.FullPath, path)
                    && string.Equals(model.Configuration, config, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("工程图“" + name + "”页的“" + view.Name + "”未能核对所引用的模型或配置。");
            if (view.IsModelOutOfDate())
                throw new InvalidOperationException("工程图“" + name + "”页的“" + view.Name + "”仍未更新到当前模型。");
            item = view.GetNextView();
        }
        if (!displayUpdated && views.Count == 1)
            throw new InvalidOperationException("SolidWorks 未能确认工程图“" + name + "”页重建成功，请核对该页的视图和重建错误。");
    }
}
