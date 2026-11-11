using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed class MySqlProjectFileFormRepository(IOptions<PdmDatabaseOptions> options, MySqlProjectFileRepository files, TimeProvider clock) : IProjectFileFormRepository
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string SummaryColumns = "id Id,form_number Number,row_version RowVersion,archived_revision ArchivedRevision,file_id FileId,file_version FileVersion,evidence_file_id EvidenceFileId,updated_at UpdatedAt,archived_layout_version ArchivedLayoutVersion";
    private const string Where = " WHERE project_id=@ProjectId AND form_kind=@Kind AND id=@Id";

    public async Task<IReadOnlyList<ProjectFileFormSummary>> ListAsync(Guid projectId, string kind, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        var rows = await connection.QueryAsync<FormRow>(new CommandDefinition(
            $"SELECT {SummaryColumns} FROM project_file_form WHERE project_id=@ProjectId AND form_kind=@Kind ORDER BY form_sequence DESC",
            new { ProjectId = projectId, Kind = kind }, cancellationToken: ct));
        return rows.Select(row => row.Summary()).ToArray();
    }

    public async Task<ProjectFileForm?> FindAsync(Guid projectId, string kind, Guid id, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        return (await ReadAsync(connection, null, projectId, kind, id, ct))?.Form();
    }

    public async Task<ProjectFileForm> SaveAsync(Project project, string kind, Guid id, long expectedRevision,
        ProjectFileFormData data, string actor, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await LockProjectAsync(connection, transaction, project.Id, ct);
        var current = await ReadAsync(connection, transaction, project.Id, kind, id, ct);
        var payload = JsonSerializer.Serialize(data, Json);
        var now = clock.GetUtcNow();
        ProjectFileFormSummary summary;
        if (current is null)
        {
            if (expectedRevision != 0) throw new PdmConflictException("单据已变动，请重新打开后编辑。");
            var sequence = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COALESCE(MAX(form_sequence),0)+1 FROM project_file_form WHERE project_id=@ProjectId AND form_kind=@Kind",
                new { ProjectId = project.Id, Kind = kind }, transaction, cancellationToken: ct));
            if (ProjectFileFormIdentity.IsSinglePerProject(kind) && sequence != 1)
                throw new PdmConflictException("该项目已有同类型单据，请重新打开后编辑。");
            var number = ProjectFileFormIdentity.Number(project, kind, sequence);
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO project_file_form(id,project_id,form_kind,form_sequence,form_number,payload_json,row_version,updated_by,updated_at)
                VALUES(@Id,@ProjectId,@Kind,@Sequence,@Number,@Payload,1,@Actor,@Now)
                """, new { Id = id, ProjectId = project.Id, Kind = kind, Sequence = sequence, Number = number, Payload = payload, Actor = actor, Now = now.UtcDateTime }, transaction, cancellationToken: ct));
            summary = new(id, number, 1, 0, null, 0, null, now);
        }
        else
        {
            // Comparing normalized JSON avoids creating a new version on a no-op save or retried request.
            if (current.RowVersion >= expectedRevision && current.RowVersion - expectedRevision <= 1
                && JsonSerializer.Serialize(current.Form().Data, Json) == payload) return current.Form();
            if (current.RowVersion != expectedRevision) throw new PdmConflictException("单据已被其他人修改，请重新打开后编辑。");
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE project_file_form SET payload_json=@Payload,row_version=row_version+1,updated_by=@Actor,updated_at=@Now" + Where,
                new { Payload = payload, Actor = actor, Now = now.UtcDateTime, ProjectId = project.Id, Kind = kind, Id = id }, transaction, cancellationToken: ct));
            summary = current.Summary() with { RowVersion = current.RowVersion + 1, UpdatedAt = now };
        }
        await AuditAsync(connection, transaction, id, actor, "draft.save", $"{summary.Number} · 草稿修订 {summary.RowVersion}", now, ct);
        await transaction.CommitAsync(ct);
        return new(summary, data);
    }

    public async Task<ProjectFileFormArchiveResult> ArchiveAsync(Project project, string kind, Guid id, long expectedRevision,
        int expectedFileVersion, int layoutVersion, StoredProjectFileUpload workbook, StoredProjectFileUpload? evidence, string actor, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await LockProjectAsync(connection, transaction, project.Id, ct);
        var current = await ReadAsync(connection, transaction, project.Id, kind, id, ct)
            ?? throw new PdmNotFoundException("当前项目中没有这张单据。");
        if (current.RowVersion != expectedRevision) throw new PdmConflictException("单据已更新，请重新打开后生成存档。");
        if (current.ArchivedRevision == expectedRevision && current.ArchivedLayoutVersion >= layoutVersion) return new(current.Summary(), false);
        var now = clock.GetUtcNow();
        var title = ProjectFileFormPermissions.Title(kind);
        var added = await files.AddVersionInTransactionAsync(connection, transaction, workbook, actor, $"{title} · {current.Number}", ct,
            generated: true, expectedFileId: current.FileId, expectedVersion: expectedFileVersion);
        Guid? evidenceId = current.EvidenceFileId;
        if (evidence is not null)
            evidenceId = (await files.AddVersionInTransactionAsync(connection, transaction, evidence, actor, $"{current.Number} · 对应 Excel V{added.Version}", ct,
                generated: true, expectedFileId: current.EvidenceFileId)).Id;
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE project_file_form SET archived_revision=@Revision,archived_layout_version=@LayoutVersion,file_id=@FileId,file_version=@Version,
                evidence_file_id=@EvidenceId,updated_by=@Actor,updated_at=@Now
            """ + Where, new { Revision = expectedRevision, LayoutVersion = layoutVersion, FileId = added.Id, added.Version, EvidenceId = evidenceId,
                Actor = actor, Now = now.UtcDateTime, ProjectId = project.Id, Kind = kind, Id = id }, transaction, cancellationToken: ct));
        await AuditAsync(connection, transaction, id, actor, "archive", $"{current.Number} · 文件 V{added.Version}", now, ct);
        await transaction.CommitAsync(CancellationToken.None);
        return new(current.Summary() with { ArchivedRevision = expectedRevision, FileId = added.Id, FileVersion = added.Version,
            EvidenceFileId = evidenceId, UpdatedAt = now, ArchivedLayoutVersion = layoutVersion }, true);
    }

    private static Task<FormRow?> ReadAsync(MySqlConnection connection, MySqlTransaction? transaction, Guid projectId, string kind, Guid id, CancellationToken ct) =>
        connection.QuerySingleOrDefaultAsync<FormRow>(new CommandDefinition(
            $"SELECT {SummaryColumns},payload_json Payload FROM project_file_form" + Where + (transaction is null ? "" : " FOR UPDATE"),
            new { ProjectId = projectId, Kind = kind, Id = id }, transaction, cancellationToken: ct));

    // Keep project and form lock order identical for saving and archiving.
    private static async Task LockProjectAsync(MySqlConnection connection, MySqlTransaction transaction, Guid projectId, CancellationToken ct)
    {
        if (await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition("SELECT id FROM project WHERE id=@Id FOR UPDATE",
            new { Id = projectId }, transaction, cancellationToken: ct)) is null)
            throw new PdmNotFoundException("项目不存在。");
    }

    private static Task AuditAsync(MySqlConnection connection, MySqlTransaction transaction, Guid id, string actor,
        string action, string detail, DateTimeOffset now, CancellationToken ct) => connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO audit_entry(id,occurred_at,actor,action_name,entity_type,entity_id,detail_json)
            VALUES(@Id,@At,@Actor,@Action,'ProjectFileForm',@EntityId,@Detail)
            """, new { Id = Guid.NewGuid(), At = now.UtcDateTime, Actor = actor, Action = "project.file.form." + action,
            EntityId = id.ToString(), Detail = JsonSerializer.Serialize(new { detail }, Json) }, transaction, cancellationToken: ct));

    private async Task<MySqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new MySqlConnection(options.Value.ConnectionString);
        try { await connection.OpenAsync(ct); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }

    private sealed class FormRow
    {
        public Guid Id { get; init; }
        public string Number { get; init; } = "";
        public long RowVersion { get; init; }
        public long ArchivedRevision { get; init; }
        public int ArchivedLayoutVersion { get; init; }
        public Guid? FileId { get; init; }
        public int FileVersion { get; init; }
        public Guid? EvidenceFileId { get; init; }
        public DateTime UpdatedAt { get; init; }
        public string? Payload { get; init; }
        public ProjectFileFormSummary Summary() => new(Id, Number, RowVersion, ArchivedRevision, FileId, FileVersion, EvidenceFileId,
            new(DateTime.SpecifyKind(UpdatedAt, DateTimeKind.Utc)), ArchivedLayoutVersion);
        public ProjectFileForm Form() => new(Summary(), JsonSerializer.Deserialize<ProjectFileFormData>(Payload!, Json)
            ?? throw new PdmRuleException("单据数据无法读取，请联系管理员。"));
    }
}
