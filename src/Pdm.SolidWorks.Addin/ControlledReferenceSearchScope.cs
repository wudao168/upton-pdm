using System;
using System.IO;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Upton.Pdm.SolidWorks;

internal sealed class ControlledReferenceSearchScope : IDisposable
{
    private readonly ISldWorks application;
    private readonly string previousDirectory;
    private readonly string previousFolders;
    private readonly bool previousSearchRules;

    public ControlledReferenceSearchScope(ISldWorks application, string rootPath, ControlledOpenManifestDto manifest)
    {
        this.application = application;
        previousDirectory = application.GetCurrentWorkingDirectory() ?? string.Empty;
        previousFolders = application.GetSearchFolders((int)swSearchFolderTypes_e.swDocumentType) ?? string.Empty;
        previousSearchRules = application.GetUserPreferenceToggle((int)swUserPreferenceToggle_e.swUseFolderSearchRules);
        var directory = Path.GetDirectoryName(rootPath);
        var folders = manifest.Files.Select(file => Path.GetDirectoryName(Path.GetFullPath(
            Path.Combine(directory, file.RelativePath ?? file.FileName)))).Distinct(StringComparer.OrdinalIgnoreCase);
        try
        {
            application.SetCurrentWorkingDirectory(directory);
            if (!application.SetSearchFolders((int)swSearchFolderTypes_e.swDocumentType, string.Join(";", folders)))
                throw new InvalidOperationException("SolidWorks无法设置本次受控文件搜索目录，已停止打开。");
            application.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swUseFolderSearchRules, true);
            if (!application.GetUserPreferenceToggle((int)swUserPreferenceToggle_e.swUseFolderSearchRules))
                throw new InvalidOperationException("SolidWorks无法启用受控文件搜索规则，已停止打开。");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        try { application.SetSearchFolders((int)swSearchFolderTypes_e.swDocumentType, previousFolders); }
        finally
        {
            try { application.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swUseFolderSearchRules, previousSearchRules); }
            finally { application.SetCurrentWorkingDirectory(previousDirectory); }
        }
    }
}
