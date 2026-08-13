using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed class PdmWorkflowService(IPdmRepository repository, IFileStorage fileStorage, IReleasePackagePublisher publisher, TimeProvider timeProvider)
{
    public async Task<Project> CreateNumberedProjectAsync(CreateNumberedProjectCommand command, string actor, UserRole role, CancellationToken cancellationToken)
    {
        RequireRole(role, UserRole.Engineer, UserRole.Administrator);
        ValidateProjectDetails(command.Name, command.ProjectAlias, command.Quantity);

        var options = await repository.GetProjectNumberingOptionsAsync(cancellationToken);
        if (!options.Organizations.Any(item => item.Id == command.OrganizationId && item.IsActive))
            throw new PdmRuleException("所选组织不存在或已停用。");
        if (!options.ProjectTypes.Any(item => string.Equals(item.Code, command.ProjectTypeCode, StringComparison.OrdinalIgnoreCase) && item.IsActive))
            throw new PdmRuleException("所选项目类型不存在或已停用。");
        if (!options.EquipmentTypes.Any(item => item.Code == command.EquipmentTypeCode && item.IsActive))
            throw new PdmRuleException("所选设备类型不存在或已停用。");
        var customer = await repository.FindCustomerAsync(command.CustomerId, cancellationToken);
        if (customer is null || !customer.IsActive)
            throw new PdmRuleException("所选客户不存在或已停用。");

        var settings = await repository.GetSystemSettingsAsync(cancellationToken);
        var vaultRoot = StorageLocationPolicy.Normalize(settings.VaultRoot);
        var releaseRoot = StorageLocationPolicy.Normalize(settings.ReleaseRoot);
        if (string.Equals(vaultRoot, releaseRoot, StringComparison.OrdinalIgnoreCase))
            throw new PdmRuleException("图档库与生产发包目录不能是同一位置。");

        var project = await repository.CreateNumberedProjectAsync(command with
        {
            ProjectTypeCode = command.ProjectTypeCode.Trim().ToUpperInvariant(),
            Name = command.Name.Trim(),
            ProjectAlias = NullIfWhiteSpace(command.ProjectAlias),
            Owner = actor,
            VaultLocation = vaultRoot,
            ReleaseLocation = releaseRoot
        }, cancellationToken);
        await AuditAsync(actor, "project.create", nameof(Project), project.Id.ToString(), $"{project.Code} · {project.Name} · 数量{project.Quantity}", cancellationToken);
        return project;
    }

    public async Task<Project> CreateSubprojectAsync(CreateSubprojectCommand command, string actor, UserRole role, CancellationToken cancellationToken)
    {
        RequireRole(role, UserRole.Engineer, UserRole.Administrator);
        ValidateProjectDetails(command.Name, command.ProjectAlias, command.Quantity);
        if (!await repository.HasProjectReadAccessAsync(command.ParentProjectId, actor, role, cancellationToken))
            throw new UnauthorizedAccessException("当前用户没有该主项目的访问权限。");
        var settings = await repository.GetSystemSettingsAsync(cancellationToken);
        var project = await repository.CreateSubprojectAsync(command with
        {
            Name = command.Name.Trim(),
            ProjectAlias = NullIfWhiteSpace(command.ProjectAlias),
            VaultRoot = StorageLocationPolicy.Normalize(settings.VaultRoot),
            ReleaseRoot = StorageLocationPolicy.Normalize(settings.ReleaseRoot)
        }, cancellationToken);
        await AuditAsync(actor, "project.child.create", nameof(Project), project.Id.ToString(), $"{project.Code} · {project.Name} · 数量{project.Quantity}", cancellationToken);
        return project;
    }

    public async Task<PdmCustomer> SaveCustomerAsync(Guid? customerId, string code, string name, bool isActive, string actor, UserRole role, CancellationToken cancellationToken)
    {
        RequireRole(role, UserRole.Administrator);
        code = code?.Trim().ToUpperInvariant() ?? string.Empty;
        name = name?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name))
            throw new PdmRuleException("客户编码和客户名称不能为空。");
        if (code.Length > 30 || name.Length > 200)
            throw new PdmRuleException("客户编码或名称超过允许长度。");
        if (code.Any(character => !char.IsLetterOrDigit(character) && character is not ('-' or '_')))
            throw new PdmRuleException("客户编码只能包含字母、数字、短横线和下划线。");
        var customer = await repository.SaveCustomerAsync(customerId, code, name, isActive, cancellationToken);
        await AuditAsync(actor, customerId is null ? "customer.create" : "customer.update", nameof(PdmCustomer), customer.Id.ToString(), $"{customer.Code} · {customer.Name} · {(customer.IsActive ? "启用" : "停用")}", cancellationToken);
        return customer;
    }

    public async Task<EquipmentTypeDefinition> SaveEquipmentTypeAsync(int code, string name, bool isActive, string actor, UserRole role, CancellationToken cancellationToken)
    {
        RequireRole(role, UserRole.Administrator);
        name = name?.Trim() ?? string.Empty;
        if (code is < 0 or > 99) throw new PdmRuleException("设备类型编码必须为0到99。");
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100) throw new PdmRuleException("设备类型名称不能为空且不能超过100个字符。");
        var equipmentType = await repository.SaveEquipmentTypeAsync(code, name, isActive, cancellationToken);
        await AuditAsync(actor, "equipment-type.update", nameof(EquipmentTypeDefinition), code.ToString("D2"), $"{code:D2} · {name} · {(isActive ? "启用" : "停用")}", cancellationToken);
        return equipmentType;
    }

    public async Task<PdmSystemSettings> UpdateSystemSettingsAsync(string vaultRoot, string releaseRoot, string actor, UserRole role, CancellationToken cancellationToken)
    {
        RequireRole(role, UserRole.Administrator);
        vaultRoot = StorageLocationPolicy.Normalize(vaultRoot);
        releaseRoot = StorageLocationPolicy.Normalize(releaseRoot);
        if (string.Equals(vaultRoot, releaseRoot, StringComparison.OrdinalIgnoreCase))
            throw new PdmRuleException("图档库与生产发包目录不能是同一位置。");
        var settings = await repository.UpdateSystemSettingsAsync(new(vaultRoot, releaseRoot), cancellationToken);
        await AuditAsync(actor, "system.storage.update", nameof(PdmSystemSettings), "storage", $"图档根目录：{settings.VaultRoot}；发包根目录：{settings.ReleaseRoot}", cancellationToken);
        return settings;
    }

    public async Task<Project> SetProjectResponsibleUsersAsync(Guid projectId, IReadOnlyList<string> usernames, string actor, UserRole role, CancellationToken cancellationToken)
    {
        RequireRole(role, UserRole.Administrator);
        var normalized = usernames.Select(username => username?.Trim() ?? string.Empty)
            .Where(username => username.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalized.Length == 0) throw new PdmRuleException("请至少选择一个项目负责人。");
        var activeUsers = await repository.ListUsersAsync(cancellationToken);
        if (normalized.Any(username => !activeUsers.Any(user => user.IsActive && string.Equals(user.Username, username, StringComparison.OrdinalIgnoreCase))))
            throw new PdmRuleException("负责人列表中包含不存在或已停用的账号。");
        var project = await repository.SetProjectResponsibleUsersAsync(projectId, normalized, cancellationToken);
        await AuditAsync(actor, "project.responsibles.update", nameof(Project), project.Id.ToString(), $"{project.Code} · {string.Join('、', normalized)}", cancellationToken);
        return project;
    }

    public async Task<Project> CreateProjectAsync(CreateProjectCommand command, string actor, UserRole role, CancellationToken cancellationToken)
    {
        RequireRole(role, UserRole.Engineer, UserRole.Administrator);
        var code = command.Code?.Trim() ?? string.Empty;
        var name = command.Name?.Trim() ?? string.Empty;
        var owner = command.Owner?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(code)
            || string.IsNullOrWhiteSpace(name)
            || string.IsNullOrWhiteSpace(owner)
            || string.IsNullOrWhiteSpace(command.VaultLocation)
            || string.IsNullOrWhiteSpace(command.ReleaseLocation))
        {
            throw new PdmRuleException("项目编码、项目名称、负责人和存储位置不能为空。");
        }

        if (code.Length > 80 || name.Length > 200 || owner.Length > 100)
        {
            throw new PdmRuleException("项目编码、名称或负责人超过允许长度。");
        }

        if (role != UserRole.Administrator && !string.Equals(owner, actor, StringComparison.OrdinalIgnoreCase))
        {
            throw new PdmRuleException("非管理员只能把自己设为项目负责人。");
        }

        if (code.Any(character => !char.IsLetterOrDigit(character) && character is not ('-' or '_' or '.')))
        {
            throw new PdmRuleException("项目编码只能包含字母、数字、短横线、下划线和点。");
        }

        var vaultLocation = StorageLocationPolicy.Normalize(command.VaultLocation);
        var releaseLocation = StorageLocationPolicy.Normalize(command.ReleaseLocation);
        if (string.Equals(vaultLocation, releaseLocation, StringComparison.OrdinalIgnoreCase))
        {
            throw new PdmRuleException("图档库与生产发包目录不能是同一位置。");
        }

        var project = await repository.CreateProjectAsync(
            new CreateProjectCommand(code, name, owner, vaultLocation, releaseLocation),
            actor,
            cancellationToken);
        await AuditAsync(actor, "project.create", nameof(Project), project.Id.ToString(), $"{project.Code} · {project.Name}", cancellationToken);
        return project;
    }

    public async Task<PdmDocument> RegisterDocumentAsync(RegisterDocumentCommand command, string actor, UserRole role, CancellationToken cancellationToken)
    {
        RequireRole(role, UserRole.Engineer, UserRole.Administrator);
        if (string.IsNullOrWhiteSpace(command.DrawingNumber)
            || string.IsNullOrWhiteSpace(command.Name)
            || string.IsNullOrWhiteSpace(command.FileName))
        {
            throw new PdmRuleException("图号、名称和文件名不能为空。");
        }

        if (command.Kind is not (DocumentKind.Assembly or DocumentKind.Part or DocumentKind.Drawing))
        {
            throw new PdmRuleException("只有SolidWorks装配体、零件和工程图可以登记。");
        }

        var project = await repository.FindProjectAsync(command.ProjectId, cancellationToken)
            ?? throw new PdmNotFoundException("项目不存在。");
        if (!project.IsActive)
        {
            throw new PdmConflictException("项目已停用，不能登记图档。");
        }

        var normalized = command with
        {
            DrawingNumber = command.DrawingNumber.Trim(),
            Name = command.Name.Trim(),
            FileName = Path.GetFileName(command.FileName.Trim())
        };
        var document = await repository.RegisterDocumentAsync(normalized, actor, cancellationToken);
        await AuditAsync(actor, "document.register", nameof(PdmDocument), document.Id.ToString(), document.FileName, cancellationToken);
        return document;
    }

    public async Task<PdmDocument> CheckoutAsync(Guid documentId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        RequireRole(role, UserRole.Engineer, UserRole.Administrator);
        await RequireDocumentReadAccessAsync(documentId, actor, role, cancellationToken);
        var document = await repository.FindDocumentAsync(documentId, cancellationToken)
            ?? throw new PdmNotFoundException("图档不存在。 ");

        if (document.State == DocumentLifecycleState.InReview)
        {
            throw new PdmConflictException("图档正在审批，不能获取编辑权限。 ");
        }

        if (document.CheckedOutBy is not null && !string.Equals(document.CheckedOutBy, actor, StringComparison.OrdinalIgnoreCase))
        {
            throw new PdmConflictException($"图档正在由{document.CheckedOutBy}编辑。 ");
        }

        var updated = await repository.CheckoutAsync(documentId, actor, cancellationToken);
        await AuditAsync(actor, "document.checkout", nameof(PdmDocument), documentId.ToString(), updated.Revision.Display, cancellationToken);
        return updated;
    }

    public async Task<DocumentCheckInResult> CheckInAsync(
        Guid documentId,
        string actor,
        UserRole role,
        StoredFile file,
        string changeNote,
        IReadOnlyDictionary<string, string?> properties,
        CadReferenceSnapshot snapshot,
        bool isProjectRoot,
        bool forceVersion,
        CancellationToken cancellationToken)
    {
        RequireRole(role, UserRole.Engineer, UserRole.Administrator);
        await RequireDocumentReadAccessAsync(documentId, actor, role, cancellationToken);
        var document = await repository.FindDocumentAsync(documentId, cancellationToken)
            ?? throw new PdmNotFoundException("图档不存在。 ");

        if (snapshot.ProjectId != document.ProjectId || snapshot.RootDocumentId != documentId)
        {
            throw new PdmRuleException("引用树快照必须属于当前项目和当前图档。");
        }

        if (!string.Equals(document.CheckedOutBy, actor, StringComparison.OrdinalIgnoreCase))
        {
            throw new PdmConflictException("只有当前编辑人员可以提交存档。 ");
        }

        if (string.IsNullOrWhiteSpace(changeNote))
        {
            throw new PdmRuleException("请输入本次变更内容后再提交存档。");
        }

        if (snapshot.Root.HasBlockingIssue)
        {
            throw new PdmRuleException("结构树存在缺失引用，不能提交存档。 ");
        }

        var project = await repository.FindProjectAsync(document.ProjectId, cancellationToken)
            ?? throw new PdmNotFoundException("项目不存在。");
        await fileStorage.VerifyStoredFileAsync(project, file, cancellationToken);
        var mechanical = await repository.GetBomAsync(document.ProjectId, BomKind.Mechanical, cancellationToken);
        var electrical = await repository.GetBomAsync(document.ProjectId, BomKind.Electrical, cancellationToken);
        var result = await repository.CheckInVersionAsync(
            documentId,
            actor,
            new DocumentVersionCommit(
                file,
                changeNote.Trim(),
                properties,
                snapshot,
                mechanical,
                electrical,
                IsProjectRoot: isProjectRoot,
                ForceVersion: forceVersion),
            cancellationToken);
        if (result.VersionCreated && result.Version is not null)
        {
            await AuditAsync(actor, "document.checkin", nameof(DocumentVersion), result.Version.Id.ToString(), result.Version.Revision.Display, cancellationToken);
        }
        else
        {
            await AuditAsync(actor, "document.edit.complete-unchanged", nameof(PdmDocument), documentId.ToString(), result.Document.Revision.Display, cancellationToken);
        }
        return result;
    }

    public async Task<PdmDocument> CompleteEditWithoutChangesAsync(Guid documentId, string actor, UserRole role, string sha256, CancellationToken cancellationToken)
    {
        RequireRole(role, UserRole.Engineer, UserRole.Administrator);
        await RequireDocumentReadAccessAsync(documentId, actor, role, cancellationToken);
        if (string.IsNullOrWhiteSpace(sha256))
        {
            throw new PdmRuleException("文件指纹不能为空。");
        }

        var document = await repository.CompleteEditWithoutChangesAsync(documentId, actor, sha256.Trim(), cancellationToken);
        await AuditAsync(actor, "document.edit.complete-unchanged", nameof(PdmDocument), documentId.ToString(), document.Revision.Display, cancellationToken);
        return document;
    }

    public async Task<PdmDocument> DiscardCheckoutAsync(Guid documentId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        RequireRole(role, UserRole.Engineer, UserRole.Administrator);
        await RequireDocumentReadAccessAsync(documentId, actor, role, cancellationToken);
        var document = await repository.DiscardCheckoutAsync(documentId, actor, cancellationToken);
        await AuditAsync(actor, "document.checkout.discard", nameof(PdmDocument), documentId.ToString(), document.Revision.Display, cancellationToken);
        return document;
    }

    public async Task<(PdmDocument Document, DocumentVersion Version)> RestoreVersionAsync(
        Guid documentId,
        Guid sourceVersionId,
        string actor,
        UserRole role,
        string changeNote,
        CancellationToken cancellationToken)
    {
        RequireRole(role, UserRole.Engineer, UserRole.Administrator);
        var document = await RequireDocumentAsync(documentId, cancellationToken);
        await RequireDocumentReadAccessAsync(documentId, actor, role, cancellationToken);
        var source = await repository.FindDocumentVersionAsync(documentId, sourceVersionId, cancellationToken)
            ?? throw new PdmNotFoundException("历史版本不存在。");
        var project = await repository.FindProjectAsync(document.ProjectId, cancellationToken)
            ?? throw new PdmNotFoundException("项目不存在。");
        var restoredPath = Path.Combine(".versions", document.Id.ToString("N"), Guid.NewGuid().ToString("N"), document.FileName);
        var restoredFile = await fileStorage.CopyVersionAsync(
            project,
            new StoredFile(source.StorageRelativePath, source.FileLength, source.Sha256, source.CreatedAt),
            restoredPath,
            cancellationToken);
        var result = await repository.RestoreVersionAsync(documentId, sourceVersionId, actor, restoredFile, changeNote, cancellationToken);
        await AuditAsync(actor, "document.version.restore", nameof(DocumentVersion), sourceVersionId.ToString(), $"生成{result.Version.Revision.Display}", cancellationToken);
        return result;
    }

    public async Task<DocumentVersionComparison> CompareVersionsAsync(Guid documentId, Guid leftVersionId, Guid rightVersionId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        RequireDocumentReadRole(role);
        await RequireDocumentReadAccessAsync(documentId, actor, role, cancellationToken);
        _ = await RequireDocumentAsync(documentId, cancellationToken);
        var left = await repository.FindDocumentVersionAsync(documentId, leftVersionId, cancellationToken)
            ?? throw new PdmNotFoundException("左侧历史版本不存在。");
        var right = await repository.FindDocumentVersionAsync(documentId, rightVersionId, cancellationToken)
            ?? throw new PdmNotFoundException("右侧历史版本不存在。");
        var comparison = DocumentVersionDiff.Compare(left, right);
        await AuditAsync(actor, "document.version.compare", nameof(PdmDocument), documentId.ToString(), $"{left.Revision.Display} -> {right.Revision.Display}", cancellationToken);
        return comparison;
    }

    public async Task<DocumentVersion> PublishVersionAsync(Guid documentId, Guid sourceVersionId, Guid releasePackageId, Guid approvalTaskId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        RequireRole(role, UserRole.Approver, UserRole.Administrator);
        await RequireDocumentReadAccessAsync(documentId, actor, role, cancellationToken);
        _ = await RequireDocumentAsync(documentId, cancellationToken);
        var version = await repository.PublishDocumentVersionAsync(documentId, sourceVersionId, releasePackageId, approvalTaskId, actor, cancellationToken);
        await AuditAsync(actor, "document.version.publish", nameof(DocumentVersion), version.Id.ToString(), version.Revision.Display, cancellationToken);
        return version;
    }

    public async Task AuditVersionReadAsync(Guid documentId, Guid versionId, string actor, UserRole role, string action, CancellationToken cancellationToken)
    {
        RequireDocumentReadRole(role);
        await RequireDocumentReadAccessAsync(documentId, actor, role, cancellationToken);
        _ = await RequireDocumentAsync(documentId, cancellationToken);
        if (versionId != Guid.Empty)
        {
            _ = await repository.FindDocumentVersionAsync(documentId, versionId, cancellationToken)
                ?? throw new PdmNotFoundException("历史版本不存在。");
        }
        await AuditAsync(actor, action, nameof(DocumentVersion), versionId.ToString(), documentId.ToString(), cancellationToken);
    }

    public async Task<ControlledOpenManifest> CreateControlledOpenManifestAsync(
        Guid documentId,
        Guid? versionId,
        bool releasedOnly,
        bool forEdit,
        string actor,
        UserRole role,
        CancellationToken cancellationToken)
    {
        await RequireDocumentReadAccessAsync(documentId, actor, role, cancellationToken);
        if (forEdit)
        {
            RequireRole(role, UserRole.Engineer, UserRole.Administrator);
            if (releasedOnly || versionId.HasValue)
            {
                throw new PdmRuleException("编辑模式只能获取当前最新受控版本；历史版和正式版只能只读打开。");
            }
        }

        var rootDocument = await repository.FindDocumentAsync(documentId, cancellationToken)
            ?? throw new PdmNotFoundException("图档不存在。");
        var project = await repository.FindProjectAsync(rootDocument.ProjectId, cancellationToken)
            ?? throw new PdmNotFoundException("项目不存在。");
        var rootVersions = await repository.ListDocumentVersionsAsync(documentId, cancellationToken);
        var rootVersion = versionId.HasValue
            ? rootVersions.SingleOrDefault(item => item.Id == versionId.Value)
            : releasedOnly
                ? rootVersions.FirstOrDefault(item => item.Status == DocumentVersionStatus.Released)
                : rootVersions.FirstOrDefault(item => item.Revision.Display.Equals(rootDocument.Revision.Display, StringComparison.OrdinalIgnoreCase))
                    ?? rootVersions.FirstOrDefault();
        if (rootVersion is null)
        {
            throw new PdmNotFoundException(releasedOnly ? "该图档尚无正式发布版本。" : "该图档尚无可打开的受控版本。");
        }

        if (forEdit
            && rootDocument.CheckedOutBy is not null
            && !rootDocument.CheckedOutBy.Equals(actor, StringComparison.OrdinalIgnoreCase))
        {
            throw new PdmConflictException($"图档正在由{rootDocument.CheckedOutBy}编辑。");
        }

        var files = new List<ControlledOpenFile>();
        var filesByDocument = new Dictionary<Guid, ControlledOpenFile>();
        var fileNames = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var allowCurrentVersionFallback = !versionId.HasValue && !releasedOnly;
        var currentProjectDocuments = allowCurrentVersionFallback
            ? await repository.ListDocumentsAsync(project.Id, cancellationToken)
            : Array.Empty<PdmDocument>();
        var nodes = FlattenOpenNodes(rootVersion.ReferenceSnapshot).ToArray();
        for (var index = 0; index < nodes.Length; index++)
        {
            var node = nodes[index];
            var isRoot = index == 0;
            if (!isRoot && node.Kind == DocumentKind.Drawing)
            {
                continue;
            }
            if (node.Status == ReferenceNodeStatus.Missing)
            {
                throw new PdmRuleException($"引用文件{node.FileName}缺失，不能生成完整打开清单。");
            }
            if (node.Status == ReferenceNodeStatus.Virtual)
            {
                continue;
            }
            Guid referencedDocumentId;
            if (node.DocumentId.HasValue)
            {
                referencedDocumentId = node.DocumentId.Value;
            }
            else
            {
                if (!allowCurrentVersionFallback)
                {
                    throw new PdmRuleException($"引用文件{node.FileName}尚未登记，不能生成完整打开清单。");
                }

                var referencedFileName = Path.GetFileName(node.FileName);
                var matches = currentProjectDocuments
                    .Where(document => document.FileName.Equals(referencedFileName, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (matches.Length == 0)
                {
                    throw new PdmRuleException($"引用文件{node.FileName}尚未登记，不能生成完整打开清单。");
                }
                if (matches.Length > 1)
                {
                    throw new PdmRuleException($"项目中存在多个同名图档{node.FileName}，不能安全关联。");
                }

                referencedDocumentId = matches[0].Id;
            }

            DocumentVersion version;
            if (isRoot)
            {
                if (referencedDocumentId != documentId)
                {
                    throw new PdmRuleException("版本引用快照的根图档与所选图档不一致。");
                }
                version = rootVersion;
            }
            else
            {
                var versions = await repository.ListDocumentVersionsAsync(referencedDocumentId, cancellationToken);
                if (node.Revision is null)
                {
                    if (versionId.HasValue || releasedOnly)
                    {
                        throw new PdmRuleException($"引用文件{node.FileName}的快照未记录版本，不能用当前最新版本替代。");
                    }

                    version = versions.FirstOrDefault()
                        ?? throw new PdmNotFoundException($"引用文件{node.FileName}尚无可用的最新受控版本。");
                }
                else
                {
                    var referencedRevision = node.Revision.GetValueOrDefault().Display;
                    version = versions.FirstOrDefault(item => item.Revision.Display.Equals(referencedRevision, StringComparison.OrdinalIgnoreCase))
                        ?? throw new PdmNotFoundException($"引用文件{node.FileName}的受控版本{referencedRevision}不存在。");
                }
            }

            if (filesByDocument.TryGetValue(referencedDocumentId, out var existing))
            {
                if (existing.VersionId != version.Id)
                {
                    throw new PdmRuleException($"同一图档{node.FileName}在快照中引用了不同版本，不能安全打开。");
                }
                continue;
            }

            var fileName = Path.GetFileName(node.FileName);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                throw new PdmRuleException("引用快照包含无效文件名。");
            }
            if (fileNames.TryGetValue(fileName, out var conflictingDocumentId) && conflictingDocumentId != referencedDocumentId)
            {
                throw new PdmRuleException($"项目中存在同名文件{fileName}，不能放入同一受控工作区。");
            }

            await fileStorage.VerifyStoredFileAsync(
                project,
                new StoredFile(version.StorageRelativePath, version.FileLength, version.Sha256, version.CreatedAt),
                cancellationToken);
            var item = new ControlledOpenFile(
                referencedDocumentId,
                version.Id,
                version.Revision.Display,
                fileName,
                fileName,
                version.FileLength,
                version.Sha256,
                node.Configuration,
                isRoot);
            files.Add(item);
            filesByDocument.Add(referencedDocumentId, item);
            fileNames[fileName] = referencedDocumentId;
        }

        var rootFile = files.Single(item => item.IsRoot);
        var manifest = new ControlledOpenManifest(
            Guid.NewGuid(),
            project.Id,
            project.Code,
            rootDocument.Id,
            rootVersion.Id,
            rootVersion.Revision.Display,
            rootFile.RelativePath,
            forEdit,
            files);
        await AuditAsync(
            actor,
            forEdit ? "document.open-manifest.edit" : releasedOnly ? "document.open-manifest.released" : "document.open-manifest.readonly",
            nameof(PdmDocument),
            documentId.ToString(),
            $"{rootVersion.Revision.Display}; files={files.Count}",
            cancellationToken);
        return manifest;
    }

    private static IEnumerable<DocumentReferenceNode> FlattenOpenNodes(DocumentReferenceNode root)
    {
        var pending = new Stack<DocumentReferenceNode>();
        pending.Push(root);
        while (pending.TryPop(out var node))
        {
            yield return node;
            for (var index = node.Children.Count - 1; index >= 0; index--)
            {
                pending.Push(node.Children[index]);
            }
        }
    }

    public async Task<ReleasePackage> CreateReleasePackageAsync(
        Guid projectId,
        Guid? referenceSnapshotId,
        string number,
        string processReviewer,
        string approver,
        string actor,
        UserRole role,
        CancellationToken cancellationToken)
    {
        RequireRole(role, UserRole.Engineer, UserRole.Administrator);
        var project = await repository.FindProjectAsync(projectId, cancellationToken)
            ?? throw new PdmNotFoundException("项目不存在。 ");

        if (string.IsNullOrWhiteSpace(number) || number.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new PdmRuleException("发布包编号不能为空，且不能包含文件名非法字符。");
        }

        if (string.IsNullOrWhiteSpace(processReviewer) || string.IsNullOrWhiteSpace(approver))
        {
            throw new PdmRuleException("必须指定工艺审核人和批准人。");
        }

        var snapshot = await repository.GetLatestReferenceSnapshotAsync(projectId, cancellationToken)
            ?? throw new PdmRuleException("项目尚无已存档的引用树快照，不能创建发布包。");
        if (referenceSnapshotId.HasValue && referenceSnapshotId.Value != Guid.Empty && referenceSnapshotId.Value != snapshot.SnapshotId)
        {
            throw new PdmConflictException("指定的引用树快照不是项目当前最新快照，请刷新后重试。");
        }

        var mechanical = await repository.GetBomAsync(projectId, BomKind.Mechanical, cancellationToken);
        var electrical = await repository.GetBomAsync(projectId, BomKind.Electrical, cancellationToken);
        if (mechanical.Count == 0 || electrical.Count == 0 || mechanical.Concat(electrical).Any(item => !item.IsComplete))
        {
            throw new PdmRuleException("机械BOM和电气BOM必须存在且完整。 ");
        }

        var packageId = Guid.NewGuid();
        var tasks = new[]
        {
            new ApprovalTask(Guid.NewGuid(), packageId, ApprovalStage.ProcessReview, processReviewer, null, null, null, null),
            new ApprovalTask(Guid.NewGuid(), packageId, ApprovalStage.Approval, approver, null, null, null, null)
        };
        var package = new ReleasePackage(
            packageId,
            projectId,
            number,
            ReleasePackageState.Draft,
            snapshot.SnapshotId,
            BomRevision("M", mechanical),
            BomRevision("E", electrical),
            tasks,
            timeProvider.GetUtcNow(),
            null,
            null)
        {
            MechanicalBomSnapshot = mechanical.ToArray(),
            ElectricalBomSnapshot = electrical.ToArray()
        };

        var created = await repository.CreateReleasePackageAsync(package, cancellationToken);
        await publisher.PrepareAsync(created, project, cancellationToken);
        await AuditAsync(actor, "release-package.create", nameof(ReleasePackage), packageId.ToString(), number, cancellationToken);
        return created;
    }

    public async Task<IReadOnlyList<BomItem>> ReplaceBomAsync(
        Guid projectId,
        BomKind kind,
        IReadOnlyList<BomItemInput> inputs,
        string actor,
        UserRole role,
        CancellationToken cancellationToken)
    {
        RequireRole(role, UserRole.Engineer, UserRole.Administrator);
        _ = await repository.FindProjectAsync(projectId, cancellationToken)
            ?? throw new PdmNotFoundException("项目不存在。");
        if (inputs.Count == 0)
        {
            throw new PdmRuleException("BOM至少需要一条物料。");
        }

        var duplicateSequence = inputs.GroupBy(item => item.Sequence).FirstOrDefault(group => group.Count() > 1);
        if (duplicateSequence is not null)
        {
            throw new PdmRuleException($"BOM序号{duplicateSequence.Key}重复。");
        }

        var duplicateDrawing = inputs.GroupBy(item => item.DrawingNumber.Trim(), StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
        if (duplicateDrawing is not null)
        {
            throw new PdmRuleException($"BOM图号{duplicateDrawing.Key}重复。");
        }

        var items = inputs.OrderBy(item => item.Sequence).Select(input =>
        {
            if (input.Sequence <= 0 || input.Quantity <= 0 || string.IsNullOrWhiteSpace(input.DrawingNumber)
                || string.IsNullOrWhiteSpace(input.Name) || string.IsNullOrWhiteSpace(input.Unit)
                || string.IsNullOrWhiteSpace(input.Revision))
            {
                throw new PdmRuleException("BOM序号、图号、名称、数量、单位和版本必须有效。");
            }

            return new BomItem(
                Guid.NewGuid(), projectId, kind, input.Sequence, input.DrawingNumber.Trim(), input.Name.Trim(), input.Quantity,
                input.Unit.Trim(), NullIfWhiteSpace(input.Material), NullIfWhiteSpace(input.Specification), input.Revision.Trim(), input.IsComplete);
        }).ToArray();

        var saved = await repository.ReplaceBomAsync(projectId, kind, items, cancellationToken);
        await AuditAsync(actor, "bom.replace", nameof(BomItem), projectId.ToString(), $"{kind}:{saved.Count}", cancellationToken);
        return saved;
    }

    public async Task<ReleasePackage> SubmitReleasePackageAsync(Guid releasePackageId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        RequireRole(role, UserRole.Engineer, UserRole.Administrator);
        var package = await repository.FindReleasePackageAsync(releasePackageId, cancellationToken)
            ?? throw new PdmNotFoundException("发布包不存在。");
        var project = await repository.FindProjectAsync(package.ProjectId, cancellationToken)
            ?? throw new PdmNotFoundException("发布包对应的项目不存在。");
        await publisher.ValidateAsync(package, project, cancellationToken);
        var submitted = await repository.SubmitReleasePackageAsync(releasePackageId, actor, cancellationToken);
        await AuditAsync(actor, package.State == ReleasePackageState.Rejected ? "release-package.resubmit" : "release-package.submit", nameof(ReleasePackage), package.Id.ToString(), package.Number, cancellationToken);
        return submitted;
    }

    public async Task<ReleasePackage> DecideAsync(Guid taskId, string actor, UserRole role, ApprovalDecision decision, string? comment, CancellationToken cancellationToken)
    {
        if (role is not (UserRole.ProcessReviewer or UserRole.Approver or UserRole.Administrator))
        {
            throw new PdmRuleException("当前角色不能处理审批。 ");
        }

        var package = await repository.DecideApprovalAsync(taskId, actor, decision, comment, cancellationToken);
        await AuditAsync(actor, "approval.decide", nameof(ApprovalTask), taskId.ToString(), decision.ToString(), cancellationToken);

        if (package.State != ReleasePackageState.Publishing)
        {
            return package;
        }

        var project = await repository.FindProjectAsync(package.ProjectId, cancellationToken)
            ?? throw new PdmNotFoundException("发布包对应的项目不存在。 ");
        string publishedPath;
        try
        {
            publishedPath = await publisher.PublishAsync(package, project, cancellationToken);
        }
        catch (Exception exception)
        {
            await repository.MarkPublishFailedAsync(package.Id, exception.Message, cancellationToken);
            await AuditAsync(actor, "release-package.publish-failed", nameof(ReleasePackage), package.Id.ToString(), exception.Message, cancellationToken);
            throw;
        }

        var publishedAt = timeProvider.GetUtcNow();
        var releasedVersions = await repository.PublishReleasePackageVersionsAsync(package.Id, package.ApprovalTasks.Single(task => task.Stage == ApprovalStage.Approval).Id, actor, cancellationToken);
        foreach (var version in releasedVersions)
        {
            await AuditAsync(actor, "document.version.publish", nameof(DocumentVersion), version.Id.ToString(), version.Revision.Display, cancellationToken);
        }
        await repository.MarkPublishedAsync(package.Id, publishedPath, publishedAt, cancellationToken);
        await AuditAsync(actor, "release-package.publish", nameof(ReleasePackage), package.Id.ToString(), publishedPath, cancellationToken);
        return (await repository.FindReleasePackageAsync(package.Id, cancellationToken))!;
    }

    private static void RequireRole(UserRole actual, params UserRole[] allowed)
    {
        if (!allowed.Contains(actual))
        {
            throw new PdmRuleException("当前角色无权执行此操作。 ");
        }
    }

    private async Task<PdmDocument> RequireDocumentAsync(Guid documentId, CancellationToken cancellationToken) =>
        await repository.FindDocumentAsync(documentId, cancellationToken) ?? throw new PdmNotFoundException("图档不存在。");

    private static void RequireDocumentReadRole(UserRole role) =>
        RequireRole(role, UserRole.Engineer, UserRole.ProcessReviewer, UserRole.Approver, UserRole.ProductionViewer, UserRole.Administrator);

    private async Task RequireDocumentReadAccessAsync(Guid documentId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        if (!await repository.HasDocumentReadAccessAsync(documentId, actor, role, cancellationToken))
            throw new UnauthorizedAccessException("当前用户没有该项目或图档的读取权限。");
    }

    private Task AuditAsync(string actor, string action, string entityType, string entityId, string detail, CancellationToken cancellationToken) =>
        repository.AppendAuditAsync(new AuditEntry(Guid.NewGuid(), timeProvider.GetUtcNow(), actor, action, entityType, entityId, detail), cancellationToken);

    private static string BomRevision(string prefix, IReadOnlyList<BomItem> items)
    {
        var json = JsonSerializer.Serialize(items, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return $"{prefix}-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..8]}";
    }

    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void ValidateProjectDetails(string? name, string? projectAlias, int quantity)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new PdmRuleException("项目名称不能为空。");
        if (name.Trim().Length > 200 || projectAlias?.Trim().Length > 200) throw new PdmRuleException("项目名称或项目别名超过允许长度。");
        if (quantity is < 1 or > 10000) throw new PdmRuleException("数量必须在1到10000之间。");
    }
}
