using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    private sealed class CompletedWorkingHead
    {
        internal ControlledOpenFileDto File;
        internal ControlledOpenManifestDto Manifest;
        internal ControlledOpenManifestDto Parent;
        internal DocumentVersionDto Version;
        internal bool RequiresSessionReceipt;
    }

    // A parent archive keeps its own frozen references after a child is checked in.
    // Verify the child's independent current head; never rewrite that parent archive
    // or relabel the old bytes of an in-memory drawing as the committed drawing.
    private async Task<Dictionary<Guid, CompletedWorkingHead>> FindCompletedWorkingHeadsAsync(string directory,
        ControlledOpenManifestDto working, ControlledOpenManifestDto parent, ISet<Guid> requestedIds)
    {
        var result = new Dictionary<Guid, CompletedWorkingHead>();
        if (working.ProjectId != parent.ProjectId || working.RootDocumentId != parent.RootDocumentId
            || working.RootVersionId != parent.RootVersionId || !PathsEqual(working.RootRelativePath, parent.RootRelativePath)) return result;
        foreach (var before in working.Files.Where(file => file.DocumentId != working.RootDocumentId
            && DocumentKindFromPath(file.FileName) == CadDocumentKind.Part))
        {
            var frozen = parent.Files.SingleOrDefault(file => file.DocumentId == before.DocumentId);
            if (frozen == null || frozen.VersionId == before.VersionId || !WorkingHeadPathMatches(before, frozen)) continue;
            var path = ControlledReferenceMaterializer.LocalPath(directory, before);
            var requiresReceipt = !requestedIds.Contains(before.DocumentId);
            if (requiresReceipt && !completedWorkingArchives.ContainsKey(path)) continue;
            if (!WorkingHeadDiskMatches(directory, working.ProjectId, before)) continue;
            var manifest = await apiClient.CreateControlledOpenManifestAsync(before.DocumentId, null, false, true,
                lifetime.Token, allowMissingComponents: true);
            if (!WorkingHeadScopeMatches(directory, working, parent, before, manifest)) continue;
            var file = manifest.Files.Single(item => item.DocumentId == before.DocumentId);
            var exact = (await apiClient.GetVersionsAsync(before.DocumentId, lifetime.Token, 1)).FirstOrDefault();
            if (!WorkingReuseSnapshotVersionMatches(file, exact) || exact.DocumentId != before.DocumentId) continue;
            var previous = (await apiClient.GetVersionsAsync(before.DocumentId, lifetime.Token)).FirstOrDefault(version => version.Id == frozen.VersionId);
            if (!WorkingReuseSnapshotVersionMatches(frozen, previous)
                || !WorkingHeadReferenceScopeMatches(previous.ReferenceSnapshot, exact.ReferenceSnapshot)) continue;
            var head = new CompletedWorkingHead { File = new ControlledOpenFileDto {
                DocumentId = file.DocumentId, VersionId = file.VersionId, FileName = before.FileName,
                RelativePath = before.RelativePath, Configuration = before.Configuration, IsRoot = before.IsRoot,
                Revision = file.Revision, Sha256 = file.Sha256, FileLength = file.FileLength,
                NativeIsolation = file.NativeIsolation, IsNativeIsolated = file.IsNativeIsolated },
                Manifest = manifest, Parent = parent, Version = exact, RequiresSessionReceipt = requiresReceipt };
            if (!WorkingCompletedHeadEvidenceMatches(directory, working, head)) continue;
            result.Add(file.DocumentId, head);
            LogOperation("获取权限已独立核对已存档子件最新基准，保留父装配历史引用：" + file.FileName
                + "；版本=" + file.VersionId + "；显式选择=" + !requiresReceipt);
        }
        return result;
    }

    private static bool WorkingHeadPathMatches(ControlledOpenFileDto before, ControlledOpenFileDto current) =>
        before.DocumentId == current.DocumentId
        && string.Equals(before.FileName, current.FileName, StringComparison.OrdinalIgnoreCase)
        && string.Equals(before.RelativePath ?? before.FileName, current.RelativePath ?? current.FileName, StringComparison.OrdinalIgnoreCase)
        && WorkingConfigurationMatches(before, current);

    private static bool WorkingHeadDiskMatches(string directory, Guid projectId, ControlledOpenFileDto file)
    {
        var path = ControlledReferenceMaterializer.LocalPath(directory, file);
        return WorkingReuseBindingMatches(path, projectId, file) && File.Exists(path)
            && new FileInfo(path).Length == file.FileLength && !string.IsNullOrWhiteSpace(file.Sha256)
            && string.Equals(ComputeFileHash(path), file.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static bool WorkingHeadScopeMatches(string directory, ControlledOpenManifestDto working,
        ControlledOpenManifestDto parent, ControlledOpenFileDto selected, ControlledOpenManifestDto independent)
    {
        if (independent == null || !independent.ForEdit || independent.ProjectId != working.ProjectId
            || independent.RootDocumentId != selected.DocumentId || independent.RootVersionId != selected.VersionId
            || !PathsEqual(independent.RootRelativePath, selected.RelativePath ?? selected.FileName)
            || independent.Files == null || independent.Files.Count == 0
            || independent.Files.GroupBy(file => file.DocumentId).Any(group => group.Count() != 1)
            || independent.Files.GroupBy(file => file.RelativePath ?? file.FileName, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() != 1)) return false;
        var root = independent.Files.SingleOrDefault(file => file.DocumentId == selected.DocumentId);
        if (root == null || !root.IsRoot || root.VersionId != independent.RootVersionId) return false;
        foreach (var file in independent.Files)
        {
            var drawing = DocumentKindFromPath(file.FileName) == CadDocumentKind.Drawing;
            var expected = (drawing ? parent : working).Files.SingleOrDefault(candidate => candidate.DocumentId == file.DocumentId);
            if (expected == null || expected.VersionId != file.VersionId || !WorkingHeadPathMatches(expected, file)
                || !WorkingReuseFileBytesMatch(expected, file)) return false;
            // Drawings retain their separate disk/memory proof in the normal checkout
            // path. This independent part proof never approves or updates their bytes.
            if (!drawing && !WorkingHeadDiskMatches(directory, working.ProjectId, expected)) return false;
        }
        return true;
    }

    private static bool WorkingHeadReferenceScopeMatches(DocumentReferenceNodeDto previous, DocumentReferenceNodeDto current) =>
        previous != null && current != null && previous.DocumentId == current.DocumentId
        && previous.Kind == (int)CadDocumentKind.Part && current.Kind == previous.Kind
        && string.Equals(previous.FileName, current.FileName, StringComparison.OrdinalIgnoreCase)
        && string.Equals(previous.Configuration ?? string.Empty, current.Configuration ?? string.Empty, StringComparison.OrdinalIgnoreCase)
        && previous.Role == current.Role && previous.IsContextReference == current.IsContextReference
        && previous.TreeScopeOnly == current.TreeScopeOnly && previous.IsSuppressed == current.IsSuppressed
        && previous.Quantity == current.Quantity && previous.Status == current.Status
        && previous.NativeIsolationVerified == current.NativeIsolationVerified
        && WorkingReuseReleaseIsolationMatches(previous.NativeIsolation, current.NativeIsolation)
        && !previous.ReferenceTypeUnverified && !current.ReferenceTypeUnverified
        && !previous.StructureIndexPending && !current.StructureIndexPending
        && previous.IncludesDesignDependencies == current.IncludesDesignDependencies
        && previous.UsesNativeFileInventory == current.UsesNativeFileInventory
        && previous.Children != null && current.Children != null && previous.Children.Count == current.Children.Count
        && previous.Children.Zip(current.Children, WorkingReuseReleaseReferenceEvidenceMatches).All(matches => matches);

    private bool WorkingCompletedHeadEvidenceMatches(string directory, ControlledOpenManifestDto working, CompletedWorkingHead head)
    {
        var source = working.Files.SingleOrDefault(file => file.DocumentId == head.File.DocumentId);
        if (source == null || source.VersionId != head.File.VersionId || !WorkingHeadPathMatches(source, head.File)
            || !WorkingReuseFileBytesMatch(source, head.File) || !WorkingHeadDiskMatches(directory, working.ProjectId, source)) return false;
        if (!string.Equals(head.Version.ReferenceSnapshot.Configuration ?? string.Empty, source.Configuration ?? string.Empty, StringComparison.OrdinalIgnoreCase)) return false;
        return WorkingHeadScopeMatches(directory, working, head.Parent, source, head.Manifest)
            && (!head.RequiresSessionReceipt || CompletedWorkingFile(directory, working, head.File) != null);
    }

    private async Task VerifyCompletedWorkingHeadsAsync(string directory, ControlledOpenManifestDto working,
        IReadOnlyDictionary<Guid, CompletedWorkingHead> heads)
    {
        foreach (var head in heads.Values)
        {
            var exact = (await apiClient.GetVersionsAsync(head.File.DocumentId, lifetime.Token, 1)).FirstOrDefault();
            var independent = await apiClient.CreateControlledOpenManifestAsync(head.File.DocumentId, null, false, true,
                lifetime.Token, allowMissingComponents: true);
            if (!WorkingReuseSnapshotVersionMatches(head.File, exact) || exact.DocumentId != head.File.DocumentId
                || !WorkingReuseReleaseReferenceEvidenceMatches(head.Version.ReferenceSnapshot, exact.ReferenceSnapshot)
                || !WorkingReuseManifestsMatch(head.Manifest, independent))
                throw new InvalidOperationException("已存档子件的服务器最新版本或引用在获取期间发生变化，已保留窗口、文件和现有权限，请刷新核对：" + head.File.FileName);
        }
        // Recheck every local proof after the awaits, including earlier children.
        foreach (var head in heads.Values)
            if (!WorkingCompletedHeadEvidenceMatches(directory, working, head))
                throw new InvalidOperationException("已存档子件的本地基准在获取期间发生变化，已保留窗口、文件和现有权限，尚未解锁：" + head.File.FileName);
    }
}
