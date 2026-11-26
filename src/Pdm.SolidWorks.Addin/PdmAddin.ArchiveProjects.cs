using System;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using SolidWorks.Interop.sldworks;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    private async Task<bool> PrepareArchiveContextAsync(ArchiveSession session, bool standardLibrary = false)
    {
        if (session.ArchiveContextPrepared) return true;
        var token = session.Token;
        var client = apiClient;
        var scope = client.EditJournalScope;
        var actor = authenticatedUsername;
        var root = (application.ActiveDoc as IModelDoc2)?.GetPathName();
        if (string.IsNullOrWhiteSpace(root)) throw new InvalidOperationException("请先打开并保存要存档的图档。");
        session.SetStage("正在读取可选存档项目…");
        async Task<StandardStructureViewDto> ReadLibraryAsync()
        {
            try { return await apiClient.GetStandardStructureCapabilitiesAsync(token); }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) { LogDiagnostic("读取存档窗口标准库权限", error); return null; }
        }
        var projects = RefreshArchiveProjectsAsync(token);
        var library = ReadLibraryAsync();
        await Task.WhenAll(projects, library);
        standardLibraryArchiveAllowed = (await library)?.CanArchive == true;
        var hasProject = availableProjects.Any(p => p.CanSubmitArchive);
        if (!hasProject && !standardLibraryArchiveAllowed)
            throw new InvalidOperationException("当前账号没有可存档的项目或标准库。");
        var owner = ResolveArchiveProject(root);
        session.ArchiveProjectId = owner;
        session.StartInStandardLibrary = standardLibrary || !hasProject && standardLibraryArchiveAllowed;
        if (!owner.HasValue && !session.StartInStandardLibrary)
        {
            // Unbound local files need a destination before scanning the assembly. The picker
            // lives inside the same archive window; choosing it only prepares the editable list.
            var target = session.Invoke(() =>
            {
                using var picker = new ProjectBrowserDialog(availableProjects, null, true);
                picker.ConfigureArchiveDestination(standardLibraryArchiveAllowed);
                var result = session.ShowInline(picker);
                return (result, project: picker.SelectedProject?.Id, library: picker.StandardLibraryDestination);
            });
            if (target.result != System.Windows.Forms.DialogResult.OK)
            {
                session.CancelBeforeSubmission();
                return false;
            }
            session.StartInStandardLibrary = target.library;
            session.ArchiveProjectId = target.library ? null : target.project;
        }
        if (apiClient != client || !string.Equals(scope, client.EditJournalScope, StringComparison.Ordinal)
            || !string.Equals(actor, authenticatedUsername, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("选择项目期间登录账号或公司已切换，请重新开始存档。");
        if (!PathsEqual(root, (application.ActiveDoc as IModelDoc2)?.GetPathName()))
            throw new InvalidOperationException("选择项目期间活动图档已切换，请从目标图档重新开始存档。");
        session.ArchiveRootPath = root;
        session.ArchiveContextPrepared = true;
        return true;
    }

    private Guid? ResolveArchiveProject(string root)
    {
        var owners = controlledOpenManifests.Where(pair => IsPathWithinDirectory(root, pair.Key)
            && pair.Value.Files.Any(file => PathsEqual(root, ControlledReferenceMaterializer.ManifestPath(pair.Key, file))))
            .Select(pair => pair.Value.ProjectId).Distinct().Take(2).ToArray();
        return owners.Length == 1 ? owners[0] : GetExplicitProjectId(root);
    }

    private async Task RefreshArchiveProjectsAsync(CancellationToken token)
    {
        var scope = apiClient.EditJournalScope;
        var projects = await apiClient.GetProjectsAsync(token);
        token.ThrowIfCancellationRequested();
        if (!string.Equals(scope, apiClient.EditJournalScope, StringComparison.Ordinal))
            throw new OperationCanceledException("账号或公司已切换，请重新打开存档窗口。", token);
        // Project counts/permissions are metadata, not CAD structure. Refresh once
        // per archive entry rather than reusing the list captured at login.
        availableProjects = projects;
    }
}
