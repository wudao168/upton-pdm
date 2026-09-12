using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed class ProjectCopyService(
    IPdmRepository repository,
    IFileStorage documentStorage,
    IProjectFileRepository projectFiles,
    IProjectFileStorage projectFileStorage,
    IValidationPlanRepository validationPlans,
    TimeProvider timeProvider)
{
    private static readonly BomKind[] CopiedBomKinds =
        [BomKind.Standard, BomKind.NonStandard, BomKind.Unclassified, BomKind.Virtual, BomKind.Electrical];

    public async Task<ProjectCopyPreview> PreviewAsync(
        Guid sourceProjectId,
        Guid targetProjectId,
        ProjectCopyOptions options,
        string actor,
        UserRole role,
        CancellationToken cancellationToken)
    {
        if (sourceProjectId == targetProjectId) throw new PdmRuleException("源项目和目标项目不能相同。");
        await RequirePermissionAsync(actor, role, PermissionCodes.ProjectCreate, cancellationToken);
        var source = await RequireProjectAsync(sourceProjectId, cancellationToken);
        var target = await RequireProjectAsync(targetProjectId, cancellationToken);
        if (!source.IsActive || !target.IsActive) throw new PdmConflictException("源项目和目标项目都必须处于启用状态。");
        if (!await repository.HasProjectContentReadAccessAsync(source.Id, actor, role, cancellationToken))
            throw new UnauthorizedAccessException("当前用户没有源项目内容读取权限。");
        if (!await repository.HasProjectContentReadAccessAsync(target.Id, actor, role, cancellationToken))
            throw new UnauthorizedAccessException("当前用户没有目标项目内容权限。");

        await repository.EnsureProjectFolderTreeAsync(source.Id, cancellationToken);
        await repository.EnsureProjectFolderTreeAsync(target.Id, cancellationToken);
        var sourceFolders = await repository.ListProjectFoldersAsync(source.Id, actor, role, cancellationToken);
        var targetFolders = await repository.ListProjectFoldersAsync(target.Id, actor, role, cancellationToken);
        var sourceRootId = source.RootProjectId ?? source.Id;
        var targetRootId = target.RootProjectId ?? target.Id;
        var eligibleFolders = sourceFolders
            .Where(folder => folder.Purpose == ProjectFolderPurpose.Standard
                && (folder.EffectiveAccess & FolderAccess.Download) == FolderAccess.Download)
            .ToArray();
        var selectedFolderIds = ExpandedSelectedFolderIds(eligibleFolders, options.FolderIds);
        var folderOptions = new List<ProjectCopyFolderOption>(eligibleFolders.Length);
        var selectedSourceFiles = new List<ProjectFile>();
        foreach (var folder in eligibleFolders.OrderBy(item => FolderPath(item, sourceFolders), StringComparer.CurrentCultureIgnoreCase))
        {
            var files = await projectFiles.ListAsync(sourceRootId, folder.Id, false, cancellationToken);
            var current = files.Where(file => file.CurrentVersion is not null).ToArray();
            var defaultSelected = IsDefaultGasSequenceFolder(folder);
            folderOptions.Add(new(folder.Id, folder.Name, FolderPath(folder, sourceFolders), folder.TemplateKey,
                current.Length, current.Sum(file => file.CurrentVersion!.FileLength), defaultSelected));
            if (selectedFolderIds.Contains(folder.Id)) selectedSourceFiles.AddRange(current);
        }

        var sourceDocuments = await repository.ListDocumentsAsync(source.Id, cancellationToken);
        var sourceVersions = await repository.ListProjectDocumentVersionsAsync(source.Id, cancellationToken);
        var latestVersions = sourceVersions
            .GroupBy(version => version.DocumentId)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(version => version.CreatedAt).First());
        var selectedDocuments = sourceDocuments
            .Where(document => document.Kind == DocumentKind.Drawing ? options.CopyDrawings : options.CopyModels)
            .ToArray();
        var missingLatest = selectedDocuments.Count(document => !latestVersions.ContainsKey(document.Id));
        var copyableDocuments = selectedDocuments.Where(document => latestVersions.ContainsKey(document.Id)).ToArray();
        var sourceBom = options.CopyBom
            ? (await Task.WhenAll(CopiedBomKinds.Select(kind => repository.GetBomAsync(source.Id, kind, cancellationToken)))).SelectMany(items => items).Where(item => item.DeletedAt is null).ToArray()
            : [];
        var sourcePlan = options.CopyValidationItems ? await validationPlans.FindPlanAsync(source.Id, cancellationToken) : null;

        var blocking = new List<string>();
        var warnings = new List<string>();
        if (!options.CopyModels && !options.CopyDrawings && !options.CopyBom && !options.CopyValidationItems && selectedFolderIds.Count == 0)
            blocking.Add("请至少选择一项要复制的内容。");
        if (missingLatest > 0) blocking.Add($"源项目有{missingLatest}个所选图档尚无存档版本，不能复制。");
        if (copyableDocuments.Any(document => latestVersions[document.Id].ReferenceSnapshot.HasBlockingIssue))
            blocking.Add("源项目最新图档版本存在缺失引用，不能复制。");
        var copiedIds = copyableDocuments.Select(document => document.Id).ToHashSet();
        var externalReferenceCount = copyableDocuments
            .SelectMany(document => Enumerate(latestVersions[document.Id].ReferenceSnapshot))
            .Select(node => node.DocumentId)
            .Where(id => id.HasValue && !copiedIds.Contains(id.Value))
            .Distinct()
            .Count();
        if (externalReferenceCount > 0) blocking.Add($"所选3D/2D图档包含{externalReferenceCount}个未纳入复制的受控引用，请同时复制全部依赖图档。");

        var targetDocuments = await repository.ListDocumentsAsync(target.Id, cancellationToken);
        if (options.CopyModels && targetDocuments.Any(document => document.Kind != DocumentKind.Drawing)) blocking.Add("目标项目已经包含3D图档。");
        if (options.CopyDrawings && targetDocuments.Any(document => document.Kind == DocumentKind.Drawing)) blocking.Add("目标项目已经包含2D图档。");
        if (options.CopyBom && (await Task.WhenAll(CopiedBomKinds.Select(kind => repository.GetBomAsync(target.Id, kind, cancellationToken)))).Any(items => items.Count > 0))
            blocking.Add("目标项目已经包含BOM数据。");
        if (options.CopyValidationItems && await validationPlans.FindPlanAsync(target.Id, cancellationToken) is not null)
            blocking.Add("目标项目已经包含验证计划。");

        foreach (var sourceFolderId in selectedFolderIds)
        {
            var sourceFolder = eligibleFolders.First(folder => folder.Id == sourceFolderId);
            var targetFolder = FindTargetFolder(sourceFolder, targetFolders, target.Id);
            if (targetFolder is null)
            {
                if (sourceFolder.IsSystem) blocking.Add($"目标项目缺少对应目录：{FolderPath(sourceFolder, sourceFolders)}。");
                continue;
            }
            if ((targetFolder.EffectiveAccess & FolderAccess.Upload) != FolderAccess.Upload)
                blocking.Add($"当前用户没有目标目录上传权限：{FolderPath(targetFolder, targetFolders)}。");
            if ((await projectFiles.ListAsync(targetRootId, targetFolder.Id, false, cancellationToken)).Count > 0)
                blocking.Add($"目标目录已有文件：{FolderPath(targetFolder, targetFolders)}。");
        }

        if (options.CopyModels && copyableDocuments.All(document => document.Kind == DocumentKind.Drawing)) warnings.Add("源项目没有可复制的3D图档。");
        if (options.CopyDrawings && copyableDocuments.All(document => document.Kind != DocumentKind.Drawing)) warnings.Add("源项目没有可复制的2D图档。");
        if (options.CopyBom && sourceBom.Length == 0) warnings.Add("源项目没有可复制的BOM数据。");
        if (options.CopyValidationItems && (sourcePlan is null || sourcePlan.Items.Count == 0)) warnings.Add("源项目没有可复制的验证检查项目。");

        var documentBytes = copyableDocuments.Sum(document => latestVersions[document.Id].FileLength);
        var projectFileBytes = selectedSourceFiles.Sum(file => file.CurrentVersion!.FileLength);
        return new(source.Id, target.Id,
            copyableDocuments.Count(document => document.Kind != DocumentKind.Drawing),
            copyableDocuments.Count(document => document.Kind == DocumentKind.Drawing),
            sourceBom.Length,
            sourcePlan?.Items.Count ?? 0,
            selectedSourceFiles.Count,
            documentBytes + projectFileBytes,
            folderOptions,
            blocking.Distinct().ToArray(),
            warnings);
    }

    public async Task<ProjectCopyResult> ExecuteAsync(
        Guid sourceProjectId,
        Guid targetProjectId,
        ProjectCopyOptions options,
        string actor,
        UserRole role,
        CancellationToken cancellationToken)
    {
        var preview = await PreviewAsync(sourceProjectId, targetProjectId, options, actor, role, cancellationToken);
        if (!preview.CanExecute) throw new PdmConflictException(string.Join("；", preview.BlockingReasons));
        if (options.CopyModels || options.CopyDrawings) await RequirePermissionAsync(actor, role, PermissionCodes.DocumentEdit, cancellationToken);
        if (options.CopyBom) await RequirePermissionAsync(actor, role, PermissionCodes.BomEdit, cancellationToken);
        if (options.CopyValidationItems) await RequirePermissionAsync(actor, role, PermissionCodes.ValidationPlanEdit, cancellationToken);

        var source = await RequireProjectAsync(sourceProjectId, cancellationToken);
        var target = await RequireProjectAsync(targetProjectId, cancellationToken);
        var sourceFolders = await repository.ListProjectFoldersAsync(source.Id, actor, role, cancellationToken);
        var targetFolders = (await repository.ListProjectFoldersAsync(target.Id, actor, role, cancellationToken)).ToList();
        var sourceDocuments = await repository.ListDocumentsAsync(source.Id, cancellationToken);
        var sourceRelations = (await repository.ListDocumentRelationsAsync(source.Id, cancellationToken)).ToDictionary(relation => relation.DrawingDocumentId);
        var latestVersions = (await repository.ListProjectDocumentVersionsAsync(source.Id, cancellationToken))
            .GroupBy(version => version.DocumentId)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(version => version.CreatedAt).First());
        var selectedDocuments = sourceDocuments
            .Where(document => document.Kind == DocumentKind.Drawing ? options.CopyDrawings : options.CopyModels)
            .OrderBy(document => document.Kind == DocumentKind.Drawing ? 1 : 0)
            .ThenBy(document => document.FileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var sourceDocument in selectedDocuments)
        {
            var version = latestVersions[sourceDocument.Id];
            await documentStorage.VerifyStoredFileAsync(source,
                new StoredFile(version.StorageRelativePath, version.FileLength, version.Sha256, version.CreatedAt), cancellationToken);
        }
        var eligibleProjectFolders = sourceFolders.Where(folder => folder.Purpose == ProjectFolderPurpose.Standard).ToArray();
        var selectedProjectFolderIds = ExpandedSelectedFolderIds(eligibleProjectFolders, options.FolderIds);
        var sourceRootId = source.RootProjectId ?? source.Id;
        foreach (var folder in eligibleProjectFolders.Where(folder => selectedProjectFolderIds.Contains(folder.Id)))
        {
            foreach (var file in await projectFiles.ListAsync(sourceRootId, folder.Id, false, cancellationToken))
            {
                if (file.CurrentVersion is not null) await projectFileStorage.VerifyAsync(file.CurrentVersion, cancellationToken);
            }
        }
        var documentMap = new Dictionary<Guid, Guid>();
        foreach (var sourceDocument in selectedDocuments.Where(document => document.Kind != DocumentKind.Drawing))
        {
            var registered = await RegisterCopyAsync(sourceDocument, null, target, sourceFolders, targetFolders, latestVersions[sourceDocument.Id], actor, cancellationToken);
            documentMap[sourceDocument.Id] = registered.Id;
        }
        foreach (var sourceDocument in selectedDocuments.Where(document => document.Kind == DocumentKind.Drawing))
        {
            Guid? relatedModelId = null;
            if (sourceRelations.TryGetValue(sourceDocument.Id, out var relation)) relatedModelId = documentMap.GetValueOrDefault(relation.ModelDocumentId);
            var registered = await RegisterCopyAsync(sourceDocument, relatedModelId, target, sourceFolders, targetFolders, latestVersions[sourceDocument.Id], actor, cancellationToken);
            documentMap[sourceDocument.Id] = registered.Id;
        }

        var sourceProjectRoot = await repository.GetLatestReferenceSnapshotAsync(source.Id, cancellationToken);
        long documentBytes = 0;
        foreach (var sourceDocument in selectedDocuments)
        {
            var sourceVersion = latestVersions[sourceDocument.Id];
            var targetDocumentId = documentMap[sourceDocument.Id];
            var copiedFile = await documentStorage.CopyVersionAsync(
                source,
                target,
                new StoredFile(sourceVersion.StorageRelativePath, sourceVersion.FileLength, sourceVersion.Sha256, sourceVersion.CreatedAt),
                Path.Combine(".versions", targetDocumentId.ToString("N"), "W1", sourceDocument.FileName),
                cancellationToken);
            var checkoutSessionId = Guid.NewGuid();
            await repository.CheckoutAsync(targetDocumentId, actor, checkoutSessionId, "PROJECT-COPY", timeProvider.GetUtcNow().AddMinutes(30), cancellationToken);
            var remappedRoot = RemapReference(sourceVersion.ReferenceSnapshot, documentMap);
            var targetSnapshot = new CadReferenceSnapshot(
                Guid.NewGuid(), target.Id, targetDocumentId, timeProvider.GetUtcNow(), actor, remappedRoot, HashReference(remappedRoot));
            await repository.CheckInVersionAsync(targetDocumentId, actor, checkoutSessionId, new DocumentVersionCommit(
                copiedFile,
                $"从项目{source.Code}复制最新版本{sourceVersion.Revision.Display}",
                sourceVersion.PropertySnapshot,
                targetSnapshot,
                [],
                [],
                SourceVersionId: sourceVersion.Id,
                SourceDescription: $"复制自{source.Code} · {sourceVersion.Revision.Display}",
                IsProjectRoot: sourceProjectRoot?.RootDocumentId == sourceDocument.Id,
                ForceVersion: true,
                DrawingNumber: sourceDocument.DrawingNumber,
                Name: sourceDocument.Name,
                FileName: sourceDocument.FileName), cancellationToken);
            documentBytes += sourceVersion.FileLength;
        }

        var copiedBomCount = 0;
        if (options.CopyBom)
        {
            foreach (var kind in CopiedBomKinds)
            {
                var sourceItems = (await repository.GetBomAsync(source.Id, kind, cancellationToken)).Where(item => item.DeletedAt is null).ToArray();
                var copied = sourceItems.Select(item => item with
                {
                    Id = Guid.NewGuid(),
                    ProjectId = target.Id,
                    Revision = item.SourceDocumentId.HasValue && documentMap.ContainsKey(item.SourceDocumentId.Value) ? "W1" : item.Revision,
                    SourceDocumentId = item.SourceDocumentId.HasValue ? documentMap.GetValueOrDefault(item.SourceDocumentId.Value) : null,
                    PropertyWritebackStatus = null,
                    ReconciliationStatus = null,
                    ReconciliationNote = null,
                    ReconciliationUpdatedBy = null,
                    ReconciliationUpdatedAt = null,
                    DeletedAt = null,
                    DeletedBy = null,
                    DeleteReason = null
                }).ToArray();
                if (copied.Length > 0) await repository.ReplaceBomAsync(target.Id, kind, copied, cancellationToken);
                copiedBomCount += copied.Length;
            }
        }

        var copiedValidationCount = 0;
        if (options.CopyValidationItems && await validationPlans.FindPlanAsync(source.Id, cancellationToken) is { } sourcePlan)
        {
            var now = timeProvider.GetUtcNow();
            var copiedItems = sourcePlan.Items.Select(item => item with
            {
                Id = Guid.NewGuid(),
                ValidationDate = null,
                Result = null,
                ResponsiblePerson = null,
                Remark = null
            }).ToArray();
            if (copiedItems.Length > 0)
            {
                await validationPlans.SavePlanAsync(new ProjectValidationPlan(
                    Guid.NewGuid(), target.Id, 1, ProjectValidationPlanState.Draft,
                    null, null, copiedItems, [], [], actor, now, actor, now, 1), null, cancellationToken);
                copiedValidationCount = copiedItems.Length;
            }
        }

        var copiedProjectFiles = await CopySelectedProjectFilesAsync(source, target, options.FolderIds, sourceFolders, targetFolders, actor, role, cancellationToken);
        await repository.AppendAuditAsync(new AuditEntry(Guid.NewGuid(), timeProvider.GetUtcNow(), actor,
            "project.content.copy", nameof(Project), target.Id.ToString(),
            $"从{source.Code}复制到{target.Code}：图档{documentMap.Count}，BOM{copiedBomCount}，验证检查项{copiedValidationCount}，项目文件{copiedProjectFiles.Count}"), cancellationToken);
        return new(source.Id, target.Id, documentMap.Count, copiedBomCount, copiedValidationCount, copiedProjectFiles.Count,
            documentBytes + copiedProjectFiles.Sum(item => item.CurrentVersion?.FileLength ?? 0));
    }

    private async Task<PdmDocument> RegisterCopyAsync(
        PdmDocument sourceDocument,
        Guid? relatedModelId,
        Project target,
        IReadOnlyList<ProjectFolder> sourceFolders,
        IReadOnlyList<ProjectFolder> targetFolders,
        DocumentVersion sourceVersion,
        string actor,
        CancellationToken cancellationToken)
    {
        var sourceFolder = sourceFolders.FirstOrDefault(folder => folder.Id == sourceDocument.FolderId);
        var targetFolder = sourceFolder is null ? null : FindTargetFolder(sourceFolder, targetFolders, target.Id);
        var sourceFingerprint = sourceVersion.PropertySnapshot.GetValueOrDefault("SourceFileSha256") ?? sourceVersion.Sha256;
        return await repository.RegisterDocumentAsync(new RegisterDocumentCommand(
            target.Id, sourceDocument.DrawingNumber, sourceDocument.Name, sourceDocument.FileName, sourceDocument.Kind,
            targetFolder?.Id, relatedModelId, sourceFingerprint, true,
            $"从项目复制实施：{sourceDocument.ProjectId}"), actor, cancellationToken);
    }

    private async Task<IReadOnlyList<ProjectFile>> CopySelectedProjectFilesAsync(
        Project source,
        Project target,
        IReadOnlyList<Guid>? folderIds,
        IReadOnlyList<ProjectFolder> sourceFolders,
        List<ProjectFolder> targetFolders,
        string actor,
        UserRole role,
        CancellationToken cancellationToken)
    {
        var eligible = sourceFolders.Where(folder => folder.Purpose == ProjectFolderPurpose.Standard).ToArray();
        var selected = ExpandedSelectedFolderIds(eligible, folderIds);
        var sourceRootId = source.RootProjectId ?? source.Id;
        var settings = await repository.GetSystemSettingsAsync(cancellationToken);
        var copied = new List<ProjectFile>();
        foreach (var sourceFolder in eligible.Where(folder => selected.Contains(folder.Id)).OrderBy(folder => FolderDepth(folder, sourceFolders)))
        {
            var targetFolder = await EnsureTargetFolderAsync(sourceFolder, sourceFolders, targetFolders, target, cancellationToken);
            if ((targetFolder.EffectiveAccess & FolderAccess.Upload) != FolderAccess.Upload)
                throw new UnauthorizedAccessException($"当前用户没有目标目录上传权限：{targetFolder.Name}。");
            foreach (var sourceFile in await projectFiles.ListAsync(sourceRootId, sourceFolder.Id, false, cancellationToken))
            {
                if (sourceFile.CurrentVersion is null) continue;
                var upload = await projectFileStorage.CopyVersionAsync(sourceFile.CurrentVersion, target.RootProjectId ?? target.Id,
                    targetFolder.Id, settings.VaultRoot, actor, cancellationToken);
                try
                {
                    copied.Add(await projectFiles.AddVersionAsync(upload, actor, $"从项目{source.Code}复制最新版本", cancellationToken));
                }
                catch
                {
                    await projectFileStorage.DiscardAsync(upload, cancellationToken);
                    throw;
                }
            }
        }
        return copied;
    }

    private async Task<ProjectFolder> EnsureTargetFolderAsync(ProjectFolder sourceFolder, IReadOnlyList<ProjectFolder> sourceFolders,
        List<ProjectFolder> targetFolders, Project target, CancellationToken cancellationToken)
    {
        var existing = FindTargetFolder(sourceFolder, targetFolders, target.Id);
        if (existing is not null) return existing;
        if (sourceFolder.IsSystem) throw new PdmConflictException($"目标项目缺少系统目录：{sourceFolder.Name}。");
        var sourceParent = sourceFolders.First(folder => folder.Id == sourceFolder.ParentFolderId);
        var targetParent = await EnsureTargetFolderAsync(sourceParent, sourceFolders, targetFolders, target, cancellationToken);
        var created = await repository.CreateProjectFolderAsync(target.Id, targetParent.Id, sourceFolder.Name, cancellationToken);
        targetFolders.Add(created);
        return created;
    }

    private static ProjectFolder? FindTargetFolder(ProjectFolder source, IReadOnlyList<ProjectFolder> targets, Guid targetProjectId)
    {
        if (source.Purpose == ProjectFolderPurpose.ProjectContainer)
            return targets.FirstOrDefault(folder => folder.TemplateKey == source.TemplateKey && folder.TargetProjectId == targetProjectId);
        if (!string.Equals(source.TemplateKey, "custom", StringComparison.OrdinalIgnoreCase))
            return targets.FirstOrDefault(folder => string.Equals(folder.TemplateKey, source.TemplateKey, StringComparison.OrdinalIgnoreCase));
        return null;
    }

    private static HashSet<Guid> ExpandedSelectedFolderIds(IReadOnlyList<ProjectFolder> folders, IReadOnlyList<Guid>? requested)
    {
        var selected = requested is null
            ? folders.Where(IsDefaultGasSequenceFolder).Select(folder => folder.Id).ToHashSet()
            : requested.ToHashSet();
        if (selected.Any(id => folders.All(folder => folder.Id != id))) throw new PdmRuleException("包含不可复制或无权限的源文件夹。");
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var folder in folders.Where(folder => folder.ParentFolderId.HasValue && selected.Contains(folder.ParentFolderId.Value)))
                changed |= selected.Add(folder.Id);
        }
        return selected;
    }

    private static bool IsDefaultGasSequenceFolder(ProjectFolder folder) =>
        string.Equals(folder.TemplateKey, "mechanical.air-sequence", StringComparison.OrdinalIgnoreCase)
        || string.Equals(folder.Name, "气路时序", StringComparison.OrdinalIgnoreCase);

    private static string FolderPath(ProjectFolder folder, IReadOnlyList<ProjectFolder> folders)
    {
        var names = new Stack<string>();
        for (ProjectFolder? current = folder; current is not null; current = current.ParentFolderId.HasValue ? folders.FirstOrDefault(item => item.Id == current.ParentFolderId) : null)
            names.Push(current.Name);
        return string.Join(" / ", names);
    }

    private static int FolderDepth(ProjectFolder folder, IReadOnlyList<ProjectFolder> folders)
    {
        var depth = 0;
        for (ProjectFolder? current = folder; current?.ParentFolderId is Guid parentId; current = folders.FirstOrDefault(item => item.Id == parentId)) depth++;
        return depth;
    }

    private static IEnumerable<DocumentReferenceNode> Enumerate(DocumentReferenceNode root)
    {
        var pending = new Stack<DocumentReferenceNode>();
        pending.Push(root);
        while (pending.TryPop(out var node))
        {
            yield return node;
            foreach (var child in node.Children) pending.Push(child);
        }
    }

    private static DocumentReferenceNode RemapReference(DocumentReferenceNode source, IReadOnlyDictionary<Guid, Guid> map)
    {
        Guid? documentId = null;
        if (source.DocumentId.HasValue)
        {
            if (!map.TryGetValue(source.DocumentId.Value, out var mapped))
                throw new PdmConflictException($"图档引用{source.FileName}未纳入复制范围。");
            documentId = mapped;
        }
        return source with
        {
            NodeId = Guid.NewGuid(),
            DocumentId = documentId,
            Revision = source.DocumentId.HasValue ? RevisionLabel.InitialWork() : source.Revision,
            CheckedOutBy = null,
            Children = source.Children.Select(child => RemapReference(child, map)).ToArray()
        };
    }

    private static string HashReference(DocumentReferenceNode root) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(root))));

    private async Task<Project> RequireProjectAsync(Guid projectId, CancellationToken cancellationToken) =>
        await repository.FindProjectAsync(projectId, cancellationToken) ?? throw new PdmNotFoundException("项目不存在。");

    private async Task RequirePermissionAsync(string actor, UserRole role, string permission, CancellationToken cancellationToken)
    {
        if (!await repository.HasUserPermissionAsync(actor, role, permission, cancellationToken))
            throw new UnauthorizedAccessException("当前用户没有执行项目复制所需的权限。");
    }
}
