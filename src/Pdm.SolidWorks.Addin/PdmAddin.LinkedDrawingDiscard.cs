using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    private async Task<IReadOnlyList<string>> ReleaseUnchangedLinkedDrawingsAfterDiscardAsync(IReadOnlyList<CadTreeNode> completed)
    {
        var warnings = new List<string>();
        var models = completed.Where(node => node.Kind == CadDocumentKind.Part || node.Kind == CadDocumentKind.Assembly).ToArray();
        if (models.Length == 0) return warnings;
        // A context with no drawing, drawing recovery record or drawing file has
        // no linked permission to release. Keep ordinary part-only discard cheap.
        var nodes = currentTree == null ? Array.Empty<CadTreeNode>() : EnumerateCadNodes(currentTree).ToArray();
        if (!nodes.Any(node => node.Kind == CadDocumentKind.Drawing)
            && !localEdits.Values.Any(entry => !entry.Closed && DocumentKindFromPath(entry.SourcePath) == CadDocumentKind.Drawing)
            && !controlledOpenManifests.Values.Any(manifest => manifest.Files.Any(file => DocumentKindFromPath(file.FileName) == CadDocumentKind.Drawing))
            && !models.Any(model => File.Exists(Path.ChangeExtension(model.FullPath, ".SLDDRW")))) return warnings;
        foreach (var project in models.GroupBy(model => PdmDocumentIdentityStore.ReadProjectId(model.FullPath) ?? Guid.Empty))
        {
            try
            {
                if (project.Key == Guid.Empty) throw new IOException("当前工作文件的项目关联需要核对，未自动释放工程图权限。");
                var selected = project.ToArray();
                var ids = new HashSet<Guid>(selected.Where(node => node.DocumentId.HasValue).Select(node => node.DocumentId.Value));
                var relations = await apiClient.GetDocumentRelationsAsync(project.Key, lifetime.Token);
                var linked = relations.Where(relation => ids.Contains(relation.ModelDocumentId)).Select(relation => relation.DrawingDocumentId).Distinct().ToArray();
                if (linked.Length == 0) continue;
                var documents = (await apiClient.GetDocumentsAsync(project.Key, lifetime.Token)).ToDictionary(document => document.Id);
                foreach (var id in linked)
                {
                    if (!documents.TryGetValue(id, out var document) || document.ProjectId != project.Key
                        || document.Kind != (int)CadDocumentKind.Drawing || document.CheckoutSessionId != checkoutSessionId
                        || !string.Equals(document.CheckedOutBy, authenticatedUsername, StringComparison.OrdinalIgnoreCase)) continue;
                    var name = document.FileName;
                    try
                    {
                        var related = relations.Where(relation => relation.DrawingDocumentId == id).Select(relation => relation.ModelDocumentId).Distinct().ToArray();
                        if (related.Any(modelId => !documents.TryGetValue(modelId, out var model) || !string.IsNullOrWhiteSpace(model.CheckedOutBy)))
                            throw new InvalidOperationException("仍有关联模型正在编辑或其权限尚未核对，已保留工程图权限");
                        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var model in selected.Where(model => related.Contains(model.DocumentId.Value)))
                        {
                            paths.Add(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(model.FullPath), Path.GetFileName(name))));
                            foreach (var context in controlledOpenManifests.Where(pair => pair.Value.ProjectId == project.Key
                                && pair.Value.Files.Any(file => file.DocumentId == model.DocumentId
                                    && PathsEqual(model.FullPath, Path.Combine(pair.Key, file.RelativePath ?? file.FileName)))))
                                foreach (var file in context.Value.Files.Where(file => file.DocumentId == id))
                                {
                                    var candidate = Path.GetFullPath(Path.Combine(context.Key, file.RelativePath ?? file.FileName));
                                    if (IsPathWithinDirectory(candidate, context.Key)) paths.Add(candidate);
                                }
                        }
                        var local = paths.Where(File.Exists).Where(path => PdmDocumentIdentityStore.TryReadLocalMetadata(path, out var binding)
                            && binding.DocumentId == id && binding.ProjectId == project.Key).ToArray();
                        if (local.Length != 1) throw new InvalidOperationException("当前工作区的工程图路径无法唯一核对，已保留权限");
                        var path = local[0];
                        var latest = (await apiClient.GetVersionsAsync(id, lifetime.Token, 1)).FirstOrDefault();
                        if (latest == null || !PdmDocumentIdentityStore.TryReadLocalMetadata(path, out var metadata) || metadata.VersionId != latest.Id)
                            throw new InvalidOperationException("当前工程图版本需要核对，已保留权限");
                        var before = ComputeFileHash(path);
                        if (!VersionMatchesLocalFile(latest, path, before) || FindLoadedDocument(path)?.GetSaveFlag() == true)
                            throw new InvalidOperationException("存在已保存或未保存的修改，已保留文件、窗口和编辑权限；如需放弃，请单独选择该工程图");
                        var released = await apiClient.DiscardCheckoutAsync(id, checkoutSessionId, lifetime.Token);
                        if (released.Id != id || released.ProjectId != project.Key || !string.IsNullOrWhiteSpace(released.CheckedOutBy)
                            || released.CheckoutSessionId.HasValue)
                            throw new IOException("服务器尚未确认工程图权限已释放，未认定联动完成");
                        documents[id] = released;
                        var node = nodes.FirstOrDefault(candidate => candidate.DocumentId == id && PathsEqual(candidate.FullPath, path))
                            ?? new CadTreeNode { DocumentId = id, FullPath = path, FileName = name, Kind = CadDocumentKind.Drawing };
                        ApplyCheckoutDocumentToMatchingInstances(FindMatchingDocumentInstances(node, id), released);
                        if (!string.Equals(before, ComputeFileHash(path), StringComparison.OrdinalIgnoreCase) || FindLoadedDocument(path)?.GetSaveFlag() == true)
                            throw new IOException("服务器已释放工程图权限，但等待期间产生了新修改；已保留内容和窗口，请先另存这些修改再获取权限");
                        SetFileReadOnly(path, true, "随模型放弃未修改工程图的编辑权限");
                        var loaded = FindLoadedDocument(path);
                        if (loaded != null) EnsureDocumentOpenedReadOnly(loaded, path);
                        FinishLinkedDrawingEditAtPath(id, project.Key, path);
                        node.WorkState = CadWorkState.None;
                        node.IsModifiedInSolidWorks = false;
                        LogOperation("随模型放弃未修改工程图权限 document=" + id + " path=" + path + " closed=0 replaced=0");
                    }
                    catch (Exception error)
                    {
                        LogDiagnostic("联动放弃工程图：" + name, error);
                        warnings.Add(name + "：" + DiscardRecoveryError(error));
                    }
                }
            }
            catch (Exception error)
            {
                LogDiagnostic("核对配套工程图放弃权限", error);
                warnings.Add("模型已放弃，工程图联动尚未完成：" + DiscardRecoveryError(error));
            }
        }
        return warnings;
    }

    private void FinishLinkedDrawingEditAtPath(Guid id, Guid projectId, string path)
    {
        if (!EnsureEditJournal()) throw new IOException("服务器已释放工程图权限，本地恢复记录尚未结束，请重新登录后核对。");
        foreach (var entry in editJournal.ReadAll().Where(entry => entry.DocumentId == id && entry.ProjectId == projectId
            && entry.SessionId == checkoutSessionId && PathsEqual(entry.SourcePath, path)))
        {
            entry.Closed = true;
            entry.PendingClose = entry.RestoreOnOpen = entry.DiscardPending = false;
            EditDraftJournal.ClearMaintenanceFailure(entry);
            editJournal.Save(entry);
        }
        if (localEdits.TryGetValue(id, out var current) && current.ProjectId == projectId
            && current.SessionId == checkoutSessionId && PathsEqual(current.SourcePath, path)) localEdits.Remove(id);
        shownEditRequests.Remove(id);
        lock (checkoutDocumentSync) pendingCloseDocumentIds.Remove(id);
    }
}
