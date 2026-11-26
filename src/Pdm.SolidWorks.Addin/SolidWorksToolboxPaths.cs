using System;
using System.IO;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Upton.Pdm.SolidWorks;

internal static class SolidWorksToolboxPaths
{
    internal static bool IsBrowserPart(string path, string browser) =>
        !string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(browser)
        && string.Equals(Path.GetExtension(path), ".SLDPRT", StringComparison.OrdinalIgnoreCase)
        && path.StartsWith(browser, StringComparison.OrdinalIgnoreCase);

    internal static bool HasNativeToolboxType(IModelDoc2 model)
    {
        try
        {
            var type = model?.Extension?.ToolboxPartType ?? (int)swToolBoxPartType_e.swNotAToolboxPart;
            return (type & (int)(swToolBoxPartType_e.swToolboxStandardPart | swToolBoxPartType_e.swToolboxCopiedPart)) != 0;
        }
        catch (Exception error)
        {
            PluginDiagnosticLog.Record("Toolbox零件类型核对", error.ToString());
            return false;
        }
    }

    internal static string BrowserDirectory(ISldWorks application)
    {
        try
        {
            var folder = application?.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swHoleWizardToolBoxFolder);
            if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathRooted(folder)) return null;
            var location = new DirectoryInfo(Path.GetFullPath(folder));
            var parent = location.Parent;
            // SOLIDWORKS 2022 can return the language directory of the Hole Wizard database.
            if (string.Equals(parent?.Name, "lang", StringComparison.OrdinalIgnoreCase) && parent.Parent != null)
                location = parent.Parent;
            return Path.Combine(location.FullName, "browser") + Path.DirectorySeparatorChar;
        }
        catch (Exception error)
        {
            PluginDiagnosticLog.Record("Toolbox目录核对", "未能读取SolidWorks标准件目录：" + error);
            return null;
        }
    }
}
