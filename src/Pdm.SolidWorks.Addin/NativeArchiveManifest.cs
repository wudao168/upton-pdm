using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Upton.Pdm.SolidWorks;

internal static class NativeArchiveManifest
{
    public static IReadOnlyList<string> Validate(CadTreeNode root, IReadOnlyList<string> nativeFiles)
    {
        var embeddedNodes = new HashSet<CadTreeNode>(EmbeddedVirtualNodes(root, false));
        var embeddedPaths = EmbeddedPaths(embeddedNodes);
        var embeddedKeys = embeddedPaths.GroupBy(VirtualComponentKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), StringComparer.OrdinalIgnoreCase);
        var nodes = Walk(root).Where(node => !string.IsNullOrWhiteSpace(node.FullPath)
            && node.Status != CadReferenceStatus.Virtual && !embeddedNodes.Contains(node)
            && !PdmAddin.IsSolidWorksTemporaryVirtualComponentPath(node.FullPath)).ToArray();
        var paths = new HashSet<string>(nodes.Select(node => Path.GetFullPath(node.FullPath)), StringComparer.OrdinalIgnoreCase);
        var names = nodes.GroupBy(node => Path.GetFileName(node.FullPath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(node => Path.GetFullPath(node.FullPath))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), StringComparer.OrdinalIgnoreCase);
        var aliases = nodes.Where(node => !string.IsNullOrWhiteSpace(node.SourceReferencePath))
            .GroupBy(node => Path.GetFullPath(node.SourceReferencePath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(node => Path.GetFullPath(node.FullPath))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), StringComparer.OrdinalIgnoreCase);
        var failures = new List<string>();
        var external = new List<string>();
        var additionalParts = new List<string>();
        var embeddedCount = 0;
        var rootDirectory = Path.GetDirectoryName(Path.GetFullPath(root.FullPath)) + Path.DirectorySeparatorChar;
        foreach (var original in nativeFiles.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var path = Path.GetFullPath(original);
            if (embeddedPaths.Contains(path)) { embeddedCount++; continue; }
            var virtualKey = VirtualComponentKey(path);
            if (virtualKey != null && embeddedKeys.TryGetValue(virtualKey, out var matches))
            {
                if (matches.Length != 1)
                    failures.Add("嵌入式虚拟组件对应到多个当前实例，不能自动关联：" + original);
                else embeddedCount++;
                continue;
            }
            if (!paths.Contains(path) && aliases.TryGetValue(path, out var resolved) && resolved.Length == 1)
            {
                // A saved reference can still exist at its old path. It must not silently
                // override a different working copy simply because the component names match.
                if (File.Exists(path) && !string.Equals(path, resolved[0], StringComparison.OrdinalIgnoreCase)
                    && ArchiveFileFingerprint.Read(path) != ArchiveFileFingerprint.Read(resolved[0]))
                    failures.Add("原始引用与当前工作副本内容不同：" + original + "；当前副本：" + resolved[0]);
                path = resolved[0]; // Only an actual scanned instance can establish this mapping.
            }
            if (paths.Contains(path) && File.Exists(path)) continue;
            var extension = Path.GetExtension(original);
            var cad = new[] { ".SLDPRT", ".SLDASM", ".SLDDRW" }.Contains(extension, StringComparer.OrdinalIgnoreCase);
            if (!cad) { external.Add(original); continue; }
            if (extension.Equals(".SLDPRT", StringComparison.OrdinalIgnoreCase) && File.Exists(path)
                && path.StartsWith(rootDirectory, StringComparison.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(path);
                if (names.TryGetValue(name, out var collisions) && collisions.Any(existing =>
                    !string.Equals(existing, path, StringComparison.OrdinalIgnoreCase)))
                {
                    failures.Add("原生依赖与已核对图档同名但路径不同，不能自动关联：" + path);
                    continue;
                }
                names[name] = new[] { path };
                additionalParts.Add(path);
                continue;
            }
            failures.Add((!File.Exists(path) ? "原生图档引用文件缺失或路径未确认："
                : "原生图档依赖尚未对应到已核对的实例/配置：") + original);
        }
        if (failures.Count > 0)
        {
            PluginDiagnosticLog.Record("存档范围", "原生文件清单与存档结构不一致：" + string.Join("；", failures));
            throw new InvalidOperationException("原生文件清单与存档结构不一致，本次尚未上传文件。请核对以下引用及其配置；系统不会按同名文件替代或忽略：\r\n"
                + string.Join("\r\n", failures.Take(12)) + (failures.Count > 12 ? "\r\n另有" + (failures.Count - 12) + "项，请查看诊断日志。" : string.Empty));
        }
        foreach (var path in additionalParts)
        {
            var name = Path.GetFileName(path);
            root.Children.Add(new CadTreeNode
            {
                FullPath = path,
                SourceReferencePath = path,
                FileName = name,
                DisplayName = "原生文件依赖 · " + Path.GetFileNameWithoutExtension(path),
                DrawingNumber = Path.GetFileNameWithoutExtension(path),
                Kind = CadDocumentKind.Part,
                Role = CadReferenceRole.ExternalFileDependency,
                InstancePath = root.InstancePath + "/native-file/" + name,
                Status = CadReferenceStatus.Normal
            });
        }
        PluginDiagnosticLog.Record("存档范围", "原生清单中图档核对通过；随父装配保存的嵌入式虚拟组件" + embeddedCount
            + "份；独立归档但不计入装配BOM的零件依赖" + additionalParts.Count
            + "份；非SolidWorks外部资料" + external.Count
            + "项。"
            + (additionalParts.Count == 0 ? string.Empty : " 依赖零件：" + string.Join("；", additionalParts))
            + (external.Count == 0 ? string.Empty : " 外部资料：" + string.Join("；", external)));
        return external;
    }

    private static IEnumerable<CadTreeNode> Walk(CadTreeNode node)
    {
        yield return node;
        foreach (var child in node.Children)
            foreach (var descendant in Walk(child)) yield return descendant;
    }

    internal static HashSet<string> VerifiedEmbeddedReferences(CadTreeNode root, IEnumerable<string> nativeFiles)
    {
        var paths = EmbeddedPaths(EmbeddedVirtualNodes(root, false));
        var keys = paths.GroupBy(VirtualComponentKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Distinct(StringComparer.OrdinalIgnoreCase).Count(), StringComparer.OrdinalIgnoreCase);
        return new HashSet<string>(nativeFiles.Where(path => paths.Contains(Path.GetFullPath(path))
            || VirtualComponentKey(path) is string key && keys.TryGetValue(key, out var count) && count == 1), StringComparer.OrdinalIgnoreCase);
    }

    private static HashSet<string> EmbeddedPaths(IEnumerable<CadTreeNode> nodes) => new HashSet<string>(
        nodes.SelectMany(node => new[] { node.FullPath, node.SourceReferencePath })
            .Where(PdmAddin.IsSolidWorksTemporaryVirtualComponentPath).Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<CadTreeNode> EmbeddedVirtualNodes(CadTreeNode node, bool parentVirtual)
    {
        var virtualComponent = parentVirtual || node.Status == CadReferenceStatus.Virtual;
        if (virtualComponent && (PdmAddin.IsSolidWorksTemporaryVirtualComponentPath(node.FullPath)
            || PdmAddin.IsSolidWorksTemporaryVirtualComponentPath(node.SourceReferencePath))) yield return node;
        foreach (var child in node.Children)
            foreach (var embedded in EmbeddedVirtualNodes(child, virtualComponent)) yield return embedded;
    }

    private static string VirtualComponentKey(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) return null;
        var parts = Path.GetFullPath(path).Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
        var marker = Array.FindIndex(parts, part => string.Equals(part, "VC~~", StringComparison.OrdinalIgnoreCase));
        // Saved references can originate from another Windows user. Only the
        // canonical Temp/swx*/VC~~ layout may alias a verified embedded hierarchy;
        // this does not classify or exclude any real component from file operations.
        if (!PdmAddin.IsSolidWorksTemporaryVirtualComponentPath(path)
            && !(marker >= 6 && parts[marker - 6].Equals("Users", StringComparison.OrdinalIgnoreCase)
                && parts[marker - 4].Equals("AppData", StringComparison.OrdinalIgnoreCase)
                && parts[marker - 3].Equals("Local", StringComparison.OrdinalIgnoreCase)
                && parts[marker - 2].Equals("Temp", StringComparison.OrdinalIgnoreCase)
                && parts[marker - 1].StartsWith("swx", StringComparison.OrdinalIgnoreCase))) return null;
        return marker >= 0 && marker < parts.Length - 1
            ? string.Join("/", parts.Skip(marker + 1)) : null;
    }
}
