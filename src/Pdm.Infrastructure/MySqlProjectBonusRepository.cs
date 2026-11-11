using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed class MySqlProjectBonusRepository(IOptions<PdmDatabaseOptions> options) : IProjectBonusRepository
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public async Task<ProjectBonusSettings?> GetSettingsAsync(Guid companyId, CancellationToken ct)
    {
        await using var db = new MySqlConnection(options.Value.ConnectionString);
        var value = await db.QuerySingleOrDefaultAsync<string>(new CommandDefinition("SELECT payload_json FROM project_bonus_settings WHERE company_id=@companyId", new { companyId }, cancellationToken: ct));
        return value is null ? null : JsonSerializer.Deserialize<ProjectBonusSettings>(value, Json);
    }
    public async Task<ProjectBonusSettings> SaveSettingsAsync(Guid companyId, ProjectBonusSettings settings, long expectedVersion, CancellationToken ct)
    {
        await using var db = new MySqlConnection(options.Value.ConnectionString); await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.ExecuteAsync(new CommandDefinition("INSERT IGNORE INTO project_bonus_settings(company_id,row_version) VALUES(@companyId,0)", new { companyId }, tx, cancellationToken: ct));
        var version = await db.ExecuteScalarAsync<long>(new CommandDefinition("SELECT row_version FROM project_bonus_settings WHERE company_id=@companyId FOR UPDATE", new { companyId }, tx, cancellationToken: ct));
        if (version != expectedVersion) throw new PdmConflictException("奖金规则已被其他人更新，请刷新后重试。");
        var saved = settings with { Version = version + 1 };
        await db.ExecuteAsync(new CommandDefinition("""
            UPDATE project_bonus_settings SET row_version=@Version,payload_json=@Payload WHERE company_id=@companyId;
            INSERT INTO project_bonus_settings_history(company_id,row_version,payload_json) VALUES(@companyId,@Version,@Payload);
            """, new { companyId, saved.Version, Payload = JsonSerializer.Serialize(saved, Json) }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct); return saved;
    }
    public async Task<ProjectBonusBook?> FindAsync(Guid projectId, CancellationToken ct)
    {
        await using var db = new MySqlConnection(options.Value.ConnectionString);
        var value = await db.QuerySingleOrDefaultAsync<string>(new CommandDefinition("SELECT payload_json FROM project_bonus_book WHERE project_id=@projectId", new { projectId }, cancellationToken: ct));
        return value is null ? null : JsonSerializer.Deserialize<ProjectBonusBook>(value, Json);
    }
    public async Task<IReadOnlyList<ProjectBonusBook>> HistoryAsync(Guid projectId, CancellationToken ct)
    {
        await using var db = new MySqlConnection(options.Value.ConnectionString);
        var rows = await db.QueryAsync<string>(new CommandDefinition("SELECT payload_json FROM project_bonus_history WHERE project_id=@projectId ORDER BY row_version DESC LIMIT 100", new { projectId }, cancellationToken: ct));
        return rows.Select(row => JsonSerializer.Deserialize<ProjectBonusBook>(row, Json)!).ToArray();
    }
    public async Task<ProjectBonusBook> SaveAsync(ProjectBonusBook book, long expectedVersion, IReadOnlyList<UserNotification> notifications, CancellationToken ct)
    {
        await using var db = new MySqlConnection(options.Value.ConnectionString); await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.ExecuteScalarAsync<long>(new CommandDefinition("SELECT row_version FROM project WHERE id=@ProjectId FOR UPDATE", new { book.ProjectId }, tx, cancellationToken: ct));
        var version = await db.ExecuteScalarAsync<long?>(new CommandDefinition("SELECT row_version FROM project_bonus_book WHERE project_id=@ProjectId FOR UPDATE", new { book.ProjectId }, tx, cancellationToken: ct));
        if ((version ?? 0) != expectedVersion) throw new PdmConflictException("奖金申请已更新，请刷新后重试，避免覆盖其他部门的处理结果。");
        var saved = book with { RowVersion = expectedVersion + 1 };
        await db.ExecuteAsync(new CommandDefinition("""
            INSERT INTO project_bonus_book(project_id,row_version,payload_json) VALUES(@ProjectId,@RowVersion,@Payload)
            ON DUPLICATE KEY UPDATE row_version=@RowVersion,payload_json=@Payload;
            INSERT INTO project_bonus_history(project_id,row_version,payload_json) VALUES(@ProjectId,@RowVersion,@Payload);
            UPDATE user_notification SET resolved_at=@At,read_at=COALESCE(read_at,@At)
            WHERE category='ProjectBonusPending' AND project_id=@ProjectId AND resolved_at IS NULL;
            """, new { saved.ProjectId, saved.RowVersion, Payload = JsonSerializer.Serialize(saved, Json), At = DateTime.UtcNow }, tx, cancellationToken: ct));
        if (notifications.Count > 0) await db.ExecuteAsync(new CommandDefinition("""
            INSERT INTO user_notification(id,recipient_username,category,title,content,project_id,source_key,created_at)
            VALUES(@Id,@Recipient,@Category,@Title,@Content,@ProjectId,@SourceKey,@CreatedAt)
            """, notifications.Select(n => new { n.Id, n.Recipient, n.Category, n.Title, n.Content, n.ProjectId, n.SourceKey, CreatedAt = n.CreatedAt.UtcDateTime }), tx, cancellationToken: ct));
        await tx.CommitAsync(ct); return saved;
    }
}
