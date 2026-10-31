using System;
using System.Collections.Generic;
using System.IO;

namespace Upton.Pdm.SolidWorks;

// The original server fingerprint stays unchanged. Saved edits are accepted only
// during this explicitly verified native open, not as an unchanged server cache.
internal sealed class ControlledWorkingCopyScope : IDisposable
{
    [ThreadStatic] private static ControlledWorkingCopyScope current;
    private readonly ControlledWorkingCopyScope previous;
    private readonly Guid projectId;
    private readonly Dictionary<string, Tuple<ControlledOpenFileDto, string>> files =
        new Dictionary<string, Tuple<ControlledOpenFileDto, string>>(StringComparer.OrdinalIgnoreCase);

    internal ControlledWorkingCopyScope(string rootPath, ControlledOpenManifestDto manifest)
    {
        previous = current; projectId = manifest.ProjectId;
        foreach (var file in manifest.Files)
            if (manifest.RetainedWorkingHashes.TryGetValue(file.DocumentId, out var hash))
                files.Add(ControlledReferenceMaterializer.LocalPath(Path.GetDirectoryName(rootPath), file), Tuple.Create(file, hash));
        current = this;
    }
    internal static bool Matches(string path, ControlledOpenFileDto file, string hash)
    {
        if (current == null || !current.files.TryGetValue(Path.GetFullPath(path), out var proof)) return false;
        var expected = proof.Item1;
        return expected.DocumentId == file.DocumentId && expected.VersionId == file.VersionId
            && string.Equals(expected.Sha256, file.Sha256, StringComparison.OrdinalIgnoreCase)
            && string.Equals(proof.Item2, hash, StringComparison.OrdinalIgnoreCase)
            && PdmDocumentIdentityStore.TryReadLocalMetadata(path, out var binding)
            && binding.ProjectId == current.projectId && binding.DocumentId == file.DocumentId && binding.VersionId == file.VersionId
            && string.Equals(binding.Sha256, file.Sha256, StringComparison.OrdinalIgnoreCase)
            && (file.NativeIsolation != null ? binding.NativeIsolationSnapshotId == file.NativeIsolation.ReferenceSnapshotId
                : !file.IsNativeIsolated || binding.NativeIsolationSnapshotId == file.VersionId);
    }
    internal static bool Contains(string path, ControlledOpenFileDto file) => current != null
        && current.files.TryGetValue(Path.GetFullPath(path), out var proof) && proof.Item1.DocumentId == file.DocumentId
        && proof.Item1.VersionId == file.VersionId && string.Equals(proof.Item1.Sha256, file.Sha256, StringComparison.OrdinalIgnoreCase);
    public void Dispose() { current = previous; }
}
