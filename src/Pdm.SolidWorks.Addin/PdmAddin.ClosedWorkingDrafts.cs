using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    // User-selected policy: restore the latest verified saved work, including after
    // checkout auto-close or cache eviction. Never revive an explicitly discarded draft.
    private async Task<IReadOnlyList<DocumentDto>> RestoreClosedWorkingDraftsAsync(ControlledOpenManifestDto manifest)
    {
        var files = manifest.Files.ToDictionary(file => file.DocumentId);
        var entries = editJournal.ReadAll().Where(entry => entry.ProjectId == manifest.ProjectId && entry.RestoreOnOpen
            && !entry.DiscardPending && !entry.PendingClose && (entry.Closed || entry.SessionId == checkoutSessionId)
            && files.ContainsKey(entry.DocumentId))
            .GroupBy(entry => entry.DocumentId).Select(group => group.OrderByDescending(entry => entry.SavedAtUtc)
                .ThenByDescending(entry => entry.CheckedOutAt).First()).ToArray();
        if (entries.Length == 0) return Array.Empty<DocumentDto>();
        var projectDirectory = ControlledWorkspaceManager.ProjectViewDirectory(controlledWorkspace.WorkspaceRoot, manifest.ProjectCode);
        foreach (var entry in entries)
        {
            var file = files[entry.DocumentId];
            if (!IsPathWithinDirectory(entry.SourcePath, projectDirectory) || !PdmDocumentIdentityStore.IsControlledWorkspacePath(entry.SourcePath)
                || !string.Equals(Path.GetFileName(entry.SourcePath), file.FileName, StringComparison.OrdinalIgnoreCase)
                || entry.BaseVersionId != file.VersionId || !editJournal.HasVerifiedDraft(entry)
                || entry.SessionId != checkoutSessionId && EditDraftJournal.OwnerProcessRunning(entry))
                throw new InvalidDataException("上次编辑草稿的版本、原位置或校验记录需要核对，草稿已保留，未自动覆盖：" + file.FileName);
            if (File.Exists(entry.SourcePath))
            {
                var hash = ArchiveFileFingerprint.Read(entry.SourcePath);
                if (!string.Equals(hash, entry.DraftSha256, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(hash, entry.DiskSha256, StringComparison.OrdinalIgnoreCase)
                    && !ControlledReferenceMaterializer.Matches(entry.SourcePath, file))
                    throw new InvalidDataException("原工作文件与恢复草稿还有不同修改，已保留两份内容，请核对后恢复：" + file.FileName);
            }
        }
        var documents = (await apiClient.GetDocumentsAsync(manifest.ProjectId, lifetime.Token)).ToDictionary(document => document.Id);
        var targetIds = new HashSet<Guid>(entries.Select(entry => entry.DocumentId));
        var modelIds = new HashSet<Guid>(entries.Where(entry => DocumentKindFromPath(files[entry.DocumentId].FileName) != CadDocumentKind.Drawing)
            .Select(entry => entry.DocumentId));
        if (modelIds.Count > 0)
        {
            var relations = await apiClient.GetDocumentRelationsAsync(manifest.ProjectId, lifetime.Token);
            foreach (var relation in relations.Where(relation => modelIds.Contains(relation.ModelDocumentId) && files.ContainsKey(relation.DrawingDocumentId)))
                targetIds.Add(relation.DrawingDocumentId);
        }
        var directory = controlledWorkspace.UseRecoveryWorkingDirectory(manifest);
        ControlledCacheMaintenance.RequireOrdinaryPath(directory, controlledWorkspace.WorkspaceRoot);
        foreach (var file in manifest.Files)
            ControlledCacheMaintenance.RequireOrdinaryPath(ControlledWorkspaceManager.PlannedFilePath(directory, file), controlledWorkspace.WorkspaceRoot);
        ControlledOpenBoundary.RequireLoadedSources(application, Path.Combine(directory, manifest.RootRelativePath), manifest);
        var leaseRecords = editJournal.ReadAll();
        foreach (var id in targetIds)
        {
            var file = files[id];
            if (!documents.TryGetValue(id, out var document) || document.ProjectId != manifest.ProjectId
                || !string.Equals(document.FileName, file.FileName, StringComparison.OrdinalIgnoreCase)
                || document.CheckedOutBy != null && (!string.Equals(document.CheckedOutBy, authenticatedUsername, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(document.CheckoutMachine, checkoutMachineName, StringComparison.OrdinalIgnoreCase)
                    || document.CheckoutSessionId != checkoutSessionId && !leaseRecords.Any(record => record.DocumentId == id
                        && record.ProjectId == manifest.ProjectId && record.SessionId == document.CheckoutSessionId
                        && record.BaseVersionId == file.VersionId && !record.DiscardPending
                        && document.CheckedOutAt.HasValue && record.CheckedOutAt.ToUniversalTime() == document.CheckedOutAt.Value.ToUniversalTime()
                        && IsPathWithinDirectory(record.SourcePath, projectDirectory) && !EditDraftJournal.OwnerProcessRunning(record))))
                throw new InvalidOperationException("恢复所需图档正在其他会话编辑或身份不一致，草稿已保留，请稍后重试：" + file.FileName);
            if (FindLoadedDocument(ControlledWorkspaceManager.PlannedFilePath(directory, file)) != null)
                throw new InvalidOperationException("恢复目标图档仍在窗口中打开，原窗口和草稿已保留，请保存并关闭该图档后重试：" + file.FileName);
        }
        await RequireRetainedManifestUnchangedAsync(manifest);
        var resumed = new List<DocumentDto>();
        foreach (var id in targetIds)
        {
            var document = documents[id];
            if (document.CheckoutSessionId != checkoutSessionId)
            {
                plmAuthoritativeCheckouts.Add(id);
                try { document = await apiClient.CheckoutAsync(id, checkoutSessionId, checkoutMachineName, lifetime.Token); }
                finally { plmAuthoritativeCheckouts.Remove(id); }
            }
            if (document.Id != id || document.ProjectId != manifest.ProjectId || !document.CheckedOutAt.HasValue
                || document.CheckoutSessionId != checkoutSessionId || !string.Equals(document.CheckedOutBy, authenticatedUsername, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(document.CheckoutMachine, checkoutMachineName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("服务器未确认草稿恢复所需的编辑权限，草稿及已获取的权限已保留。");
            resumed.Add(document); TrackCheckoutDocument(document);
            var file = files[id]; var original = entries.FirstOrDefault(entry => entry.DocumentId == id);
            using (var process = Process.GetCurrentProcess())
            {
                var continued = new LocalEditDraft { DocumentId = id, ProjectId = manifest.ProjectId, SessionId = checkoutSessionId,
                    CheckedOutAt = document.CheckedOutAt.Value, SourcePath = ControlledWorkspaceManager.PlannedFilePath(directory, file),
                    BaseVersionId = file.VersionId, BaseSha256 = file.Sha256, DraftPath = original?.DraftPath,
                    DraftSha256 = original?.DraftSha256, DiskSha256 = original?.DiskSha256, SavedAtUtc = original?.SavedAtUtc ?? DateTime.MinValue,
                    RestoreOnOpen = original != null, ProcessId = process.Id, ProcessStartedUtcTicks = process.StartTime.ToUniversalTime().Ticks };
                editJournal.Save(continued); localEdits[id] = continued;
            }
        }
        await RequireRetainedManifestUnchangedAsync(manifest);
        foreach (var entry in entries)
        {
            var file = files[entry.DocumentId]; var target = ControlledWorkspaceManager.PlannedFilePath(directory, file);
            if (File.Exists(target) && string.Equals(ArchiveFileFingerprint.Read(target), entry.DraftSha256, StringComparison.OrdinalIgnoreCase))
            {
                if (!PdmDocumentIdentityStore.TryWriteControlledVersion(target, file.DocumentId, manifest.ProjectId,
                    file.VersionId, file.Revision, file.Sha256, file.FileLength)) throw new IOException("草稿基准记录未能保存，原文件已保留。");
                manifest.RetainedWorkingHashes[file.DocumentId] = entry.DraftSha256;
            }
        }
        // Fetch missing unchanged files first. Verified retained files are excluded
        // from downloads; unknown local revisions still stop the operation.
        await controlledWorkspace.PreparePlmWorkingCopyAsync(manifest, resumed.Select(document => document.Id).ToArray(), lifetime.Token);
        foreach (var entry in entries)
        {
            var file = files[entry.DocumentId]; var target = ControlledWorkspaceManager.PlannedFilePath(directory, file);
            var continued = localEdits[entry.DocumentId];
            string verifiedBase = null;
            if (!manifest.RetainedWorkingHashes.ContainsKey(entry.DocumentId)) verifiedBase = ControlledReferenceMaterializer.ReadVerifiedCopyHash(target, file);
            editJournal.RestoreWithVerifiedBase(continued, target, file.VersionId, file.Sha256, verifiedBase);
            if (!string.Equals(ArchiveFileFingerprint.Read(target), entry.DraftSha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("恢复后的图档与已校验草稿不一致，草稿和编辑权限已保留，未继续打开。");
            continued.RestoreOnOpen = false; continued.DiskSha256 = entry.DraftSha256; editJournal.Save(continued);
            if (entry.SessionId != checkoutSessionId) { entry.RestoreOnOpen = false; editJournal.Save(entry); }
            manifest.RetainedWorkingHashes[entry.DocumentId] = entry.DraftSha256;
        }
        foreach (var document in resumed) manifest.ResumedDocumentIds.Add(document.Id);
        await RequireRetainedManifestUnchangedAsync(manifest);
        LogOperation("已恢复上次未提交的编辑草稿 files=" + entries.Length + " permissions=" + resumed.Count + " newArchiveVersions=0");
        return resumed;
    }
}
