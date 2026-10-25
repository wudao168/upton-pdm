using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Upton.Pdm.SolidWorks;

// Only downloaded/working copies are rewritten. Archived bytes and their version
// hashes remain immutable; the separately verified local hash is not a new version.
internal static class ControlledReferenceMaterializer
{
    internal static bool Matches(string path, ControlledOpenFileDto file)
        => TryReadVerifiedCopyHash(path, file, out _);

    internal static bool TryReadVerifiedCopyHash(string path, ControlledOpenFileDto file, out string hash)
    {
        hash = null;
        if (!File.Exists(path)) return false;
        hash = ArchiveFileFingerprint.Read(path);
        return MatchesHash(path, file, hash);
    }

    private static bool MatchesHash(string path, ControlledOpenFileDto file, string hash)
    {
        return string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase)
            || PdmDocumentIdentityStore.TryReadLocalMetadata(path, out var metadata)
                && metadata.DocumentId == file.DocumentId
                && PdmDocumentIdentityStore.MatchesResolvedVersion(path, file.VersionId, file.Sha256, hash);
    }

    internal static string ReadVerifiedCopyHash(string path, ControlledOpenFileDto file)
    {
        var hash = ArchiveFileFingerprint.Read(path);
        if (!MatchesHash(path, file, hash)) throw new IOException("服务器版本副本校验失败，未恢复工作文件。");
        return hash;
    }

    internal static void Prepare(ISldWorks app, string rootPath, ControlledOpenManifestDto manifest) =>
        PrepareCore(app, rootPath, manifest, false);

    internal static void PrepareEditable(ISldWorks app, string rootPath, ControlledOpenManifestDto manifest) =>
        PrepareCore(app, rootPath, manifest, true);

    internal static void PrepareDrawingOnly(ISldWorks app, string rootPath, ControlledOpenManifestDto manifest) =>
        PrepareCore(app, rootPath, manifest, false, rootPath);

    private static void PrepareCore(ISldWorks app, string rootPath, ControlledOpenManifestDto manifest, bool preserveRecognizedEdits,
        string onlyOwner = null)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(rootPath));
        var files = manifest.Files.ToDictionary(file => file.FileName, StringComparer.OrdinalIgnoreCase);
        var loaded = new HashSet<string>(((app.GetDocuments() as Array)?.Cast<object>() ?? Enumerable.Empty<object>())
            .OfType<IModelDoc2>().Select(model => model.GetPathName()).Where(path => !string.IsNullOrWhiteSpace(path)),
            StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            var owner = LocalPath(directory, file);
            if (onlyOwner != null && !string.Equals(owner, onlyOwner, StringComparison.OrdinalIgnoreCase)) continue;
            if (loaded.Contains(owner)) continue;
            if (!Matches(owner, file))
            {
                if (preserveRecognizedEdits && PdmDocumentIdentityStore.TryReadLocalMetadata(owner, out var edited)
                    && edited.DocumentId == file.DocumentId && edited.ProjectId == manifest.ProjectId && edited.VersionId.HasValue)
                {
                    PluginDiagnosticLog.Record("本地修改保留", "[F11] 编辑工作副本及原基准版本已保留，跳过引用改写：" + file.FileName);
                    continue;
                }
                var message = "受控副本存在未核对的本地修改，已保留文件，未重定位引用：" + file.FileName;
                if (DocumentRuleGate.ShouldBlock("F11", message)) throw new IOException(message);
                // Disabling the gate must not overwrite an unverified local edit.
                // Let the native open and its loaded-path validation determine usability.
                continue;
            }
            if (!IsNative(owner)) continue;
            var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var values = app.GetDocumentDependencies2(owner, false, false, false) as Array;
                if (values == null || values.Length % 2 != 0)
                {
                    PluginDiagnosticLog.Record("引用重定位", file.FileName + "：旧引用清单未完整读取，将按受控搜索目录原生打开。");
                    continue;
                }
                for (var index = 1; index < values.Length; index += 2)
                {
                    var native = (Convert.ToString(values.GetValue(index)) ?? "").Split('|')[0];
                    if (!IsNative(native)) continue;
                    if (!files.TryGetValue(Path.GetFileName(native), out var target)) continue;
                    var destination = LocalPath(directory, target);
                    if (!string.Equals(native, destination, StringComparison.OrdinalIgnoreCase)) replacements[native] = destination;
                }
            }
            catch (Exception error)
            {
                PluginDiagnosticLog.Record("引用重定位", file.FileName + "：旧引用清单读取失败，将核对实际打开来源：" + error);
                continue;
            }
            if (replacements.Count == 0) continue;
            var before = ArchiveFileFingerprint.Read(owner);
            PdmDocumentIdentityStore.TryReadLocalMetadata(owner, out var baseline);
            var unchanged = baseline?.VersionId != null && baseline.DocumentId == file.DocumentId
                && (string.Equals(before, baseline.Sha256, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(before, baseline.ResolvedReferenceSha256, StringComparison.OrdinalIgnoreCase));
            var attributes = File.GetAttributes(owner);
            var backup = Path.Combine(Path.GetDirectoryName(owner), ".ref-" + Guid.NewGuid().ToString("N") + ".bak");
            File.Copy(owner, backup, false);
            try
            {
                File.SetAttributes(owner, attributes & ~FileAttributes.ReadOnly);
                foreach (var pair in replacements)
                    if (!app.ReplaceReferencedDocument(owner, pair.Key, pair.Value))
                        throw new IOException("原生接口未完成引用替换：" + file.FileName + " → " + Path.GetFileName(pair.Value));
                if (unchanged && !PdmDocumentIdentityStore.TryRecordResolvedReferences(owner, baseline, ArchiveFileFingerprint.Read(owner)))
                    throw new IOException("引用已定位，但无法记录本地副本校验信息：" + file.FileName);
            }
            catch (Exception error)
            {
                // F14-F16: restore every partial rewrite before the native-open fallback.
                File.Copy(backup, owner, true);
                PluginDiagnosticLog.Record("引用重定位", file.FileName + "：已恢复原始副本，将尝试原生打开并核对实际来源：" + error);
            }
            finally
            {
                File.SetAttributes(owner, attributes);
                File.SetAttributes(backup, FileAttributes.Normal);
                File.Delete(backup);
            }
        }
        // F25/F26: no speculative OpenDoc/Save3, BreakLink or virtual externalization.
        // The actual user-requested open performs the loaded-source verification.
    }
    private static bool IsNative(string path) => new[] { ".SLDASM", ".SLDPRT", ".SLDDRW" }
        .Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

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
