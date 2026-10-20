using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Upton.Pdm.LocalSettings;

namespace Upton.Pdm.SolidWorks;

internal static class DrawingDraftPath
{
    internal static string PreservePreviousCopy(string path, string expectedHash)
    {
        var workspace = Path.GetFullPath(WorkspaceSettingsStore.GetWorkspaceRoot()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var source = Path.GetFullPath(path);
        if (!source.StartsWith(workspace, StringComparison.OrdinalIgnoreCase)
            || source.IndexOf(Path.DirectorySeparatorChar + "Latest" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) < 0
            || !string.Equals(Path.GetExtension(source), ".SLDDRW", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(EditDraftJournal.Hash(source), expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("原工程图在切换期间发生变化，已保留两份文件，请核对后重试。");
        var recovery = Path.Combine(workspace, ".uplm", "Recovery", "DrawingDraft-" + Guid.NewGuid().ToString("N"), Path.GetFileName(source));
        Directory.CreateDirectory(Path.GetDirectoryName(recovery));
        File.Move(source, recovery);
        return recovery;
    }

    internal static string ForSource(string sourcePath, Guid? projectId, Guid? modelId)
    {
        string identity;
        using (var hash = SHA256.Create())
            identity = modelId?.ToString("N") ?? BitConverter.ToString(hash.ComputeHash(
                Encoding.UTF8.GetBytes(Path.GetFullPath(sourcePath).ToUpperInvariant()))).Replace("-", "");
        return Path.Combine(WorkspaceSettingsStore.GetWorkspaceRoot(), "View", "工程图草稿",
            projectId?.ToString("N") ?? "未关联项目", identity, Path.ChangeExtension(Path.GetFileName(sourcePath), ".SLDDRW"));
    }
}
