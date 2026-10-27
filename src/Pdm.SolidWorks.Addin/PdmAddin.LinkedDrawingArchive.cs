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
            var relations = await apiClient.GetDocumentRelationsAsync(projectId, token);
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
                    await PrepareBatchCheckInPermissionAsync(new BatchOperationItem(node, 0), projectId, token);
                    SetFileReadOnly(path, false);
                    EnsureLoadedDocumentEditable(path);
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

    private async Task<HashSet<string>> ChangedReadOnlyAssembliesAsync(IEnumerable<BatchOperationItem> items, CancellationToken token)
    {
        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in DistinctBatchItems(items))
        {
            var node = item.Node;
            if (node.Kind != CadDocumentKind.Assembly || !node.DocumentId.HasValue
                || node.Role == CadReferenceRole.ExternalFileDependency || IsCheckedOutByCurrentUser(node)) continue;
            var loaded = FindLoadedDocument(node.FullPath);
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
                throw new InvalidOperationException("配套工程图关联的模型仍有未存档修改，请将对应模型一并勾选存档；本次尚未上传或升版：" + drawing.FileName);
            drawing.RequiresModelUpdate = drawing.RequiresModelUpdate || modelChanged || !referencesMatch;
            if (drawing.RequiresModelUpdate && !planned.Contains(item)) planned.Add(item);
        }
    }

    private async Task<BatchCheckInPlan> UpdateLinkedDrawingsAsync(BatchCheckInPlan plan, Guid projectId, CancellationToken token)
    {
        if (!plan.Items.Any(item => item.Node.RequiresModelUpdate)) return plan;
        var pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var active = (application.ActiveDoc as IModelDoc2)?.GetPathName();
        foreach (var item in plan.Items.Where(item => item.Node.Kind == CadDocumentKind.Drawing && item.Node.RequiresModelUpdate))
        {
            var node = item.Node;
            IModelDoc2 drawing = null;
            var opened = false;
            try
            {
                token.ThrowIfCancellationRequested();
                await PrepareBatchCheckInPermissionAsync(item, projectId, token);
                drawing = FindLoadedDocument(node.FullPath);
                if (drawing == null) { drawing = OpenDrawingForArchive(node.FullPath); opened = true; }
                if (!PathsEqual(drawing?.GetPathName(), node.FullPath))
                    throw new IOException("未能加载对应的工程图，原文件已保留。");
                EnsureDocumentEditable(drawing, node.FullPath);
                var expected = node.ReferencedModels.Where(model => model.DocumentId.HasValue).Select(model => model.DocumentId.Value).Distinct().ToArray();
                scanner.UpdateDrawingReferences(drawing, node);
                var activateErrors = 0;
                application.ActivateDoc3(drawing.GetTitle(), false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref activateErrors);
                if (activateErrors != 0) throw new IOException("未能激活工程图，当前图档已保留。");
                var drawingDocument = drawing as IDrawingDoc;
                var originalSheet = (drawingDocument?.GetCurrentSheet() as ISheet)?.GetName();
                var sheets = drawingDocument?.GetSheetNames() as string[];
                if (sheets == null || sheets.Length == 0) throw new IOException("未能读取工程图图纸页，请打开工程图核对。");
                try
                {
                    foreach (var sheet in sheets)
                        if (!drawingDocument.ActivateSheet(sheet) || !drawing.ForceRebuild3(false))
                            throw new IOException("工程图视图未能完成更新，请核对图纸页：" + sheet);
                }
                finally { if (!string.IsNullOrWhiteSpace(originalSheet)) drawingDocument.ActivateSheet(originalSheet); }
                scanner.UpdateDrawingReferences(drawing, node);
                if (node.StructureIndexPending || expected.Any(id => !node.ReferencedModels.Any(model => model.DocumentId == id)))
                    throw new IOException("尚未确认工程图视图使用本次模型，已保留工程图待更新状态。");
                // Loading/rebuilding drawing views can dirty a model configuration. Keep
                // the drawing open, persist only models already in this batch, then save
                // the drawing against that settled model state. Unselected references stay untouched.
                await EnsureBatchCheckInSavedStateAsync(plan.Items.Where(candidate => candidate.Node.Kind != CadDocumentKind.Drawing
                    && candidate.Node.Role != CadReferenceRole.ExternalFileDependency).ToArray(), projectId, token);
                var errors = 0;
                var warnings = 0;
                if (!drawing.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref errors, ref warnings) || errors != 0 || drawing.GetSaveFlag())
                    throw new IOException("工程图更新后未能保存，当前修改和权限已保留。错误码：" + errors);
                node.IsModifiedInSolidWorks = false;
                node.WorkState = CadWorkState.PendingCheckIn;
                node.DrawingUpdatePendingReason = null;
                LogOperation("关联工程图已按本次模型更新并保存：" + node.FileName);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                LogDiagnostic("关联工程图待更新：" + node.FileName, error);
                node.DrawingUpdatePendingReason = ArchivePreflightError(error);
                pending.Add(node.FullPath);
            }
            finally
            {
                try { if (opened && drawing != null && !drawing.GetSaveFlag()) CloseArchiveReadDocument(drawing); }
                catch (Exception error) { LogDiagnostic("工程图临时窗口保留：" + node.FileName, error); }
            }
        }
        if (!string.IsNullOrWhiteSpace(active) && FindLoadedDocument(active) != null)
        {
            var errors = 0;
            application.ActivateDoc3(active, false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref errors);
        }
        if (pending.Count > 0)
            throw new InvalidOperationException("配套工程图更新未完成，已停止本批次存档，尚未上传或升版。请核对工程图视图、引用及保存状态后重试："
                + string.Join("；", plan.Items.Where(item => pending.Contains(item.Node.FullPath))
                    .Select(item => item.Node.FileName + "：" + item.Node.DrawingUpdatePendingReason)));
        return new BatchCheckInPlan(plan.Items.Where(item => !pending.Contains(item.Node.FullPath)).ToArray(),
            plan.SkippedFiles + pending.Count, plan.Preflights);
    }
}
