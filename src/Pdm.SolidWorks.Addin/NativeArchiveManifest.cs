using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Upton.Pdm.SolidWorks;

internal static class NativeArchiveManifest
{
    internal static string[] ReadFiles(CadTreeNode root, IEnumerable<string> entries)
    {
        var files = new List<string>();
        var index = 0;
        foreach (var entry in entries)
        {
            var step = "读取SolidWorks原生文件清单第" + (++index) + "项";
            var parts = (entry ?? string.Empty).Split('|');
            // SOLIDWORKS can append |imported_file_path to a native dependency.
            // Decode only at this API boundary; ordinary Windows paths still reject '|'.
            if (parts.Length == 2 && new[] { ".SLDPRT", ".SLDASM" }
                .Any(extension => parts[0].EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                && !string.IsNullOrWhiteSpace(parts[1]))
            {
                foreach (var part in parts)
                    files.Add(ArchiveReferencePath.Resolve(part, Path.GetDirectoryName(root.FullPath), step,
                        root.FullPath, root.InstancePath, root.Configuration));
                var native = files[files.Count - 2];
                var imported = files[files.Count - 1];
                if (!Path.IsPathRooted(parts[0]) || !Path.IsPathRooted(parts[1]))
                    throw new InvalidOperationException(step + "的导入复合引用不是完整路径，已停止存档。原始引用：" + ArchiveReferencePath.Visible(entry));
                if (IsInterconnectCachePath(native))
                {
                    var matches = Walk(root).Where(node => string.Equals(node.FullPath, native, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(node.SourceReferencePath, native, StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (matches.Length == 0 || matches.Any(node => !string.Equals(node.ImportedSourcePath, imported, StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidOperationException(step + "的导入源与当前组件未能对应，已停止存档。原始引用："
                            + ArchiveReferencePath.Visible(entry) + "；请核对导入源和配置后重试。");
                }
                PluginDiagnosticLog.Record("导入引用", step + "：原始值=" + ArchiveReferencePath.Visible(entry)
                    + "；原生路径=" + parts[0] + "；导入源路径=" + parts[1] + "；两项均纳入核对。");
            }
            else files.Add(ArchiveReferencePath.Resolve(entry, Path.GetDirectoryName(root.FullPath), step,
                root.FullPath, root.InstancePath, root.Configuration));
        }
        return files.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static bool IsInterconnectCachePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(temp, StringComparison.OrdinalIgnoreCase)) return false;
        var parts = full.Substring(temp.Length).Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3 && parts[0].StartsWith("swx", StringComparison.OrdinalIgnoreCase)
            && parts[1].Equals("IC~~", StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<string> Validate(CadTreeNode root, IReadOnlyList<string> nativeFiles)
    {
        var rootPath = ArchiveReferencePath.Resolve(root.FullPath, null, "核对原生存档根路径", root.FullPath, root.InstancePath, root.Configuration);
        var rootDirectory = Path.GetDirectoryName(rootPath) + Path.DirectorySeparatorChar;
        var embeddedNodes = new HashSet<CadTreeNode>(EmbeddedVirtualNodes(root, false));
        var embeddedPaths = EmbeddedPaths(embeddedNodes);
        var importedSources = Walk(root).Where(node => node.Status == CadReferenceStatus.Virtual
            && IsInterconnectCachePath(node.FullPath) && !string.IsNullOrWhiteSpace(node.ImportedSourcePath))
            .Select(node => ArchiveReferencePath.Resolve(node.ImportedSourcePath, rootDirectory,
                "核对3D Interconnect导入源", root.FullPath, node.InstancePath, node.Configuration))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var embeddedKeys = embeddedPaths.Where(path => VirtualComponentKey(path) != null).GroupBy(VirtualComponentKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), StringComparer.OrdinalIgnoreCase);
        var nodes = Walk(root).Where(node => !string.IsNullOrWhiteSpace(node.FullPath)
            && node.Status != CadReferenceStatus.Virtual && !embeddedNodes.Contains(node)
            && !PdmAddin.IsSolidWorksTemporaryVirtualComponentPath(node.FullPath)).ToArray();
        var paths = new HashSet<string>(nodes.Select(node => ArchiveReferencePath.Resolve(node.FullPath, rootDirectory,
            "核对当前实例文件路径", root.FullPath, node.InstancePath, node.Configuration)), StringComparer.OrdinalIgnoreCase);
        var names = nodes.GroupBy(node => Path.GetFileName(node.FullPath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(node => Path.GetFullPath(node.FullPath))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), StringComparer.OrdinalIgnoreCase);
        var aliases = nodes.Where(node => !string.IsNullOrWhiteSpace(node.SourceReferencePath))
            .GroupBy(node => ArchiveReferencePath.Resolve(node.SourceReferencePath, rootDirectory,
                "核对实例原始引用映射", root.FullPath, node.InstancePath, node.Configuration), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(node => Path.GetFullPath(node.FullPath))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), StringComparer.OrdinalIgnoreCase);
        var failures = new List<string>();
        var external = new List<string>();
        var additionalParts = new List<string>();
        var additionalSources = new List<string>();
        var sourceReadFailures = 0;
        foreach (var source in importedSources)
        {
            var failure = ProbeImportedSource(source);
            if (failure != null)
            {
                sourceReadFailures++;
                failures.Add("STEP导入源在记录路径处无法读取（" + failure + "）：" + source);
                var instances = embeddedNodes.Where(node => string.Equals(node.ImportedSourcePath, source, StringComparison.OrdinalIgnoreCase))
                    .GroupBy(node => node.FullPath, StringComparer.OrdinalIgnoreCase).ToArray();
                PluginDiagnosticLog.Record("导入源诊断", "原路径=" + source + "；原因=" + failure
                    + "；报错电脑=" + Environment.MachineName + "；装配=" + rootPath
                    + "；对应原生缓存文件=" + instances.Length + "份。缓存存在不代表原STEP文件已保存。");
                foreach (var instance in instances)
                    PluginDiagnosticLog.Record("导入缓存诊断", "路径=" + instance.Key + "；读取结果="
                        + (ProbeImportedSource(instance.Key) ?? "可读取") + "；实例=" + instance.First().InstancePath
                        + "；配置=" + instance.First().Configuration + "；源路径=" + source);
            }
            else if (!paths.Contains(source)) additionalSources.Add(source);
        }
        var embeddedCount = 0;
        foreach (var original in nativeFiles.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var path = ArchiveReferencePath.Resolve(original, rootDirectory, "核对原生文件依赖", root.FullPath, root.InstancePath, root.Configuration);
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
            if (!cad)
            {
                if (!importedSources.Contains(path, StringComparer.OrdinalIgnoreCase)) external.Add(original);
                continue;
            }
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
            throw new InvalidOperationException((sourceReadFailures > 0
                ? "STEP导入源读取检查未通过，本次尚未上传文件，也未产生存档版本：\r\n"
                : "原生文件清单与存档结构不一致，本次尚未上传文件。请核对以下引用及其配置；系统不会按同名文件替代或忽略：\r\n")
                + string.Join("\r\n", failures.Take(12)) + (failures.Count > 12 ? "\r\n另有" + (failures.Count - 12) + "项，请查看诊断日志。" : string.Empty)
                + (sourceReadFailures > 0 ? "\r\n请在当前业务电脑确认源文件位置及访问权限；若仅剩现有装配，请先保留图档及缓存，再核对保存方案。系统不会忽略源文件、修改引用或自动断开链接。" : string.Empty));
        }
        foreach (var path in additionalParts.Concat(additionalSources))
        {
            var name = Path.GetFileName(path);
            root.Children.Add(new CadTreeNode
            {
                FullPath = path,
                SourceReferencePath = path,
                FileName = name,
                DisplayName = (additionalSources.Contains(path) ? "导入源资料 · " : "原生文件依赖 · ") + Path.GetFileNameWithoutExtension(path),
                DrawingNumber = Path.GetFileNameWithoutExtension(path),
                Kind = additionalSources.Contains(path) ? CadDocumentKind.Other : CadDocumentKind.Part,
                Role = CadReferenceRole.ExternalFileDependency,
                InstancePath = root.InstancePath + "/native-file/" + name,
                Status = CadReferenceStatus.Normal
            });
        }
        PluginDiagnosticLog.Record("存档范围", "原生清单中图档核对通过；随父装配保存的嵌入式虚拟组件" + embeddedCount
            + "份；独立归档但不计入装配BOM的零件依赖" + additionalParts.Count
            + "份；非SolidWorks外部资料" + external.Count
            + "项；同批存档的导入源资料" + additionalSources.Count + "份。"
            + (additionalParts.Count == 0 ? string.Empty : " 依赖零件：" + string.Join("；", additionalParts))
            + (external.Count == 0 ? string.Empty : " 外部资料：" + string.Join("；", external)));
        return external;
    }

    internal static string ProbeImportedSource(string path)
    {
        try
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                return stream.ReadByte() < 0 ? "文件为空" : null;
        }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is System.Security.SecurityException)
        {
            PluginDiagnosticLog.Record("导入源技术诊断", "路径=" + ArchiveReferencePath.Visible(path) + "；异常=" + error.GetType().Name
                + "；HResult=0x" + error.HResult.ToString("X8") + "；技术原因=" + error.Message);
            return SourceReadFailureReason(error);
        }
    }

    internal static string SourceReadFailureReason(Exception error)
    {
        if (error is UnauthorizedAccessException || error is System.Security.SecurityException) return "当前账号无读取权限";
        if (error is FileNotFoundException) return "记录路径中找不到文件，不能据此判断文件已删除";
        if (error is DirectoryNotFoundException) return "记录目录不存在或当前不可访问";
        var code = error.HResult & 0xffff;
        if (code == 32 || code == 33) return "文件被其他程序独占，暂时无法读取";
        if (code == 53 || code == 64 || code == 67 || code == 121 || code == 1231 || code == 1232) return "网络位置当前不可访问";
        return "文件读取异常，请检查存储位置和占用情况；技术原因已写入诊断日志";
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
        var keys = paths.Where(path => VirtualComponentKey(path) != null).GroupBy(VirtualComponentKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Distinct(StringComparer.OrdinalIgnoreCase).Count(), StringComparer.OrdinalIgnoreCase);
        return new HashSet<string>(nativeFiles.Where(path => paths.Contains(Path.GetFullPath(path))
            || VirtualComponentKey(path) is string key && keys.TryGetValue(key, out var count) && count == 1), StringComparer.OrdinalIgnoreCase);
    }

    private static HashSet<string> EmbeddedPaths(IEnumerable<CadTreeNode> nodes) => new HashSet<string>(
        nodes.SelectMany(node => new[] { node.FullPath, node.SourceReferencePath })
            .Where(path => PdmAddin.IsSolidWorksTemporaryVirtualComponentPath(path) || IsInterconnectCachePath(path))
            .Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<CadTreeNode> EmbeddedVirtualNodes(CadTreeNode node, bool parentVirtual)
    {
        var virtualComponent = parentVirtual || node.Status == CadReferenceStatus.Virtual;
        if (virtualComponent && (PdmAddin.IsSolidWorksTemporaryVirtualComponentPath(node.FullPath)
            || PdmAddin.IsSolidWorksTemporaryVirtualComponentPath(node.SourceReferencePath)
            || IsInterconnectCachePath(node.FullPath) && !string.IsNullOrWhiteSpace(node.ImportedSourcePath))) yield return node;
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
