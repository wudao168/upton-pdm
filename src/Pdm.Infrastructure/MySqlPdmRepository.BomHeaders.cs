using Dapper;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed partial class MySqlPdmRepository
{
    public async Task<IReadOnlyList<ProjectBomHeaderBinding>> ListProjectBomHeaderBindingsAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<ProjectBomHeaderRow>(new CommandDefinition(
            "SELECT project_id,bom_kind,parent_bom_kind,material_id,updated_by,updated_at,row_version FROM project_bom_header WHERE project_id=@ProjectId ORDER BY FIELD(bom_kind,'Master','Standard','NonStandard','Electrical')",
            new { ProjectId = projectId }, cancellationToken: cancellationToken));
        return rows.Select(MapProjectBomHeader).ToArray();
    }

    public async Task<ProjectBomHeaderBinding> SaveProjectBomHeaderBindingAsync(Guid projectId, ProjectBomHeaderKind kind, Guid materialId, long expectedRowVersion, string actor, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var existing = await connection.QuerySingleOrDefaultAsync<ProjectBomHeaderRow>(new CommandDefinition(
            "SELECT project_id,bom_kind,parent_bom_kind,material_id,updated_by,updated_at,row_version FROM project_bom_header WHERE project_id=@ProjectId AND bom_kind=@Kind FOR UPDATE",
            new { ProjectId = projectId, Kind = kind.ToString() }, transaction, cancellationToken: cancellationToken));
        var duplicate = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT bom_kind FROM project_bom_header WHERE project_id=@ProjectId AND material_id=@MaterialId AND bom_kind<>@Kind FOR UPDATE",
            new { ProjectId = projectId, MaterialId = materialId, Kind = kind.ToString() }, transaction, cancellationToken: cancellationToken));
        if (!string.IsNullOrWhiteSpace(duplicate)) throw new PdmConflictException("同一项目的主BOM及三类子BOM必须分别使用不同料号。");

        var now = timeProvider.GetUtcNow();
        if (existing is null)
        {
            if (expectedRowVersion != 0) throw new PdmConflictException("BOM料号绑定已变化，请刷新后重试。");
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO project_bom_header(project_id,bom_kind,parent_bom_kind,material_id,updated_by,updated_at,row_version) VALUES(@ProjectId,@Kind,@ParentKind,@MaterialId,@Actor,@Now,1)",
                new { ProjectId = projectId, Kind = kind.ToString(), ParentKind = kind == ProjectBomHeaderKind.Master ? null : ProjectBomHeaderKind.Master.ToString(), MaterialId = materialId, Actor = actor, Now = now.UtcDateTime },
                transaction, cancellationToken: cancellationToken));
        }
        else
        {
            var affected = await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE project_bom_header SET material_id=@MaterialId,updated_by=@Actor,updated_at=@Now,row_version=row_version+1 WHERE project_id=@ProjectId AND bom_kind=@Kind AND row_version=@ExpectedRowVersion",
                new { ProjectId = projectId, Kind = kind.ToString(), MaterialId = materialId, Actor = actor, Now = now.UtcDateTime, ExpectedRowVersion = expectedRowVersion },
                transaction, cancellationToken: cancellationToken));
            if (affected != 1) throw new PdmConflictException("BOM料号绑定已变化，请刷新后重试。");
        }

        var saved = await connection.QuerySingleAsync<ProjectBomHeaderRow>(new CommandDefinition(
            "SELECT project_id,bom_kind,parent_bom_kind,material_id,updated_by,updated_at,row_version FROM project_bom_header WHERE project_id=@ProjectId AND bom_kind=@Kind",
            new { ProjectId = projectId, Kind = kind.ToString() }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return MapProjectBomHeader(saved);
    }

    private static ProjectBomHeaderBinding MapProjectBomHeader(ProjectBomHeaderRow row) => new(
        row.ProjectId, Enum.Parse<ProjectBomHeaderKind>(row.BomKind),
        string.IsNullOrWhiteSpace(row.ParentBomKind) ? null : Enum.Parse<ProjectBomHeaderKind>(row.ParentBomKind),
        row.MaterialId, row.UpdatedBy, AsUtc(row.UpdatedAt), row.RowVersion);

    private sealed class ProjectBomHeaderRow
    {
        public Guid ProjectId { get; init; }
        public string BomKind { get; init; } = string.Empty;
        public string? ParentBomKind { get; init; }
        public Guid MaterialId { get; init; }
        public string UpdatedBy { get; init; } = string.Empty;
        public DateTime UpdatedAt { get; init; }
        public long RowVersion { get; init; }
    }
}
