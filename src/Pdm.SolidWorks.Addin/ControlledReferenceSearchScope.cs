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
    private readonly ControlledOpenBoundary boundary;

    public ControlledReferenceSearchScope(ISldWorks application, string rootPath, ControlledOpenManifestDto manifest)
    {
        this.application = application;
        previousDirectory = application.GetCurrentWorkingDirectory() ?? string.Empty;
        previousFolders = application.GetSearchFolders((int)swSearchFolderTypes_e.swDocumentType) ?? string.Empty;
        previousSearchRules = application.GetUserPreferenceToggle((int)swUserPreferenceToggle_e.swUseFolderSearchRules);
        boundary = new ControlledOpenBoundary(Path.GetDirectoryName(rootPath), manifest);
        var directory = Path.GetDirectoryName(rootPath);
        var folders = manifest.Files.Select(file => Path.GetDirectoryName(Path.GetFullPath(
            Path.Combine(directory, file.RelativePath ?? file.FileName))))
            .Concat(manifest.RetainedLocalReferences.Values.SelectMany(record => record.Paths).Select(Path.GetDirectoryName))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        try
        {
            application.SetCurrentWorkingDirectory(directory);
            if (!application.SetSearchFolders((int)swSearchFolderTypes_e.swDocumentType, string.Join(";", folders)))
                PluginDiagnosticLog.Record("受控搜索目录", "搜索目录设置未完成，将核对实际加载来源。");
            application.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swUseFolderSearchRules, true);
            if (!application.GetUserPreferenceToggle((int)swUserPreferenceToggle_e.swUseFolderSearchRules))
                PluginDiagnosticLog.Record("受控搜索目录", "搜索规则未启用，将核对实际加载来源。");
        }
        catch (Exception error)
        {
            PluginDiagnosticLog.Record("受控搜索目录", "搜索目录设置失败，将尝试原生打开并核对实际来源：" + error);
        }
    }

    internal void VerifyLoadedSources() => boundary.Verify();

    public void Dispose()
    {
        boundary.Dispose();
        try { application.SetSearchFolders((int)swSearchFolderTypes_e.swDocumentType, previousFolders); }
        catch (Exception error) { PluginDiagnosticLog.Record("受控搜索目录", "恢复原搜索目录失败：" + error); }
        try { application.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swUseFolderSearchRules, previousSearchRules); }
        catch (Exception error) { PluginDiagnosticLog.Record("受控搜索目录", "恢复原搜索规则失败：" + error); }
        try { application.SetCurrentWorkingDirectory(previousDirectory); }
        catch (Exception error) { PluginDiagnosticLog.Record("受控搜索目录", "恢复原工作目录失败：" + error); }
    }
}
