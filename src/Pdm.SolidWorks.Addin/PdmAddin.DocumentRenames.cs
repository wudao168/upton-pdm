using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Web.Script.Serialization;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    private static string documentRenameRecoveryDirectory = Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "UPTON PDM", "rename-recovery");

    private string[] ReadRenameReferences(string path, bool search)
    {
        var previous = application.GetCurrentWorkingDirectory();
        try
        {
            var values = application.GetDocumentDependencies2(path, false, search, false) as Array;
            if (values == null) return Array.Empty<string>();
            if (values.Length % 2 != 0) throw new InvalidOperationException("图档引用清单不完整，已停止改名：" + Path.GetFileName(path));
            return values.Cast<object>().Where((value, index) => index % 2 == 1).Select(Convert.ToString)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(Path.GetDirectoryName(path), value)))
                .ToArray();
        }
        finally { application.SetCurrentWorkingDirectory(previous ?? string.Empty); }
    }

    private void RenameRootDocumentAndReferences(CadTreeNode node, IModelDoc2 document, string newPath)
    {
        var oldPath = Path.GetFullPath(node.FullPath);
        var hasEmbeddedComponents = EnumerateCadNodes(currentTree).Any(candidate => candidate.Status == CadReferenceStatus.Virtual);
        PluginDiagnosticLog.Record("重命名", "正在核对主图档的工程图及外部上下文引用：" + oldPath);
        var configuration = document.ConfigurationManager?.ActiveConfiguration?.Name ?? node.Configuration;
        var candidates = EnumerateCadNodes(currentTree).Where(candidate => !string.IsNullOrWhiteSpace(candidate.FullPath)
            && candidate.Status != CadReferenceStatus.Virtual && !IsSolidWorksTemporaryVirtualComponentPath(candidate.FullPath))
            .GroupBy(candidate => candidate.FullPath, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToList();
        var nativePack = document.Extension.GetPackAndGo() as IPackAndGo;
        if (nativePack == null) throw new InvalidOperationException("无法读取完整图档清单，已停止主图档改名。");
        nativePack.IncludeSuppressed = true; nativePack.IncludeDrawings = true;
        if (!nativePack.GetDocumentNames(out var nativeNames) || !(nativeNames is Array nativePaths))
            throw new InvalidOperationException("SolidWorks未返回完整图档清单，已停止主图档改名。");
        if (nativePaths.Length == 0 || nativePaths.Cast<object>().Select(Convert.ToString).Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException("原生图档清单为空或包含空路径，已停止主图档改名。");
        var embeddedReferences = NativeArchiveManifest.VerifiedEmbeddedReferences(currentTree, nativePaths.Cast<object>().Select(Convert.ToString));
        foreach (var path in nativePaths.Cast<object>().Select(Convert.ToString).Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            if (embeddedReferences.Contains(path)) continue;
            if (IsSolidWorksTemporaryVirtualComponentPath(path)) throw new InvalidOperationException("嵌入式组件的原生路径尚未完整核对，已停止主图档改名。");
            if (candidates.Any(candidate => PathsEqual(candidate.FullPath, path)
                || candidate.IsRenamePendingSave && PathsEqual(candidate.SourceReferencePath, path))) continue;
            var kind = DocumentKindFromPath(path);
            if (kind != CadDocumentKind.Part && kind != CadDocumentKind.Assembly && kind != CadDocumentKind.Drawing) continue;
            var candidate = new CadTreeNode { FullPath = path, SourceReferencePath = path, FileName = Path.GetFileName(path), Kind = kind };
            RestorePersistedDocumentIdentities(candidate);
            candidates.Add(candidate);
        }
        // Include unopened, differently named drawings, and suppressed model dependencies.
        // Only saved/resolved full paths establish a link to the root; filenames never do.
        var dependents = new List<CadTreeNode>();
        foreach (var candidate in candidates.Where(candidate => !PathsEqual(candidate.FullPath, oldPath)))
        {
            var readablePath = File.Exists(candidate.FullPath) ? candidate.FullPath : candidate.SourceReferencePath;
            if (string.IsNullOrWhiteSpace(readablePath) || !File.Exists(readablePath)) continue;
            if (!ReadRenameReferences(readablePath, true).Any(path => PathsEqual(path, oldPath))) continue;
            if (candidate.IsExternalProvenance || candidate.IsReadOnlyPreview)
                throw new InvalidOperationException("引用当前总装的图档不是可编辑工作副本，请先从所属项目获取：" + candidate.FileName);
            if (candidate.DocumentId.HasValue && !IsCheckedOutByCurrentUser(candidate))
                throw new InvalidOperationException("请先获取引用当前总装的图档的编辑权限：" + candidate.FileName);
            // RenameDocument can change the in-memory path before SaveReferenced creates
            // that path on disk. Validate the known original, then recheck the saved target.
            var pendingPath = candidate.IsRenamePendingSave && !PathsEqual(readablePath, candidate.FullPath);
            EnsureLocalRenameFileWritable(pendingPath
                ? new CadTreeNode { FullPath = readablePath, DocumentId = candidate.DocumentId } : candidate);
            var loadedDependent = FindLoadedDocument(candidate.FullPath)
                ?? (pendingPath ? FindLoadedDocument(readablePath) : null);
            if (loadedDependent?.Visible == true)
                throw new InvalidOperationException("请先保存并关闭引用当前总装的图档，再执行改名：" + candidate.FileName);
            if (loadedDependent?.IsOpenedReadOnly() == true)
                throw new InvalidOperationException("引用当前总装的图档以只读方式打开，已停止改名：" + candidate.FileName);
            dependents.Add(candidate);
        }
        if (document.GetSaveFlag() || document.Extension.HasRenamedDocuments())
        {
            var errors = 0; var warnings = 0;
            if (!document.Save3((int)(swSaveAsOptions_e.swSaveAsOptions_Silent | swSaveAsOptions_e.swSaveAsOptions_SaveReferenced), ref errors, ref warnings)
                || errors != 0 || document.GetSaveFlag())
                throw new IOException("总装及待改名子件尚未保存，已停止主图档改名。请先保存后重试。错误码：" + errors + "，警告码：" + warnings);
        }
        foreach (var pending in candidates.Where(candidate => candidate.IsRenamePendingSave))
            if (!File.Exists(pending.FullPath) || new FileInfo(pending.FullPath).Length == 0)
                throw new IOException("待改名子件的新文件尚未完整保存，已停止主图档改名：" + pending.FileName);
        var references = dependents.ToDictionary(candidate => candidate.FullPath,
            candidate =>
            {
                var saved = ReadRenameReferences(candidate.FullPath, false);
                var resolved = ReadRenameReferences(candidate.FullPath, true);
                if (saved.Length != resolved.Length) throw new InvalidOperationException("图档引用在核对期间发生变化，已停止改名：" + candidate.FileName);
                var matching = saved.Where((path, index) => PathsEqual(path, oldPath) || PathsEqual(resolved[index], oldPath))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (matching.Length == 0) throw new InvalidOperationException("图档的原总装引用已变化，已停止改名：" + candidate.FileName);
                return matching;
            }, StringComparer.OrdinalIgnoreCase);
        PluginDiagnosticLog.Record("重命名", "已核对需要迁移的引用图档：" + references.Count + "份；新名称：" + newPath);
        var recovery = Path.Combine(documentRenameRecoveryDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(recovery);
        var backups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var originalAttributes = new Dictionary<string, FileAttributes>(StringComparer.OrdinalIgnoreCase);
        var writtenHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var moved = false; var completed = false; var closed = false;
        try
        {
            foreach (var path in new[] { oldPath }.Concat(references.Keys))
            {
                var backup = Path.Combine(recovery, backups.Count.ToString("D4") + Path.GetExtension(path));
                backups.Add(path, backup); originalAttributes.Add(path, File.GetAttributes(path));
                File.Copy(path, backup, false);
                if (ArchiveFileFingerprint.Read(path) != ArchiveFileFingerprint.Read(backup))
                    throw new IOException("图档在改名前发生变化，已停止操作：" + Path.GetFileName(path));
            }
            File.WriteAllText(Path.Combine(recovery, "rename.json"), new JavaScriptSerializer().Serialize(new { oldPath, newPath, backups, originalAttributes }));
            Interlocked.Increment(ref draftSnapshotInProgress);
            try
            {
                application.CloseDoc(document.GetTitle()); closed = true;
                foreach (var path in backups.Keys)
                    if (FindLoadedDocument(path) != null)
                        throw new InvalidOperationException("图档仍被其他已打开的装配引用，请先关闭相关窗口：" + Path.GetFileName(path));
                foreach (var pair in backups)
                    if (ArchiveFileFingerprint.Read(pair.Key) != ArchiveFileFingerprint.Read(pair.Value))
                        throw new IOException("图档在关闭期间发生变化，已停止改名：" + Path.GetFileName(pair.Key));
                File.Move(oldPath, newPath); moved = true;
                writtenHashes[newPath] = ArchiveFileFingerprint.Read(newPath);
                foreach (var pair in references)
                {
                    if (ArchiveFileFingerprint.Read(pair.Key) != ArchiveFileFingerprint.Read(backups[pair.Key]))
                        throw new IOException("引用图档在改名期间发生变化，已停止操作：" + Path.GetFileName(pair.Key));
                    File.SetAttributes(pair.Key, originalAttributes[pair.Key] & ~FileAttributes.ReadOnly);
                    try
                    {
                        // Saved paths can differ from the resolved working directory. Determine
                        // the exact saved reference from its paired resolved entry, not its name.
                        var matching = pair.Value;
                        foreach (var original in matching)
                            if (!application.ReplaceReferencedDocument(pair.Key, original, newPath))
                                throw new IOException("SolidWorks未能更新总装引用：" + Path.GetFileName(pair.Key));
                        var verified = ReadRenameReferences(pair.Key, false);
                        if (!verified.Any(path => PathsEqual(path, newPath)) || verified.Any(path => matching.Contains(path, StringComparer.OrdinalIgnoreCase)))
                            throw new IOException("总装新引用未完整写入，已停止改名：" + Path.GetFileName(pair.Key));
                    }
                    finally { writtenHashes[pair.Key] = ArchiveFileFingerprint.Read(pair.Key); File.SetAttributes(pair.Key, originalAttributes[pair.Key]); }
                }
                foreach (var pair in writtenHashes)
                    if (ArchiveFileFingerprint.Read(pair.Key) != pair.Value)
                        throw new IOException("图档在改名期间被其他程序更改，已停止操作：" + Path.GetFileName(pair.Key));
                var opened = OpenRenamedRoot(newPath, configuration);
                if (hasEmbeddedComponents)
                {
                    // Reopening changes virtual owner names in memory; persist them in
                    // the parent instead of trusting the old Pack and Go temp paths.
                    var errors = 0; var warnings = 0;
                    try
                    {
                        if (!opened.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref errors, ref warnings)
                            || errors != 0 || opened.GetSaveFlag())
                            throw new IOException("主图档改名后的嵌入式组件尚未保存完整，已停止操作。错误码：" + errors + "，警告码：" + warnings);
                    }
                    finally { writtenHashes[newPath] = ArchiveFileFingerprint.Read(newPath); }
                }
                var pack = opened.Extension.GetPackAndGo() as IPackAndGo;
                if (pack != null) { pack.IncludeSuppressed = true; pack.IncludeDrawings = true; }
                if (pack == null || !pack.GetDocumentNames(out var names) || !(names is Array native)
                    || native.Cast<object>().Select(Convert.ToString).Any(path => PathsEqual(path, oldPath)))
                    throw new IOException("改名后的原生引用核对未通过，已停止操作。");
                var verifiedPaths = native.Cast<object>().Select(Convert.ToString).ToArray();
                foreach (var original in nativePaths.Cast<object>().Select(Convert.ToString).Where(path => !embeddedReferences.Contains(path)))
                {
                    var expected = PathsEqual(original, oldPath) ? newPath : candidates.FirstOrDefault(candidate => PathsEqual(candidate.SourceReferencePath, original))?.FullPath ?? original;
                    if (!verifiedPaths.Any(path => PathsEqual(path, expected)))
                        throw new IOException("改名后遗漏了原有关联图档，已停止操作：" + Path.GetFileName(original));
                }
                foreach (var pending in candidates.Where(candidate => candidate.IsRenamePendingSave && File.Exists(candidate.FullPath)))
                { pending.IsRenamePendingSave = false; pending.IsModifiedInSolidWorks = false; pending.WorkState = CadWorkState.PendingCheckIn; }
                foreach (var dependent in dependents)
                { dependent.IsModifiedInSolidWorks = false; dependent.CurrentRevision = "本地修改"; dependent.WorkState = CadWorkState.PendingCheckIn; }
                foreach (var entry in localEdits.Values.Where(entry => PathsEqual(entry.SourcePath, oldPath)))
                { entry.SourcePath = newPath; editJournal?.Save(entry); }
                completed = true;
                PluginDiagnosticLog.Record("重命名", "主图档及引用已更新并重新打开核对；原名称已移除，未创建存档版本：" + newPath);
            }
            finally { Interlocked.Decrement(ref draftSnapshotInProgress); }
        }
        catch (Exception error)
        {
            if (moved)
            {
                Interlocked.Increment(ref draftSnapshotInProgress);
                try
                {
                    var opened = FindLoadedDocument(newPath);
                    if (opened != null) application.CloseDoc(opened.GetTitle());
                    foreach (var pair in writtenHashes)
                        if (!File.Exists(pair.Key) || ArchiveFileFingerprint.Read(pair.Key) != pair.Value)
                            throw new IOException("改名期间图档被其他程序更改，已保留恢复副本：" + recovery);
                    File.Move(newPath, oldPath);
                    File.SetAttributes(oldPath, originalAttributes[oldPath] & ~FileAttributes.ReadOnly);
                    File.Copy(backups[oldPath], oldPath, true);
                    File.SetAttributes(oldPath, originalAttributes[oldPath]);
                    foreach (var pair in backups.Where(pair => !PathsEqual(pair.Key, oldPath) && writtenHashes.ContainsKey(pair.Key)))
                    { File.SetAttributes(pair.Key, originalAttributes[pair.Key] & ~FileAttributes.ReadOnly); File.Copy(pair.Value, pair.Key, true); File.SetAttributes(pair.Key, originalAttributes[pair.Key]); }
                    OpenRenamedRoot(oldPath, configuration);
                    completed = true;
                }
                catch (Exception rollback) { throw new IOException("主图档改名未完成，恢复资料已保留，请勿删除：" + recovery, new AggregateException(error, rollback)); }
                finally { Interlocked.Decrement(ref draftSnapshotInProgress); }
            }
            else if (closed && FindLoadedDocument(oldPath) == null) OpenRenamedRoot(oldPath, configuration);
            LogDiagnostic("主图档改名", error);
            throw new InvalidOperationException(moved
                ? "主图档改名未完成，已恢复原名称和引用。请关闭相关图档窗口后重试；技术详情已写入诊断日志。"
                : "主图档改名未完成，原文件已保留。请关闭相关图档窗口后重试；技术详情已写入诊断日志。", error);
        }
        finally
        {
            if (completed || !moved)
            {
                try
                {
                    foreach (var backup in backups.Values.Where(File.Exists)) { File.SetAttributes(backup, FileAttributes.Normal); File.Delete(backup); }
                    var journal = Path.Combine(recovery, "rename.json"); if (File.Exists(journal)) File.Delete(journal);
                    Directory.Delete(recovery, false);
                }
                catch (IOException cleanup) { LogDiagnostic("清理主图档改名恢复资料", cleanup); }
                catch (UnauthorizedAccessException cleanup) { LogDiagnostic("清理主图档改名恢复资料", cleanup); }
            }
        }
        LogOperation("Root document references renamed old=" + oldPath + " new=" + newPath + " dependents=" + references.Count);
    }

    private IModelDoc2 OpenRenamedRoot(string path, string configuration)
    {
        var errors = 0; var warnings = 0;
        var opened = application.OpenDoc6(path, (int)(DocumentKindFromPath(path) == CadDocumentKind.Assembly
            ? swDocumentTypes_e.swDocASSEMBLY : swDocumentTypes_e.swDocPART),
            (int)swOpenDocOptions_e.swOpenDocOptions_Silent, configuration ?? string.Empty, ref errors, ref warnings) as IModelDoc2;
        if (opened == null || errors != 0 || !PathsEqual(opened.GetPathName(), path))
            throw new IOException("改名后的主图档未能重新打开。错误码：" + errors + "，警告码：" + warnings);
        application.ActivateDoc3(path, false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref errors);
        return opened;
    }
}
