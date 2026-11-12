using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed class DocumentOperationAccessService(IPdmRepository repository)
{
    public async Task<IReadOnlyList<PdmDocument>> ApplyAsync(Guid projectId, IEnumerable<PdmDocument> documents,
        string actor, UserRole role, CancellationToken cancellationToken)
    {
        var content = await repository.HasProjectContentReadAccessAsync(projectId, actor, role, cancellationToken)
            && await AuthorizationPolicy.CanAsync(repository, projectId, actor, ProjectCenterPermissions.Tab("documents"), cancellationToken);
        var edit = content && await repository.HasUserPermissionAsync(actor, role, PermissionCodes.DocumentEdit, cancellationToken)
            && await AuthorizationPolicy.CanAsync(repository, projectId, actor, ProjectCenterPermissions.Write, cancellationToken);
        var archive = edit && await AuthorizationPolicy.CanAsync(repository, projectId, actor, "document.archive", cancellationToken)
            && await AuthorizationPolicy.CanAsync(repository, projectId, actor, AuthorizationActions.ArchiveSubmit, cancellationToken);
        var checkout = archive && await AuthorizationPolicy.CanAsync(repository, projectId, actor, "document.checkout", cancellationToken);
        var restore = archive && await AuthorizationPolicy.CanAsync(repository, projectId, actor, "document.restore", cancellationToken);
        var source = documents.ToArray();
        var editable = new HashSet<Guid>();
        // Reuse the repository's authoritative directory policy, including legacy
        // documents with an implicit folder. One evaluation per distinct folder.
        foreach (var group in source.Where(document => document.ProjectId == projectId).GroupBy(document => document.FolderId))
            if (await repository.HasDocumentAccessAsync(group.First().Id, actor, role, FolderAccess.View | FolderAccess.Edit, cancellationToken))
                foreach (var document in group) editable.Add(document.Id);
        return source.Select(document =>
        {
            var folder = editable.Contains(document.Id);
            return document with { OperationAccess = new(checkout && folder, archive && folder, restore && folder,
                !content ? "当前账号无该项目内容访问权限。" : !edit ? "权限中心未授权图档编辑。"
                : !folder ? "当前账号无该图档目录的编辑权限。" : !archive ? "权限中心未授权该项目存档。"
                : !checkout ? "权限中心未授权该项目获取权限。" : "") };
        }).ToArray();
    }
}
