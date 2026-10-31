using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Upton.Pdm.LocalSettings;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    private async Task<string> RememberControlledVersionIdentityAndManifestAsync(string path, Guid documentId, Guid projectId, DocumentVersionDto version)
    {
        var projectCode = availableProjects.FirstOrDefault(project => project.Id == projectId)?.Code;
        var workspaceRoot = WorkspaceSettingsStore.GetWorkspaceRoot();
        var user = authenticatedUsername; var scope = apiClient?.EditJournalScope;
        try
        {
            var recorded = await Task.Run(() =>
            {
                RememberControlledVersionIdentity(path, documentId, projectId, version);
                if (!PdmDocumentIdentityStore.TryReadLocalMetadata(path, out var metadata))
                    throw new IOException("无法读取刚写入的本地校验记录。");
                return metadata;
            });
            if (user != authenticatedUsername || scope != apiClient?.EditJournalScope)
                return Path.GetFileName(path) + "：服务器已存档，账号或公司已切换，请在对应项目刷新本地状态。";
            // Only the owning CAD thread updates the in-memory working identities.
            var fullPath = Path.GetFullPath(path); var revision = version.Revision?.Display ?? string.Empty;
            completedWorkingArchives[fullPath] = new CompletedWorkingArchive { ProjectId = projectId, DocumentId = documentId,
                VersionId = version.Id, StoredSha256 = version.Sha256, StoredLength = version.FileLength,
                SourceSha256 = recorded.Sha256, SourceLength = recorded.FileLength.Value, Username = user, ServerScope = scope };
            foreach (var context in controlledOpenManifests.Where(pair => pair.Value?.ProjectId == projectId && IsPathWithinDirectory(fullPath, pair.Key)).ToArray())
            {
                foreach (var file in (context.Value.Files ?? new List<ControlledOpenFileDto>()).Where(file => file.DocumentId == documentId))
                { file.VersionId = version.Id; file.Revision = revision; file.Sha256 = recorded.Sha256; file.FileLength = recorded.FileLength.Value; }
                if (context.Value.RootDocumentId == documentId) { context.Value.RootVersionId = version.Id; context.Value.RootRevision = revision; }
            }
            if (string.IsNullOrWhiteSpace(projectCode)) return string.Empty;
            var cached = await Task.Run(() => CacheCheckedInVersionInProjectView(workspaceRoot, projectCode, path, documentId, projectId, version));
            if (string.IsNullOrWhiteSpace(cached)) throw new IOException("工作区缓存未同步，已有副本可能包含尚未保存到服务器的内容。");
            if (!PathsEqual(cached, path)) LogOperation("Checked-in file cached in project workspace source=" + path + " target=" + cached);
            return string.Empty;
        }
        catch (Exception error)
        {
            LogDiagnostic("Cache checked-in file in project workspace." + Path.GetFileName(path), error);
            return Path.GetFileName(path) + "：服务器存档已完成，但本地文件或校验记录未能同步。请先备份本地文件，再检查磁盘空间及权限并重新获取；若本地有新增修改，请获取编辑权限后重新存档。";
        }
    }
}
