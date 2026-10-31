using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    private async Task<IReadOnlyList<DocumentDto>> ResumeRetainedWorkingCopyAsync(ControlledOpenManifestDto manifest)
    {
        if (!EnsureEditJournal()) return Array.Empty<DocumentDto>();
        var recovered = await RestoreClosedWorkingDraftsAsync(manifest);
        if (recovered.Count > 0) return recovered;
        string rootPath;
        try { rootPath = controlledWorkspace.GetWorkingRootPath(manifest); }
        catch (FileNotFoundException) { return Array.Empty<DocumentDto>(); }
        var directory = Path.GetDirectoryName(rootPath);
        var files = manifest.Files.ToDictionary(file => file.DocumentId);
        var entries = editJournal.ReadAll().Where(entry => entry.ProjectId == manifest.ProjectId && !entry.Closed
            && !entry.DiscardPending && !entry.PendingClose && files.TryGetValue(entry.DocumentId, out var file)
            && PathsEqual(entry.SourcePath, ControlledReferenceMaterializer.LocalPath(directory, file)))
            .GroupBy(entry => entry.DocumentId).Select(group => group.OrderByDescending(entry => entry.CheckedOutAt).First()).ToArray();
        if (entries.Length == 0 || !entries.Any(entry => !ControlledReferenceMaterializer.Matches(entry.SourcePath, files[entry.DocumentId])))
            return Array.Empty<DocumentDto>();
        var documents = (await apiClient.GetDocumentsAsync(manifest.ProjectId, lifetime.Token)).ToDictionary(document => document.Id);
        bool MatchesLease(LocalEditDraft entry)
        {
            var file = files[entry.DocumentId];
            return documents.TryGetValue(entry.DocumentId, out var document) && document.ProjectId == manifest.ProjectId
                && string.Equals(document.FileName, file.FileName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(document.CheckedOutBy, authenticatedUsername, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(authenticatedUsername) && document.CheckoutSessionId == entry.SessionId
                && string.Equals(document.CheckoutMachine, checkoutMachineName, StringComparison.OrdinalIgnoreCase)
                && document.CheckedOutAt.HasValue && document.CheckedOutAt.Value.ToUniversalTime() == entry.CheckedOutAt.ToUniversalTime()
                && (entry.SessionId == checkoutSessionId || !EditDraftJournal.OwnerProcessRunning(entry))
                && entry.BaseVersionId == file.VersionId && WorkingReuseBindingMatches(entry.SourcePath, manifest.ProjectId, file)
                && PdmDocumentIdentityStore.TryReadLocalMetadata(entry.SourcePath, out var binding)
                && !string.IsNullOrWhiteSpace(entry.BaseSha256)
                && (string.Equals(entry.BaseSha256, binding.Sha256, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(entry.BaseSha256, binding.ResolvedReferenceSha256, StringComparison.OrdinalIgnoreCase));
        }
        var hashes = new Dictionary<Guid, string>();
        foreach (var file in manifest.Files)
        {
            var path = ControlledReferenceMaterializer.LocalPath(directory, file);
            if (ControlledReferenceMaterializer.TryReadVerifiedCopyHash(path, file, out var hash)) continue;
            var entry = entries.SingleOrDefault(item => item.DocumentId == file.DocumentId);
            if (hash == null || entry == null || !MatchesLease(entry))
                throw new InvalidDataException("不能自动续接该本地修改，文件及恢复记录已保留：" + file.FileName
                    + "。请核对原编辑账号、电脑、服务器版本或未完成的放弃操作，未下载覆盖文件。");
            hashes.Add(file.DocumentId, hash);
        }
        var resumed = new List<DocumentDto>();
        foreach (var entry in entries.Where(MatchesLease))
        {
            var file = files[entry.DocumentId];
            if (hashes.TryGetValue(entry.DocumentId, out var savedHash))
            {
                editJournal.Preserve(entry, entry.SourcePath, false);
                if (!string.Equals(entry.DraftSha256, savedHash, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("保存续接恢复副本期间文件发生变化，已保留文件和原编辑权限，请稍后重试。");
            }
            var document = documents[entry.DocumentId];
            if (document.CheckoutSessionId != checkoutSessionId)
            {
                plmAuthoritativeCheckouts.Add(entry.DocumentId);
                try { document = await apiClient.CheckoutAsync(entry.DocumentId, checkoutSessionId, checkoutMachineName, lifetime.Token); }
                finally { plmAuthoritativeCheckouts.Remove(entry.DocumentId); }
            }
            if (document.Id != entry.DocumentId || document.ProjectId != manifest.ProjectId
                || document.CheckoutSessionId != checkoutSessionId || !document.CheckedOutAt.HasValue
                || !string.Equals(document.CheckedOutBy, authenticatedUsername, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(document.CheckoutMachine, checkoutMachineName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("服务器未确认本机原编辑会话的续接，原文件和恢复记录已保留。");
            LocalEditDraft continued;
            using (var process = Process.GetCurrentProcess())
                continued = new LocalEditDraft { DocumentId = entry.DocumentId, ProjectId = entry.ProjectId,
                    SessionId = checkoutSessionId, CheckedOutAt = document.CheckedOutAt.Value, SourcePath = entry.SourcePath,
                    BaseVersionId = entry.BaseVersionId, BaseSha256 = entry.BaseSha256, DraftPath = entry.DraftPath,
                    DraftSha256 = entry.DraftSha256, DiskSha256 = entry.DiskSha256, SavedAtUtc = entry.SavedAtUtc,
                    ProcessId = process.Id, ProcessStartedUtcTicks = process.StartTime.ToUniversalTime().Ticks };
            editJournal.Save(continued);
            localEdits[entry.DocumentId] = continued;
            TrackCheckoutDocument(document);
            if (entry.SessionId != checkoutSessionId)
            {
                entry.Closed = true; entry.RestoreOnOpen = false; editJournal.Save(entry);
            }
            resumed.Add(document);
        }
        await RequireRetainedManifestUnchangedAsync(manifest);
        foreach (var pair in hashes) manifest.RetainedWorkingHashes.Add(pair.Key, pair.Value);
        foreach (var document in resumed) manifest.ResumedDocumentIds.Add(document.Id);
        LogOperation("已续接本机原编辑会话 files=" + resumed.Count + " savedEdits=" + hashes.Count + " replaced=0 downloaded=0");
        return resumed;
    }

    private async Task RequireRetainedManifestUnchangedAsync(ControlledOpenManifestDto manifest)
    {
        var files = manifest.Files.ToDictionary(file => file.DocumentId);
        var current = await apiClient.CreateControlledOpenManifestAsync(manifest.RootDocumentId, null, false, manifest.ForEdit, lifetime.Token);
        if (current.ProjectId != manifest.ProjectId || current.RootDocumentId != manifest.RootDocumentId
            || current.RootVersionId != manifest.RootVersionId || !PathsEqual(current.RootRelativePath, manifest.RootRelativePath)
            || current.Files.Count != manifest.Files.Count || current.Files.Any(file => !files.TryGetValue(file.DocumentId, out var prior)
                || file.VersionId != prior.VersionId || file.FileLength != prior.FileLength
                || !string.Equals(file.Sha256, prior.Sha256, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(file.RelativePath ?? file.FileName, prior.RelativePath ?? prior.FileName, StringComparison.OrdinalIgnoreCase)
                || !WorkingConfigurationMatches(prior, file) || file.IsNativeIsolated != prior.IsNativeIsolated
                || file.NativeIsolation?.ReferenceSnapshotId != prior.NativeIsolation?.ReferenceSnapshotId))
            throw new InvalidOperationException("续接期间服务器版本或引用清单发生变化，已保留本机修改及续接记录，未打开其他版本。");
    }
}
