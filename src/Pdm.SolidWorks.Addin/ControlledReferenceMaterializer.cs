using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SolidWorks.Interop.sldworks;

namespace Upton.Pdm.SolidWorks;

// Only downloaded/working copies are rewritten. Archived bytes and their version
// hashes remain immutable; the separately verified local hash is not a new version.
internal static class ControlledReferenceMaterializer
{
    internal static bool Matches(string path, ControlledOpenFileDto file)
    {
        if (!File.Exists(path)) return false;
        var hash = ArchiveFileFingerprint.Read(path);
        return string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase)
            || PdmDocumentIdentityStore.TryReadLocalMetadata(path, out var metadata)
                && metadata.DocumentId == file.DocumentId
                && PdmDocumentIdentityStore.MatchesResolvedVersion(path, file.VersionId, file.Sha256, hash);
    }

    internal static void Prepare(ISldWorks app, string rootPath, ControlledOpenManifestDto manifest)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(rootPath));
        var files = manifest.Files.ToDictionary(file => file.FileName, StringComparer.OrdinalIgnoreCase);
        var loaded = new HashSet<string>(((app.GetDocuments() as Array)?.Cast<object>() ?? Enumerable.Empty<object>())
            .OfType<IModelDoc2>().Select(model => model.GetPathName()).Where(path => !string.IsNullOrWhiteSpace(path)),
            StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            var owner = LocalPath(directory, file);
            if (loaded.Contains(owner)) continue; // Never rewrite an open document's saved file underneath it.
            var values = app.GetDocumentDependencies2(owner, false, false, false) as Array;
            if (values == null) continue;
            if (values.Length % 2 != 0) throw new IOException("受控图档引用清单未完整读取，尚未打开：" + file.FileName);
            var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 1; index < values.Length; index += 2)
            {
                var original = Convert.ToString(values.GetValue(index)) ?? string.Empty;
                var native = original.Split('|')[0];
                string name;
                try { name = Path.GetFileName(native); }
                catch (ArgumentException) { continue; } // Historical auxiliary fields are not file identities.
                if (string.IsNullOrWhiteSpace(name) || !files.TryGetValue(name, out var target)) continue;
                var destination = LocalPath(directory, target);
                if (!string.Equals(native, destination, StringComparison.OrdinalIgnoreCase))
                    replacements[native] = destination;
            }
            if (replacements.Count == 0) continue;
            var before = ArchiveFileFingerprint.Read(owner);
            PdmDocumentIdentityStore.TryReadLocalMetadata(owner, out var baseline);
            var unchanged = baseline?.VersionId != null && baseline.DocumentId == file.DocumentId
                && (string.Equals(before, baseline.Sha256, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(before, baseline.ResolvedReferenceSha256, StringComparison.OrdinalIgnoreCase));
            var attributes = File.GetAttributes(owner);
            var backup = owner + "." + Guid.NewGuid().ToString("N") + ".reference-backup";
            File.Copy(owner, backup, false);
            try
            {
                File.SetAttributes(owner, attributes & ~FileAttributes.ReadOnly);
                foreach (var pair in replacements)
                    if (!app.ReplaceReferencedDocument(owner, pair.Key, pair.Value))
                        throw new IOException("未能将受控图档引用定位到本次PLM副本：" + file.FileName + " → " + Path.GetFileName(pair.Value));
                if (unchanged && !PdmDocumentIdentityStore.TryRecordResolvedReferences(owner, baseline, ArchiveFileFingerprint.Read(owner)))
                    throw new IOException("引用已定位，但无法记录本地副本校验信息：" + file.FileName);
            }
            catch
            {
                File.Copy(backup, owner, true);
                throw;
            }
            finally
            {
                File.SetAttributes(owner, attributes);
                File.SetAttributes(backup, FileAttributes.Normal);
                File.Delete(backup);
            }
        }
    }

    internal static string LocalPath(string directory, ControlledOpenFileDto file)
    {
        var prefix = Path.GetFullPath(directory).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(prefix, file.RelativePath ?? file.FileName));
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            throw new IOException("PLM受控清单中的文件尚未完整下载：" + file.FileName);
        return path;
    }

    internal static void CopyResolvedBinding(string source, string target)
    {
        if (!PdmDocumentIdentityStore.TryReadLocalMetadata(source, out var baseline)
            || !baseline.VersionId.HasValue || string.IsNullOrWhiteSpace(baseline.ResolvedReferenceSha256)) return;
        var hash = ArchiveFileFingerprint.Read(target);
        if (!string.Equals(hash, baseline.ResolvedReferenceSha256, StringComparison.OrdinalIgnoreCase)) return;
        if (!PdmDocumentIdentityStore.TryWriteControlledVersion(target, baseline.DocumentId, baseline.ProjectId,
            baseline.VersionId.Value, baseline.Revision, baseline.Sha256, baseline.FileLength ?? new FileInfo(source).Length)
            || !PdmDocumentIdentityStore.TryReadLocalMetadata(target, out var copied)
            || !PdmDocumentIdentityStore.TryRecordResolvedReferences(target, copied, hash))
            throw new IOException("工作副本引用校验记录未能保存：" + Path.GetFileName(target));
    }
}
