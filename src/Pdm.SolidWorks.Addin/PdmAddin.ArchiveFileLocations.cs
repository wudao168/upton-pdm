using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    // Called only after the display scan has returned, during an explicit archive.
    // No FeatureManager/component handles are retained while native search runs.
    private IReadOnlyDictionary<string, string> ResolveArchiveFileLocations(CadTreeNode root)
    {
        if (IsSolidWorksCommandActive())
            throw new InvalidOperationException("请先完成SolidWorks当前命令，再核对存档引用位置。");
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var restore = new List<Action>();
        var inventories = new Dictionary<string, Tuple<string[], string[]>>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<CadTreeNode>();
        Tuple<string[], string[]> Inventory(CadTreeNode owner)
        {
            if (inventories.TryGetValue(owner.FullPath, out var cached)) return cached;
            var previous = application.GetCurrentWorkingDirectory();
            try
            {
                if (!application.SetCurrentWorkingDirectory(Path.GetDirectoryName(owner.FullPath)))
                    throw new InvalidOperationException("无法设置存档引用核对目录：" + owner.FileName);
                var saved = PdmDependencyEntries(application.GetDocumentDependencies2(owner.FullPath, false, false, false) as Array, owner.FullPath)
                    .SelectMany(entry => entry.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var resolved = PdmDependencyEntries(application.GetDocumentDependencies2(owner.FullPath, false, true, false) as Array, owner.FullPath)
                    .SelectMany(entry => entry.Value).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                return inventories[owner.FullPath] = Tuple.Create(saved, resolved);
            }
            finally
            {
                if (!application.SetCurrentWorkingDirectory(previous ?? string.Empty))
                    throw new InvalidOperationException("存档引用核对后未能恢复SolidWorks原工作目录，请重试；原图档未改写。");
            }
        }
        void Visit(CadTreeNode node, CadTreeNode owner)
        {
            if (node == null || !visited.Add(node)) return;
            if (owner != null && node.Role != CadReferenceRole.ExternalFileDependency
                && node.Status != CadReferenceStatus.Virtual && !node.IsStoredImport
                && !string.IsNullOrWhiteSpace(node.FullPath) && !File.Exists(node.FullPath)
                && !NativeArchiveManifest.IsInterconnectCachePath(node.FullPath)
                && !IsSolidWorksTemporaryVirtualComponentPath(node.FullPath) && File.Exists(owner.FullPath))
            {
                var old = node.FullPath;
                var inventory = Inventory(owner);
                var matches = inventory.Item2.Where(path => string.Equals(Path.GetFileName(path), node.FileName, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length > 1)
                    throw new InvalidOperationException("所属图档的原生引用解析出多个同名文件，未自动替换：" + owner.FileName + " → " + node.FileName);
                var actual = matches.SingleOrDefault();
                // For a moved local design, a recorded reference can be found beside its
                // actual owner even if native search returns the obsolete saved spelling.
                var peer = Path.Combine(Path.GetDirectoryName(owner.FullPath), Path.GetFileName(old));
                if (actual == null && (PathsEqual(node.SourceReferencePath, old)
                    || inventory.Item1.Any(path => PathsEqual(path, old) || PathsEqual(path, node.SourceReferencePath)))
                    && File.Exists(peer)) actual = peer;
                if (actual != null && node.DocumentId.HasValue
                    && PdmDocumentIdentityStore.TryReadLocalMetadata(actual, out var actualBinding)
                    && actualBinding.DocumentId != node.DocumentId)
                    throw new InvalidOperationException("定位到的文件与当前图档身份不一致，未自动替换：" + node.FileName);
                if (actual != null && PdmDocumentIdentityStore.IsControlledWorkspacePath(owner.FullPath))
                {
                    if (!PdmDocumentIdentityStore.TryReadLocalMetadata(owner.FullPath, out var parentBinding)
                        || !PdmDocumentIdentityStore.TryReadLocalMetadata(actual, out var binding)
                        || !parentBinding.ProjectId.HasValue || binding.ProjectId != parentBinding.ProjectId || !binding.VersionId.HasValue
                        || node.DocumentId.HasValue && node.DocumentId != binding.DocumentId) actual = null;
                }
                if (actual != null)
                {
                    if (aliases.TryGetValue(old, out var existing) && !PathsEqual(existing, actual))
                        throw new InvalidOperationException("同一旧地址对应不同实际文件，未自动更改存档范围：" + node.FileName);
                    aliases[old] = actual;
                    var originalReference = node.SourceReferencePath;
                    var originalStatus = node.Status;
                    var outside = node.IsOutsideNativeFileScope;
                    restore.Add(() => { node.FullPath = old; node.SourceReferencePath = originalReference;
                        node.Status = originalStatus; node.IsOutsideNativeFileScope = outside; });
                    if (string.IsNullOrWhiteSpace(node.SourceReferencePath)) node.SourceReferencePath = old;
                    node.FullPath = actual;
                    node.IsOutsideNativeFileScope = false;
                    if (node.Status == CadReferenceStatus.Missing)
                        node.Status = node.IsSuppressed ? CadReferenceStatus.Suppressed : CadReferenceStatus.Normal;
                    PluginDiagnosticLog.Record("存档引用定位", owner.FileName + "：" + old + " → " + actual + "；仅更新本次存档位置，原文件与名称保留。");
                }
            }
            var childOwner = node.Kind == CadDocumentKind.Assembly || node.Kind == CadDocumentKind.Drawing ? node : owner;
            foreach (var child in node.Children) Visit(child, childOwner);
            foreach (var model in node.ReferencedModels) Visit(model, childOwner);
        }
        try { Visit(root, null); }
        catch { foreach (var undo in restore.AsEnumerable().Reverse()) undo(); throw; }
        return aliases;
    }
}
