using System.Data.Common;
using System.Text.Json;
using Dapper;
using MySqlConnector;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed partial class MySqlPdmRepository
{
    public async Task<IReadOnlyList<DocumentVersion>> ListDocumentVersionsAsync(Guid documentId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<DocumentVersionRow>(new CommandDefinition(VersionSelect + " WHERE document_id = @DocumentId ORDER BY created_at DESC", new { DocumentId = documentId }, cancellationToken: cancellationToken));
        return rows.Select(MapDocumentVersion).ToArray();
    }

    public async Task<IReadOnlyList<DocumentVersion>> ListProjectDocumentVersionsAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<DocumentVersionRow>(new CommandDefinition(
            VersionSelect + " WHERE document_id IN (SELECT id FROM document WHERE project_id=@ProjectId)",
            new { ProjectId = projectId },
            cancellationToken: cancellationToken));
        return rows.Select(MapDocumentVersion).ToArray();
    }

    public async Task<DocumentVersion?> FindDocumentVersionAsync(Guid documentId, Guid versionId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await FindDocumentVersionAsync(connection, null, documentId, versionId, cancellationToken);
    }

    public async Task<DocumentCheckInResult> CheckInVersionAsync(Guid documentId, string actor, DocumentVersionCommit commit, CancellationToken cancellationToken)
    {
        var document = await FindDocumentAsync(documentId, cancellationToken) ?? throw new PdmNotFoundException("图档不存在。");
        if (document.CheckoutSessionId is null) throw new PdmConflictException("当前编辑权限没有有效会话，请重新获取权限。");
        return await CheckInVersionAsync(documentId, actor, document.CheckoutSessionId.Value, commit, null, cancellationToken);
    }

    public async Task<DocumentCheckInResult> CheckInVersionAsync(Guid documentId, string actor, Guid sessionId, DocumentVersionCommit commit, CancellationToken cancellationToken)
        => await CheckInVersionAsync(documentId, actor, sessionId, commit, null, cancellationToken);

    public async Task<DocumentCheckInResult> CheckInVersionAsync(Guid documentId, string actor, Guid sessionId, DocumentVersionCommit commit, Guid? drawingReviewWritebackId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var locked = await LockDocumentAsync(connection, transaction, documentId, cancellationToken);
        if (!string.Equals(locked.CheckedOutBy, actor, StringComparison.OrdinalIgnoreCase) || locked.CheckoutSessionId != sessionId)
            throw new PdmConflictException("编辑会话已经失效，不能提交存档。请另存本地修改或重新获取权限。");
        var drawingReviewLocked = await IsDocumentUnderActiveDrawingReviewAsync(connection, transaction, documentId, cancellationToken);
        var controlledWriteback = drawingReviewWritebackId.HasValue
            && await IsActiveDrawingReviewWritebackAsync(connection, transaction, documentId, drawingReviewWritebackId.Value, cancellationToken);
        if (drawingReviewLocked && !controlledWriteback)
            throw new PdmConflictException("图档正在进行图纸审核，不能提交存档。");

        var latestFile = await connection.QuerySingleOrDefaultAsync<LatestVersionFingerprintRow>(new CommandDefinition(
            """
            SELECT sha256, JSON_UNQUOTE(JSON_EXTRACT(property_snapshot_json, '$.SourceFileSha256')) AS SourceFileSha256
            FROM document_version
            WHERE document_id=@DocumentId
            ORDER BY created_at DESC
            LIMIT 1
            """,
            new { DocumentId = documentId }, transaction, cancellationToken: cancellationToken));
        commit.Properties.TryGetValue("SourceFileSha256", out var sourceFileSha256);
        var sameFile = latestFile is not null
            && (string.Equals(latestFile.Sha256, commit.File.Sha256, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(latestFile.SourceFileSha256)
                    && !string.IsNullOrWhiteSpace(sourceFileSha256)
                    && string.Equals(latestFile.SourceFileSha256, sourceFileSha256, StringComparison.OrdinalIgnoreCase)));
        if (!commit.ForceVersion
            && sameFile)
        {
            if (commit.IsProjectRoot)
            {
                await InsertReferenceSnapshotAsync(connection, transaction, commit.ReferenceSnapshot, cancellationToken);
                await SetProjectReferenceRootAsync(connection, transaction, commit.ReferenceSnapshot, cancellationToken);
            }

            var unchanged = await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE document SET drawing_number=COALESCE(@DrawingNumber,drawing_number),name=COALESCE(@Name,name),file_name=COALESCE(@FileName,file_name),
                    source_fingerprint_sha256=COALESCE(@SourceFileSha256,source_fingerprint_sha256),
                    checked_out_by=NULL,checked_out_at=NULL,checkout_session_id=NULL,checkout_machine=NULL,
                    checkout_last_heartbeat_at=NULL,checkout_lease_expires_at=NULL,checkout_release_requested_by=NULL,
                    checkout_release_requested_at=NULL,checkout_release_request_reason=NULL,updated_at=@Now,row_version=row_version+1
                WHERE id=@DocumentId AND row_version=@RowVersion AND checked_out_by=@Actor AND checkout_session_id=@SessionId
                """,
                new { DocumentId = documentId, Actor = actor, SessionId = sessionId, RowVersion = locked.RowVersion, Now = timeProvider.GetUtcNow().UtcDateTime, commit.DrawingNumber, commit.Name, commit.FileName, SourceFileSha256 = sourceFileSha256 }, transaction, cancellationToken: cancellationToken));
            if (unchanged != 1) throw new PdmConflictException("图档编辑状态已经变化，请刷新后重试。");
            var unchangedDocument = await FindDocumentAsync(connection, transaction, documentId, cancellationToken) ?? throw new PdmNotFoundException("图档不存在。");
            await transaction.CommitAsync(cancellationToken);
            return new DocumentCheckInResult(unchangedDocument, null, false);
        }

        var nextRevision = await NextWorkRevisionAsync(connection, transaction, locked, cancellationToken);
        // 受控属性回写只写入属性、不修改几何；下游据此把版本变化与真实内容修改区分开。
        var changeKind = controlledWriteback ? DocumentVersionChangeKind.PropertyWriteback : commit.ChangeKind;
        var version = CreateVersion(documentId, nextRevision, actor, commit with { ChangeKind = changeKind }, timeProvider.GetUtcNow(), DocumentVersionStatus.Work);
        await InsertVersionAsync(connection, transaction, version, cancellationToken);
        await InsertReferenceSnapshotAsync(connection, transaction, commit.ReferenceSnapshot, cancellationToken);
        if (commit.IsProjectRoot)
        {
            await SetProjectReferenceRootAsync(connection, transaction, commit.ReferenceSnapshot, cancellationToken);
        }

        var affected = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE document SET drawing_number=COALESCE(@DrawingNumber,drawing_number),name=COALESCE(@Name,name),file_name=COALESCE(@FileName,file_name),
                source_fingerprint_sha256=COALESCE(@SourceFileSha256,source_fingerprint_sha256),
                revision_label=@Revision,lifecycle_state='Work',checked_out_by=NULL,checked_out_at=NULL,
                checkout_session_id=NULL,checkout_machine=NULL,checkout_last_heartbeat_at=NULL,checkout_lease_expires_at=NULL,
                checkout_release_requested_by=NULL,checkout_release_requested_at=NULL,checkout_release_request_reason=NULL,
                updated_at=@Now,row_version=row_version+1
            WHERE id=@DocumentId AND row_version=@RowVersion AND checked_out_by=@Actor AND checkout_session_id=@SessionId
            """,
            new { DocumentId = documentId, Revision = nextRevision.Display, Actor = actor, SessionId = sessionId, RowVersion = locked.RowVersion, Now = version.CreatedAt.UtcDateTime, commit.DrawingNumber, commit.Name, commit.FileName, SourceFileSha256 = sourceFileSha256 }, transaction, cancellationToken: cancellationToken));
        if (affected != 1) throw new PdmConflictException("图档已被其他存档操作更新，本次存档未生效。");
        var document = await FindDocumentAsync(connection, transaction, documentId, cancellationToken) ?? throw new PdmNotFoundException("图档不存在。");
        await transaction.CommitAsync(cancellationToken);
        return new DocumentCheckInResult(document, version, true);
    }

    public async Task<(PdmDocument Document, DocumentVersion Version)> RestoreVersionAsync(Guid documentId, Guid sourceVersionId, string actor, StoredFile restoredFile, string changeNote, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var locked = await LockDocumentAsync(connection, transaction, documentId, cancellationToken);
        if (locked.CheckedOutBy is not null && !string.Equals(locked.CheckedOutBy, actor, StringComparison.OrdinalIgnoreCase))
            throw new PdmConflictException($"图档正在由{locked.CheckedOutBy}编辑。");
        var source = await FindDocumentVersionAsync(connection, transaction, documentId, sourceVersionId, cancellationToken) ?? throw new PdmNotFoundException("历史版本不存在。");
        var nextRevision = await NextWorkRevisionAsync(connection, transaction, locked, cancellationToken);
        var version = source with
        {
            Id = Guid.NewGuid(), Revision = nextRevision, Status = DocumentVersionStatus.Work,
            StorageRelativePath = restoredFile.RelativePath, FileLength = restoredFile.Length, Sha256 = restoredFile.Sha256,
            CreatedBy = actor, CreatedAt = timeProvider.GetUtcNow(), ChangeNote = changeNote,
            SourceVersionId = source.Id, SourceDescription = $"由{source.Revision.Display}恢复生成{nextRevision.Display}",
            ApprovalTaskId = null, ReleasePackageId = null, Preview = null
        };
        await InsertVersionAsync(connection, transaction, version, cancellationToken);
        source.PropertySnapshot.TryGetValue("SourceFileSha256", out var restoredSourceSha256);
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE document SET revision_label=@Revision,lifecycle_state='Work',source_fingerprint_sha256=COALESCE(@SourceFileSha256,source_fingerprint_sha256),checked_out_by=NULL,checked_out_at=NULL,checkout_session_id=NULL,checkout_machine=NULL,checkout_last_heartbeat_at=NULL,checkout_lease_expires_at=NULL,checkout_release_requested_by=NULL,checkout_release_requested_at=NULL,checkout_release_request_reason=NULL,updated_at=@Now,row_version=row_version+1 WHERE id=@DocumentId AND row_version=@RowVersion",
            new { DocumentId = documentId, Revision = nextRevision.Display, RowVersion = locked.RowVersion, Now = version.CreatedAt.UtcDateTime, SourceFileSha256 = restoredSourceSha256 }, transaction, cancellationToken: cancellationToken));
        if (affected != 1) throw new PdmConflictException("图档已被其他恢复或存档操作更新，本次恢复未生效。");
        var document = await FindDocumentAsync(connection, transaction, documentId, cancellationToken) ?? throw new PdmNotFoundException("图档不存在。");
        await transaction.CommitAsync(cancellationToken);
        return (document, version);
    }

    public async Task<DocumentVersion> PublishDocumentVersionAsync(Guid documentId, Guid sourceVersionId, Guid releasePackageId, Guid approvalTaskId, string actor, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var locked = await LockDocumentAsync(connection, transaction, documentId, cancellationToken);
        var source = await FindDocumentVersionAsync(connection, transaction, documentId, sourceVersionId, cancellationToken) ?? throw new PdmNotFoundException("待发布工作版本不存在。");
        if (source.Status != DocumentVersionStatus.Work) throw new PdmConflictException("只能从工作版本生成正式版本。");
        if (!string.Equals(source.Revision.Display, locked.RevisionLabel, StringComparison.OrdinalIgnoreCase))
            throw new PdmConflictException("只能发布图档当前最新的工作版本。");
        var packageState = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition("SELECT state FROM release_package WHERE id=@ReleasePackageId FOR UPDATE", new { ReleasePackageId = releasePackageId }, transaction, cancellationToken: cancellationToken));
        if (packageState is null) throw new PdmNotFoundException("发布包不存在。");
        if (packageState is not ("Publishing" or "Published")) throw new PdmConflictException("发布包尚未审批通过，不能生成正式版本。");
        var taskMatches = await connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM approval_task WHERE id=@ApprovalTaskId AND release_package_id=@ReleasePackageId AND decision_value='Approved'", new { ApprovalTaskId = approvalTaskId, ReleasePackageId = releasePackageId }, transaction, cancellationToken: cancellationToken));
        if (taskMatches != 1) throw new PdmConflictException("最终批准记录与发布包不匹配或尚未批准。");
        var releasedRevision = source.Revision.Release();
        var released = source with
        {
            Id = Guid.NewGuid(), Revision = releasedRevision, Status = DocumentVersionStatus.Released,
            CreatedBy = actor, CreatedAt = timeProvider.GetUtcNow(), ChangeNote = $"审批发布{releasedRevision.Display}",
            SourceVersionId = source.Id, SourceDescription = $"由{source.Revision.Display}审批发布",
            ApprovalTaskId = approvalTaskId, ReleasePackageId = releasePackageId
        };
        await InsertVersionAsync(connection, transaction, released, cancellationToken);
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE document SET revision_label=@Revision,lifecycle_state='Released',checked_out_by=NULL,checked_out_at=NULL,checkout_session_id=NULL,checkout_machine=NULL,checkout_last_heartbeat_at=NULL,checkout_lease_expires_at=NULL,checkout_release_requested_by=NULL,checkout_release_requested_at=NULL,checkout_release_request_reason=NULL,updated_at=@Now,row_version=row_version+1 WHERE id=@DocumentId AND row_version=@RowVersion",
            new { DocumentId = documentId, Revision = releasedRevision.Display, RowVersion = locked.RowVersion, Now = released.CreatedAt.UtcDateTime }, transaction, cancellationToken: cancellationToken));
        if (affected != 1) throw new PdmConflictException("图档版本已变化，不能重复发布。");
        await transaction.CommitAsync(cancellationToken);
        return released;
    }

    public async Task<IReadOnlyList<ReleasePreviewSource>> ListReleasePreviewSourcesAsync(Guid releasePackageId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var package = await connection.QuerySingleOrDefaultAsync<PackagePublishRow>(new CommandDefinition(
            "SELECT project_id,reference_snapshot_id,state,non_standard_bom_snapshot_json FROM release_package WHERE id=@PackageId",
            new { PackageId = releasePackageId }, cancellationToken: cancellationToken))
            ?? throw new PdmNotFoundException("发布包不存在。");
        // 转图与发布解耦：首次发布时包处于"发布中"，后台重试转图时包已经是"已发布"。
        if (package.State is not (nameof(ReleasePackageState.Publishing) or nameof(ReleasePackageState.Published)))
            throw new PdmConflictException("发布包尚未进入服务器转换状态。");
        if (!package.ReferenceSnapshotId.HasValue)
            return [];
        var rootJson = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT root_json FROM reference_snapshot WHERE id=@SnapshotId AND project_id=@ProjectId",
            new { SnapshotId = package.ReferenceSnapshotId.Value, package.ProjectId }, cancellationToken: cancellationToken))
            ?? throw new PdmConflictException("发布包引用树快照不存在。");
        var root = JsonSerializer.Deserialize<DocumentReferenceNode>(rootJson, jsonOptions)
            ?? throw new InvalidDataException("发布包引用树快照损坏。");
        // 转图范围：只转非标件BOM中的物料（模型出STEP、其关联2D工程图出PDF）以及这些物料在引用树上的上级装配体，
        // 标准件/外购件等其他零件不转图。
        var nonStandardModels = ParseNonStandardModels(package.NonStandardBomSnapshotJson);
        IReadOnlyList<Guid> previewScope = nonStandardModels.Count == 0
            ? []
            : await ResolvePreviewScopeAsync(connection, root, nonStandardModels, null, cancellationToken);
        var sources = new List<ReleasePreviewSource>();
        foreach (var documentId in previewScope)
        {
            var row = await connection.QuerySingleOrDefaultAsync<ReleasePreviewSourceRow>(new CommandDefinition(
                """
                SELECT d.id document_id,d.drawing_number,d.file_name,d.kind,
                       v.id source_version_id,v.storage_relative_path,v.file_length,v.sha256,v.property_snapshot_json
                FROM document d
                INNER JOIN document_version v ON v.document_id=d.id
                WHERE d.id=@DocumentId AND d.kind IN ('Assembly','Part','Drawing')
                ORDER BY v.created_at DESC
                LIMIT 1
                """,
                new { DocumentId = documentId }, cancellationToken: cancellationToken));
            if (row is null) continue;
            var properties = JsonSerializer.Deserialize<Dictionary<string, string?>>(row.PropertySnapshotJson, jsonOptions) ?? [];
            properties.TryGetValue("SourceFileSha256", out var sourceSha256);
            sources.Add(new ReleasePreviewSource(
                row.DocumentId,
                row.SourceVersionId,
                row.DrawingNumber,
                row.FileName,
                Enum.Parse<DocumentKind>(row.Kind),
                row.StorageRelativePath,
                row.FileLength,
                row.Sha256,
                string.IsNullOrWhiteSpace(sourceSha256) ? row.Sha256 : sourceSha256));
        }
        return sources;
    }

    /// <summary>
    /// 发布包引用树上的全部图档（含不参与转图的标准件/外购件）：转图时作为参考文件一起送到转图电脑，
    /// SolidWorks 打开装配体时才能解析被引用的其它零件。
    /// </summary>
    public async Task<IReadOnlyList<ReleasePreviewSource>> ListReleaseReferenceSourcesAsync(Guid releasePackageId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var package = await connection.QuerySingleOrDefaultAsync<PackagePublishRow>(new CommandDefinition(
            "SELECT project_id,reference_snapshot_id,state,non_standard_bom_snapshot_json FROM release_package WHERE id=@PackageId",
            new { PackageId = releasePackageId }, cancellationToken: cancellationToken));
        if (package is null || !package.ReferenceSnapshotId.HasValue) return [];
        var rootJson = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT root_json FROM reference_snapshot WHERE id=@SnapshotId AND project_id=@ProjectId",
            new { SnapshotId = package.ReferenceSnapshotId.Value, package.ProjectId }, cancellationToken: cancellationToken));
        if (string.IsNullOrWhiteSpace(rootJson)) return [];
        var root = JsonSerializer.Deserialize<DocumentReferenceNode>(rootJson, jsonOptions);
        if (root is null) return [];
        var documentIds = EnumerateDocumentIds(root).Distinct().ToArray();
        if (documentIds.Length == 0) return [];
        var sources = new List<ReleasePreviewSource>();
        // 逐个图档取最新版本：与转图源清单同样的写法，避免一次性大排序把 MySQL 排序内存打爆。
        foreach (var documentId in documentIds)
        {
            var row = await connection.QuerySingleOrDefaultAsync<ReleasePreviewSourceRow>(new CommandDefinition(
                """
                SELECT d.id document_id,d.drawing_number,d.file_name,d.kind,
                       v.id source_version_id,v.storage_relative_path,v.file_length,v.sha256,v.property_snapshot_json
                FROM document d
                INNER JOIN document_version v ON v.document_id=d.id
                WHERE d.id=@DocumentId AND d.kind IN ('Assembly','Part','Drawing')
                ORDER BY v.created_at DESC
                LIMIT 1
                """,
                new { DocumentId = documentId }, cancellationToken: cancellationToken));
            if (row is null) continue;
            var properties = JsonSerializer.Deserialize<Dictionary<string, string?>>(row.PropertySnapshotJson, jsonOptions) ?? [];
            properties.TryGetValue("SourceFileSha256", out var sourceSha256);
            sources.Add(new ReleasePreviewSource(
                row.DocumentId,
                row.SourceVersionId,
                row.DrawingNumber,
                row.FileName,
                Enum.Parse<DocumentKind>(row.Kind),
                row.StorageRelativePath,
                row.FileLength,
                row.Sha256,
                string.IsNullOrWhiteSpace(sourceSha256) ? row.Sha256 : sourceSha256));
        }
        return sources;
    }

    /// <summary>
    /// 解析转图范围：非标件BOM物料在引用树上的节点 + 这些节点的上级装配体 + 物料关联的2D工程图（转PDF）。
    /// </summary>
    private static async Task<IReadOnlyList<Guid>> ResolvePreviewScopeAsync(
        MySqlConnection connection,
        DocumentReferenceNode root,
        HashSet<Guid> nonStandardModels,
        DbTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var scope = new HashSet<Guid>();

        bool Visit(DocumentReferenceNode node)
        {
            var isTarget = node.DocumentId is Guid own && nonStandardModels.Contains(own);
            foreach (var child in node.Children) isTarget |= Visit(child);
            if (isTarget && node.DocumentId is Guid id) scope.Add(id);
            return isTarget;
        }

        Visit(root);
        if (scope.Count == 0) return [];
        foreach (var drawing in await ResolveNonStandardDrawingIdsAsync(connection, scope, transaction, cancellationToken)) scope.Add(drawing);
        return [.. scope];
    }

    /// <summary>非标件BOM中物料对应的2D工程图（模型→图纸关系）：转图出PDF，发布时随物料一起转正式版本。</summary>
    private static async Task<IReadOnlyList<Guid>> ResolveNonStandardDrawingIdsAsync(
        MySqlConnection connection,
        IEnumerable<Guid> candidateModels,
        DbTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var modelIds = candidateModels.Distinct().ToArray();
        if (modelIds.Length == 0) return [];
        return (await connection.QueryAsync<Guid>(new CommandDefinition(
            "SELECT drawing_document_id FROM document_model_drawing_relation WHERE model_document_id IN @ModelIds",
            new { ModelIds = modelIds }, transaction, cancellationToken: cancellationToken))).ToArray();
    }

    /// <summary>非标件BOM快照里的来源模型（转图与发布的图纸范围都以它为准）。</summary>
    private HashSet<Guid> ParseNonStandardModels(string snapshotJson) =>
        (JsonSerializer.Deserialize<List<BomItem>>(snapshotJson, jsonOptions) ?? [])
            .Where(item => item.SourceDocumentId.HasValue)
            .Select(item => item.SourceDocumentId!.Value)
            .ToHashSet();

    public async Task<IReadOnlyList<DocumentVersion>> PublishReleasePackageVersionsAsync(
        Guid releasePackageId,
        Guid approvalTaskId,
        string actor,
        IReadOnlyDictionary<Guid, DocumentPreviewArtifact> previews,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var package = await connection.QuerySingleOrDefaultAsync<PackagePublishRow>(new CommandDefinition(
            "SELECT project_id,reference_snapshot_id,state,non_standard_bom_snapshot_json FROM release_package WHERE id=@PackageId FOR UPDATE",
            new { PackageId = releasePackageId }, transaction, cancellationToken: cancellationToken))
            ?? throw new PdmNotFoundException("发布包不存在。");
        if (package.State != ReleasePackageState.Publishing.ToString()) throw new PdmConflictException("发布包尚未进入发布状态。");
        var taskMatches = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM approval_task WHERE id=@ApprovalTaskId AND release_package_id=@PackageId AND decision_value='Approved'",
            new { ApprovalTaskId = approvalTaskId, PackageId = releasePackageId }, transaction, cancellationToken: cancellationToken));
        if (taskMatches != 1) throw new PdmConflictException("最终批准记录与发布包不匹配或尚未批准。");
        if (!package.ReferenceSnapshotId.HasValue)
        {
            await transaction.CommitAsync(cancellationToken);
            return [];
        }
        var rootJson = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT root_json FROM reference_snapshot WHERE id=@SnapshotId AND project_id=@ProjectId",
            new { SnapshotId = package.ReferenceSnapshotId.Value, package.ProjectId }, transaction, cancellationToken: cancellationToken))
            ?? throw new PdmConflictException("发布包引用树快照不存在。");
        var root = JsonSerializer.Deserialize<DocumentReferenceNode>(rootJson, jsonOptions)
            ?? throw new InvalidDataException("发布包引用树快照损坏。");

        var documentIds = EnumerateDocumentIds(root).Distinct().ToList();
        // 非标件BOM中物料关联的2D工程图随发布一起转正式版本：转图时生成PDF，交付包与"机械发布"目录才有图纸。
        var nonStandardModels = ParseNonStandardModels(package.NonStandardBomSnapshotJson);
        documentIds.AddRange(await ResolveNonStandardDrawingIdsAsync(connection,
            EnumerateDocumentIds(root).Distinct().Where(nonStandardModels.Contains), transaction, cancellationToken));
        var distinctDocumentIds = documentIds.Distinct().ToArray();
        var releasedVersions = new List<DocumentVersion>();
        foreach (var documentId in distinctDocumentIds)
        {
            var locked = await LockDocumentAsync(connection, transaction, documentId, cancellationToken);
            var sourceRow = await connection.QuerySingleOrDefaultAsync<DocumentVersionRow>(new CommandDefinition(
                VersionSelect + " WHERE document_id=@DocumentId ORDER BY created_at DESC LIMIT 1",
                new { DocumentId = documentId }, transaction, cancellationToken: cancellationToken));
            if (sourceRow is null) continue;
            var source = MapDocumentVersion(sourceRow);
            if (source.Status == DocumentVersionStatus.Released) continue;
            if (!string.Equals(source.Revision.Display, locked.RevisionLabel, StringComparison.OrdinalIgnoreCase))
                throw new PdmConflictException($"图档{documentId}最新工作版本已变化，发布包不能继续发布。");
            DocumentPreviewArtifact? preview = null;
            // 转图与发布解耦：发布时缺预览不再阻断正式版本生成，预览由后台转图任务补齐。
            if (Enum.Parse<DocumentKind>(locked.Kind) is DocumentKind.Assembly or DocumentKind.Part or DocumentKind.Drawing)
                previews.TryGetValue(documentId, out preview);
            var revision = source.Revision.Release();
            var released = source with
            {
                Id = Guid.NewGuid(), Revision = revision, Status = DocumentVersionStatus.Released,
                CreatedBy = actor, CreatedAt = timeProvider.GetUtcNow(), ChangeNote = $"审批发布{revision.Display}",
                SourceVersionId = source.Id, SourceDescription = $"由{source.Revision.Display}审批发布",
                ApprovalTaskId = approvalTaskId, ReleasePackageId = releasePackageId, Preview = preview
            };
            await InsertVersionAsync(connection, transaction, released, cancellationToken);
            var affected = await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE document SET revision_label=@Revision,lifecycle_state='Released',checked_out_by=NULL,checked_out_at=NULL,checkout_session_id=NULL,checkout_machine=NULL,checkout_last_heartbeat_at=NULL,checkout_lease_expires_at=NULL,checkout_release_requested_by=NULL,checkout_release_requested_at=NULL,checkout_release_request_reason=NULL,updated_at=@Now,row_version=row_version+1 WHERE id=@DocumentId AND row_version=@RowVersion",
                new { DocumentId = documentId, Revision = revision.Display, RowVersion = locked.RowVersion, Now = released.CreatedAt.UtcDateTime }, transaction, cancellationToken: cancellationToken));
            if (affected != 1) throw new PdmConflictException("图档版本已变化，发布包正式版本事务未生效。");
            releasedVersions.Add(released);
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE document SET lifecycle_state='Released',updated_at=@Now,row_version=row_version+1 WHERE id IN @DocumentIds AND lifecycle_state='InReview'",
            new { DocumentIds = documentIds, Now = timeProvider.GetUtcNow().UtcDateTime }, transaction, cancellationToken: cancellationToken));

        await transaction.CommitAsync(cancellationToken);
        return releasedVersions;
    }

    private static IEnumerable<Guid> EnumerateDocumentIds(DocumentReferenceNode node)
    {
        if (node.DocumentId.HasValue) yield return node.DocumentId.Value;
        foreach (var child in node.Children)
            foreach (var id in EnumerateDocumentIds(child)) yield return id;
    }

    /// <summary>记录发布包的转图状态：转图是发布之后的独立事项，失败不影响发布结果。</summary>
    public async Task<ReleasePackage> MarkReleasePreviewStateAsync(Guid releasePackageId, string state, string? error, int attempts, DateTimeOffset updatedAt, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE release_package SET preview_state=@State,preview_error=@Error,preview_attempts=@Attempts,preview_updated_at=@UpdatedAt,row_version=row_version+1 WHERE id=@PackageId",
            new { PackageId = releasePackageId, State = state, Error = error, Attempts = attempts, UpdatedAt = updatedAt.UtcDateTime },
            cancellationToken: cancellationToken));
        if (affected != 1) throw new PdmNotFoundException("发布包不存在。");
        return await FindReleasePackageAsync(releasePackageId, cancellationToken)
            ?? throw new PdmNotFoundException("发布包不存在。");
    }

    /// <summary>列出需要继续转图的已发布包（待处理，或失败但未超过重试上限）。</summary>
    public async Task<IReadOnlyList<ReleasePackage>> ListReleasePackagesAwaitingPreviewAsync(int maxAttempts, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var ids = await connection.QueryAsync<Guid>(new CommandDefinition(
            """
            SELECT id FROM release_package
            WHERE state='Published'
              AND preview_state IN ('Pending','Failed')
              AND preview_attempts < @MaxAttempts
            ORDER BY preview_updated_at IS NULL DESC, preview_updated_at, created_at
            LIMIT @Limit
            """,
            new { MaxAttempts = maxAttempts, Limit = limit }, cancellationToken: cancellationToken));
        var packages = new List<ReleasePackage>();
        foreach (var id in ids)
        {
            var package = await FindReleasePackageAsync(id, cancellationToken);
            if (package is not null) packages.Add(package);
        }
        return packages;
    }

    /// <summary>服务重启/崩溃后恢复：把停留在"发布中"的发布包标记为发布失败，避免整单卡死、BOM 一直锁定。</summary>
    public async Task<int> RecoverInterruptedPublishesAsync(string reason, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE release_package SET state='PublishFailed',publish_error=@Reason,row_version=row_version+1 WHERE state='Publishing'",
            new { Reason = reason }, cancellationToken: cancellationToken));
    }

    /// <summary>服务重启/崩溃后恢复：把停留在"转换中"的转图任务重新排队（转图本身可重入）。</summary>
    public async Task<int> RecoverInterruptedPreviewRunsAsync(string reason, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE release_package SET preview_state='Pending',preview_error=@Reason,preview_updated_at=@UpdatedAt,row_version=row_version+1 WHERE state='Published' AND preview_state='Running'",
            new { Reason = reason, UpdatedAt = timeProvider.GetUtcNow().UtcDateTime }, cancellationToken: cancellationToken));
    }

    /// <summary>列出最近发布的发布包（服务重启后用于补登记发布成品归档）。</summary>
    public async Task<IReadOnlyList<Guid>> ListRecentPublishedReleasePackageIdsAsync(int limit, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return (await connection.QueryAsync<Guid>(new CommandDefinition(
            """
            SELECT id FROM release_package
            WHERE state='Published' AND published_path IS NOT NULL
            ORDER BY COALESCE(published_at, created_at) DESC
            LIMIT @Limit
            """,
            new { Limit = limit }, cancellationToken: cancellationToken))).ToArray();
    }

    /// <summary>把转图结果补挂到发布包对应的正式版本（发布时缺预览的正式版本后补 STEP/PDF）。</summary>
    /// <summary>列出停留在“发布中”的发布包（服务重启后用于自动续跑发布）。</summary>
    public async Task<IReadOnlyList<Guid>> ListPublishingReleasePackageIdsAsync(int limit, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return (await connection.QueryAsync<Guid>(new CommandDefinition(
            "SELECT id FROM release_package WHERE state='Publishing' ORDER BY created_at LIMIT @Limit",
            new { Limit = limit }, cancellationToken: cancellationToken))).ToArray();
    }

    /// <summary>把转图结果补挂到发布包对应的正式版本（发布时缺预览的正式版本后补 STEP/PDF）。</summary>
    public async Task<IReadOnlyList<DocumentVersion>> AttachReleasePreviewArtifactsAsync(Guid releasePackageId, IReadOnlyDictionary<Guid, DocumentPreviewArtifact> previews, CancellationToken cancellationToken)
    {
        if (previews.Count == 0) return [];
        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<DocumentVersionRow>(new CommandDefinition(
            // 不按 created_at 排序：document_version 行含多个大 JSON 快照，排序会超出 MySQL 排序内存（Out of sort memory），
            // 而这里的调用方按图档匹配预览，顺序无关。
            VersionSelect + " WHERE release_package_id=@PackageId",
            new { PackageId = releasePackageId }, cancellationToken: cancellationToken));
        var updated = new List<DocumentVersion>();
        foreach (var row in rows)
        {
            var version = MapDocumentVersion(row);
            if (!previews.TryGetValue(version.DocumentId, out var preview)) continue;
            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE document_version
                SET preview_format=@Format,preview_storage_relative_path=@Path,preview_file_length=@Length,
                    preview_sha256=@Sha256,preview_source_sha256=@SourceSha256
                WHERE id=@VersionId AND release_package_id=@PackageId
                """,
                new
                {
                    VersionId = version.Id,
                    PackageId = releasePackageId,
                    Format = preview.Format.ToString(),
                    Path = preview.StorageRelativePath,
                    Length = preview.FileLength,
                    Sha256 = preview.Sha256,
                    SourceSha256 = preview.SourceSha256
                }, cancellationToken: cancellationToken));
            updated.Add(version with { Preview = preview });
        }
        return updated;
    }

    private static async Task<RevisionLabel> NextWorkRevisionAsync(DbConnection connection, DbTransaction transaction, LockedDocumentRow document, CancellationToken cancellationToken)
    {
        var current = RevisionLabel.Parse(document.RevisionLabel);
        var versionCount = await connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM document_version WHERE document_id=@DocumentId", new { DocumentId = document.Id }, transaction, cancellationToken: cancellationToken));
        return versionCount == 0 && !current.IsReleased ? RevisionLabel.InitialWork() : current.NextWork();
    }

    private static DocumentVersion CreateVersion(Guid documentId, RevisionLabel revision, string actor, DocumentVersionCommit commit, DateTimeOffset now, DocumentVersionStatus status) =>
        new(Guid.NewGuid(), documentId, revision, status, commit.File.RelativePath, commit.File.Length, commit.File.Sha256, actor, now, commit.ChangeNote,
            commit.Properties, commit.ReferenceSnapshot.Root, commit.MechanicalBomSnapshot, commit.ElectricalBomSnapshot,
            commit.SourceVersionId, commit.SourceDescription, null, null)
        { ChangeKind = commit.ChangeKind };

    private async Task InsertReferenceSnapshotAsync(DbConnection connection, DbTransaction transaction, CadReferenceSnapshot snapshot, CancellationToken cancellationToken) =>
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO reference_snapshot(id,project_id,root_document_id,captured_at,captured_by,sha256,root_json) VALUES(@Id,@ProjectId,@RootDocumentId,@CapturedAt,@CapturedBy,@Sha256,@RootJson)",
            new { Id = snapshot.SnapshotId, snapshot.ProjectId, snapshot.RootDocumentId, CapturedAt = snapshot.CapturedAt.UtcDateTime, snapshot.CapturedBy, snapshot.Sha256, RootJson = JsonSerializer.Serialize(snapshot.Root, jsonOptions) }, transaction, cancellationToken: cancellationToken));

    private async Task SetProjectReferenceRootAsync(DbConnection connection, DbTransaction transaction, CadReferenceSnapshot snapshot, CancellationToken cancellationToken) =>
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO project_reference_root(project_id, reference_snapshot_id, updated_at)
            VALUES(@ProjectId, @ReferenceSnapshotId, @UpdatedAt)
            ON DUPLICATE KEY UPDATE
                reference_snapshot_id = VALUES(reference_snapshot_id),
                updated_at = VALUES(updated_at)
            """,
            new
            {
                snapshot.ProjectId,
                ReferenceSnapshotId = snapshot.SnapshotId,
                UpdatedAt = snapshot.CapturedAt.UtcDateTime
            },
            transaction,
            cancellationToken: cancellationToken));

    private async Task InsertVersionAsync(DbConnection connection, DbTransaction transaction, DocumentVersion version, CancellationToken cancellationToken) =>
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO document_version(id,document_id,revision_label,version_status,change_kind,storage_relative_path,file_length,sha256,comment,property_snapshot_json,reference_snapshot_json,mechanical_bom_snapshot_json,electrical_bom_snapshot_json,source_version_id,source_description,approval_task_id,release_package_id,preview_format,preview_storage_relative_path,preview_file_length,preview_sha256,preview_source_sha256,created_by,created_at)
            VALUES(@Id,@DocumentId,@Revision,@Status,@ChangeKind,@Path,@Length,@Sha256,@Comment,@Properties,@Reference,@Mechanical,@Electrical,@SourceVersionId,@SourceDescription,@ApprovalTaskId,@ReleasePackageId,@PreviewFormat,@PreviewPath,@PreviewLength,@PreviewSha256,@PreviewSourceSha256,@CreatedBy,@CreatedAt)
            """,
            new { version.Id, version.DocumentId, Revision = version.Revision.Display, Status = version.Status.ToString(), ChangeKind = version.ChangeKind.ToString(), Path = version.StorageRelativePath, Length = version.FileLength, version.Sha256, Comment = version.ChangeNote,
                Properties = JsonSerializer.Serialize(version.PropertySnapshot, jsonOptions), Reference = JsonSerializer.Serialize(version.ReferenceSnapshot, jsonOptions), Mechanical = JsonSerializer.Serialize(version.MechanicalBomSnapshot, jsonOptions), Electrical = JsonSerializer.Serialize(version.ElectricalBomSnapshot, jsonOptions),
                version.SourceVersionId, version.SourceDescription, version.ApprovalTaskId, version.ReleasePackageId,
                PreviewFormat = version.Preview?.Format.ToString(), PreviewPath = version.Preview?.StorageRelativePath,
                PreviewLength = version.Preview?.FileLength, PreviewSha256 = version.Preview?.Sha256, PreviewSourceSha256 = version.Preview?.SourceSha256,
                version.CreatedBy, CreatedAt = version.CreatedAt.UtcDateTime }, transaction, cancellationToken: cancellationToken));

    private async Task<DocumentVersion?> FindDocumentVersionAsync(DbConnection connection, DbTransaction? transaction, Guid documentId, Guid versionId, CancellationToken cancellationToken)
    {
        var row = await connection.QuerySingleOrDefaultAsync<DocumentVersionRow>(new CommandDefinition(VersionSelect + " WHERE document_id=@DocumentId AND id=@VersionId", new { DocumentId = documentId, VersionId = versionId }, transaction, cancellationToken: cancellationToken));
        return row is null ? null : MapDocumentVersion(row);
    }

    private static async Task<LockedDocumentRow> LockDocumentAsync(DbConnection connection, DbTransaction transaction, Guid documentId, CancellationToken cancellationToken) =>
        await connection.QuerySingleOrDefaultAsync<LockedDocumentRow>(new CommandDefinition("SELECT id,kind,revision_label,checked_out_by,checkout_session_id,row_version FROM document WHERE id=@DocumentId FOR UPDATE", new { DocumentId = documentId }, transaction, cancellationToken: cancellationToken))
        ?? throw new PdmNotFoundException("图档不存在。");

    private DocumentVersion MapDocumentVersion(DocumentVersionRow row) => new(
        row.Id, row.DocumentId, RevisionLabel.Parse(row.RevisionLabel), Enum.Parse<DocumentVersionStatus>(row.VersionStatus), row.StorageRelativePath, row.FileLength, row.Sha256, row.CreatedBy,
        new DateTimeOffset(DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc)), row.Comment ?? string.Empty,
        JsonSerializer.Deserialize<Dictionary<string, string?>>(row.PropertySnapshotJson, jsonOptions) ?? new(),
        JsonSerializer.Deserialize<DocumentReferenceNode>(row.ReferenceSnapshotJson, jsonOptions) ?? throw new InvalidDataException("版本引用树快照损坏。"),
        JsonSerializer.Deserialize<List<BomItem>>(row.MechanicalBomSnapshotJson, jsonOptions) ?? [],
        JsonSerializer.Deserialize<List<BomItem>>(row.ElectricalBomSnapshotJson, jsonOptions) ?? [],
        row.SourceVersionId, row.SourceDescription, row.ApprovalTaskId, row.ReleasePackageId,
        string.IsNullOrWhiteSpace(row.PreviewFormat)
            ? null
            : new DocumentPreviewArtifact(
                Enum.Parse<DocumentPreviewFormat>(row.PreviewFormat),
                row.PreviewStorageRelativePath ?? throw new InvalidDataException("版本预览路径缺失。"),
                row.PreviewFileLength ?? throw new InvalidDataException("版本预览大小缺失。"),
                row.PreviewSha256 ?? throw new InvalidDataException("版本预览SHA-256缺失。"),
                row.PreviewSourceSha256 ?? throw new InvalidDataException("版本预览源SHA-256缺失。")))
    { ChangeKind = ParseChangeKind(row.ChangeKind) };

    private static DocumentVersionChangeKind ParseChangeKind(string? value) =>
        !string.IsNullOrWhiteSpace(value) && Enum.TryParse<DocumentVersionChangeKind>(value, true, out var parsed)
            ? parsed
            : DocumentVersionChangeKind.Content;

    private const string VersionSelect = "SELECT id,document_id,revision_label,version_status,change_kind,storage_relative_path,file_length,sha256,comment,property_snapshot_json,reference_snapshot_json,mechanical_bom_snapshot_json,electrical_bom_snapshot_json,source_version_id,source_description,approval_task_id,release_package_id,preview_format,preview_storage_relative_path,preview_file_length,preview_sha256,preview_source_sha256,created_by,created_at FROM document_version";

    private sealed class LockedDocumentRow { public Guid Id { get; init; } public string Kind { get; init; } = string.Empty; public string RevisionLabel { get; init; } = string.Empty; public string? CheckedOutBy { get; init; } public Guid? CheckoutSessionId { get; init; } public long RowVersion { get; init; } }
    private sealed class LatestVersionFingerprintRow { public string Sha256 { get; init; } = string.Empty; public string? SourceFileSha256 { get; init; } }
    private sealed class PackagePublishRow
    {
        public Guid ProjectId { get; init; }
        public Guid? ReferenceSnapshotId { get; init; }
        public string State { get; init; } = string.Empty;
        public string NonStandardBomSnapshotJson { get; init; } = "[]";
    }
    private sealed class ReleasePreviewSourceRow
    {
        public Guid DocumentId { get; init; }
        public Guid SourceVersionId { get; init; }
        public string DrawingNumber { get; init; } = string.Empty;
        public string FileName { get; init; } = string.Empty;
        public string Kind { get; init; } = string.Empty;
        public string StorageRelativePath { get; init; } = string.Empty;
        public long FileLength { get; init; }
        public string Sha256 { get; init; } = string.Empty;
        public string PropertySnapshotJson { get; init; } = "{}";
    }
    private sealed class DocumentVersionRow
    {
        public Guid Id { get; init; } public Guid DocumentId { get; init; } public string RevisionLabel { get; init; } = string.Empty; public string VersionStatus { get; init; } = string.Empty; public string? ChangeKind { get; init; }
        public string StorageRelativePath { get; init; } = string.Empty; public long FileLength { get; init; } public string Sha256 { get; init; } = string.Empty; public string? Comment { get; init; }
        public string PropertySnapshotJson { get; init; } = "{}"; public string ReferenceSnapshotJson { get; init; } = "{}"; public string MechanicalBomSnapshotJson { get; init; } = "[]"; public string ElectricalBomSnapshotJson { get; init; } = "[]";
        public Guid? SourceVersionId { get; init; } public string? SourceDescription { get; init; } public Guid? ApprovalTaskId { get; init; } public Guid? ReleasePackageId { get; init; }
        public string? PreviewFormat { get; init; } public string? PreviewStorageRelativePath { get; init; } public long? PreviewFileLength { get; init; } public string? PreviewSha256 { get; init; } public string? PreviewSourceSha256 { get; init; }
        public string CreatedBy { get; init; } = string.Empty; public DateTime CreatedAt { get; init; }
    }
}
