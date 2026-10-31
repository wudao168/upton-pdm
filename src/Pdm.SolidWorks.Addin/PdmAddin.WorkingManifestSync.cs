using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    private static ControlledOpenManifestDto WithWorkingFiles(ControlledOpenManifestDto source, IReadOnlyList<ControlledOpenFileDto> files) =>
        new ControlledOpenManifestDto { Id = source.Id, ProjectId = source.ProjectId, ProjectCode = source.ProjectCode,
            RootDocumentId = source.RootDocumentId, RootVersionId = source.RootVersionId, RootRevision = source.RootRevision,
            RootRelativePath = source.RootRelativePath, ForEdit = source.ForEdit, Files = files.ToList(), Warnings = source.Warnings };

    private static ControlledOpenManifestDto WorkingAcquisitionScope(ControlledOpenManifestDto source, ISet<Guid> drawings) =>
        WithWorkingFiles(source, source.Files.Where(file => DocumentKindFromPath(file.FileName) != CadDocumentKind.Drawing
            || drawings.Contains(file.DocumentId)).ToArray());

    private ControlledOpenManifestDto ReconcileRequestedWorkingArchives(string directory, ControlledOpenManifestDto previous,
        ControlledOpenManifestDto latest, ISet<Guid> drawings)
    {
        var reconciled = ReconcileCompletedWorkingArchives(directory, WorkingAcquisitionScope(previous, drawings), WorkingAcquisitionScope(latest, drawings));
        var files = reconciled.Files.Concat(previous.Files.Where(file => DocumentKindFromPath(file.FileName) == CadDocumentKind.Drawing
            && !drawings.Contains(file.DocumentId))).ToList();
        foreach (var drawing in latest.Files.Where(file => DocumentKindFromPath(file.FileName) == CadDocumentKind.Drawing
            && !drawings.Contains(file.DocumentId) && files.All(existing => existing.DocumentId != file.DocumentId)))
        {
            // Adopt only a confirmed local receipt; absent/unrelated drawings are
            // neither downloaded nor prerequisites for this model checkout.
            var completed = CompletedWorkingFile(directory, latest, drawing);
            if (completed != null) files.Add(completed);
        }
        return WithWorkingFiles(previous, files);
    }

    private async Task<ControlledOpenManifestDto> ReconcileStoredWorkingMembershipAsync(string directory,
        ControlledOpenManifestDto previous, ControlledOpenManifestDto latest, ISet<Guid> drawings)
    {
        if (previous.ProjectId != latest.ProjectId || previous.RootDocumentId != latest.RootDocumentId
            || previous.RootVersionId != latest.RootVersionId || !PathsEqual(previous.RootRelativePath, latest.RootRelativePath)
            || latest.Files.GroupBy(file => file.DocumentId).Any(group => group.Count() != 1)) return previous;
        var oldModels = previous.Files.Where(file => DocumentKindFromPath(file.FileName) != CadDocumentKind.Drawing)
            .Select(file => file.DocumentId).OrderBy(id => id).ToArray();
        var newModels = latest.Files.Where(file => DocumentKindFromPath(file.FileName) != CadDocumentKind.Drawing)
            .Select(file => file.DocumentId).OrderBy(id => id).ToArray();
        if (oldModels.SequenceEqual(newModels)) return previous;
        var oldRoot = previous.Files.SingleOrDefault(file => file.DocumentId == previous.RootDocumentId);
        var newRoot = latest.Files.SingleOrDefault(file => file.DocumentId == latest.RootDocumentId);
        if (oldRoot == null || newRoot == null || oldRoot.VersionId != newRoot.VersionId
            || !WorkingConfigurationMatches(oldRoot, newRoot)) return previous;
        var rootPath = ControlledReferenceMaterializer.LocalPath(directory, oldRoot);
        if (!WorkingReuseBindingMatches(rootPath, previous.ProjectId, oldRoot)
            || !ControlledReferenceMaterializer.Matches(rootPath, oldRoot)) return previous;
        if (!WorkingReuseFileBytesMatch(oldRoot, newRoot)
            && !WorkingReuseStoredSourceVersionMatches(oldRoot, newRoot,
                (await apiClient.GetVersionsAsync(newRoot.DocumentId, lifetime.Token)).FirstOrDefault(item => item.Id == newRoot.VersionId))) return previous;
        var files = new List<ControlledOpenFileDto>();
        foreach (var current in latest.Files)
        {
            var before = previous.Files.FirstOrDefault(file => file.DocumentId == current.DocumentId);
            var optionalDrawing = DocumentKindFromPath(current.FileName) == CadDocumentKind.Drawing && !drawings.Contains(current.DocumentId);
            if (optionalDrawing)
            {
                if (before != null) files.Add(before); // Do not replace unrelated local drawing windows or contents.
                else
                {
                    var completed = CompletedWorkingFile(directory, latest, current);
                    if (completed != null) files.Add(completed);
                }
                continue;
            }
            if (before == null || before.VersionId != current.VersionId)
            {
                var completed = CompletedWorkingFile(directory, latest, current);
                if (completed == null) return previous;
                files.Add(completed); continue;
            }
            if (!WorkingConfigurationMatches(before, current)
                || !string.Equals(before.RelativePath ?? before.FileName, current.RelativePath ?? current.FileName, StringComparison.OrdinalIgnoreCase)) return previous;
            var path = ControlledReferenceMaterializer.LocalPath(directory, before);
            // The caller verifies current bytes and any live draft before acquiring.
            // Preserve a child's own unsent edits while refreshing the parent's membership.
            if (!WorkingReuseBindingMatches(path, previous.ProjectId, before)) return previous;
            if (!WorkingReuseFileBytesMatch(before, current)
                && !WorkingReuseStoredSourceVersionMatches(before, current,
                    (await apiClient.GetVersionsAsync(current.DocumentId, lifetime.Token)).FirstOrDefault(item => item.Id == current.VersionId))) return previous;
            files.Add(before);
        }
        LogOperation("已按当前已存档总装基准同步工作清单 old=" + previous.Files.Count + " current=" + files.Count + " deletedLocalFiles=0");
        return WithWorkingFiles(previous, files);
    }

    private ControlledOpenFileDto CompletedWorkingFile(string directory, ControlledOpenManifestDto manifest, ControlledOpenFileDto file)
    {
        var path = ControlledWorkspaceManager.PlannedFilePath(directory, file);
        if (!File.Exists(path)) return null;
        if (!completedWorkingArchives.TryGetValue(path, out var receipt) || receipt.ProjectId != manifest.ProjectId
            || receipt.DocumentId != file.DocumentId || receipt.VersionId != file.VersionId || receipt.Username != authenticatedUsername
            || receipt.ServerScope != apiClient.EditJournalScope || !string.Equals(receipt.StoredSha256, file.Sha256, StringComparison.OrdinalIgnoreCase)
            || receipt.StoredLength != file.FileLength || !PdmDocumentIdentityStore.TryReadLocalMetadata(path, out var binding)
            || binding.DocumentId != receipt.DocumentId || binding.ProjectId != receipt.ProjectId || binding.VersionId != receipt.VersionId
            || binding.Sha256 != receipt.SourceSha256 || binding.FileLength != receipt.SourceLength
            || new FileInfo(path).Length != receipt.SourceLength || ComputeFileHash(path) != receipt.SourceSha256) return null;
        return new ControlledOpenFileDto { DocumentId = file.DocumentId, VersionId = file.VersionId, FileName = file.FileName,
            RelativePath = file.RelativePath, Configuration = file.Configuration, IsRoot = file.IsRoot, Revision = file.Revision,
            Sha256 = receipt.SourceSha256, FileLength = receipt.SourceLength, NativeIsolation = file.NativeIsolation, IsNativeIsolated = file.IsNativeIsolated };
    }

    private async Task RefreshCommittedWorkingMembershipsAsync(IEnumerable<Guid> documentIds)
    {
        var ids = new HashSet<Guid>(documentIds);
        foreach (var context in controlledOpenManifests.Where(pair => ids.Contains(pair.Value.RootDocumentId)).ToArray())
        {
            try
            {
                var latest = await apiClient.CreateControlledOpenManifestAsync(context.Value.RootDocumentId, null, false, true, lifetime.Token);
                var reconciled = ReconcileRequestedWorkingArchives(context.Key, context.Value, latest, new HashSet<Guid>());
                controlledOpenManifests[context.Key] = await ReconcileStoredWorkingMembershipAsync(context.Key, reconciled, latest,
                    new HashSet<Guid>()); // A baseline update does not acquire or update any drawing.
            }
            catch (Exception error) { LogDiagnostic("图档已存档，工作清单稍后重试同步；原文件和窗口保留", error); }
        }
    }

    private async Task<string> WorkingReuseVerifiedDifferencesAsync(ControlledOpenManifestDto before, ControlledOpenManifestDto current)
    {
        var conflicts = new List<string>();
        foreach (var file in current.Files)
        {
            var original = before.Files.FirstOrDefault(item => item.DocumentId == file.DocumentId);
            if (original != null && original.VersionId == file.VersionId && WorkingConfigurationMatches(original, file)
                && string.Equals(original.FileName, file.FileName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(original.RelativePath ?? original.FileName, file.RelativePath ?? file.FileName, StringComparison.OrdinalIgnoreCase))
            {
                if (WorkingReuseFileBytesMatch(original, file)) continue;
                var exact = (await apiClient.GetVersionsAsync(file.DocumentId, lifetime.Token)).FirstOrDefault(item => item.Id == file.VersionId);
                if (WorkingReuseStoredSourceVersionMatches(original, file, exact)) continue;
            }
            conflicts.Add(file.FileName + "（基准需核对）");
        }
        conflicts.AddRange(before.Files.Where(file => !current.Files.Any(item => item.DocumentId == file.DocumentId))
            .Select(file => file.FileName + "（已不属于服务器当前装配结构，本地文件保留）"));
        if (before.RootVersionId != current.RootVersionId) conflicts.Insert(0, Path.GetFileName(before.RootRelativePath) + "（总装基准版本已变化）");
        return conflicts.Count == 0 ? "工作清单范围需核对" : string.Join("、", conflicts.Take(6));
    }
}
