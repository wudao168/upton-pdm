using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed class MySqlExpenseRequestRepository(IOptions<PdmDatabaseOptions> options) : IExpenseRequestRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public async Task<ExpenseRequest?> FindAsync(Guid id, CancellationToken ct)
    {
        await using var connection = new MySqlConnection(options.Value.ConnectionString);
        var json = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT payload_json FROM project_expense_request WHERE id=@id", new { id }, cancellationToken: ct));
        return json is null ? null : JsonSerializer.Deserialize<ExpenseRequest>(json, JsonOptions);
    }
    public async Task<IReadOnlyList<ExpenseRequest>> ListAsync(Guid projectId, CancellationToken ct)
    {
        await using var connection = new MySqlConnection(options.Value.ConnectionString);
        var rows = await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT payload_json FROM project_expense_request WHERE project_id=@projectId ORDER BY updated_at DESC,id", new { projectId }, cancellationToken: ct));
        return rows.Select(json => JsonSerializer.Deserialize<ExpenseRequest>(json, JsonOptions)!).ToArray();
    }
    public async Task<ExpenseRequest> SaveAsync(ExpenseRequest request, long expectedRowVersion, IReadOnlyList<UserNotification> notifications, CancellationToken ct)
    {
        await using var connection = new MySqlConnection(options.Value.ConnectionString);
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        // Lock the parent for first saves as well as concurrent decisions on an existing request.
        await connection.ExecuteScalarAsync<long>(new CommandDefinition("SELECT row_version FROM project WHERE id=@ProjectId FOR UPDATE",
            new { request.ProjectId }, transaction, cancellationToken: ct));
        var version = await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT row_version FROM project_expense_request WHERE id=@Id FOR UPDATE", new { request.Id }, transaction, cancellationToken: ct));
        if ((version ?? 0) != expectedRowVersion) throw new PdmConflictException("费用申请已更新，请刷新后再操作。");
        var saved = request with { RowVersion = expectedRowVersion + 1 };
        var parameters = new { saved.Id, saved.ProjectId, saved.RowVersion, Json = JsonSerializer.Serialize(saved, JsonOptions), At = saved.UpdatedAt.UtcDateTime };
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO project_expense_request(id,project_id,row_version,payload_json,updated_at) VALUES(@Id,@ProjectId,@RowVersion,@Json,@At)
            ON DUPLICATE KEY UPDATE row_version=@RowVersion,payload_json=@Json,updated_at=@At;
            INSERT INTO project_expense_request_version(request_id,row_version,payload_json,updated_at) VALUES(@Id,@RowVersion,@Json,@At);
            """, parameters, transaction, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE user_notification SET resolved_at=@At,read_at=COALESCE(read_at,@At)
            WHERE category='ExpenseApprovalPending' AND source_key LIKE @Prefix AND resolved_at IS NULL
            """, new { At = saved.UpdatedAt.UtcDateTime, Prefix = $"expense-request:{saved.Id:N}:%" }, transaction, cancellationToken: ct));
        if (notifications.Count > 0)
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO user_notification(id,recipient_username,category,title,content,project_id,source_key,created_at)
                VALUES(@Id,@Recipient,@Category,@Title,@Content,@ProjectId,@SourceKey,@CreatedAt)
                """, notifications.Select(item => new { item.Id, item.Recipient, item.Category, item.Title, item.Content, item.ProjectId, item.SourceKey,
                    CreatedAt = item.CreatedAt.UtcDateTime }), transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
        return saved;
    }
}
