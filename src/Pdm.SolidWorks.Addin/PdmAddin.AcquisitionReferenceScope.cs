using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    private async Task<HashSet<Guid>> ReadRequiredOperationFileIdsAsync(ControlledOpenManifestDto manifest,
        IDictionary<Guid, DocumentVersionDto> verifiedVersions)
    {
        var all = new HashSet<Guid>(manifest.Files.Where(file => file.DocumentId == manifest.RootDocumentId
                || DocumentKindFromPath(file.FileName) != CadDocumentKind.Drawing)
            .Select(file => file.DocumentId));
        // The download manifest can expand an assembly environment into hundreds
        // of files. That inventory is not the selected model's design dependency scope.
        // An environment may have a named configuration (native import preserves it).
        // Configuration text cannot establish whether a file is a design dependency.
        if (!manifest.Files.Any(file => file.DocumentId != manifest.RootDocumentId
            && DocumentKindFromPath(file.FileName) == CadDocumentKind.Assembly))
            return all;
        var files = manifest.Files.ToDictionary(file => file.DocumentId);
        var required = new HashSet<Guid>();
        var hasVerifiedEnvironment = false;
        var pending = new Queue<Guid>();
        pending.Enqueue(manifest.RootDocumentId);
        using var slots = new System.Threading.SemaphoreSlim(6);
        while (pending.Count > 0)
        {
            var owners = new List<ControlledOpenFileDto>();
            while (pending.Count > 0)
            {
                var id = pending.Dequeue();
                if (!required.Add(id)) continue;
                if (!files.TryGetValue(id, out var owner)) return all;
                owners.Add(owner);
            }
            // Read each immutable version once in this operation. Bound concurrent
            // metadata requests instead of serially waiting once per component.
            var versions = await Task.WhenAll(owners.Select(async owner =>
            {
                await slots.WaitAsync(lifetime.Token);
                try
                {
                    if (!verifiedVersions.TryGetValue(owner.DocumentId, out var version) || version.Id != owner.VersionId)
                        version = (await apiClient.GetVersionsAsync(owner.DocumentId, lifetime.Token)).FirstOrDefault(item => item.Id == owner.VersionId);
                    if (!WorkingReuseSnapshotVersionMatches(owner, version) || version.DocumentId != owner.DocumentId
                        || version.ReferenceSnapshot.Kind != (int)DocumentKindFromPath(owner.FileName))
                        throw new InvalidOperationException("所选图档的引用版本证据需要核对，已保留窗口和图档：" + owner.FileName);
                    return version;
                }
                finally { slots.Release(); }
            }));
            foreach (var version in versions) verifiedVersions[version.DocumentId] = version;
            foreach (var version in versions)
            {
                // Walk each exact owner once. Explicit drivers are retained even when
                // the same file is also mentioned as an environment by another edge.
                var references = new Stack<DocumentReferenceNodeDto>(version.ReferenceSnapshot.Children ?? new List<DocumentReferenceNodeDto>());
                while (references.Count > 0)
                {
                    var reference = references.Pop();
                    if (reference.Kind == (int)CadDocumentKind.Drawing || reference.Status == (int)CadReferenceStatus.Virtual) continue;
                    if (!reference.DocumentId.HasValue || !files.TryGetValue(reference.DocumentId.Value, out var file))
                    {
                        if (reference.Status == (int)CadReferenceStatus.Missing) continue;
                        return all; // Unproved membership must never silently reduce the checks.
                    }
                    if (!string.Equals(reference.FileName, file.FileName, StringComparison.OrdinalIgnoreCase)) return all;
                    if (IsVerifiedAssemblyEnvironmentReference(reference)) { hasVerifiedEnvironment = true; continue; }
                    pending.Enqueue(file.DocumentId);
                    foreach (var child in reference.Children ?? new List<DocumentReferenceNodeDto>()) references.Push(child);
                }
            }
        }
        if (!hasVerifiedEnvironment) return all;
        LogOperation("已按实际引用区分图档操作范围 root=" + manifest.RootDocumentId + " required=" + required.Count
            + " environmentOnly=" + all.Count(id => !required.Contains(id)) + " downloaded=" + manifest.Files.Count);
        return required;
    }

    private static bool WorkingReuseRootVersionMatches(ControlledOpenManifestDto before, ControlledOpenManifestDto current) =>
        before.RootVersionId == current.RootVersionId
        // A projected component scope retains the workspace identity and path, but
        // its parent's version is not a version of any file being acquired/changed.
        || !before.Files.Any(file => file.DocumentId == before.RootDocumentId)
            && !current.Files.Any(file => file.DocumentId == current.RootDocumentId);
}
