using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    private readonly HashSet<string> completedDiscardPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private async Task DiscardSelectedWorkingFilesAsync(IReadOnlyList<CadTreeNode> requestedNodes)
    {
        var failures = new List<string>();
        var restore = new List<Tuple<CadTreeNode, Guid>>();
        var alreadyReleased = 0;
        foreach (var node in requestedNodes)
        {
            var released = false;
            try
            {
                if (await SkipRepeatedDiscardAsync(node)) { alreadyReleased++; continue; }
                var archived = (await apiClient.GetVersionsAsync(node.DocumentId.Value, lifetime.Token, 1)).Count > 0;
                var document = await apiClient.DiscardCheckoutAsync(node.DocumentId.Value, checkoutSessionId, lifetime.Token);
                if (document.Id != node.DocumentId || document.ProjectId == Guid.Empty
                    || !string.IsNullOrWhiteSpace(document.CheckedOutBy) || document.CheckoutSessionId.HasValue)
                    throw new InvalidOperationException("服务器尚未确认所选图档权限已释放，或图档关联需要核对，未恢复本地文件。");
                released = true;
                var matching = FindMatchingDocumentInstances(node, document.Id);
                ApplyCheckoutDocumentToMatchingInstances(matching, document);
                foreach (var instance in matching) instance.WorkState = CadWorkState.None;
                if (archived)
                {
                    MarkDiscardedWorkingPath(node, document.ProjectId);
                    if (File.Exists(node.FullPath)) SetFileReadOnly(node.FullPath, true, "服务器已释放编辑权限，恢复期间保护当前文件");
                    restore.Add(Tuple.Create(node, document.ProjectId));
                }
                else FinishLocalEdit(document.Id);
                historicalEditContexts.Remove(document.Id);
            }
            catch (Exception error)
            {
                LogDiagnostic("放弃所选工作文件：" + node.FullPath, error);
                failures.Add(node.FileName + "：" + (released ? "服务器已释放编辑权限，本地恢复尚未完成。" : "服务器尚未确认放弃编辑。") + DiscardRecoveryError(error));
            }
        }
        var completed = new List<CadTreeNode>();
        foreach (var project in restore.GroupBy(item => item.Item2))
        {
            try
            {
                ControlledOpenManifestDto combined = null;
                foreach (var target in project)
                {
                    var next = await apiClient.CreateControlledOpenManifestAsync(target.Item1.DocumentId.Value, null, false, false, lifetime.Token);
                    if (next.ProjectId != project.Key || !next.Files.Any(file => file.DocumentId == target.Item1.DocumentId))
                        throw new InvalidOperationException("服务器恢复清单与所选图档或项目不一致。");
                    if (combined != null && combined.Files.Join(next.Files, left => left.DocumentId, right => right.DocumentId,
                        (left, right) => left.VersionId != right.VersionId || left.Sha256 != right.Sha256).Any(conflict => conflict))
                        throw new InvalidOperationException("恢复清单的引用版本发生变化，工作文件及待恢复记录已保留。");
                    combined = combined == null ? next : MergeEditContext(combined, next);
                }
                var paths = project.Select(item => item.Item1.FullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                await RestoreDiscardedWorkingPathsAsync(combined, paths);
                completed.AddRange(project.Select(item => item.Item1));
            }
            catch (Exception error)
            {
                LogDiagnostic("服务器已放弃编辑，本地工作文件恢复未完成", error);
                failures.Add("服务器已释放编辑权限，本地恢复尚未完成；文件、草稿和待恢复记录均保留。" + DiscardRecoveryError(error));
            }
        }
        foreach (var node in completed)
        {
            completedDiscardPaths.Add(node.FullPath);
            foreach (var instance in FindMatchingDocumentInstances(node, node.DocumentId.Value).Where(item => PathsEqual(item.FullPath, node.FullPath)))
            { instance.WorkState = CadWorkState.None; instance.IsModifiedInSolidWorks = false; }
        }
        if (failures.Count > 0) throw new InvalidOperationException(string.Join("\r\n", failures.Take(8)));
        if (completed.Count == 0)
        {
            if (alreadyReleased > 0) taskPaneControl?.SetCheckoutReminder("编辑权限已释放，无需重复放弃；当前窗口及文件均保留。", false);
            return;
        }
        var linkedWarnings = await ReleaseUnchangedLinkedDrawingsAfterDiscardAsync(completed);
        taskPaneControl?.SetCheckoutReminder("已放弃所选图档编辑，原工作文件已恢复为存档版本并只读。"
            + (linkedWarnings.Count == 0 ? string.Empty : "\r\n配套工程图：" + string.Join("；", linkedWarnings)), linkedWarnings.Count > 0);
    }

    private async Task<bool> SkipRepeatedDiscardAsync(CadTreeNode node)
    {
        if (!EnsureEditJournal()) return false;
        var records = editJournal.ReadAll().Where(entry => entry.DocumentId == node.DocumentId
            && PathsEqual(entry.SourcePath, node.FullPath)).ToArray();
        var active = localEdits.TryGetValue(node.DocumentId.Value, out var edit) && !edit.Closed
            && edit.SessionId == checkoutSessionId && PathsEqual(edit.SourcePath, node.FullPath);
        if (!completedDiscardPaths.Contains(node.FullPath) && !string.IsNullOrWhiteSpace(node.CheckedOutBy)
            && (active || !records.Any(entry => entry.Closed && !entry.DiscardPending))) return false;
        var projectId = PdmDocumentIdentityStore.ReadProjectId(node.FullPath);
        if (!projectId.HasValue) throw new IOException("无法核对当前图档所属项目，已保留全部窗口和文件，未重复恢复。");
        var document = (await apiClient.GetDocumentsAsync(projectId.Value, lifetime.Token)).FirstOrDefault(item => item.Id == node.DocumentId);
        if (document == null) throw new IOException("服务器图档权限尚未核对，已保留全部窗口和文件，未重复恢复。");
        ApplyCheckoutDocumentToMatchingInstances(FindMatchingDocumentInstances(node, document.Id), document);
        if (!string.IsNullOrWhiteSpace(document.CheckedOutBy) || document.CheckoutSessionId.HasValue)
        {
            if (document.CheckoutSessionId == checkoutSessionId && string.Equals(document.CheckedOutBy, authenticatedUsername, StringComparison.OrdinalIgnoreCase)) return false;
            throw new InvalidOperationException("该图档的编辑权限属于其他人员或会话，已保留窗口和文件，未执行放弃。");
        }
        foreach (var matching in FindMatchingDocumentInstances(node, document.Id)) matching.WorkState = CadWorkState.None;
        if (records.Any(entry => entry.DiscardPending))
            throw new InvalidOperationException("编辑权限已释放，但上次本地恢复尚未完成。已保留当前窗口及待恢复记录，请从PLM打开最新版本完成核对，未重复关闭窗口。");
        LogOperation("重复放弃已跳过 document=" + document.Id + " path=" + node.FullPath + " closed=0 restored=0");
        return true;
    }

    private void MarkDiscardedLocalEdit(CadTreeNode node)
    {
        if (!PdmDocumentIdentityStore.TryReadLocalMetadata(node.FullPath, out var metadata)
            || metadata.DocumentId != node.DocumentId || !metadata.ProjectId.HasValue)
            throw new IOException("当前工作文件的项目关联无法核对，已保留文件，请核对后重试。");
        MarkDiscardedWorkingPath(node, metadata.ProjectId.Value);
    }

    private void MarkDiscardedWorkingPath(CadTreeNode node, Guid projectId)
    {
        if (!EnsureEditJournal()) throw new IOException("无法记录放弃编辑状态，请重新登录后重试；本地文件已保留。");
        if (PdmDocumentIdentityStore.TryReadLocalMetadata(node.FullPath, out var existingBinding)
            && (existingBinding.DocumentId != node.DocumentId || existingBinding.ProjectId != projectId))
            throw new IOException("当前工作路径关联了其他图档或项目，未改动其文件和恢复记录。");
        var all = editJournal.ReadAll();
        var entries = all.Where(entry => entry.DocumentId == node.DocumentId.Value && entry.ProjectId == projectId
            && PathsEqual(entry.SourcePath, node.FullPath)).ToList();
        if (entries.Count == 0)
        {
            PdmDocumentIdentityStore.TryReadLocalMetadata(node.FullPath, out var metadata);
            entries.Add(new LocalEditDraft { DocumentId = node.DocumentId.Value, ProjectId = projectId,
                SessionId = all.Any(entry => entry.DocumentId == node.DocumentId && entry.SessionId == checkoutSessionId) ? Guid.NewGuid() : checkoutSessionId,
                SourcePath = node.FullPath, BaseVersionId = metadata?.VersionId, BaseSha256 = metadata?.Sha256 });
        }
        foreach (var entry in entries) editJournal.MarkDiscarded(entry);
        if (localEdits.TryGetValue(node.DocumentId.Value, out var local) && local.ProjectId == projectId && PathsEqual(local.SourcePath, node.FullPath))
        {
            localEdits.Remove(node.DocumentId.Value);
            lock (checkoutDocumentSync) pendingCloseDocumentIds.Remove(node.DocumentId.Value);
        }
    }

    private async Task RestoreDiscardedEditsAsync(ControlledOpenManifestDto manifest)
    {
        try { await RestoreDiscardedEditsCoreAsync(manifest); }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            LogDiagnostic("恢复已放弃的工作文件", error);
            var message = error.Message ?? string.Empty;
            if (error is System.Net.Http.HttpRequestException || message.Length == 0 || message[0] < '\u3400' || message[0] > '\u9fff')
                message = "未能恢复服务器版本，放弃状态及本地文件已保留。请检查网络、磁盘空间和文件占用后从PLM重新打开。";
            throw new InvalidOperationException(message, error);
        }
    }

    private async Task<string> RestoreDiscardedEditsForOpenAsync(ControlledOpenManifestDto manifest, bool readOnly)
    {
        try { await RestoreDiscardedEditsAsync(manifest); return null; }
        catch (InvalidOperationException error) when (readOnly
            && (error.InnerException is IOException || error.InnerException is UnauthorizedAccessException))
        {
            // A pending working-copy recovery does not invalidate the independently
            // verified server snapshot. Editing continues to require full recovery.
            LogDiagnostic("工作文件待恢复，继续打开服务器只读副本", error);
            return "工作文件恢复尚未完成，已打开服务器只读副本；原文件、草稿及待恢复记录均保留。详情：" + error.Message;
        }
    }

    private Task RestoreDiscardedEditsCoreAsync(ControlledOpenManifestDto manifest) => RestoreDiscardedWorkingPathsAsync(manifest, null);

    private async Task RestoreDiscardedWorkingPathsAsync(ControlledOpenManifestDto manifest, IReadOnlyCollection<string> selectedPaths)
    {
        if (!EnsureEditJournal()) throw new IOException("放弃编辑的本地恢复记录尚未准备完成，工作文件已保留。");
        var entries = editJournal.ReadAll().Where(entry => entry.DiscardPending && entry.ProjectId == manifest.ProjectId
            && manifest.Files.Any(file => file.DocumentId == entry.DocumentId)
            && (selectedPaths == null || selectedPaths.Any(path => PathsEqual(path, entry.SourcePath)))).ToArray();
        if (entries.Length == 0) return;
        var groups = entries.GroupBy(entry => entry.SourcePath, StringComparer.OrdinalIgnoreCase).ToArray();
        var selectedWorkingPaths = new HashSet<string>(groups.Select(group => group.Key), StringComparer.OrdinalIgnoreCase);
        var changed = new List<IGrouping<string, LocalEditDraft>>();
        foreach (var group in groups)
        {
            var entry = group.OrderByDescending(item => item.SavedAtUtc).First();
            var file = manifest.Files.Single(item => item.DocumentId == entry.DocumentId);
            var expectedBinding = ValidateDiscardBinding(entry);
            var loaded = FindLoadedDocument(entry.SourcePath);
            if (loaded?.GetSaveFlag() == true || !ControlledReferenceMaterializer.TryReadVerifiedCopyHash(entry.SourcePath, file, out var verifiedHash)) { changed.Add(group); continue; }
            if (!KeepUnchangedAssemblyWorkingReferences(loaded, entry.SourcePath, file, manifest, group, verifiedHash))
            {
                var referencePlan = PlanDiscardWorkingReferences(entry.SourcePath, entry.SourcePath, manifest, selectedWorkingPaths);
                if (referencePlan.Any(pair => !PathsEqual(pair.Key, pair.Value))) { changed.Add(group); continue; }
            }
            // Nothing was edited: release only this actual working file in place.
            SetFileReadOnly(entry.SourcePath, true, "当前工作文件已匹配存档，放弃编辑后原地只读");
            if (loaded != null) EnsureDocumentOpenedReadOnly(loaded, entry.SourcePath);
            CompleteDiscardBinding(group, file, manifest.ProjectId, verifiedHash, expectedBinding);
            LogOperation("Discard working unchanged completed path=" + entry.SourcePath);
        }
        if (changed.Count == 0) return;
        var paths = new HashSet<string>(changed.Select(group => group.Key), StringComparer.OrdinalIgnoreCase);
        var closing = DiscardAffectedDocuments(paths);
        var staging = Path.Combine(Path.GetTempPath(), "UPLM", "discard-restore", Guid.NewGuid().ToString("N"));
        var restoreFiles = new Dictionary<Guid, string>();
        // Fetch canonical bytes, never a cached Snapshot whose references may point
        // into that Snapshot directory. No user window is closed until validation.
        foreach (var entry in changed.Select(group => group.First()).GroupBy(entry => entry.DocumentId).Select(group => group.First()))
        {
            var file = manifest.Files.Single(item => item.DocumentId == entry.DocumentId);
            restoreFiles[entry.DocumentId] = await apiClient.DownloadControlledOpenFileAsync(file,
                Path.Combine(staging, entry.DocumentId.ToString("N")), lifetime.Token);
        }
        var references = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in changed)
        {
            var entry = group.OrderByDescending(item => item.SavedAtUtc).First();
            var file = manifest.Files.Single(item => item.DocumentId == entry.DocumentId);
            ValidateDiscardBinding(entry);
            editJournal.ValidateDiscardedFile(entry, restoreFiles[entry.DocumentId], file.Sha256);
            references[entry.SourcePath] = PlanDiscardWorkingReferences(restoreFiles[entry.DocumentId], entry.SourcePath, manifest,
                selectedWorkingPaths);
        }
        closing = DiscardAffectedDocuments(paths); // CAD state may change while HTTP is pending.
        var ambiguous = CaptureLoadedDocumentSnapshots().GroupBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Select(item => item.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1
                && closing.Any(item => string.Equals(item.Title, group.Key, StringComparison.OrdinalIgnoreCase)));
        if (ambiguous != null)
            throw new IOException("同名图档的工作路径不唯一，未按标题关闭任何窗口；待恢复记录已保留：" + ambiguous.Key);
        var previousWindows = CapturePlmOpenWindows();
        var closingPaths = new HashSet<string>(closing.Select(document => document.Path), StringComparer.OrdinalIgnoreCase);
        PreserveReadOnlyContextStates(closing.Where(item => !paths.Contains(item.Path)));
        foreach (var document in closing.OrderByDescending(item => item.DocumentType == (int)swDocumentTypes_e.swDocASSEMBLY))
        {
            application.CloseDoc(document.Title);
            LogOperation("放弃恢复临时关闭引用窗口 path=" + document.Path);
        }
        if (CaptureLoadedDocumentSnapshots().Any(item => paths.Contains(item.Path)))
            throw new IOException("图档仍被打开的装配或工程图占用；放弃状态已记录，请关闭引用窗口后从PLM重新打开。");
        foreach (var group in changed)
        {
            var entry = group.OrderByDescending(item => item.SavedAtUtc).First();
            var file = manifest.Files.Single(item => item.DocumentId == entry.DocumentId);
            var expectedBinding = ValidateDiscardBinding(entry);
            var source = restoreFiles[entry.DocumentId];
            var replaced = false;
            var restoredHash = file.Sha256;
            try
            {
                editJournal.RestoreDiscardedFile(entry, source, file.Sha256);
                replaced = true;
                restoredHash = RestoreDiscardWorkingReferences(entry.SourcePath, source, references[entry.SourcePath], file.Sha256);
                CompleteDiscardBinding(group, file, manifest.ProjectId, restoredHash, expectedBinding);
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                // If reference rebasing succeeded but its binding could not be saved,
                // keep a canonical retryable file rather than an untracked rewrite.
                if (replaced && !ControlledReferenceMaterializer.Matches(entry.SourcePath, file)
                    && string.Equals(ComputeFileHash(entry.SourcePath), restoredHash, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(ComputeFileHash(source), file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        SetFileReadOnly(entry.SourcePath, false, "放弃恢复未完成，保留可重试的存档原始字节");
                        File.Copy(source, entry.SourcePath, true);
                        SetFileReadOnly(entry.SourcePath, true, "放弃恢复待重试");
                    }
                    catch (Exception rollback) { LogDiagnostic("放弃恢复的原始字节回退未完成，待恢复记录保留", rollback); }
                }
                LogDiagnostic("放弃恢复文件失败；路径=" + entry.SourcePath + "；副本=" + source
                    + "；记录指纹=" + entry.DiskSha256 + "；恢复指纹=" + file.Sha256
                    + "；底层错误码=0x" + error.GetBaseException().HResult.ToString("X8"), error);
                throw new IOException("本地工作文件恢复失败：" + file.FileName + "；路径：" + entry.SourcePath
                    + "。文件及备份已保留，请检查文件占用和目录写入权限后重试；仍可从PLM打开最新只读版本。", error);
            }
            LogOperation("Discard working restored completed path=" + entry.SourcePath);
        }
        RestoreDiscardWorkingWindows(previousWindows, closingPaths, paths);
    }

    private void RestoreDiscardWorkingWindows(PlmOpenWindow[] previous, ISet<string> closedPaths, ISet<string> discardedPaths)
    {
        var failures = new List<string>();
        foreach (var window in previous.Where(window => closedPaths.Contains(window.Path))
            .OrderByDescending(window => window.DocumentType == (int)swDocumentTypes_e.swDocASSEMBLY))
        {
            try
            {
                var context = controlledOpenManifests.FirstOrDefault(pair => IsPathWithinDirectory(window.Path, pair.Key));
                using (var search = context.Value == null ? null : new ControlledReferenceSearchScope(application, window.Path, context.Value))
                {
                    var opened = OpenOrActivateDocumentOnSolidWorksThread(window.Path, window.DocumentType, window.Configuration,
                        window.ReadOnly || discardedPaths.Contains(window.Path));
                    if (opened == null) throw new IOException("原工作窗口尚未恢复：" + window.Path);
                    if (context.Value != null) ValidateControlledLoadedPathsCore(opened, window.Path, context.Value, application, true);
                    search?.VerifyLoadedSources();
                }
            }
            catch (Exception error) { LogDiagnostic("恢复放弃编辑前窗口：" + window.Path, error); failures.Add(window.Path); }
        }
        var active = previous.FirstOrDefault(window => window.WasActive);
        if (active != null && !PathsEqual((application.ActiveDoc as IModelDoc2)?.GetPathName(), active.Path))
            if (OpenOrActivateDocumentOnSolidWorksThread(active.Path, active.DocumentType, active.Configuration,
                active.ReadOnly || discardedPaths.Contains(active.Path)) == null) failures.Add(active.Path);
        if (failures.Count > 0) throw new IOException("文件已恢复，但部分原窗口尚未恢复；文件和恢复记录已保留：" + string.Join("；", failures));
        LogOperation("放弃编辑前窗口已恢复 count=" + previous.Count(window => closedPaths.Contains(window.Path)) + " active=" + active?.Path);
    }

    private bool KeepUnchangedAssemblyWorkingReferences(IModelDoc2 loaded, string path, ControlledOpenFileDto file,
        ControlledOpenManifestDto manifest, IEnumerable<LocalEditDraft> records, string verifiedHash)
    {
        if (!string.Equals(Path.GetExtension(path), ".SLDASM", StringComparison.OrdinalIgnoreCase)) return false;
        if (loaded == null)
        {
            // A previous no-op discard may have closed the window before failing
            // its serialized-address check. Complete only its unchanged, observed
            // baseline; the subsequent controlled open still validates real sources.
            return records.Any(entry => entry.ObservedOpen && !entry.UnsavedChanges && entry.BaseVersionId == file.VersionId
                && string.Equals(entry.DiskSha256, verifiedHash, StringComparison.OrdinalIgnoreCase)
                && (string.Equals(entry.BaseSha256, verifiedHash, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(file.Sha256, verifiedHash, StringComparison.OrdinalIgnoreCase)));
        }
        if (!(loaded is IAssemblyDoc)) return false;
        // Stored source strings (and the native dependency cache) can retain old
        // addresses while every actual component already uses this working copy.
        // Validate those real sources without rewriting any file or closing it.
        ValidateControlledLoadedPathsCore(loaded, path, manifest, application, true);
        var directory = Path.GetDirectoryName(path);
        var loadedPaths = new HashSet<string>(((application.GetDocuments() as Array)?.OfType<IModelDoc2>() ?? Enumerable.Empty<IModelDoc2>())
            .Select(model => model.GetPathName()).Where(value => !string.IsNullOrWhiteSpace(value)), StringComparer.OrdinalIgnoreCase);
        foreach (var reference in manifest.Files)
        {
            var expected = ControlledReferenceMaterializer.LocalPath(directory, reference);
            if (loadedPaths.Contains(expected) && (!PdmDocumentIdentityStore.TryReadLocalMetadata(expected, out var binding)
                || binding.DocumentId != reference.DocumentId || binding.ProjectId != manifest.ProjectId || binding.VersionId != reference.VersionId))
                throw new IOException("总装内容未修改，但实际引用的版本关联需要核对，已保留原窗口及文件，未重写引用：" + reference.FileName);
        }
        LogOperation("未修改总装原位放弃，已核对实际加载来源，不按旧引用地址重写文件 path=" + path + " files=" + manifest.Files.Count);
        return true;
    }

    private static PdmControlledFileMetadata ValidateDiscardBinding(LocalEditDraft entry)
    {
        if (!PdmDocumentIdentityStore.TryReadLocalMetadata(entry.SourcePath, out var metadata)
            || metadata.DocumentId != entry.DocumentId || metadata.ProjectId != entry.ProjectId)
            throw new IOException("工作路径的图档或项目关联无法核对，未覆盖文件：" + entry.SourcePath);
        return metadata;
    }

    private void CompleteDiscardBinding(IEnumerable<LocalEditDraft> records, ControlledOpenFileDto file, Guid projectId,
        string verifiedHash, PdmControlledFileMetadata expectedBinding)
    {
        var group = records.ToArray();
        var path = group[0].SourcePath;
        if (string.IsNullOrWhiteSpace(verifiedHash) || !string.Equals(ComputeFileHash(path), verifiedHash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("恢复核对后工作文件又发生变化，未认定放弃完成，文件及待恢复记录已保留。");
        if (expectedBinding?.DocumentId != file.DocumentId || expectedBinding.ProjectId != projectId
            || !PdmDocumentIdentityStore.TryRecoverControlledVersion(path, expectedBinding, projectId,
            file.VersionId, file.Revision, file.Sha256, file.FileLength)
            || !PdmDocumentIdentityStore.TryReadLocalMetadata(path, out var metadata)
            || (!string.Equals(verifiedHash, file.Sha256, StringComparison.OrdinalIgnoreCase)
                && !PdmDocumentIdentityStore.TryRecordResolvedReferences(path, metadata, verifiedHash))
            || !string.Equals(ComputeFileHash(path), verifiedHash, StringComparison.OrdinalIgnoreCase)
            || !ControlledReferenceMaterializer.Matches(path, file))
            throw new IOException("工作文件恢复后的版本关联未能确认，待恢复状态已保留，请重试。");
        SetFileReadOnly(path, true, "已验证放弃恢复的实际工作文件及版本关联");
        foreach (var context in controlledOpenManifests.Where(pair => pair.Value.ProjectId == projectId))
        {
            foreach (var local in context.Value.Files.Where(local => local.DocumentId == file.DocumentId
                && PathsEqual(path, Path.Combine(context.Key, local.RelativePath ?? local.FileName))))
            {
                local.VersionId = file.VersionId;
                local.Revision = file.Revision;
                local.Sha256 = file.Sha256;
                local.FileLength = file.FileLength;
                if (context.Value.RootDocumentId == file.DocumentId && PathsEqual(path, Path.Combine(context.Key, context.Value.RootRelativePath)))
                { context.Value.RootVersionId = file.VersionId; context.Value.RootRevision = file.Revision; }
            }
        }
        foreach (var record in group)
        {
            record.DiscardPending = false;
            record.RestoreOnOpen = false;
            editJournal.Save(record);
        }
    }

    private Dictionary<string, string> PlanDiscardWorkingReferences(string canonicalPath, string targetPath,
        ControlledOpenManifestDto manifest, ISet<string> selectedPaths)
    {
        var plan = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var files = manifest.Files.ToDictionary(file => file.FileName, StringComparer.OrdinalIgnoreCase);
        foreach (var source in ReadDiscardNativeReferences(canonicalPath))
        {
            if (!files.TryGetValue(Path.GetFileName(source), out var file))
                throw new IOException("存档引用未在受控恢复清单中，未猜测替代文件：" + Path.GetFileName(source));
            var target = ControlledReferenceMaterializer.LocalPath(Path.GetDirectoryName(targetPath), file);
            if (!PdmDocumentIdentityStore.TryReadLocalMetadata(target, out var metadata)
                || metadata.DocumentId != file.DocumentId || metadata.ProjectId != manifest.ProjectId
                || (!selectedPaths.Contains(target) && metadata.VersionId != file.VersionId))
                throw new IOException("引用工作文件的身份或基准版本不一致，未覆盖其他文件：" + file.FileName);
            plan[source] = target;
        }
        return plan;
    }

    private string[] ReadDiscardNativeReferences(string path)
    {
        var values = application.GetDocumentDependencies2(path, false, false, false) as Array;
        if (values == null)
        {
            if (Path.GetExtension(path).Equals(".SLDPRT", StringComparison.OrdinalIgnoreCase)) return Array.Empty<string>();
            throw new IOException("未能完整读取存档图档的引用，未改写工作文件：" + Path.GetFileName(path));
        }
        if (values.Length % 2 != 0) throw new IOException("存档引用清单不完整，工作文件已保留：" + Path.GetFileName(path));
        return Enumerable.Range(0, values.Length / 2).Select(index => (Convert.ToString(values.GetValue(index * 2 + 1)) ?? "").Split('|')[0])
            .Where(reference => new[] { ".SLDPRT", ".SLDASM", ".SLDDRW" }.Contains(Path.GetExtension(reference), StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private string RestoreDiscardWorkingReferences(string target, string canonical, IReadOnlyDictionary<string, string> references, string canonicalHash)
    {
        if (!string.Equals(ComputeFileHash(target), canonicalHash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("工作文件在恢复后又发生变化，未改写引用，待恢复记录已保留。");
        if (references.Count == 0) return canonicalHash;
        var writtenHash = canonicalHash;
        SetFileReadOnly(target, false, "仅恢复所选工作文件的受控引用");
        try
        {
            foreach (var pair in references.Where(pair => !PathsEqual(pair.Key, pair.Value)))
            {
                if (!string.Equals(ComputeFileHash(target), writtenHash, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("引用恢复期间文件又发生变化，后续修改已保留。");
                var replaced = application.ReplaceReferencedDocument(target, pair.Key, pair.Value);
                writtenHash = ComputeFileHash(target);
                if (!replaced)
                    throw new IOException("所选工作文件的引用恢复未完成：" + Path.GetFileName(target));
            }
            var actual = ReadDiscardNativeReferences(target);
            if (actual.Any(path => !references.Values.Any(expected => PathsEqual(path, expected)))
                || references.Values.Any(path => !actual.Any(current => PathsEqual(path, current))))
                throw new IOException("恢复后的引用没有全部指向已核对的工作文件：" + Path.GetFileName(target));
            if (!string.Equals(ComputeFileHash(target), writtenHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("引用核对期间工作文件又发生变化，文件及待恢复记录已保留。");
            return writtenHash;
        }
        catch
        {
            if (string.Equals(ComputeFileHash(target), writtenHash, StringComparison.OrdinalIgnoreCase)
                && string.Equals(ComputeFileHash(canonical), canonicalHash, StringComparison.OrdinalIgnoreCase))
                File.Copy(canonical, target, true);
            throw;
        }
        finally { SetFileReadOnly(target, true, "所选工作文件恢复结束，保持只读"); }
    }

    private async Task RestoreCurrentDiscardedEditsForOpenAsync(ControlledOpenManifestDto manifest)
    {
        if (!EnsureEditJournal()) return;
        var currentPaths = new HashSet<string>(CaptureLoadedDocumentSnapshots().Select(item => item.Path), StringComparer.OrdinalIgnoreCase);
        foreach (var node in currentTree == null ? Array.Empty<CadTreeNode>() : EnumerateArchiveNodes(currentTree))
            if (!string.IsNullOrWhiteSpace(node.FullPath)) currentPaths.Add(node.FullPath);
        var currentIds = new HashSet<Guid>();
        foreach (var path in currentPaths)
            if (PdmDocumentIdentityStore.TryReadLocalMetadata(path, out var binding) && binding.ProjectId == manifest.ProjectId)
                currentIds.Add(binding.DocumentId);
        // A failed replacement can already have closed its owner window. Resume
        // only one unambiguous, explicitly discarded physical path, never every
        // historical copy sharing that document ID or another current workspace.
        foreach (var group in editJournal.ReadAll().Where(entry => entry.DiscardPending && entry.ProjectId == manifest.ProjectId
            && manifest.Files.Any(file => file.DocumentId == entry.DocumentId)).GroupBy(entry => entry.DocumentId))
        {
            if (group.Any(entry => currentPaths.Contains(entry.SourcePath)) || currentIds.Contains(group.Key)) continue;
            var pendingPaths = group.Select(entry => entry.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (pendingPaths.Length != 1)
                throw new InvalidOperationException("服务器已释放编辑权限，但有多个工作目录等待放弃恢复，未猜测或覆盖任何目录。请从原工作目录打开后重试。");
            currentPaths.Add(pendingPaths[0]);
        }
        if (currentPaths.Count == 0) return;
        try { await RestoreDiscardedWorkingPathsAsync(manifest, currentPaths); }
        catch (Exception error)
        {
            LogDiagnostic("恢复当前工作区已放弃的编辑", error);
            throw new InvalidOperationException("服务器已释放编辑权限，但原工作文件尚未恢复；当前文件及待恢复记录已保留。" + DiscardRecoveryError(error), error);
        }
    }

    private static string DiscardRecoveryError(Exception error)
    {
        if (IsArchiveConnectionFailure(error))
            return "PLM服务器连接中断或响应超时，请在网络恢复后重试本次放弃或恢复操作。";
        var message = error.Message ?? string.Empty;
        if ((error is IOException || error is InvalidOperationException) && message.Length > 0
            && message[0] >= '\u3400' && message[0] <= '\u9fff') return message;
        if (error is UnauthorizedAccessException) return "当前工作目录读写权限不足，文件已保留，请检查权限后重试。";
        return "工作文件恢复校验尚未完成，文件及待恢复记录已保留，请检查文件占用和读写权限后重试；技术详情已写入诊断日志。";
    }

    private LoadedDocumentSnapshot[] DiscardAffectedDocuments(HashSet<string> discarded)
    {
        var snapshots = CaptureLoadedDocumentSnapshots();
        var models = (application?.GetDocuments() as Array)?.OfType<IModelDoc2>().ToArray() ?? Array.Empty<IModelDoc2>();
        var references = models.Where(model => !string.IsNullOrWhiteSpace(model.GetPathName()))
            .GroupBy(model => model.GetPathName(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => LoadedModelReferencePaths(group.First()), StringComparer.OrdinalIgnoreCase);
        var closing = new HashSet<string>(discarded, StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            var owners = references.Where(pair => !closing.Contains(pair.Key) && pair.Value.Any(closing.Contains)).Select(pair => pair.Key).ToArray();
            if (owners.Length == 0) break;
            closing.UnionWith(owners);
        }
        var affected = new HashSet<string>(closing, StringComparer.OrdinalIgnoreCase);
        foreach (var owner in closing.Where(references.ContainsKey)) affected.UnionWith(references[owner]);
        var dirty = snapshots.FirstOrDefault(item => affected.Contains(item.Path) && !discarded.Contains(item.Path)
            && item.HasUnsavedChanges && (!item.HasDiscardableReadOnlySaveFlag || !closing.Contains(item.Path)));
        if (dirty != null)
            throw new InvalidOperationException("放弃状态已保留，但引用窗口还有其他未保存修改：" + dirty.Title
                + "。请保存该图档后从PLM重新打开；不会丢弃其他零件的修改。");
        return snapshots.Where(item => closing.Contains(item.Path)).ToArray();
    }

    private static string[] LoadedModelReferencePaths(IModelDoc2 model)
    {
        if (model is IAssemblyDoc assembly)
            return ((assembly.GetComponents(false) as Array)?.OfType<IComponent2>() ?? Enumerable.Empty<IComponent2>())
                .Select(component => (component.GetModelDoc2() as IModelDoc2)?.GetPathName() ?? component.GetPathName())
                .Where(path => !string.IsNullOrWhiteSpace(path)).ToArray();
        if (model is IDrawingDoc drawing)
            return (drawing.GetViews() as object[] ?? Array.Empty<object>()).OfType<object[]>()
                .SelectMany(sheet => sheet.Skip(1).OfType<IView>())
                .Select(view => (view.ReferencedDocument as IModelDoc2)?.GetPathName() ?? view.GetReferencedModelName())
                .Where(path => !string.IsNullOrWhiteSpace(path)).ToArray();
        return Array.Empty<string>();
    }
}
