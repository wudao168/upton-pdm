using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    // Runs only in the dedicated server reader. No add-in connection, user session or save is involved.
    public static string ReadBrowserArchive(ISldWorks app, string directory, string rootPath,
        Dictionary<string, string> originalHashes, Dictionary<string, string> inputPaths, bool previews, Action<string> progress,
        Action<string> archiveReady = null, Action<string> previewCheckpoint = null, bool retryMissingPreviews = false)
    {
        var prefix = Path.GetFullPath(directory).TrimEnd('\\') + "\\";
        string Relative(string path) => Path.GetFullPath(path).Substring(prefix.Length).Replace('\\', '/');
        Guid Key(string text) { using (var sha = SHA256.Create()) return new Guid(sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToLowerInvariant())).Take(16).ToArray()); }
        var physical = Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            .Where(path => new[] { ".sldasm", ".sldprt", ".slddrw", ".step", ".stp", ".txt", ".xls", ".xlsx" }
                .Contains(Path.GetExtension(path).ToLowerInvariant())).ToArray();
        var byName = physical.ToDictionary(Path.GetFileName, StringComparer.OrdinalIgnoreCase);
        var identities = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var project = Guid.NewGuid();
        PdmDocumentIdentityStore.ConfigureBrowserReadScope(directory);
        foreach (var path in physical)
        {
            identities[path] = Key(Relative(path));
        }
        // Rewrite only private copies to files explicitly provided or fetched from verified PLM versions.
        // This prevents an accessible historical workstation path from winning over the supplied design.
        var pending = new Queue<string>(); pending.Enqueue(rootPath);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (pending.Count > 0)
        {
            var path = pending.Dequeue();
            if (!visited.Add(path) || !path.EndsWith(".SLDASM", StringComparison.OrdinalIgnoreCase)
                && !path.EndsWith(".SLDDRW", StringComparison.OrdinalIgnoreCase)) continue;
            progress?.Invoke("核对原生引用：" + Path.GetFileName(path));
            var referenceTimer = Stopwatch.StartNew();
            var dependencies = app.GetDocumentDependencies2(path, false, false, false) as object[];
            PluginDiagnosticLog.Record("网页存档耗时", Path.GetFileName(path) + "：读取引用 " + referenceTimer.ElapsedMilliseconds + " 毫秒");
            if (dependencies == null) continue;
            for (var i = 1; i < dependencies.Length; i += 2)
            {
                var old = (dependencies[i] as string ?? "").Split('|')[0];
                if (!byName.TryGetValue(Path.GetFileName(old), out var local)) continue;
                pending.Enqueue(local);
                if (!PathsEqual(old, local))
                {
                    progress?.Invoke("调整临时引用：" + Path.GetFileName(path) + " → " + Path.GetFileName(local));
                    referenceTimer.Restart();
                    if (!app.ReplaceReferencedDocument(path, old, local))
                        PluginDiagnosticLog.Record("树外引用", "临时副本中的历史引用未调整；以实际设计树和加载路径核对：" + old);
                    PluginDiagnosticLog.Record("网页存档耗时", Path.GetFileName(path) + " → " + Path.GetFileName(local) + "：调整引用 " + referenceTimer.ElapsedMilliseconds + " 毫秒");
                }
            }
        }
        foreach (var path in physical)
        {
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
            if (!PdmDocumentIdentityStore.TryWriteControlledVersion(path, identities[path], project, identities[path], "W1", ComputeFileHash(path), new FileInfo(path).Length))
                throw new IOException("后台无法建立图档解析关联，请重试。");
        }
        app.SetSearchFolders((int)swSearchFolderTypes_e.swDocumentType,
            string.Join(";", physical.Select(Path.GetDirectoryName).Distinct(StringComparer.OrdinalIgnoreCase)));
        app.SetCurrentWorkingDirectory(Path.GetDirectoryName(rootPath));
        app.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swUseFolderSearchRules, true);
        var errors = 0; var warnings = 0;
        var kind = rootPath.EndsWith(".SLDASM", StringComparison.OrdinalIgnoreCase) ? 2 : rootPath.EndsWith(".SLDPRT", StringComparison.OrdinalIgnoreCase) ? 1 : 3;
        progress?.Invoke("打开根图档：" + Path.GetFileName(rootPath));
        var model = app.OpenDoc6(rootPath, kind, (int)(swOpenDocOptions_e.swOpenDocOptions_Silent | swOpenDocOptions_e.swOpenDocOptions_ReadOnly), "", ref errors, ref warnings);
        if (model == null || errors != 0) throw new InvalidOperationException("后台未能正常打开所选图档，请补齐设计树内文件后重试：" + Path.GetFileName(rootPath));
        var activation = 0;
        if (app.ActivateDoc3(model.GetTitle(), false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref activation) == null || activation != 0)
            throw new InvalidOperationException("后台图档上下文未就绪，请稍后重试。");
        if (model is IAssemblyDoc assembly) assembly.ResolveAllLightWeightComponents(false);
        var scanner = new SolidWorksReferenceTreeScanner(app);
        progress?.Invoke("读取设计树：" + Path.GetFileName(rootPath));
        var root = scanner.ScanDocument(model, true);
        var addin = new PdmAddin { application = app, scanner = scanner, currentTree = root };
        progress?.Invoke("核对压缩组件及关联工程图");
        SuppressedAssemblyLocalStructure.ExpandAsync(root, node => Task.FromResult(addin.ReadSuppressedAssemblyStructure(node)), CancellationToken.None).GetAwaiter().GetResult();
        progress?.Invoke("核对SolidWorks原生存档清单");
        string ProvidedNativePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return path;
            try { return byName.TryGetValue(Path.GetFileName(path), out var provided) ? provided : path; }
            catch (Exception error) when (error is ArgumentException || error is NotSupportedException || error is PathTooLongException)
            { return path; } // Malformed historical entries remain diagnostic data, not disk reads.
        }
        var nativeFiles = NativeArchiveManifest.ReadFiles(root, new ImportedComponentStorage(app).ReadNativeInventory(model, true))
            .Select(ProvidedNativePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        // Only explicitly supplied/private PLM copies may be read. Saved workstation
        // addresses still identify inventory omissions, but never authorize disk discovery.
        var supplied = new HashSet<string>(physical, StringComparer.OrdinalIgnoreCase);
        var availableNative = nativeFiles.Where(supplied.Contains).ToArray();
        if (root.Kind != CadDocumentKind.Drawing)
            scanner.AddRelatedDrawingPaths(root, availableNative.Where(path => path.EndsWith(".SLDDRW", StringComparison.OrdinalIgnoreCase)), false);
        NativeDesignDependencies.Collect(app, root, availableNative);
        NativeArchiveManifest.ConfirmFileScope(root, nativeFiles);
        var embedded = new HashSet<CadTreeNode>(NativeArchiveManifest.EmbeddedVirtualNodes(root, false));
        var nodes = EnumerateArchiveNodes(root).ToArray();
        foreach (var node in nodes)
        {
            node.NodeId = Key(node.InstancePath + "|" + node.FileName + "|" + node.Configuration);
            node.IsModifiedInSolidWorks = false; // This reader consumes saved files and never user memory edits.
            if (node.Status == CadReferenceStatus.Virtual || embedded.Contains(node))
            { node.DocumentId = null; node.Status = CadReferenceStatus.Virtual; continue; }
            if (node.IsOutsideNativeFileScope) continue;
            if (string.IsNullOrWhiteSpace(node.FullPath) || !Path.GetFullPath(node.FullPath).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || !identities.TryGetValue(node.FullPath, out var id) || !File.Exists(node.FullPath))
                throw new InvalidOperationException("设计树内的文件未包含在本次文件或已有PLM图档中，请补充后重试：" + node.FileName);
            node.DocumentId = id; node.CurrentRevision = "W1"; node.Revision = "W1";
        }
        PdmApiClient.ValidateArchiveReferenceCompleteness(root);
        var items = BuildBatchOperationItems(root);
        var configurations = nodes.Where(n => n.Status != CadReferenceStatus.Virtual && n.Kind != CadDocumentKind.Drawing)
            .GroupBy(n => n.FullPath, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key,
                g => (IReadOnlyList<string>)g.Select(n => n.Configuration).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), StringComparer.OrdinalIgnoreCase);
        var result = new List<BrowserArchiveFile>();
        foreach (var item in items)
        {
            var node = item.Node;
            progress?.Invoke("读取图档：" + node.FileName);
            var values = addin.ReadArchivePreflightProperties(node,
                new Dictionary<CadDocumentKind, IReadOnlyList<string>>(), configurations).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            foreach (var reference in EnumerateArchiveNodes(node).Where(n => n.Status != CadReferenceStatus.Virtual))
                if (identities.TryGetValue(reference.FullPath ?? "", out var id))
                { reference.DocumentId = id; reference.CurrentRevision = "W1"; reference.Revision = "W1"; }
            values["SourceFileSha256"] = originalHashes[Relative(node.FullPath)];
            var archivePath = node.FullPath;
            if (archiveReady != null)
            {
                // Preview APIs must never race the immutable bytes being uploaded.
                archivePath = Path.Combine(directory, "_archive", node.FileName);
                Directory.CreateDirectory(Path.GetDirectoryName(archivePath));
                if (File.Exists(archivePath)) File.SetAttributes(archivePath, FileAttributes.Normal);
                File.Copy(node.FullPath, archivePath, true);
                File.SetAttributes(archivePath, FileAttributes.ReadOnly);
            }
            result.Add(new BrowserArchiveFile { Key = node.DocumentId.Value, RelativePath = Relative(archivePath), FileName = node.FileName,
                Length = new FileInfo(archivePath).Length, Sha256 = ComputeFileHash(archivePath), Properties = values,
                InputRelativePath = inputPaths[Relative(node.FullPath)] });
        }
        foreach (var item in items)
        {
            progress?.Invoke("固定存档清单：" + item.Node.FileName);
            result.Single(file => file.Key == item.Node.DocumentId.Value).Root =
                PdmApiClient.CaptureArchiveSnapshot(item.Node, identities.Keys.ToArray()).CreateRoot();
            if (item.Node.StructureIndexPending)
                addin.archiveWarnings.Add(item.Node.FileName + "：原生文件已保留，结构或工程图关联待补全；补全前不能正式发布。");
        }
        var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        string Serialize(bool pendingPreviews) => serializer.Serialize(new { Success = true, RootKey = root.DocumentId.Value,
            Files = result, Warnings = addin.archiveWarnings.ToArray(), PreviewsPending = pendingPreviews });
        var nativeJson = Serialize(previews && archiveReady != null);
        archiveReady?.Invoke(nativeJson);
        return previews ? ReadBrowserArchivePreviews(app, directory, nativeJson, null, progress, previewCheckpoint, retryMissingPreviews) : nativeJson;
    }

    public static string ReadBrowserArchivePreviews(ISldWorks app, string directory, string nativeJson, string previousJson,
        Action<string> progress, Action<string> checkpoint, bool retryMissing)
    {
        var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        var batch = serializer.Deserialize<BrowserArchiveBatch>(nativeJson);
        if (!batch.Success || batch.Files == null || batch.Files.Count == 0) throw new InvalidOperationException("原生存档清单不完整，不能生成预览。");
        batch.PreviewGeneratorVersion = "active-document-config-v3";
        using (var hash = SHA256.Create()) batch.ArchiveSourceSha256 = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(nativeJson))).Replace("-", "");
        var prefix = Path.GetFullPath(directory).TrimEnd('\\') + "\\";
        string Under(string relative)
        {
            var path = Path.GetFullPath(Path.Combine(directory, relative));
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new IOException("预览文件路径越界。");
            return path;
        }
        BrowserArchiveBatch previous = null;
        if (!string.IsNullOrWhiteSpace(previousJson))
            try { previous = serializer.Deserialize<BrowserArchiveBatch>(previousJson); } catch (ArgumentException) { }
        if (previous?.PreviewGeneratorVersion != batch.PreviewGeneratorVersion) previous = null;
        if (previous?.ArchiveSourceSha256 == batch.ArchiveSourceSha256)
        {
            batch.TargetActivationChecks = previous.TargetActivationChecks;
            batch.VisibleActivationChecks = previous.VisibleActivationChecks;
            batch.InteractiveAddinLoadedAfterVisibleActivation = previous.InteractiveAddinLoadedAfterVisibleActivation;
            batch.VisibleActivationCheckError = previous.VisibleActivationCheckError;
        }
        var addin = new PdmAddin { application = app, scanner = new SolidWorksReferenceTreeScanner(app) };
        PdmDocumentIdentityStore.ConfigureBrowserReadScope(directory);
        foreach (var file in batch.Files)
        {
            file.PreviewPath = null; file.PreviewSha256 = null; file.PreviewLength = 0;
            if (Path.GetFileName(file.FileName) != file.FileName) throw new IOException("预览图档文件名无效。");
            var frozen = Under(file.RelativePath);
            if (!File.Exists(frozen) || new FileInfo(frozen).Length != file.Length || ComputeFileHash(frozen) != file.Sha256)
                throw new IOException("原生固定副本校验失败，未修改已存档图档：" + file.FileName);
            var working = Under(file.FileName);
            if (!File.Exists(working) || ComputeFileHash(working) != file.Sha256)
            {
                if (addin.FindLoadedDocument(working) != null) throw new IOException("预览工作副本在处理期间发生变化：" + file.FileName);
                if (File.Exists(working)) File.SetAttributes(working, FileAttributes.Normal);
                File.Copy(frozen, working, true); File.SetAttributes(working, FileAttributes.ReadOnly);
            }
            var cached = previous?.ArchiveSourceSha256 == batch.ArchiveSourceSha256
                ? previous.Files?.FirstOrDefault(candidate => candidate.Key == file.Key && candidate.Sha256 == file.Sha256) : null;
            file.PreviewError = cached?.PreviewError;
            if (cached?.PreviewPath != null && cached.PreviewLength > 0 && File.Exists(Under(cached.PreviewPath))
                && new FileInfo(Under(cached.PreviewPath)).Length == cached.PreviewLength && ComputeFileHash(Under(cached.PreviewPath)) == cached.PreviewSha256)
            { file.PreviewPath = cached.PreviewPath; file.PreviewLength = cached.PreviewLength; file.PreviewSha256 = cached.PreviewSha256; file.PreviewConfiguration = cached.PreviewConfiguration; }
        }
        batch.PreviewsPending = true;
        string Serialize() => serializer.Serialize(batch);
        checkpoint?.Invoke(Serialize());
        app.SetSearchFolders((int)swSearchFolderTypes_e.swDocumentType, directory);
        app.SetCurrentWorkingDirectory(directory);
        foreach (var file in batch.Files.Where(file => BrowserPreviewRequired(file.FileName))
            .OrderBy(file => file.PreviewError == null ? 0 : 1)
            .ThenBy(file => file.FileName.EndsWith(".SLDDRW", StringComparison.OrdinalIgnoreCase) ? 0 : 1))
        {
            if (file.PreviewPath != null) continue;
            progress?.Invoke("生成预览：" + file.FileName);
            var timer = Stopwatch.StartNew(); var path = Under(file.FileName);
            var document = addin.FindLoadedDocument(path); var owned = document == null;
            var drawing = file.FileName.EndsWith(".SLDDRW", StringComparison.OrdinalIgnoreCase);
            try
            {
                file.PreviewError = null;
                var root = file.Root as Dictionary<string, object>;
                var configuration = root != null && root.TryGetValue("configuration", out var value) ? value as string ?? "" : "";
                if (drawing) document = document ?? addin.OpenDrawingForArchive(path);
                else
                {
                    // STEP export requires the exact target to be active; an invisible
                    // document may have no active context or reuse another model's context.
                    var type = file.FileName.EndsWith(".SLDASM", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
                    var documentVisible = app.GetDocumentVisible(type);
                    try
                    {
                        app.DocumentVisible(true, type);
                        document = addin.OpenDocumentSilentlyForBatch(path, type, configuration, true);
                    }
                    finally { app.DocumentVisible(documentVisible, type); }
                    var activation = 0;
                    var activated = app.ActivateDoc3(file.FileName, false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref activation) as IModelDoc2;
                    var activeDocument = app.ActiveDoc as IModelDoc2;
                    if (activated == null || activation == (int)swActivateDocError_e.swGenericActivateError
                        || activeDocument == null || !PathsEqual(activated.GetPathName(), path) || !PathsEqual(activeDocument.GetPathName(), path))
                        throw new IOException("预览未能激活准确的图档副本。");
                    document = activeDocument;
                    batch.TargetActivationChecks++;
                    var interactive = app.GetAddInObject("{BCFD8A8A-472B-42E2-AC62-58BC17773650}");
                    batch.InteractiveAddinLoadedAfterVisibleActivation |= interactive != null;
                    if (interactive != null && Marshal.IsComObject(interactive)) Marshal.ReleaseComObject(interactive);
                    if (interactive != null) throw new IOException("后台预览会话出现交互插件，已停止当前图档预览。");
                    // OpenDoc6 keeps the current configuration when the model is
                    // already loaded. Select the saved variant planned by this file.
                    if (!string.IsNullOrWhiteSpace(configuration)
                        && !string.Equals(document.ConfigurationManager.ActiveConfiguration.Name, configuration, StringComparison.OrdinalIgnoreCase)
                        && (!document.ShowConfiguration2(configuration)
                            || !string.Equals(document.ConfigurationManager.ActiveConfiguration.Name, configuration, StringComparison.OrdinalIgnoreCase)))
                        throw new IOException("预览未能切换到计划的图档配置，请检查该配置后重试预览。");
                    document.ClearSelection2(true);
                }
                var relative = "_previews/" + file.Key.ToString("N") + (drawing ? ".pdf" : ".step");
                var output = Under(relative); Directory.CreateDirectory(Path.GetDirectoryName(output));
                var errors = 0; var warnings = 0;
                if (!drawing) file.PreviewConfiguration = document.ConfigurationManager.ActiveConfiguration.Name;
                var saved = document != null && document.Extension.SaveAs(output, (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                    (int)(swSaveAsOptions_e.swSaveAsOptions_Silent | swSaveAsOptions_e.swSaveAsOptions_Copy), null, ref errors, ref warnings);
                if (document != null && (errors & (int)swFileSaveError_e.swFileSaveAsNotSupported) != 0)
                {
                    // Some legacy drawings require a visible active document, as in
                    // the existing formal-preview converter. This is our own process.
                    var visible = app.Visible;
                    try
                    {
                        app.Visible = true; var activation = 0;
                        if (app.ActivateDoc3(document.GetTitle(), false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref activation) == null || activation != 0)
                            throw new IOException("预览恢复时未能激活图档。");
                        batch.VisibleActivationChecks++;
                        try
                        {
                            var interactive = app.GetAddInObject("{BCFD8A8A-472B-42E2-AC62-58BC17773650}");
                            batch.InteractiveAddinLoadedAfterVisibleActivation |= interactive != null;
                            if (interactive != null && Marshal.IsComObject(interactive)) Marshal.ReleaseComObject(interactive);
                        }
                        catch (Exception isolationError) { batch.VisibleActivationCheckError = isolationError.ToString(); }
                        if (File.Exists(output)) File.Delete(output);
                        errors = warnings = 0;
                        if (!drawing) file.PreviewConfiguration = document.ConfigurationManager.ActiveConfiguration.Name;
                        saved = document.Extension.SaveAs(output, (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                            (int)(swSaveAsOptions_e.swSaveAsOptions_Silent | swSaveAsOptions_e.swSaveAsOptions_Copy), null, ref errors, ref warnings);
                    }
                    finally { app.Visible = visible; }
                }
                if (!saved
                    || errors != 0 || !File.Exists(output) || new FileInfo(output).Length == 0) throw new IOException("预览未完整生成；错误码=" + errors + "，警告=" + warnings);
                file.PreviewPath = relative; file.PreviewLength = new FileInfo(output).Length; file.PreviewSha256 = ComputeFileHash(output);
            }
            catch (Exception error)
            {
                file.PreviewError = error.ToString();
                PluginDiagnosticLog.Record("网页预览", file.FileName + "：" + error);
                batch.Warnings.Add(file.FileName + "：预览暂未生成，原生图档内容保持完整。");
                // A failed COM server can poison otherwise valid files. Preserve the
                // checkpoint and let the existing worker retry in a fresh process.
                if (error is COMException com && (com.ErrorCode == unchecked((int)0x80010105)
                    || com.ErrorCode == unchecked((int)0x80010108))) break;
            }
            finally
            {
                PluginDiagnosticLog.Record("网页存档耗时", file.FileName + "：生成预览 " + timer.ElapsedMilliseconds + " 毫秒");
                if (owned && document != null) addin.CloseArchiveReadDocument(document);
                checkpoint?.Invoke(Serialize());
            }
        }
        batch.PreviewsPending = retryMissing && batch.Files.Any(file => BrowserPreviewRequired(file.FileName) && file.PreviewPath == null);
        return Serialize();
    }

    private static bool BrowserPreviewRequired(string fileName) => new[] { ".SLDASM", ".SLDPRT", ".SLDDRW" }
        .Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase);

    private sealed class BrowserArchiveBatch
    {
        public bool Success { get; set; }
        public Guid RootKey { get; set; }
        public List<BrowserArchiveFile> Files { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
        public bool PreviewsPending { get; set; }
        public string ArchiveSourceSha256 { get; set; }
        public string PreviewGeneratorVersion { get; set; }
        public int TargetActivationChecks { get; set; }
        public int VisibleActivationChecks { get; set; }
        public bool InteractiveAddinLoadedAfterVisibleActivation { get; set; }
        public string VisibleActivationCheckError { get; set; }
    }

    private sealed class BrowserArchiveFile
    {
        public Guid Key { get; set; }
        public string RelativePath { get; set; }
        public string FileName { get; set; }
        public long Length { get; set; }
        public string Sha256 { get; set; }
        public object Root { get; set; }
        public Dictionary<string, string> Properties { get; set; }
        public string PreviewPath { get; set; }
        public string PreviewSha256 { get; set; }
        public long PreviewLength { get; set; }
        public string PreviewError { get; set; }
        public string PreviewConfiguration { get; set; }
        public string InputRelativePath { get; set; }
    }
}
