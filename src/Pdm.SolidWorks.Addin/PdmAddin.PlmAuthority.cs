using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    private readonly HashSet<Guid> plmAuthoritativeCheckouts = new HashSet<Guid>();
    private string controlledOpenFailureMessage;

    private async Task<WorkspaceAcquireResult> AcquireExplicitlyFromPlmAsync(IReadOnlyList<BatchOperationItem> selected,
        Guid projectId, IReadOnlyDictionary<string, RegistrationDecision> registrationDecisions = null)
    {
        var stored = new List<BatchOperationItem>();
        var initial = new List<BatchOperationItem>();
        foreach (var item in selected)
        {
            var node = item.Node;
            if (node.DocumentId.HasValue)
            {
                var versions = await apiClient.GetVersionsAsync(node.DocumentId.Value, lifetime.Token, 1);
                node.StoredVersionStateKnown = true;
                node.HasStoredVersion = versions.Count > 0;
                if (node.HasStoredVersion) { stored.Add(item); continue; }
            }
            initial.Add(item);
        }
        if (stored.Count == 0)
            return await AcquireLatestAndCheckoutAsync(initial, projectId, registrationDecisions);
        var ids = stored.Select(item => item.Node.DocumentId.Value).Distinct().ToArray();
        var rootId = ids[0];
        if (currentTree?.DocumentId.HasValue == true
            && (await apiClient.GetVersionsAsync(currentTree.DocumentId.Value, lifetime.Token, 1)).Count > 0)
            rootId = currentTree.DocumentId.Value;
        if (Interlocked.CompareExchange(ref controlledOpenInProgress, 1, 0) != 0)
            throw new InvalidOperationException("已有PLM图档正在准备，请等待当前操作结束。");
        var reused = false;
        bool opened;
        try
        {
            reused = await TryAcquireDrawingWorkingCopiesAsync(stored, projectId)
                || await TryAcquireReusableWorkingCopyAsync(stored, projectId, rootId);
            opened = reused || await ExecuteControlledOpenAsync(projectId, rootId, ControlledOpenMode.LatestEdit,
                null, ids[0], ids, false);
        }
        finally { Interlocked.Exchange(ref controlledOpenInProgress, 0); }
        if (!opened) throw new InvalidOperationException("从PLM获取编辑副本未完成。"
            + (controlledOpenFailureMessage ?? "请查看本次操作日志中的具体原因。"));
        // New local documents are a separate registration operation. Their state must
        // never prevent retrieval of documents which already exist in PLM.
        if (initial.Count > 0 && registrationDecisions == null)
        {
            try
            {
                await ValidateBatchProjectAsync(initial, projectId);
                registrationDecisions = await ReviewRegistrationDuplicatesAsync(initial, projectId);
                if (registrationDecisions == null) return new WorkspaceAcquireResult(ids.Length, reused ? 0 : ids.Length);
            }
            catch (Exception error)
            {
                LogDiagnostic("已完成PLM取回，新增文件登记未完成", error);
                throw new InvalidOperationException("已从PLM获取" + ids.Length + "个图档；新增本地文件尚未完成登记：" + error.Message, error);
            }
        }
        var initialResult = initial.Count > 0
            ? await AcquireLatestAndCheckoutAsync(initial, projectId, registrationDecisions)
            : new WorkspaceAcquireResult(0, 0);
        return new WorkspaceAcquireResult(initialResult.CheckedOutFiles + ids.Length, initialResult.UpdatedFiles + (reused ? 0 : ids.Length));
    }

    private string CloseLocalContextsForPlmOpen(ControlledOpenManifestDto manifest, string preparedRootPath = null)
        => CloseLocalContextsForPlmOpenCore(manifest, false, preparedRootPath);

    private string CloseLocalContextsForPlmOpenCore(ControlledOpenManifestDto manifest, bool preserveUnsaved, string preparedRootPath = null)
    {
        var names = new HashSet<string>(manifest.Files.Select(file => file.FileName), StringComparer.OrdinalIgnoreCase);
        var models = ((application.GetDocuments() as Array)?.OfType<IModelDoc2>() ?? Enumerable.Empty<IModelDoc2>()).ToArray();
        var selected = new HashSet<IModelDoc2>();
        var authorized = new HashSet<IModelDoc2>();
        var manifestIds = new HashSet<Guid>(manifest.Files.Select(file => file.DocumentId));
        var selectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var references = new Dictionary<IModelDoc2, string[]>();
        foreach (var model in models)
        {
            try
            {
                var path = model.GetPathName();
                if (PdmDocumentIdentityStore.TryReadLocalMetadata(path, out var binding)
                    && binding.ProjectId == manifest.ProjectId && manifestIds.Contains(binding.DocumentId))
                    authorized.Add(model);
                var preparedFile = preparedRootPath == null ? null : manifest.Files.FirstOrDefault(file => PathsEqual(path,
                    Path.Combine(Path.GetDirectoryName(preparedRootPath), file.RelativePath ?? file.FileName)));
                if (preparedRootPath != null && preparedFile != null && authorized.Contains(model))
                {
                    references[model] = LoadedModelReferencePaths(model);
                    continue;
                }
                if (names.Contains(Path.GetFileName(path)) || names.Contains(model.GetTitle()))
                {
                    selected.Add(model);
                    if (!string.IsNullOrWhiteSpace(path)) selectedPaths.Add(path);
                }
                references[model] = LoadedModelReferencePaths(model);
            }
            catch (System.Runtime.InteropServices.COMException error) { LogDiagnostic("PLM取回前读取旧窗口", error); }
        }
        // A same-name drawing from another directory/project is not authorization
        // to close it. Leave every original window open when an identity conflicts.
        if (selected.Any(model => !authorized.Contains(model)))
            throw new InvalidOperationException("其他来源的同名图档仍在使用，已保留全部窗口和修改。请核对并关闭冲突图档后再获取受控版本："
                + string.Join("、", selected.Where(model => !authorized.Contains(model)).Select(model => model.GetTitle())));
        while (true)
        {
            var owners = references.Where(pair => !selected.Contains(pair.Key)
                && pair.Value.Any(path => selectedPaths.Contains(path))).Select(pair => pair.Key).ToArray();
            if (owners.Length == 0) break;
            foreach (var owner in owners)
            {
                selected.Add(owner);
                if (!string.IsNullOrWhiteSpace(owner.GetPathName())) selectedPaths.Add(owner.GetPathName());
            }
        }
        if (selected.Any(model => !authorized.Contains(model)))
            throw new InvalidOperationException("待更新文件仍被其他窗口引用，已保留全部窗口和修改。请先保存并关闭相关窗口后再获取："
                + string.Join("、", selected.Where(model => !authorized.Contains(model)).Select(model => model.GetTitle())));
        if (preserveUnsaved)
        {
            var affectedPaths = new HashSet<string>(selectedPaths, StringComparer.OrdinalIgnoreCase);
            foreach (var owner in selected)
                if (references.TryGetValue(owner, out var paths)) affectedPaths.UnionWith(paths);
            var dirty = models.Where(model => (selected.Contains(model) || affectedPaths.Contains(model.GetPathName()))
                && model.GetSaveFlag()).Select(model => model.GetTitle()).ToArray();
            if (dirty.Length > 0)
                throw new InvalidOperationException("当前工作副本需要重新核对，但图档仍有未保存修改，已保留全部窗口和修改："
                    + string.Join("、", dirty.Distinct()) + "。请先保存这些修改后重试获取权限。");
        }
        var backups = new List<string>();
        var backupFailures = new List<string>();
        var unrelatedBackupFailures = new List<string>();
        var unsavedModels = new List<IModelDoc2>();
        foreach (var model in selected)
        {
            var title = "本地图档";
            try
            {
                title = model.GetTitle();
                if (!model.GetSaveFlag()) continue;
                unsavedModels.Add(model);
                var source = model.GetPathName();
                if (string.IsNullOrWhiteSpace(source))
                {
                    var extension = model.GetType() == 3 ? ".SLDDRW" : model.GetType() == 2 ? ".SLDASM" : ".SLDPRT";
                    source = Path.Combine(System.IO.Path.GetTempPath(), "未保存图档" + extension);
                }
                var temporary = CreateCheckInUploadCopy(model, source, Guid.NewGuid());
                var directory = Path.Combine(System.IO.Path.GetTempPath(), "UPLM", "PLM取回前备份", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                var target = Path.Combine(directory, Path.GetFileName(source));
                File.Copy(temporary, target);
                backups.Add(target);
                try { DeleteCheckInUploadCopy(temporary); }
                catch (IOException error) { LogDiagnostic("PLM取回备份临时文件清理", error); }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                backupFailures.Add(title);
                if (!authorized.Contains(model)) unrelatedBackupFailures.Add(title);
                LogDiagnostic(authorized.Contains(model)
                    ? "目标图档备份未完成，按已授权的PLM优先规则继续取回"
                    : "无关图档未保存内容未能备份，保留原窗口", error);
            }
        }
        if (backups.Count > 0) LogOperation("旧窗口未保存内容备份：" + string.Join("；", backups));
        if (unrelatedBackupFailures.Count > 0)
        {
            foreach (var model in unsavedModels)
                try { model.SetSaveFlag(); }
                catch (System.Runtime.InteropServices.COMException error) { LogDiagnostic("恢复本地待保存标记", error); }
            throw new InvalidOperationException("本次PLM副本已准备，但以下其他图档有未保存内容且无法备份，已保留其窗口："
                + string.Join("、", unrelatedBackupFailures.Distinct()) + "。请先保存这些图档；本次获取没有改写它们。");
        }
        var windows = selected.OrderByDescending(model => references.TryGetValue(model, out var paths) ? paths.Length : 0)
            .Select(model => new { Path = model.GetPathName(), Title = model.GetTitle() }).ToArray();
        if (windows.Any(window => string.IsNullOrWhiteSpace(window.Title))
            || models.GroupBy(model => model.GetTitle(), StringComparer.OrdinalIgnoreCase)
                .Any(group => group.Select(model => model.GetPathName()).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1
                    && windows.Any(window => string.Equals(window.Title, group.Key, StringComparison.OrdinalIgnoreCase))))
            throw new InvalidOperationException("待切换窗口名称为空或存在同名图档，已保留全部窗口，请核对后重试。");
        foreach (var window in windows)
        {
            try
            {
                // Closing an assembly can unload its children. Do not call into
                // already released child COM objects on the next iteration.
                if (string.IsNullOrWhiteSpace(window.Path) || FindLoadedDocument(window.Path) != null)
                    application.CloseDoc(window.Title);
            }
            catch (System.Runtime.InteropServices.COMException error) { LogDiagnostic("PLM取回切换旧窗口", error); }
        }
        if (backupFailures.Count > 0)
            return "已按PLM版本打开；以下目标图档的本地未保存内容未能备份：" + string.Join("、", backupFailures.Distinct()) + "。原磁盘文件保留，详情见日志。";
        return backups.Count > 0 ? "已按PLM版本打开，本地未保存内容已另存备份；详情见日志。" : null;
    }

    private PlmOpenWindow[] CapturePlmOpenWindows()
    {
        var active = (application.ActiveDoc as IModelDoc2)?.GetPathName();
        return ((application.GetDocuments() as Array)?.OfType<IModelDoc2>() ?? Enumerable.Empty<IModelDoc2>())
            .Where(model => model.Visible || PathsEqual(active, model.GetPathName()))
            .Select(model => new PlmOpenWindow {
                Path = model.GetPathName(), DocumentType = model.GetType(), ReadOnly = model.IsOpenedReadOnly(),
                Configuration = model.ConfigurationManager?.ActiveConfiguration?.Name ?? string.Empty,
                WasDirty = model.GetSaveFlag(), WasActive = PathsEqual(active, model.GetPathName())
            }).Where(window => !string.IsNullOrWhiteSpace(window.Path)).ToArray();
    }

    private void RestorePlmOpenWindows(PlmOpenWindow[] previous, string preparedPath)
    {
        var failures = new List<string>();
        var previousPaths = new HashSet<string>(previous.Select(window => window.Path), StringComparer.OrdinalIgnoreCase);
        var models = ((application.GetDocuments() as Array)?.OfType<IModelDoc2>() ?? Enumerable.Empty<IModelDoc2>())
            .Select(model => new { Path = model.GetPathName(), Title = model.GetTitle(), DocumentType = model.GetType(), Dirty = model.GetSaveFlag() }).ToArray();
        // Close only clean documents introduced by this failed attempt. Other user
        // documents, including same-name files, are never rollback targets.
        var introduced = models.Where(model => !previousPaths.Contains(model.Path)
            && IsPathWithinDirectory(model.Path, System.IO.Path.GetDirectoryName(preparedPath)))
            .OrderByDescending(model => model.DocumentType == 2).ToArray();
        var hasDirtyIntroducedDocument = introduced.Any(window => window.Dirty);
        foreach (var window in introduced)
        {
            try
            {
                var model = FindLoadedDocument(window.Path);
                if (model == null) continue;
                if (hasDirtyIntroducedDocument || model.GetSaveFlag()) throw new IOException("本次打开的副本已有未保存内容，已保留该窗口，请核对后恢复原工作窗口。");
                if (string.IsNullOrWhiteSpace(window.Title) || models.Any(other => string.Equals(other.Title, window.Title,
                    StringComparison.OrdinalIgnoreCase) && !PathsEqual(other.Path, window.Path)))
                    throw new IOException("同名窗口的来源尚未核对，已保留窗口，未执行自动关闭。");
                application.CloseDoc(window.Title);
            }
            catch (Exception error) { LogDiagnostic("保留获取失败副本窗口", error); failures.Add(error.Message); }
        }
        foreach (var window in previous.OrderByDescending(window => window.DocumentType == 2))
        {
            try
            {
                if (FindLoadedDocument(window.Path) != null) continue;
                if (window.WasDirty) throw new IOException("原窗口有未保存内容，未用磁盘文件代替，请从恢复备份核对：" + window.Path);
                if (OpenOrActivateDocumentOnSolidWorksThread(window.Path, window.DocumentType, window.Configuration, window.ReadOnly) == null)
                    throw new IOException("原工作窗口尚未恢复，请从原路径重新打开：" + window.Path);
            }
            catch (Exception error) { LogDiagnostic("恢复PLM获取前窗口：" + window.Path, error); failures.Add(window.Path); }
        }
        var active = previous.FirstOrDefault(window => window.WasActive);
        if (active != null && !PathsEqual((application.ActiveDoc as IModelDoc2)?.GetPathName(), active.Path)
            && OpenOrActivateDocumentOnSolidWorksThread(active.Path, active.DocumentType, active.Configuration, active.ReadOnly) == null)
            throw new IOException("原工作窗口已恢复，但尚未切回原活动图档。");
        if (failures.Count > 0) throw new IOException("部分窗口尚未恢复，原文件及恢复记录已保留：" + string.Join("；", failures));
        LogOperation("PLM获取失败已恢复原工作窗口 count=" + previous.Length);
    }

    private sealed class PlmOpenWindow
    {
        public string Path, Configuration;
        public int DocumentType;
        public bool ReadOnly, WasDirty, WasActive;
    }

    private void RetireLocalRecoveryForPlmOpen(ControlledOpenManifestDto manifest)
        => RetireLocalRecoveryForPlmOpenCore(manifest, null);

    private void RetireLocalRecoveryForPlmOpenCore(ControlledOpenManifestDto manifest, string preservedDirectory)
    {
        bool Preserve(LocalEditDraft entry) => entry != null && (entry.DiscardPending || !entry.Closed
            || entry.RecoveryRequired || entry.RestoreOnOpen || entry.UnsavedChanges
            || preservedDirectory != null && IsPathWithinDirectory(entry.SourcePath, preservedDirectory));
        var ids = new HashSet<Guid>(manifest.Files.Select(file => file.DocumentId));
        foreach (var id in ids)
        {
            if (localEdits.TryGetValue(id, out var pending) && Preserve(pending)) continue;
            localEdits.Remove(id);
            lock (checkoutDocumentSync) pendingCloseDocumentIds.Remove(id);
        }
        try
        {
            if (!EnsureEditJournal()) return;
            foreach (var entry in editJournal.ReadAll().Where(item => ids.Contains(item.DocumentId)
                && item.ProjectId == manifest.ProjectId && !Preserve(item)))
            {
                entry.Closed = true;
                entry.PendingClose = false;
                entry.RestoreOnOpen = false;
                entry.DiscardPending = false;
                EditDraftJournal.ClearMaintenanceFailure(entry);
                try { editJournal.Save(entry); }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                { LogDiagnostic("旧恢复记录未能更新，不拦截PLM取回；路径=" + entry.SourcePath, error); }
            }
        }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is InvalidOperationException)
        { LogDiagnostic("旧恢复记录不可用，不拦截PLM取回", error); }
        finally
        {
            foreach (var id in ids)
            {
                if (localEdits.TryGetValue(id, out var pending) && Preserve(pending)) continue;
                localEdits.Remove(id);
                lock (checkoutDocumentSync) pendingCloseDocumentIds.Remove(id);
            }
        }
    }
}
