using System;
using System.IO;
using System.IO.Compression;

namespace Upton.Pdm.SolidWorks;

internal static class PluginUiPayload
{
    // Legacy updaters pass per-file hashes on the Windows command line. Ship
    // offline UI assets as one verified file and expose the usual ui directory.
    internal static void EnsureExpanded(string installDirectory)
    {
        var archive = Path.Combine(installDirectory, "uplm-ui.zip");
        var ui = Path.Combine(installDirectory, "ui");
        if (Directory.Exists(ui) || !File.Exists(archive)) return;
        var temporary = Path.Combine(installDirectory, ".ui-" + Guid.NewGuid().ToString("N"));
        try
        {
            ZipFile.ExtractToDirectory(archive, temporary);
            if (!File.Exists(Path.Combine(temporary, "index.html")))
                throw new InvalidDataException("插件离线界面文件不完整，请重新检查更新。");
            try { Directory.Move(temporary, ui); }
            catch (IOException) when (Directory.Exists(ui)) { }
        }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
    }
}
