using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed class PdmWorkflowService(IPdmRepository repository, IFileStorage fileStorage, IReleasePackagePublisher publisher, TimeProvider timeProvider)
{
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
            new DocumentVersionCommit(file, changeNote.Trim(), properties, snapshot, mechanical, electrical),
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
}
