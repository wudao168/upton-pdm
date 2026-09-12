using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed class MySqlProjectPlanningRepository : IProjectPlanningRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly string connectionString;

    public MySqlProjectPlanningRepository(IOptions<PdmDatabaseOptions> options)
    {
        connectionString = options.Value.ConnectionString;
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    public async Task<IReadOnlyList<ProjectPlanTemplate>> ListTemplatesAsync(bool includeInactive, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<PayloadRow>(new CommandDefinition(
            "SELECT payload_json,row_version FROM project_plan_template WHERE (@IncludeInactive OR is_active=1) ORDER BY name",
            new { IncludeInactive = includeInactive }, cancellationToken: cancellationToken));
        return rows.Select(row => Deserialize<ProjectPlanTemplate>(row) with { RowVersion = row.RowVersion }).ToArray();
    }

    public async Task<ProjectPlanTemplate?> FindTemplateAsync(Guid templateId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<PayloadRow>(new CommandDefinition(
            "SELECT payload_json,row_version FROM project_plan_template WHERE id=@TemplateId",
            new { TemplateId = templateId }, cancellationToken: cancellationToken));
        return row is null ? null : Deserialize<ProjectPlanTemplate>(row) with { RowVersion = row.RowVersion };
    }

    public async Task<ProjectPlanTemplate> SaveTemplateAsync(ProjectPlanTemplate template, long? expectedRowVersion, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var currentVersion = await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT row_version FROM project_plan_template WHERE id=@Id FOR UPDATE", new { template.Id }, transaction, cancellationToken: cancellationToken));
        if (currentVersion is null && expectedRowVersion is not null) throw new PdmConflictException("计划模板已不存在，请刷新后重试。");
        if (currentVersion is not null && currentVersion != expectedRowVersion) throw new PdmConflictException("计划模板已被其他用户修改，请刷新后重试。");
        var saved = template with { RowVersion = (currentVersion ?? 0) + 1 };
        if (currentVersion is null)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO project_plan_template(id,name,project_type_code,is_active,payload_json,updated_at,row_version) VALUES(@Id,@Name,@ProjectTypeCode,@IsActive,@Payload,@UpdatedAt,1)",
                new { saved.Id, saved.Name, saved.ProjectTypeCode, saved.IsActive, Payload = Serialize(saved), UpdatedAt = saved.UpdatedAt.UtcDateTime }, transaction, cancellationToken: cancellationToken));
        }
        else
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE project_plan_template SET name=@Name,project_type_code=@ProjectTypeCode,is_active=@IsActive,payload_json=@Payload,updated_at=@UpdatedAt,row_version=@RowVersion WHERE id=@Id",
                new { saved.Id, saved.Name, saved.ProjectTypeCode, saved.IsActive, Payload = Serialize(saved), UpdatedAt = saved.UpdatedAt.UtcDateTime, saved.RowVersion }, transaction, cancellationToken: cancellationToken));
        }
        await transaction.CommitAsync(cancellationToken);
        return saved;
    }

    public async Task<ProjectPlan?> FindPlanAsync(Guid projectId, CancellationToken cancellationToken, bool includeDeleted = false)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<PayloadRow>(new CommandDefinition(
            "SELECT payload_json,row_version FROM project_plan WHERE project_id=@ProjectId",
            new { ProjectId = projectId }, cancellationToken: cancellationToken));
        var plan = row is null ? null : Deserialize<ProjectPlan>(row) with { RowVersion = row.RowVersion };
        return includeDeleted || plan is { IsDeleted: false } ? plan : null;
    }

    public async Task<IReadOnlyList<ProjectPlan>> ListPlansAsync(IReadOnlyCollection<Guid>? projectIds, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var rows = projectIds is null
            ? await connection.QueryAsync<PayloadRow>(new CommandDefinition("SELECT payload_json,row_version FROM project_plan", cancellationToken: cancellationToken))
            : projectIds.Count == 0
                ? []
                : await connection.QueryAsync<PayloadRow>(new CommandDefinition("SELECT payload_json,row_version FROM project_plan WHERE project_id IN @ProjectIds", new { ProjectIds = projectIds.ToArray() }, cancellationToken: cancellationToken));
        return rows.Select(row => Deserialize<ProjectPlan>(row) with { RowVersion = row.RowVersion }).Where(plan => !plan.IsDeleted).ToArray();
    }

    public async Task<ProjectPlan> SavePlanAsync(ProjectPlan plan, long? expectedRowVersion, ProjectPlanVersion? version, CancellationToken cancellationToken)
        => (await SavePlansAsync([new(plan, expectedRowVersion, version)], cancellationToken))[0];

    public async Task<IReadOnlyList<ProjectPlan>> SavePlansAsync(IReadOnlyList<ProjectPlanWrite> writes, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var saved = new List<ProjectPlan>();
        foreach (var write in writes.OrderBy(item => item.Plan.ProjectId))
            saved.Add(await SavePlanCoreAsync(connection, transaction, write.Plan, write.ExpectedRowVersion, write.Version, cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return writes.Select(write => saved.Single(plan => plan.ProjectId == write.Plan.ProjectId)).ToArray();
    }

    public async Task DeletePlansAsync(IReadOnlyDictionary<Guid, long> expectedRowVersions, CancellationToken cancellationToken)
    {
        if (expectedRowVersions.Count == 0) return;
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var projectIds = expectedRowVersions.Keys.ToArray();
        var rows = (await connection.QueryAsync<PlanRevisionRow>(new CommandDefinition(
            "SELECT project_id,row_version FROM project_plan WHERE project_id IN @ProjectIds FOR UPDATE",
            new { ProjectIds = projectIds }, transaction, cancellationToken: cancellationToken))).ToArray();
        if (rows.Length != projectIds.Length) throw new PdmConflictException("计划已不存在，请刷新后重试。");
        if (rows.Any(row => expectedRowVersions[row.ProjectId] != row.RowVersion))
            throw new PdmConflictException("计划数据已更新，请重新确认后操作。");
        await connection.ExecuteAsync(new CommandDefinition("DELETE FROM project_plan WHERE project_id IN @ProjectIds",
            new { ProjectIds = projectIds }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<ProjectPlan> SavePlanCoreAsync(MySqlConnection connection, MySqlTransaction transaction, ProjectPlan plan, long? expectedRowVersion, ProjectPlanVersion? version, CancellationToken cancellationToken)
    {
        var currentRow = await connection.QuerySingleOrDefaultAsync<PayloadRow>(new CommandDefinition(
            "SELECT payload_json,row_version FROM project_plan WHERE project_id=@ProjectId FOR UPDATE", new { plan.ProjectId }, transaction, cancellationToken: cancellationToken));
        var current = currentRow is null ? null : Deserialize<ProjectPlan>(currentRow);
        var currentVersion = currentRow?.RowVersion;
        if (current is { IsDeleted: true } && expectedRowVersion is null)
        {
            // Reuse the archived container, preserving history and rejecting stale edits through its revision.
            plan = plan with { Id = current.Id };
            expectedRowVersion = currentVersion;
        }
        else if (current is not null && (current.Id != plan.Id || current.IsDeleted)) throw new PdmConflictException("计划已不存在或已被替换，请刷新后重试。");
        if (currentVersion is null && expectedRowVersion is not null) throw new PdmConflictException("计划已不存在，请刷新后重试。");
        if (currentVersion is not null && currentVersion != expectedRowVersion) throw new PdmConflictException("计划数据已更新，请重新确认后操作。");
        var saved = plan with { RowVersion = (currentVersion ?? 0) + 1 };
        if (currentVersion is null)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO project_plan(id,project_id,template_id,current_stage,planned_start,planned_finish,forecast_finish,payload_json,updated_at,row_version) VALUES(@Id,@ProjectId,@TemplateId,@CurrentStage,@PlannedStart,@PlannedFinish,@ForecastFinish,@Payload,@UpdatedAt,1)",
                PlanParameters(saved), transaction, cancellationToken: cancellationToken));
        }
        else
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE project_plan SET template_id=@TemplateId,current_stage=@CurrentStage,planned_start=@PlannedStart,planned_finish=@PlannedFinish,forecast_finish=@ForecastFinish,payload_json=@Payload,updated_at=@UpdatedAt,row_version=@RowVersion WHERE id=@Id",
                PlanParameters(saved), transaction, cancellationToken: cancellationToken));
        }
        if (version is not null)
        {
            var number = await connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COALESCE(MAX(version_number),0)+1 FROM project_plan_version WHERE plan_id=@PlanId", new { PlanId = plan.Id }, transaction, cancellationToken: cancellationToken));
            var numbered = version with { VersionNumber = number };
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO project_plan_version(id,plan_id,version_number,change_reason,snapshot_json,created_by,created_at) VALUES(@Id,@PlanId,@VersionNumber,@ChangeReason,@Snapshot,@CreatedBy,@CreatedAt)",
                new { numbered.Id, numbered.PlanId, numbered.VersionNumber, numbered.ChangeReason, Snapshot = Serialize(numbered.Snapshot), numbered.CreatedBy, CreatedAt = numbered.CreatedAt.UtcDateTime }, transaction, cancellationToken: cancellationToken));
        }
        return saved;
    }

    public async Task<IReadOnlyList<ProjectPlanVersion>> ListVersionsAsync(Guid planId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<VersionRow>(new CommandDefinition(
            "SELECT id,plan_id,version_number,change_reason,snapshot_json,created_by,created_at FROM project_plan_version WHERE plan_id=@PlanId ORDER BY version_number DESC",
            new { PlanId = planId }, cancellationToken: cancellationToken));
        return rows.Select(row => new ProjectPlanVersion(row.Id, row.PlanId, row.VersionNumber, row.ChangeReason, JsonSerializer.Deserialize<ProjectPlan>(row.SnapshotJson, JsonOptions)!, row.CreatedBy, new DateTimeOffset(DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc)))).ToArray();
    }

    private static object PlanParameters(ProjectPlan plan) => new
    {
        plan.Id,
        plan.ProjectId,
        plan.TemplateId,
        CurrentStage = plan.CurrentStage.ToString(),
        PlannedStart = plan.PlannedStart.ToDateTime(TimeOnly.MinValue),
        PlannedFinish = plan.PlannedFinish.ToDateTime(TimeOnly.MinValue),
        ForecastFinish = plan.ForecastFinish.ToDateTime(TimeOnly.MinValue),
        Payload = Serialize(plan),
        UpdatedAt = plan.UpdatedAt.UtcDateTime,
        plan.RowVersion
    };

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);
    private static T Deserialize<T>(PayloadRow row) => JsonSerializer.Deserialize<T>(row.PayloadJson, JsonOptions) ?? throw new InvalidOperationException("项目计划数据无法解析。");
    private async Task<MySqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private sealed class PayloadRow
    {
        public string PayloadJson { get; init; } = string.Empty;
        public long RowVersion { get; init; }
    }

    private sealed class VersionRow
    {
        public Guid Id { get; init; }
        public Guid PlanId { get; init; }
        public int VersionNumber { get; init; }
        public string ChangeReason { get; init; } = string.Empty;
        public string SnapshotJson { get; init; } = string.Empty;
        public string CreatedBy { get; init; } = string.Empty;
        public DateTime CreatedAt { get; init; }
    }

    private sealed class PlanRevisionRow
    {
        public Guid ProjectId { get; init; }
        public long RowVersion { get; init; }
    }
}
