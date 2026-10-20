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
                PluginDiagnosticLog.Record("导入引用", step + "：原始值=" + ArchiveReferencePath.Visible(entry)
                    + "；原生路径=" + parts[0] + "；导入源路径=" + parts[1] + "；历史导入记录不作为存档门槛。");
            }
            else parts = new[] { entry };
            foreach (var part in parts)
            {
                try { files.Add(ArchiveReferencePath.Resolve(part, Path.GetDirectoryName(root.FullPath), step,
                    root.FullPath, root.InstancePath, root.Configuration)); }
                catch (ArchiveReferencePathException error)
                {
                    // Actual tree paths are checked independently. A malformed historical
                    // entry must not prevent archiving the current native component.
                    files.Add(part ?? string.Empty);
                    PluginDiagnosticLog.Record("树外引用", "原生历史记录路径未解析，不影响当前结构文件收集：" + error.Message);
                }
            }
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
        var directory = Path.GetDirectoryName(ArchiveReferencePath.Resolve(root.FullPath, null, "核对存档根路径", root.FullPath));
        var nodes = Walk(root).ToArray();
        var failures = new List<string>();
        var external = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var embeddedNodes = new HashSet<CadTreeNode>(EmbeddedVirtualNodes(root, false));
        var embeddedPaths = EmbeddedPaths(embeddedNodes);
        var embeddedKeys = embeddedPaths.Where(path => VirtualComponentKey(path) != null).GroupBy(VirtualComponentKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Distinct(StringComparer.OrdinalIgnoreCase).Count(), StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes)
        {
            if (IsInterconnectCachePath(node.FullPath) && (!node.IsStoredImport || !ImportedComponentStorage.IsStableOwner(node.ImportedStorageOwnerPath)))
                failures.Add("树内导入组件尚未形成可确认的原生存档内容，请将其保存为稳定原生文件或确认随父装配嵌入保存：" + node.FileName + "；实例：" + node.InstancePath);
            else if (node.Status != CadReferenceStatus.Virtual && !embeddedNodes.Contains(node)
                && PdmAddin.IsSolidWorksTemporaryVirtualComponentPath(node.FullPath))
                failures.Add("树内临时组件的嵌入归属尚未确认：" + node.FileName);
            else if (node.Status == CadReferenceStatus.Virtual || embeddedNodes.Contains(node))
            {
                if (!embeddedNodes.Contains(node)) failures.Add("树内虚拟组件的保存归属尚未确认：" + node.FileName);
            }
            else if (string.IsNullOrWhiteSpace(node.FullPath) || !File.Exists(node.FullPath))
                failures.Add("树内图档缺失，不能完整存档：" + node.FileName + "；路径：" + node.FullPath);
            else if (new FileInfo(node.FullPath).Length == 0)
                failures.Add("树内图档为空，不能完整存档：" + node.FileName);
        }
        var ordinary = nodes.Where(node => node.Status != CadReferenceStatus.Virtual && !embeddedNodes.Contains(node)
            && !string.IsNullOrWhiteSpace(node.FullPath)).ToArray();
        var paths = new HashSet<string>(ordinary.Select(node => Path.GetFullPath(node.FullPath)), StringComparer.OrdinalIgnoreCase);
        foreach (var original in nativeFiles.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string path;
            try { path = ArchiveReferencePath.Resolve(original, directory, "识别结构内原生引用", root.FullPath); }
            catch (ArchiveReferencePathException) { external.Add(ArchiveReferencePath.Visible(original)); continue; }
            if (embeddedPaths.Contains(path)) continue;
            var key = VirtualComponentKey(path);
            if (key != null && embeddedKeys.TryGetValue(key, out var count))
            {
                if (count != 1) failures.Add("树内嵌入组件的原生路径对应不唯一：" + original);
                continue;
            }
            if (paths.Contains(path)) continue;
            // The loaded tree owns the archive scope. A saved source path outside it
            // is not another version of the current component and must not be read.
            external.Add(original);
        }
        if (failures.Count > 0)
            throw new InvalidOperationException("当前结构内容检查未通过，本次尚未上传文件：\r\n" + string.Join("\r\n", failures.Distinct().Take(12)));
        foreach (var reference in external)
            PluginDiagnosticLog.Record("树外引用", "不纳入当前结构存档，未读取、登记或改写：" + ArchiveReferencePath.Visible(reference));
        PluginDiagnosticLog.Record("存档范围", "已核对当前结构图档" + ordinary.Length + "项；嵌入组件" + embeddedNodes.Count
            + "项；树外引用" + external.Count + "项，仅提示，不纳入存档。完整性结论仅针对当前结构。");
        return external.ToArray();
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
        foreach (var child in node.Children.Where(child => child.Role != CadReferenceRole.ExternalFileDependency))
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
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);

    internal static IEnumerable<CadTreeNode> EmbeddedVirtualNodes(CadTreeNode node, bool parentVirtual)
    {
        var virtualComponent = parentVirtual || node.Status == CadReferenceStatus.Virtual;
        if (node.Status == CadReferenceStatus.Virtual || virtualComponent && (PdmAddin.IsSolidWorksTemporaryVirtualComponentPath(node.FullPath)
            || PdmAddin.IsSolidWorksTemporaryVirtualComponentPath(node.SourceReferencePath))) yield return node;
        foreach (var child in node.Children.Where(child => child.Role != CadReferenceRole.ExternalFileDependency))
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
