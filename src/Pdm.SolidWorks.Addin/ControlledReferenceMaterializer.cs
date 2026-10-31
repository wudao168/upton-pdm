using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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
        if (ControlledWorkingCopyScope.Matches(path, file, hash)) return true;
        if (file.NativeIsolation != null)
            return string.Equals(hash, file.NativeIsolation.Sha256, StringComparison.OrdinalIgnoreCase)
                || PdmDocumentIdentityStore.TryReadLocalMetadata(path, out var isolated)
                    && isolated.DocumentId == file.DocumentId && isolated.VersionId == file.VersionId
                    && isolated.NativeIsolationSnapshotId == file.NativeIsolation.ReferenceSnapshotId
                    && PdmDocumentIdentityStore.MatchesResolvedVersion(path, file.VersionId, file.Sha256, hash);
        if (file.IsNativeIsolated)
            return string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase)
                || PdmDocumentIdentityStore.TryReadLocalMetadata(path, out var independent)
                    && independent.DocumentId == file.DocumentId && independent.NativeIsolationSnapshotId == file.VersionId
                    && PdmDocumentIdentityStore.MatchesResolvedVersion(path, file.VersionId, file.Sha256, hash);
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

    internal static void PrepareEditableDrawingOnly(ISldWorks app, string rootPath, ControlledOpenManifestDto manifest) =>
        PrepareCore(app, rootPath, manifest, true, rootPath);

    private static void PrepareCore(ISldWorks app, string rootPath, ControlledOpenManifestDto manifest, bool preserveRecognizedEdits,
        string onlyOwner = null)
    {
        ControlledOpenBoundary.RequireLoadedSources(app, rootPath, manifest);
        manifest.CompletedReferencePrepasses.Clear();
        var directory = Path.GetDirectoryName(Path.GetFullPath(rootPath));
        var files = manifest.Files.ToDictionary(file => file.FileName, StringComparer.OrdinalIgnoreCase);
        var preparationKey = ReferencePreparationKey(directory, manifest);
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
            if (ControlledWorkingCopyScope.Contains(owner, file))
                continue; // A verified saved edit must never be rewritten as a cache projection.
            if (HasPreparedReferences(owner, file, manifest.ProjectId, preparationKey)) continue;
            var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var untrackedResources = new List<string>();
            var unresolvedSourceRecord = false;
            try
            {
                var values = app.GetDocumentDependencies2(owner, false, false, false) as Array;
                if (values == null)
                {
                    PluginDiagnosticLog.Record("引用重定位", file.FileName + "：旧引用清单未完整读取，将按受控搜索目录原生打开。");
                    continue;
                }
                if (values.Length % 2 != 0) throw new IOException("原生引用清单格式不完整。");
                for (var index = 1; index < values.Length; index += 2)
                {
                    foreach (var native in (Convert.ToString(values.GetValue(index)) ?? "").Split('|'))
                    {
                        if (!files.TryGetValue(Path.GetFileName(native), out var target))
                        {
                            if (IsLinkedResource(native)) unresolvedSourceRecord = true;
                            if (IsLinkedResource(native) && (File.Exists(native)
                                || File.Exists(Path.Combine(directory, Path.GetFileName(native))))) untrackedResources.Add(native);
                            continue;
                        }
                        var destination = LocalPath(directory, target);
                        if (!string.Equals(native, destination, StringComparison.OrdinalIgnoreCase)) replacements[native] = destination;
                    }
                }
            }
            catch (Exception error)
            {
                throw new IOException("无法核对原生引用来源，未继续打开图档，请稍后重试并查看诊断日志：" + file.FileName, error);
            }
            if (untrackedResources.Count > 0)
                throw new IOException("图档仍指向未纳入PLM清单的外部来源，未使用原目录文件或继续打开：" + file.FileName
                    + "\r\n请先通过补齐引用将所需文件纳入PLM：\r\n" + string.Join("\r\n", untrackedResources));
            if (replacements.Count == 0)
            {
                if (!unresolvedSourceRecord) manifest.CompletedReferencePrepasses.Add(owner);
                continue;
            }
            var before = ArchiveFileFingerprint.Read(owner);
            if (!MatchesHash(owner, file, before))
                throw new IOException("准备引用定位期间图档内容发生变化，已保留当前文件，未继续打开：" + file.FileName);
            PdmDocumentIdentityStore.TryReadLocalMetadata(owner, out var baseline);
            var unchanged = baseline?.VersionId != null && baseline.DocumentId == file.DocumentId
                && (string.Equals(before, baseline.Sha256, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(before, baseline.ResolvedReferenceSha256, StringComparison.OrdinalIgnoreCase));
            var attributes = File.GetAttributes(owner);
            var backup = Path.Combine(Path.GetDirectoryName(owner), ".ref-" + Guid.NewGuid().ToString("N") + ".bak");
            CopyVerifiedBackup(owner, backup, before);
            var keepBackup = false;
            try
            {
                File.SetAttributes(owner, attributes & ~FileAttributes.ReadOnly);
                foreach (var pair in replacements)
                    if (!app.ReplaceReferencedDocument(owner, pair.Key, pair.Value))
                        throw new IOException("原生接口未完成引用替换：" + file.FileName + " → " + Path.GetFileName(pair.Value));
                if (replacements.Keys.Any(IsLinkedResource))
                {
                    var verified = app.GetDocumentDependencies2(owner, false, false, false) as Array;
                    if (verified == null || verified.Length % 2 != 0)
                        throw new IOException("外部来源的受控路径尚未核对完成：" + file.FileName);
                    var paths = Enumerable.Range(0, verified.Length / 2)
                        .SelectMany(index => (Convert.ToString(verified.GetValue(index * 2 + 1)) ?? string.Empty).Split('|')).ToArray();
                    if (paths.Any(path => IsLinkedResource(path) && files.TryGetValue(Path.GetFileName(path), out var target)
                            && !string.Equals(path, LocalPath(directory, target), StringComparison.OrdinalIgnoreCase))
                        || replacements.Where(pair => IsLinkedResource(pair.Key)).Any(pair => !paths.Contains(pair.Value, StringComparer.OrdinalIgnoreCase)))
                        throw new IOException("外部来源的受控路径尚未核对完成：" + file.FileName);
                }
                if (unchanged && !PdmDocumentIdentityStore.TryRecordResolvedReferences(owner, baseline, ArchiveFileFingerprint.Read(owner)))
                    throw new IOException("引用已定位，但无法记录本地副本校验信息：" + file.FileName);
                if (!unresolvedSourceRecord) manifest.CompletedReferencePrepasses.Add(owner);
            }
            catch (Exception error)
            {
                // F14-F16: restore every partial rewrite before the native-open fallback.
                try { File.Copy(backup, owner, true); }
                catch (Exception recoveryError) when (recoveryError is IOException || recoveryError is UnauthorizedAccessException)
                {
                    keepBackup = true;
                    throw new IOException("引用定位未完成，当前工作副本仍被占用或无法恢复，未继续打开：" + file.FileName
                        + "。完整恢复副本已保留：" + backup, new AggregateException(error, recoveryError));
                }
                if (replacements.Keys.Any(IsLinkedResource))
                    throw new IOException("外部来源引用未能安全定位到PLM工作区，已恢复原副本，未加载原目录来源：" + file.FileName, error);
                PluginDiagnosticLog.Record("引用重定位", file.FileName + "：已恢复原始副本，将尝试原生打开并核对实际来源：" + error);
            }
            finally
            {
                File.SetAttributes(owner, attributes);
                if (!keepBackup) File.Delete(backup);
            }
        }
        // F25/F26: no speculative OpenDoc/Save3, BreakLink or virtual externalization.
        // The actual user-requested open performs the loaded-source verification.
    }

    internal static bool IsLinkedResource(string path) => new[] { ".txt", ".xls", ".xlsx", ".xlsm", ".step", ".stp", ".iges", ".igs",
        ".x_t", ".x_b", ".sat", ".stl", ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff", ".slddrt" }
        .Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static string ReferencePreparationKey(string directory, ControlledOpenManifestDto manifest)
    {
        var map = "2|" + Path.GetFullPath(directory).TrimEnd('\\', '/').ToUpperInvariant() + "|" + manifest.ProjectId
            + "|" + string.Join("|", manifest.Files.OrderBy(file => file.FileName, StringComparer.OrdinalIgnoreCase)
                .Select(file => file.DocumentId + ":" + file.FileName.ToUpperInvariant() + ":" + (file.RelativePath ?? file.FileName).ToUpperInvariant()));
        using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(map))).Replace("-", "");
    }

    private static bool HasPreparedReferences(string path, ControlledOpenFileDto file, Guid projectId, string key) =>
        PdmDocumentIdentityStore.TryReadLocalMetadata(path, out var binding) && binding.DocumentId == file.DocumentId
        && binding.ProjectId == projectId && binding.VersionId == file.VersionId && binding.PreparedReferenceKey == key
        && string.Equals(binding.Sha256, file.Sha256, StringComparison.OrdinalIgnoreCase)
        && (file.NativeIsolation != null ? binding.NativeIsolationSnapshotId == file.NativeIsolation.ReferenceSnapshotId
            : !file.IsNativeIsolated || binding.NativeIsolationSnapshotId == file.VersionId)
        && !string.IsNullOrWhiteSpace(binding.PreparedReferenceSha256)
        && string.Equals(binding.PreparedReferenceSha256, ArchiveFileFingerprint.Read(path), StringComparison.OrdinalIgnoreCase);

    internal static void RememberVerifiedOpen(string rootPath, ControlledOpenManifestDto manifest)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(rootPath));
        var key = ReferencePreparationKey(directory, manifest);
        foreach (var file in manifest.Files)
        {
            if (manifest.RetainedWorkingHashes.ContainsKey(file.DocumentId)) continue;
            var path = LocalPath(directory, file);
            if (!manifest.CompletedReferencePrepasses.Contains(path)) continue;
            if (!TryReadVerifiedCopyHash(path, file, out var actual)
                || !PdmDocumentIdentityStore.TryReadLocalMetadata(path, out var binding)
                || binding.DocumentId != file.DocumentId || binding.ProjectId != manifest.ProjectId || binding.VersionId != file.VersionId) continue;
            if (!PdmDocumentIdentityStore.TryRecordReferencePreparation(path, binding, key, actual))
                PluginDiagnosticLog.Record("引用准备缓存", "本次打开已通过校验，复用记录稍后重试：" + file.FileName);
        }
    }

    private static void CopyVerifiedBackup(string owner, string backup, string expectedHash)
    {
        try
        {
            // Copy only the native document body. File.Copy also reads NTFS identity
            // streams, whose independent locks must not block a verified native read.
            using (var input = new FileStream(owner, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var output = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                input.CopyTo(output);
            if (!string.Equals(expectedHash, ArchiveFileFingerprint.Read(backup), StringComparison.OrdinalIgnoreCase)
                || !string.Equals(expectedHash, ArchiveFileFingerprint.Read(owner), StringComparison.OrdinalIgnoreCase))
                throw new IOException("准备引用定位副本期间图档内容发生变化，未改写文件：" + Path.GetFileName(owner));
        }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
        {
            if (File.Exists(backup)) File.Delete(backup);
            throw new IOException("打开前无法准备引用定位副本，请等待文件保存或占用结束后重试；当前文件未改写："
                + Path.GetFileName(owner), error);
        }
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
            || !(baseline.NativeIsolationSnapshotId.HasValue
                ? PdmDocumentIdentityStore.TryRecordNativeIsolation(target, copied, baseline.NativeIsolationSnapshotId.Value, hash)
                : PdmDocumentIdentityStore.TryRecordResolvedReferences(target, copied, hash)))
            throw new IOException("工作副本引用校验记录未能保存：" + Path.GetFileName(target));
    }
}
