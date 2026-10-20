using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        Dictionary<string, string> originalHashes, Dictionary<string, string> inputPaths, bool previews, Action<string> progress)
    {
        var prefix = Path.GetFullPath(directory).TrimEnd('\\') + "\\";
        string Relative(string path) => Path.GetFullPath(path).Substring(prefix.Length).Replace('\\', '/');
        Guid Key(string text) { using (var sha = SHA256.Create()) return new Guid(sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToLowerInvariant())).Take(16).ToArray()); }
        var physical = Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            .Where(path => new[] { ".sldasm", ".sldprt", ".slddrw" }.Contains(Path.GetExtension(path).ToLowerInvariant())).ToArray();
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
            if (!visited.Add(path) || path.EndsWith(".SLDPRT", StringComparison.OrdinalIgnoreCase)) continue;
            var dependencies = app.GetDocumentDependencies2(path, false, false, false) as object[];
            if (dependencies == null) continue;
            for (var i = 1; i < dependencies.Length; i += 2)
            {
                var old = (dependencies[i] as string ?? "").Split('|')[0];
                if (!byName.TryGetValue(Path.GetFileName(old), out var local)) continue;
                pending.Enqueue(local);
                if (!PathsEqual(old, local) && !app.ReplaceReferencedDocument(path, old, local))
                    PluginDiagnosticLog.Record("树外引用", "临时副本中的历史引用未调整；以实际设计树和加载路径核对：" + old);
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
        var model = app.OpenDoc6(rootPath, kind, (int)(swOpenDocOptions_e.swOpenDocOptions_Silent | swOpenDocOptions_e.swOpenDocOptions_ReadOnly), "", ref errors, ref warnings);
        if (model == null || errors != 0) throw new InvalidOperationException("后台未能正常打开所选图档，请补齐设计树内文件后重试：" + Path.GetFileName(rootPath));
        var activation = 0;
        if (app.ActivateDoc3(model.GetTitle(), false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref activation) == null || activation != 0)
            throw new InvalidOperationException("后台图档上下文未就绪，请稍后重试。");
        if (model is IAssemblyDoc assembly) assembly.ResolveAllLightWeightComponents(false);
        var scanner = new SolidWorksReferenceTreeScanner(app);
        var root = scanner.ScanDocument(model, true);
        var addin = new PdmAddin { application = app, scanner = scanner, currentTree = root };
        SuppressedAssemblyLocalStructure.ExpandAsync(root, node => Task.FromResult(addin.ReadSuppressedAssemblyStructure(node)), CancellationToken.None).GetAwaiter().GetResult();
        if (root.Kind != CadDocumentKind.Drawing)
            scanner.AddRelatedDrawingPaths(root, physical.Where(path => path.EndsWith(".SLDDRW", StringComparison.OrdinalIgnoreCase)), false);
        var embedded = new HashSet<CadTreeNode>(NativeArchiveManifest.EmbeddedVirtualNodes(root, false));
        var nodes = EnumerateArchiveNodes(root).ToArray();
        foreach (var node in nodes)
        {
            node.NodeId = Key(node.InstancePath + "|" + node.FileName + "|" + node.Configuration);
            node.IsModifiedInSolidWorks = false; // This reader consumes saved files and never user memory edits.
            if (node.Status == CadReferenceStatus.Virtual || embedded.Contains(node))
            { node.DocumentId = null; node.Status = CadReferenceStatus.Virtual; continue; }
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
        var result = new List<object>();
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
            string preview = null;
            if (previews)
            {
                var document = addin.FindLoadedDocument(node.FullPath); var owned = document == null;
                try
                {
                    document = document ?? (node.Kind == CadDocumentKind.Drawing ? addin.OpenDrawingForArchive(node.FullPath)
                        : addin.OpenDocumentInvisiblyForBatch(node.FullPath, ToSolidWorksDocumentType(node.Kind), node.Configuration, true));
                    var output = Path.Combine(directory, "_previews", node.DocumentId.Value.ToString("N") + (node.Kind == CadDocumentKind.Drawing ? ".pdf" : ".step"));
                    Directory.CreateDirectory(Path.GetDirectoryName(output)); errors = warnings = 0;
                    if (document.Extension.SaveAs(output, (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                        (int)(swSaveAsOptions_e.swSaveAsOptions_Silent | swSaveAsOptions_e.swSaveAsOptions_Copy), null, ref errors, ref warnings)
                        && errors == 0 && File.Exists(output)) preview = Relative(output);
                    else addin.archiveWarnings.Add(node.FileName + "：原生图档已读取，预览可稍后补齐。");
                }
                catch (Exception) { addin.archiveWarnings.Add(node.FileName + "：预览暂未生成，原生图档内容保持完整。"); }
                finally { if (owned && document != null) addin.CloseArchiveReadDocument(document); }
            }
            result.Add(new { Key = node.DocumentId.Value, RelativePath = Relative(node.FullPath), node.FileName,
                Length = new FileInfo(node.FullPath).Length, Sha256 = ComputeFileHash(node.FullPath),
                Root = PdmApiClient.CaptureArchiveSnapshot(node, identities.Keys.ToArray()).CreateRoot(), Properties = values, PreviewPath = preview,
                InputRelativePath = inputPaths[Relative(node.FullPath)] });
        }
        return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(new { Success = true, RootKey = root.DocumentId.Value,
            Files = result, Warnings = addin.archiveWarnings.ToArray() });
    }
}
