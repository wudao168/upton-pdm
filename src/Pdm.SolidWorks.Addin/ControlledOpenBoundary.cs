using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SolidWorks.Interop.sldworks;

namespace Upton.Pdm.SolidWorks;

internal sealed class ControlledOpenBoundary : IDisposable
{
    [ThreadStatic] private static ControlledOpenBoundary current;
    private readonly ControlledOpenBoundary previous;
    private readonly Dictionary<string, string> paths;
    private readonly Dictionary<string, RetainedLocalReference> localReferences;
    private readonly HashSet<string> blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    internal static bool IsActive => current != null;

    internal ControlledOpenBoundary(string directory, ControlledOpenManifestDto manifest)
    {
        paths = Paths(directory, manifest);
        localReferences = manifest.RetainedLocalReferences;
        previous = current;
        current = this;
    }

    internal static Dictionary<string, string> Paths(string directory, ControlledOpenManifestDto manifest)
    {
        var paths = manifest.Files.ToDictionary(file => file.FileName,
            file => Path.GetFullPath(Path.Combine(directory, file.RelativePath ?? file.FileName)), StringComparer.OrdinalIgnoreCase);
        foreach (var record in manifest.RetainedLocalReferences.Values)
        {
            if (paths.ContainsKey(record.FileName) || record.Paths.Any(path => !record.Matches(path)))
                throw new InvalidOperationException("本地新增引用与恢复时的校验记录不一致，文件和草稿已保留：" + record.FileName);
            paths.Add(record.FileName, record.SourcePath);
        }
        return paths;
    }

    internal static void RequireLoadedSources(ISldWorks application, string rootPath, ControlledOpenManifestDto manifest)
    {
        var expected = Paths(Path.GetDirectoryName(rootPath), manifest);
        foreach (var model in (application.GetDocuments() as Array)?.OfType<IModelDoc2>() ?? Enumerable.Empty<IModelDoc2>())
        {
            var actual = model.GetPathName();
            if (!string.IsNullOrWhiteSpace(actual) && manifest.RetainedLocalReferences.TryGetValue(Path.GetFileName(actual), out var local)
                && local.Matches(actual))
            {
                if (local.Managed && !local.Editable && model.GetSaveFlag())
                    throw new InvalidOperationException("新装入的PLM零件仍有未保存修改，原窗口和文件已保留，请先核对该零件的编辑权限：" + local.FileName);
                continue;
            }
            if (!string.IsNullOrWhiteSpace(actual) && expected.TryGetValue(Path.GetFileName(actual), out var target)
                && !string.Equals(Path.GetFullPath(actual), target, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("已打开另一目录的同名图档，未加载PLM副本，原窗口及修改均保留。请先保存并关闭该同名图档后重试。"
                    + "\r\n当前来源：" + actual + "\r\nPLM工作副本：" + target);
        }
    }

    internal static bool Reject(string path)
    {
        if (current == null || string.IsNullOrWhiteSpace(path)) return false;
        var full = Path.GetFullPath(path);
        if (current.localReferences.TryGetValue(Path.GetFileName(full), out var local) && local.Allows(full)) return false;
        if (current.paths.TryGetValue(Path.GetFileName(full), out var expected))
        {
            if (string.Equals(full, expected, StringComparison.OrdinalIgnoreCase)) return false;
        }
        else
        {
            // Generated embedded documents are verified against their native owner
            // after loading; they are never substituted by an ordinary disk file.
            if (PdmAddin.IsSolidWorksTemporaryVirtualComponentPath(full) || NativeArchiveManifest.IsInterconnectCachePath(full)) return false;
        }
        current.blocked.Add(full);
        PluginDiagnosticLog.Record("受控打开拦截", "已阻止加载受控清单之外的文件：" + full);
        return true;
    }

    internal void Verify()
    {
        if (blocked.Count > 0)
            throw new InvalidOperationException("已阻止图档加载原目录或其他非受控文件，尚未完成打开；请核对PLM引用定位。原文件和已有窗口均保留：\r\n"
                + string.Join("\r\n", blocked.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)));
    }

    public void Dispose() { current = previous; }
}
