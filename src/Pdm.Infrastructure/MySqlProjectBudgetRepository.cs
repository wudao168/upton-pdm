using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Upton.Pdm.Application;

namespace Upton.Pdm.Infrastructure;

public sealed class MySqlProjectBudgetRepository(IOptions<PdmDatabaseOptions> options) : IProjectBudgetRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string LaborRatesKey = "budget_assessment_labor_rates";
    public async Task<AssessmentLaborRateSettings?> FindLaborRatesAsync(CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(options.Value.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var payload = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT setting_value FROM pdm_system_setting WHERE setting_key=@Key", new { Key = LaborRatesKey }, cancellationToken: cancellationToken));
        return payload is null ? null : JsonSerializer.Deserialize<AssessmentLaborRateSettings>(payload, JsonOptions);
    }
    public async Task<AssessmentLaborRateSettings> SaveLaborRatesAsync(AssessmentLaborRateSettings settings, long expectedRowVersion, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(options.Value.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT IGNORE INTO pdm_system_setting(setting_key,setting_value,updated_at) VALUES(@Key,@Payload,@Now)",
            new { Key = LaborRatesKey, Payload = JsonSerializer.Serialize(new AssessmentLaborRateSettings(new Dictionary<string, decimal?>()), JsonOptions), Now = DateTime.UtcNow }, transaction, cancellationToken: cancellationToken));
        var payload = await connection.QuerySingleAsync<string>(new CommandDefinition(
            "SELECT setting_value FROM pdm_system_setting WHERE setting_key=@Key FOR UPDATE", new { Key = LaborRatesKey }, transaction, cancellationToken: cancellationToken));
        if (JsonSerializer.Deserialize<AssessmentLaborRateSettings>(payload, JsonOptions)!.RowVersion != expectedRowVersion)
            throw new PdmConflictException("工时单价已被修改，请刷新后重试。");
        var saved = settings with { RowVersion = expectedRowVersion + 1 };
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE pdm_system_setting SET setting_value=@Payload,updated_at=@Now WHERE setting_key=@Key",
            new { Key = LaborRatesKey, Payload = JsonSerializer.Serialize(saved, JsonOptions), Now = DateTime.UtcNow }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return saved;
    }
    public async Task<ProjectBudget?> FindAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(options.Value.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var payload = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT payload_json FROM project_budget WHERE project_id=@ProjectId", new { ProjectId = projectId }, cancellationToken: cancellationToken));
        return payload is null ? null : JsonSerializer.Deserialize<ProjectBudget>(payload, JsonOptions);
    }
    public async Task<ProjectBudget> SaveAsync(ProjectBudget budget, long expectedRowVersion, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(options.Value.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // Lock the project as well, including when no budget exists yet.
        await connection.ExecuteScalarAsync<long>(new CommandDefinition("SELECT row_version FROM project WHERE id=@ProjectId FOR UPDATE",
            new { budget.ProjectId }, transaction, cancellationToken: cancellationToken));
        var version = await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT row_version FROM project_budget WHERE project_id=@ProjectId FOR UPDATE", new { budget.ProjectId }, transaction, cancellationToken: cancellationToken));
        if ((version ?? 0) != expectedRowVersion) throw new PdmConflictException("预算已被其他用户修改，请刷新后重试。");
        var saved = budget with { RowVersion = expectedRowVersion + 1 };
        var parameters = new { saved.ProjectId, saved.RowVersion, Payload = JsonSerializer.Serialize(saved, JsonOptions), saved.UpdatedBy, UpdatedAt = saved.UpdatedAt?.UtcDateTime };
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO project_budget(project_id,row_version,payload_json,updated_by,updated_at)
            VALUES(@ProjectId,@RowVersion,@Payload,@UpdatedBy,@UpdatedAt)
            ON DUPLICATE KEY UPDATE row_version=@RowVersion,payload_json=@Payload,updated_by=@UpdatedBy,updated_at=@UpdatedAt
            """, parameters, transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO project_budget_version(project_id,row_version,payload_json,updated_by,updated_at)
            VALUES(@ProjectId,@RowVersion,@Payload,@UpdatedBy,@UpdatedAt)
            """, parameters, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return saved;
    }
}
