using System.Data.Common;
using System.Text.Json;
using Dapper;
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

    public async Task<DocumentVersion?> FindDocumentVersionAsync(Guid documentId, Guid versionId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await FindDocumentVersionAsync(connection, null, documentId, versionId, cancellationToken);
    }

    public async Task<DocumentCheckInResult> CheckInVersionAsync(Guid documentId, string actor, DocumentVersionCommit commit, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var locked = await LockDocumentAsync(connection, transaction, documentId, cancellationToken);
        if (!string.Equals(locked.CheckedOutBy, actor, StringComparison.OrdinalIgnoreCase))
            throw new PdmConflictException("只有当前编辑人员可以提交存档。");

        var latestSha256 = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT sha256 FROM document_version WHERE document_id=@DocumentId ORDER BY created_at DESC LIMIT 1",
            new { DocumentId = documentId }, transaction, cancellationToken: cancellationToken));
        if (!string.IsNullOrWhiteSpace(latestSha256) && string.Equals(latestSha256, commit.File.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            var unchanged = await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE document SET checked_out_by=NULL, checked_out_at=NULL, updated_at=@Now, row_version=row_version+1 WHERE id=@DocumentId AND row_version=@RowVersion AND checked_out_by=@Actor",
                new { DocumentId = documentId, Actor = actor, RowVersion = locked.RowVersion, Now = timeProvider.GetUtcNow().UtcDateTime }, transaction, cancellationToken: cancellationToken));
            if (unchanged != 1) throw new PdmConflictException("图档编辑状态已经变化，请刷新后重试。");
            var unchangedDocument = await FindDocumentAsync(connection, transaction, documentId, cancellationToken) ?? throw new PdmNotFoundException("图档不存在。");
            await transaction.CommitAsync(cancellationToken);
            return new DocumentCheckInResult(unchangedDocument, null, false);
        }

        var nextRevision = await NextWorkRevisionAsync(connection, transaction, locked, cancellationToken);
        var version = CreateVersion(documentId, nextRevision, actor, commit, timeProvider.GetUtcNow(), DocumentVersionStatus.Work);
        await InsertVersionAsync(connection, transaction, version, cancellationToken);
        await InsertReferenceSnapshotAsync(connection, transaction, commit.ReferenceSnapshot, cancellationToken);
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE document SET revision_label=@Revision, lifecycle_state='Work', checked_out_by=NULL, checked_out_at=NULL, updated_at=@Now, row_version=row_version+1 WHERE id=@DocumentId AND row_version=@RowVersion AND checked_out_by=@Actor",
            new { DocumentId = documentId, Revision = nextRevision.Display, Actor = actor, RowVersion = locked.RowVersion, Now = version.CreatedAt.UtcDateTime }, transaction, cancellationToken: cancellationToken));
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
            ApprovalTaskId = null, ReleasePackageId = null
        };
        await InsertVersionAsync(connection, transaction, version, cancellationToken);
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE document SET revision_label=@Revision, lifecycle_state='Work', checked_out_by=NULL, checked_out_at=NULL, updated_at=@Now, row_version=row_version+1 WHERE id=@DocumentId AND row_version=@RowVersion",
            new { DocumentId = documentId, Revision = nextRevision.Display, RowVersion = locked.RowVersion, Now = version.CreatedAt.UtcDateTime }, transaction, cancellationToken: cancellationToken));
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
        var taskMatches = await connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM approval_task WHERE id=@ApprovalTaskId AND release_package_id=@ReleasePackageId AND stage='Approval' AND decision_value='Approved'", new { ApprovalTaskId = approvalTaskId, ReleasePackageId = releasePackageId }, transaction, cancellationToken: cancellationToken));
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
            "UPDATE document SET revision_label=@Revision, lifecycle_state='Released', checked_out_by=NULL, checked_out_at=NULL, updated_at=@Now, row_version=row_version+1 WHERE id=@DocumentId AND row_version=@RowVersion",
            new { DocumentId = documentId, Revision = releasedRevision.Display, RowVersion = locked.RowVersion, Now = released.CreatedAt.UtcDateTime }, transaction, cancellationToken: cancellationToken));
        if (affected != 1) throw new PdmConflictException("图档版本已变化，不能重复发布。");
        await transaction.CommitAsync(cancellationToken);
        return released;
    }

    public async Task<IReadOnlyList<DocumentVersion>> PublishReleasePackageVersionsAsync(Guid releasePackageId, Guid approvalTaskId, string actor, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var package = await connection.QuerySingleOrDefaultAsync<PackagePublishRow>(new CommandDefinition(
            "SELECT project_id,reference_snapshot_id,state FROM release_package WHERE id=@PackageId FOR UPDATE",
            new { PackageId = releasePackageId }, transaction, cancellationToken: cancellationToken))
            ?? throw new PdmNotFoundException("发布包不存在。");
        if (package.State != ReleasePackageState.Publishing.ToString()) throw new PdmConflictException("发布包尚未进入发布状态。");
        var taskMatches = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM approval_task WHERE id=@ApprovalTaskId AND release_package_id=@PackageId AND stage='Approval' AND decision_value='Approved'",
            new { ApprovalTaskId = approvalTaskId, PackageId = releasePackageId }, transaction, cancellationToken: cancellationToken));
        if (taskMatches != 1) throw new PdmConflictException("最终批准记录与发布包不匹配或尚未批准。");
        var rootJson = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT root_json FROM reference_snapshot WHERE id=@SnapshotId AND project_id=@ProjectId",
            new { SnapshotId = package.ReferenceSnapshotId, package.ProjectId }, transaction, cancellationToken: cancellationToken))
            ?? throw new PdmConflictException("发布包引用树快照不存在。");
        var root = JsonSerializer.Deserialize<DocumentReferenceNode>(rootJson, jsonOptions)
            ?? throw new InvalidDataException("发布包引用树快照损坏。");

        var releasedVersions = new List<DocumentVersion>();
        foreach (var documentId in EnumerateDocumentIds(root).Distinct())
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
            var revision = source.Revision.Release();
            var released = source with
            {
                Id = Guid.NewGuid(), Revision = revision, Status = DocumentVersionStatus.Released,
                CreatedBy = actor, CreatedAt = timeProvider.GetUtcNow(), ChangeNote = $"审批发布{revision.Display}",
                SourceVersionId = source.Id, SourceDescription = $"由{source.Revision.Display}审批发布",
                ApprovalTaskId = approvalTaskId, ReleasePackageId = releasePackageId
            };
            await InsertVersionAsync(connection, transaction, released, cancellationToken);
            var affected = await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE document SET revision_label=@Revision,lifecycle_state='Released',checked_out_by=NULL,checked_out_at=NULL,updated_at=@Now,row_version=row_version+1 WHERE id=@DocumentId AND row_version=@RowVersion",
                new { DocumentId = documentId, Revision = revision.Display, RowVersion = locked.RowVersion, Now = released.CreatedAt.UtcDateTime }, transaction, cancellationToken: cancellationToken));
            if (affected != 1) throw new PdmConflictException("图档版本已变化，发布包正式版本事务未生效。");
            releasedVersions.Add(released);
        }

        await transaction.CommitAsync(cancellationToken);
        return releasedVersions;
    }

    private static IEnumerable<Guid> EnumerateDocumentIds(DocumentReferenceNode node)
    {
        if (node.DocumentId.HasValue) yield return node.DocumentId.Value;
        foreach (var child in node.Children)
            foreach (var id in EnumerateDocumentIds(child)) yield return id;
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
            commit.SourceVersionId, commit.SourceDescription, null, null);

    private async Task InsertReferenceSnapshotAsync(DbConnection connection, DbTransaction transaction, CadReferenceSnapshot snapshot, CancellationToken cancellationToken) =>
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO reference_snapshot(id,project_id,root_document_id,captured_at,captured_by,sha256,root_json) VALUES(@Id,@ProjectId,@RootDocumentId,@CapturedAt,@CapturedBy,@Sha256,@RootJson)",
            new { Id = snapshot.SnapshotId, snapshot.ProjectId, snapshot.RootDocumentId, CapturedAt = snapshot.CapturedAt.UtcDateTime, snapshot.CapturedBy, snapshot.Sha256, RootJson = JsonSerializer.Serialize(snapshot.Root, jsonOptions) }, transaction, cancellationToken: cancellationToken));

    private async Task InsertVersionAsync(DbConnection connection, DbTransaction transaction, DocumentVersion version, CancellationToken cancellationToken) =>
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO document_version(id,document_id,revision_label,version_status,storage_relative_path,file_length,sha256,comment,property_snapshot_json,reference_snapshot_json,mechanical_bom_snapshot_json,electrical_bom_snapshot_json,source_version_id,source_description,approval_task_id,release_package_id,created_by,created_at)
            VALUES(@Id,@DocumentId,@Revision,@Status,@Path,@Length,@Sha256,@Comment,@Properties,@Reference,@Mechanical,@Electrical,@SourceVersionId,@SourceDescription,@ApprovalTaskId,@ReleasePackageId,@CreatedBy,@CreatedAt)
            """,
            new { version.Id, version.DocumentId, Revision = version.Revision.Display, Status = version.Status.ToString(), Path = version.StorageRelativePath, Length = version.FileLength, version.Sha256, Comment = version.ChangeNote,
                Properties = JsonSerializer.Serialize(version.PropertySnapshot, jsonOptions), Reference = JsonSerializer.Serialize(version.ReferenceSnapshot, jsonOptions), Mechanical = JsonSerializer.Serialize(version.MechanicalBomSnapshot, jsonOptions), Electrical = JsonSerializer.Serialize(version.ElectricalBomSnapshot, jsonOptions),
                version.SourceVersionId, version.SourceDescription, version.ApprovalTaskId, version.ReleasePackageId, version.CreatedBy, CreatedAt = version.CreatedAt.UtcDateTime }, transaction, cancellationToken: cancellationToken));

    private async Task<DocumentVersion?> FindDocumentVersionAsync(DbConnection connection, DbTransaction? transaction, Guid documentId, Guid versionId, CancellationToken cancellationToken)
    {
        var row = await connection.QuerySingleOrDefaultAsync<DocumentVersionRow>(new CommandDefinition(VersionSelect + " WHERE document_id=@DocumentId AND id=@VersionId", new { DocumentId = documentId, VersionId = versionId }, transaction, cancellationToken: cancellationToken));
        return row is null ? null : MapDocumentVersion(row);
    }

    private static async Task<LockedDocumentRow> LockDocumentAsync(DbConnection connection, DbTransaction transaction, Guid documentId, CancellationToken cancellationToken) =>
        await connection.QuerySingleOrDefaultAsync<LockedDocumentRow>(new CommandDefinition("SELECT id,revision_label,checked_out_by,row_version FROM document WHERE id=@DocumentId FOR UPDATE", new { DocumentId = documentId }, transaction, cancellationToken: cancellationToken))
        ?? throw new PdmNotFoundException("图档不存在。");

    private DocumentVersion MapDocumentVersion(DocumentVersionRow row) => new(
        row.Id, row.DocumentId, RevisionLabel.Parse(row.RevisionLabel), Enum.Parse<DocumentVersionStatus>(row.VersionStatus), row.StorageRelativePath, row.FileLength, row.Sha256, row.CreatedBy,
        new DateTimeOffset(DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc)), row.Comment ?? string.Empty,
        JsonSerializer.Deserialize<Dictionary<string, string?>>(row.PropertySnapshotJson, jsonOptions) ?? new(),
        JsonSerializer.Deserialize<DocumentReferenceNode>(row.ReferenceSnapshotJson, jsonOptions) ?? throw new InvalidDataException("版本引用树快照损坏。"),
        JsonSerializer.Deserialize<List<BomItem>>(row.MechanicalBomSnapshotJson, jsonOptions) ?? [],
        JsonSerializer.Deserialize<List<BomItem>>(row.ElectricalBomSnapshotJson, jsonOptions) ?? [],
        row.SourceVersionId, row.SourceDescription, row.ApprovalTaskId, row.ReleasePackageId);

    private const string VersionSelect = "SELECT id,document_id,revision_label,version_status,storage_relative_path,file_length,sha256,comment,property_snapshot_json,reference_snapshot_json,mechanical_bom_snapshot_json,electrical_bom_snapshot_json,source_version_id,source_description,approval_task_id,release_package_id,created_by,created_at FROM document_version";

    private sealed class LockedDocumentRow { public Guid Id { get; init; } public string RevisionLabel { get; init; } = string.Empty; public string? CheckedOutBy { get; init; } public long RowVersion { get; init; } }
    private sealed class PackagePublishRow { public Guid ProjectId { get; init; } public Guid ReferenceSnapshotId { get; init; } public string State { get; init; } = string.Empty; }
    private sealed class DocumentVersionRow
    {
        public Guid Id { get; init; } public Guid DocumentId { get; init; } public string RevisionLabel { get; init; } = string.Empty; public string VersionStatus { get; init; } = string.Empty;
        public string StorageRelativePath { get; init; } = string.Empty; public long FileLength { get; init; } public string Sha256 { get; init; } = string.Empty; public string? Comment { get; init; }
        public string PropertySnapshotJson { get; init; } = "{}"; public string ReferenceSnapshotJson { get; init; } = "{}"; public string MechanicalBomSnapshotJson { get; init; } = "[]"; public string ElectricalBomSnapshotJson { get; init; } = "[]";
        public Guid? SourceVersionId { get; init; } public string? SourceDescription { get; init; } public Guid? ApprovalTaskId { get; init; } public Guid? ReleasePackageId { get; init; }
        public string CreatedBy { get; init; } = string.Empty; public DateTime CreatedAt { get; init; }
    }
}
