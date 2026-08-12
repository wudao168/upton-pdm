using System.Data.Common;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed partial class MySqlPdmRepository : IPdmRepository
{
    private readonly string connectionString;
    private readonly TimeProvider timeProvider;
    private readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);

    public MySqlPdmRepository(IOptions<PdmDatabaseOptions> options, TimeProvider timeProvider)
    {
        connectionString = options.Value.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("PDM MySQL连接字符串未配置。 ");
        }

        this.timeProvider = timeProvider;
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    public async Task<IReadOnlyList<Project>> ListProjectsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<ProjectRow>(new CommandDefinition(
            "SELECT id, code, name, owner, vault_location, release_location, is_active FROM project ORDER BY code",
            cancellationToken: cancellationToken));
        return rows.Select(MapProject).ToArray();
    }

    public async Task<Project?> FindProjectAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await FindProjectAsync(connection, null, projectId, cancellationToken);
    }

    public async Task<IReadOnlyList<PdmDocument>> ListDocumentsAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<DocumentRow>(new CommandDefinition(
            """
            SELECT id, project_id, drawing_number, name, file_name, kind, lifecycle_state, revision_label, checked_out_by, updated_at
            FROM document
            WHERE project_id = @ProjectId
            ORDER BY drawing_number, kind
            """,
            new { ProjectId = projectId },
            cancellationToken: cancellationToken));
        return rows.Select(MapDocument).ToArray();
    }

    public async Task<PdmDocument?> FindDocumentAsync(Guid documentId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await FindDocumentAsync(connection, null, documentId, cancellationToken);
    }

    public async Task<PdmDocument> RegisterDocumentAsync(RegisterDocumentCommand command, string actor, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var projectActive = await connection.ExecuteScalarAsync<bool?>(new CommandDefinition(
            "SELECT is_active FROM project WHERE id=@ProjectId FOR UPDATE",
            new { command.ProjectId },
            transaction,
            cancellationToken: cancellationToken));
        if (projectActive != true)
        {
            throw new PdmNotFoundException("项目不存在或已停用。");
        }

        var documentId = Guid.NewGuid();
        var now = timeProvider.GetUtcNow();
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO document(id,project_id,drawing_number,name,file_name,kind,lifecycle_state,revision_label,checked_out_by,checked_out_at,row_version,created_at,updated_at)
            VALUES(@Id,@ProjectId,@DrawingNumber,@Name,@FileName,@Kind,'Work','W1',NULL,NULL,1,@Now,@Now)
            ON DUPLICATE KEY UPDATE id=id
            """,
            new
            {
                Id = documentId,
                command.ProjectId,
                command.DrawingNumber,
                command.Name,
                command.FileName,
                Kind = command.Kind.ToString(),
                Now = now.UtcDateTime
            },
            transaction,
            cancellationToken: cancellationToken));

        var row = await connection.QuerySingleAsync<DocumentRow>(new CommandDefinition(
            """
            SELECT id, project_id, drawing_number, name, file_name, kind, lifecycle_state, revision_label, checked_out_by, updated_at
            FROM document WHERE project_id=@ProjectId AND file_name=@FileName
            """,
            new { command.ProjectId, command.FileName },
            transaction,
            cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO document_user_access(document_id,username,can_read,granted_at)
            VALUES(@DocumentId,@Actor,1,@Now)
            ON DUPLICATE KEY UPDATE can_read=1
            """,
            new { DocumentId = row.Id, Actor = actor, Now = now.UtcDateTime },
            transaction,
            cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return MapDocument(row);
    }

    public async Task<bool> HasDocumentReadAccessAsync(Guid documentId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        if (role == UserRole.Administrator) return true;
        await using var connection = await OpenAsync(cancellationToken);
        var value = await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
            """
            SELECT CASE
                WHEN dua.can_read IS NOT NULL THEN dua.can_read
                WHEN p.owner = @Actor THEN 1
                ELSE COALESCE(pua.can_read, 0)
            END
            FROM document d
            INNER JOIN project p ON p.id=d.project_id
            LEFT JOIN project_user_access pua ON pua.project_id=p.id AND pua.username=@Actor
            LEFT JOIN document_user_access dua ON dua.document_id=d.id AND dua.username=@Actor
            WHERE d.id=@DocumentId
            """,
            new { DocumentId = documentId, Actor = actor }, cancellationToken: cancellationToken));
        return value == 1;
    }

    public async Task<DocumentReferenceNode?> GetReferenceTreeAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var json = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT root_json FROM reference_snapshot WHERE project_id = @ProjectId ORDER BY captured_at DESC LIMIT 1",
            new { ProjectId = projectId },
            cancellationToken: cancellationToken));
        return json is null ? null : JsonSerializer.Deserialize<DocumentReferenceNode>(json, jsonOptions);
    }

    public async Task<IReadOnlyList<BomItem>> GetBomAsync(Guid projectId, BomKind kind, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<BomRow>(new CommandDefinition(
            """
            SELECT id, project_id, bom_kind, sequence_no, drawing_number, name, quantity, unit, material, specification, revision_label, is_complete
            FROM bom_item
            WHERE project_id = @ProjectId AND bom_kind = @Kind
            ORDER BY sequence_no
            """,
            new { ProjectId = projectId, Kind = kind.ToString() },
            cancellationToken: cancellationToken));
        return rows.Select(MapBomItem).ToArray();
    }

    public async Task<IReadOnlyList<ReleasePackage>> ListReleasePackagesAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<ReleasePackageRow>(new CommandDefinition(
            """
            SELECT id, project_id, package_number, state, reference_snapshot_id, mechanical_bom_revision, electrical_bom_revision, published_at, published_path, created_at
            FROM release_package
            WHERE project_id = @ProjectId
            ORDER BY created_at DESC
            """,
            new { ProjectId = projectId },
            cancellationToken: cancellationToken));
        var result = new List<ReleasePackage>();
        foreach (var row in rows)
        {
            result.Add(await MapReleasePackageAsync(connection, null, row, cancellationToken));
        }

        return result;
    }

    public async Task<ReleasePackage?> FindReleasePackageAsync(Guid releasePackageId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await FindReleasePackageAsync(connection, null, releasePackageId, cancellationToken);
    }

    public async Task<UserAccount?> FindUserAsync(string username, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<UserRow>(new CommandDefinition(
            "SELECT id, username, display_name, password_hash, role, is_active FROM pdm_user WHERE username = @Username LIMIT 1",
            new { Username = username },
            cancellationToken: cancellationToken));
        return row is null ? null : new UserAccount(row.Id, row.Username, row.DisplayName, row.PasswordHash, Enum.Parse<UserRole>(row.Role), row.IsActive);
    }

    public async Task<int> CountUsersAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM pdm_user", cancellationToken: cancellationToken));
    }

    private async Task<MySqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task<Project?> FindProjectAsync(DbConnection connection, DbTransaction? transaction, Guid projectId, CancellationToken cancellationToken)
    {
        var row = await connection.QuerySingleOrDefaultAsync<ProjectRow>(new CommandDefinition(
            "SELECT id, code, name, owner, vault_location, release_location, is_active FROM project WHERE id = @ProjectId",
            new { ProjectId = projectId },
            transaction,
            cancellationToken: cancellationToken));
        return row is null ? null : MapProject(row);
    }

    private static async Task<PdmDocument?> FindDocumentAsync(DbConnection connection, DbTransaction? transaction, Guid documentId, CancellationToken cancellationToken)
    {
        var row = await connection.QuerySingleOrDefaultAsync<DocumentRow>(new CommandDefinition(
            """
            SELECT id, project_id, drawing_number, name, file_name, kind, lifecycle_state, revision_label, checked_out_by, updated_at
            FROM document WHERE id = @DocumentId
            """,
            new { DocumentId = documentId },
            transaction,
            cancellationToken: cancellationToken));
        return row is null ? null : MapDocument(row);
    }

    private async Task<ReleasePackage?> FindReleasePackageAsync(DbConnection connection, DbTransaction? transaction, Guid packageId, CancellationToken cancellationToken)
    {
        var row = await connection.QuerySingleOrDefaultAsync<ReleasePackageRow>(new CommandDefinition(
            """
            SELECT id, project_id, package_number, state, reference_snapshot_id, mechanical_bom_revision, electrical_bom_revision, published_at, published_path, created_at
            FROM release_package WHERE id = @PackageId
            """,
            new { PackageId = packageId },
            transaction,
            cancellationToken: cancellationToken));
        return row is null ? null : await MapReleasePackageAsync(connection, transaction, row, cancellationToken);
    }

    private static Project MapProject(ProjectRow row) =>
        new(row.Id, row.Code, row.Name, row.Owner, row.VaultLocation, row.ReleaseLocation, row.IsActive);

    private static PdmDocument MapDocument(DocumentRow row) =>
        new(row.Id, row.ProjectId, row.DrawingNumber, row.Name, row.FileName, Enum.Parse<DocumentKind>(row.Kind), Enum.Parse<DocumentLifecycleState>(row.LifecycleState), RevisionLabel.Parse(row.RevisionLabel), row.CheckedOutBy, DateTime.SpecifyKind(row.UpdatedAt, DateTimeKind.Utc));

    private static BomItem MapBomItem(BomRow row) =>
        new(row.Id, row.ProjectId, Enum.Parse<BomKind>(row.BomKind), row.SequenceNo, row.DrawingNumber, row.Name, row.Quantity, row.Unit, row.Material, row.Specification, row.RevisionLabel, row.IsComplete);

    private static async Task<ReleasePackage> MapReleasePackageAsync(DbConnection connection, DbTransaction? transaction, ReleasePackageRow row, CancellationToken cancellationToken)
    {
        var taskRows = await connection.QueryAsync<ApprovalTaskRow>(new CommandDefinition(
            """
            SELECT id, release_package_id, stage, assignee, decision_by, decision_value, decision_comment, decided_at
            FROM approval_task WHERE release_package_id = @PackageId ORDER BY stage
            """,
            new { PackageId = row.Id },
            transaction,
            cancellationToken: cancellationToken));
        var tasks = taskRows.Select(task => new ApprovalTask(
            task.Id,
            task.ReleasePackageId,
            Enum.Parse<ApprovalStage>(task.Stage),
            task.Assignee,
            task.DecisionBy,
            task.DecisionValue is null ? null : Enum.Parse<ApprovalDecision>(task.DecisionValue),
            task.DecisionComment,
            task.DecidedAt is null ? null : new DateTimeOffset(DateTime.SpecifyKind(task.DecidedAt.Value, DateTimeKind.Utc)))).ToArray();
        return new ReleasePackage(
            row.Id,
            row.ProjectId,
            row.PackageNumber,
            Enum.Parse<ReleasePackageState>(row.State),
            row.ReferenceSnapshotId,
            row.MechanicalBomRevision,
            row.ElectricalBomRevision,
            tasks,
            DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc),
            row.PublishedAt is null ? null : new DateTimeOffset(DateTime.SpecifyKind(row.PublishedAt.Value, DateTimeKind.Utc)),
            row.PublishedPath);
    }

    private sealed class ProjectRow
    {
        public Guid Id { get; init; }
        public string Code { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string Owner { get; init; } = string.Empty;
        public string VaultLocation { get; init; } = string.Empty;
        public string ReleaseLocation { get; init; } = string.Empty;
        public bool IsActive { get; init; }
    }

    private sealed class DocumentRow
    {
        public Guid Id { get; init; }
        public Guid ProjectId { get; init; }
        public string DrawingNumber { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string FileName { get; init; } = string.Empty;
        public string Kind { get; init; } = string.Empty;
        public string LifecycleState { get; init; } = string.Empty;
        public string RevisionLabel { get; init; } = string.Empty;
        public string? CheckedOutBy { get; init; }
        public DateTime UpdatedAt { get; init; }
    }

    private sealed class BomRow
    {
        public Guid Id { get; init; }
        public Guid ProjectId { get; init; }
        public string BomKind { get; init; } = string.Empty;
        public int SequenceNo { get; init; }
        public string DrawingNumber { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public decimal Quantity { get; init; }
        public string Unit { get; init; } = string.Empty;
        public string? Material { get; init; }
        public string? Specification { get; init; }
        public string RevisionLabel { get; init; } = string.Empty;
        public bool IsComplete { get; init; }
    }

    private sealed class ReleasePackageRow
    {
        public Guid Id { get; init; }
        public Guid ProjectId { get; init; }
        public string PackageNumber { get; init; } = string.Empty;
        public string State { get; init; } = string.Empty;
        public Guid ReferenceSnapshotId { get; init; }
        public string MechanicalBomRevision { get; init; } = string.Empty;
        public string ElectricalBomRevision { get; init; } = string.Empty;
        public DateTime? PublishedAt { get; init; }
        public string? PublishedPath { get; init; }
        public DateTime CreatedAt { get; init; }
    }

    private sealed class ApprovalTaskRow
    {
        public Guid Id { get; init; }
        public Guid ReleasePackageId { get; init; }
        public string Stage { get; init; } = string.Empty;
        public string Assignee { get; init; } = string.Empty;
        public string? DecisionBy { get; init; }
        public string? DecisionValue { get; init; }
        public string? DecisionComment { get; init; }
        public DateTime? DecidedAt { get; init; }
    }

    private sealed class UserRow
    {
        public Guid Id { get; init; }
        public string Username { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public string PasswordHash { get; init; } = string.Empty;
        public string Role { get; init; } = string.Empty;
        public bool IsActive { get; init; }
    }
}
