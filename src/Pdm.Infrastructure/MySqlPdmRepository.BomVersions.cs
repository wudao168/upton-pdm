using System.Data.Common;
using System.Text.Json;
using Dapper;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed partial class MySqlPdmRepository
{
    public async Task<IReadOnlyList<BomVersion>> ListBomVersionsAsync(Guid projectId, BomKind? kind, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<BomVersionRow>(new CommandDefinition(
            $"{BomVersionSelect} WHERE project_id=@ProjectId AND (@Kind IS NULL OR bom_kind=@Kind) ORDER BY bom_kind,version_number DESC",
            new { ProjectId = projectId, Kind = kind?.ToString() }, cancellationToken: cancellationToken));
        return rows.Select(MapBomVersion).ToArray();
    }

    public async Task<BomVersion?> FindBomVersionAsync(Guid projectId, Guid versionId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<BomVersionRow>(new CommandDefinition(
            $"{BomVersionSelect} WHERE project_id=@ProjectId AND id=@VersionId",
            new { ProjectId = projectId, VersionId = versionId }, cancellationToken: cancellationToken));
        return row is null ? null : MapBomVersion(row);
    }

    public async Task<BomVersion> SaveBomDraftAsync(Guid projectId, BomKind kind, IReadOnlyList<BomItem> items, string actor, CancellationToken cancellationToken)
    {
        EnsureVersionedBomKind(kind);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var existing = await connection.QuerySingleOrDefaultAsync<BomVersionRow>(new CommandDefinition(
            $"{BomVersionSelect} WHERE project_id=@ProjectId AND bom_kind=@Kind AND state='Draft' ORDER BY version_number DESC LIMIT 1 FOR UPDATE",
            new { ProjectId = projectId, Kind = kind.ToString() }, transaction, cancellationToken: cancellationToken));
        var snapshot = JsonSerializer.Serialize(items.OrderBy(item => item.Sequence), jsonOptions);
        Guid versionId;
        if (existing is not null)
        {
            versionId = existing.Id;
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE bom_version SET snapshot_json=@Snapshot,updated_by=@Actor,updated_at=@Now,row_version=row_version+1 WHERE id=@Id",
                new { Id = versionId, Snapshot = snapshot, Actor = actor, Now = now.UtcDateTime }, transaction, cancellationToken: cancellationToken));
        }
        else
        {
            var latest = await connection.QuerySingleOrDefaultAsync<BomVersionSequenceRow>(new CommandDefinition(
                "SELECT id,version_number FROM bom_version WHERE project_id=@ProjectId AND bom_kind=@Kind ORDER BY version_number DESC LIMIT 1 FOR UPDATE",
                new { ProjectId = projectId, Kind = kind.ToString() }, transaction, cancellationToken: cancellationToken));
            var versionNumber = (latest?.VersionNumber ?? 0) + 1;
            versionId = Guid.NewGuid();
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO bom_version(id,project_id,bom_kind,version_number,version_label,state,base_version_id,change_number,change_reason,effective_serial_from,effective_serial_to,snapshot_json,created_by,created_at,updated_by,updated_at,released_at,row_version)
                VALUES(@Id,@ProjectId,@Kind,@VersionNumber,@Label,'Draft',@BaseVersionId,NULL,NULL,NULL,NULL,@Snapshot,@Actor,@Now,@Actor,@Now,NULL,1)
                """,
                new { Id = versionId, ProjectId = projectId, Kind = kind.ToString(), VersionNumber = versionNumber, Label = BomVersionLabel(kind, versionNumber), BaseVersionId = latest?.Id, Snapshot = snapshot, Actor = actor, Now = now.UtcDateTime },
                transaction, cancellationToken: cancellationToken));
        }
        await transaction.CommitAsync(cancellationToken);
        return await FindBomVersionAsync(projectId, versionId, cancellationToken)
            ?? throw new PdmNotFoundException("BOM工作版本不存在。");
    }

    public async Task<BomVersion> UpdateBomVersionReleaseInfoAsync(Guid versionId, string changeNumber, string changeReason, string effectiveSerialFrom, string? effectiveSerialTo, IReadOnlyList<string> validationRequiredFields, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE bom_version SET change_number=@ChangeNumber,change_reason=@ChangeReason,effective_serial_from=@EffectiveSerialFrom,effective_serial_to=@EffectiveSerialTo,validation_rule_snapshot_json=@ValidationRules,row_version=row_version+1 WHERE id=@VersionId AND state='Draft'",
            new { VersionId = versionId, ChangeNumber = changeNumber, ChangeReason = changeReason, EffectiveSerialFrom = effectiveSerialFrom, EffectiveSerialTo = effectiveSerialTo, ValidationRules = JsonSerializer.Serialize(validationRequiredFields, jsonOptions) }, cancellationToken: cancellationToken));
        if (affected != 1) throw new PdmConflictException("已发布或已进入审批的BOM版本不可修改。");
        var row = await connection.QuerySingleAsync<BomVersionRow>(new CommandDefinition(
            $"{BomVersionSelect} WHERE id=@VersionId", new { VersionId = versionId }, cancellationToken: cancellationToken));
        return MapBomVersion(row);
    }

    public async Task SetBomVersionStateAsync(IReadOnlyList<Guid> versionIds, BomVersionState state, string actor, DateTimeOffset? releasedAt, CancellationToken cancellationToken)
    {
        if (versionIds.Count == 0) return;
        await using var connection = await OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE bom_version SET state=@State,updated_by=@Actor,updated_at=@Now,released_at=CASE WHEN @State='Released' THEN @ReleasedAt ELSE released_at END,row_version=row_version+1 WHERE id IN @VersionIds",
            new { VersionIds = versionIds, State = state.ToString(), Actor = actor, Now = timeProvider.GetUtcNow().UtcDateTime, ReleasedAt = releasedAt?.UtcDateTime }, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<ManufacturingBomBaseline>> ListManufacturingBomBaselinesAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<ManufacturingBomBaselineRow>(new CommandDefinition(
            "SELECT id,project_id,sequence_no,baseline_label,standard_bom_version_id,non_standard_bom_version_id,electrical_bom_version_id,change_number,change_reason,effective_serial_from,effective_serial_to,release_package_id,created_by,created_at FROM manufacturing_bom_baseline WHERE project_id=@ProjectId ORDER BY sequence_no DESC",
            new { ProjectId = projectId }, cancellationToken: cancellationToken));
        return rows.Select(MapManufacturingBomBaseline).ToArray();
    }

    public async Task<ManufacturingBomBaseline> CreateManufacturingBomBaselineAsync(ManufacturingBomBaseline baseline, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO manufacturing_bom_baseline(id,project_id,sequence_no,baseline_label,standard_bom_version_id,non_standard_bom_version_id,electrical_bom_version_id,change_number,change_reason,effective_serial_from,effective_serial_to,release_package_id,created_by,created_at) VALUES(@Id,@ProjectId,@Sequence,@Label,@StandardBomVersionId,@NonStandardBomVersionId,@ElectricalBomVersionId,@ChangeNumber,@ChangeReason,@EffectiveSerialFrom,@EffectiveSerialTo,@ReleasePackageId,@CreatedBy,@CreatedAt)",
            new { baseline.Id, baseline.ProjectId, baseline.Sequence, baseline.Label, baseline.StandardBomVersionId, baseline.NonStandardBomVersionId, baseline.ElectricalBomVersionId, baseline.ChangeNumber, baseline.ChangeReason, baseline.EffectiveSerialFrom, baseline.EffectiveSerialTo, baseline.ReleasePackageId, baseline.CreatedBy, CreatedAt = baseline.CreatedAt.UtcDateTime }, cancellationToken: cancellationToken));
        return baseline;
    }

    public async Task<ReleasePackage> UpdateReleasePackageBomVersionsAsync(Guid releasePackageId, BomVersion standard, BomVersion nonStandard, BomVersion electrical, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var mechanical = standard.Items.Concat(nonStandard.Items).ToArray();
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE release_package
            SET standard_bom_version_id=@StandardVersionId,
                non_standard_bom_version_id=@NonStandardVersionId,
                electrical_bom_version_id=@ElectricalVersionId,
                standard_bom_revision=@StandardRevision,
                non_standard_bom_revision=@NonStandardRevision,
                electrical_bom_revision=@ElectricalRevision,
                standard_bom_snapshot_json=@StandardSnapshot,
                non_standard_bom_snapshot_json=@NonStandardSnapshot,
                mechanical_bom_snapshot_json=@MechanicalSnapshot,
                electrical_bom_snapshot_json=@ElectricalSnapshot,
                row_version=row_version+1
            WHERE id=@ReleasePackageId AND state IN ('Draft','Rejected','PublishFailed')
            """,
            new
            {
                ReleasePackageId = releasePackageId,
                StandardVersionId = standard.Id,
                NonStandardVersionId = nonStandard.Id,
                ElectricalVersionId = electrical.Id,
                StandardRevision = standard.Label,
                NonStandardRevision = nonStandard.Label,
                ElectricalRevision = electrical.Label,
                StandardSnapshot = JsonSerializer.Serialize(standard.Items, jsonOptions),
                NonStandardSnapshot = JsonSerializer.Serialize(nonStandard.Items, jsonOptions),
                MechanicalSnapshot = JsonSerializer.Serialize(mechanical, jsonOptions),
                ElectricalSnapshot = JsonSerializer.Serialize(electrical.Items, jsonOptions)
            }, cancellationToken: cancellationToken));
        if (affected != 1) throw new PdmConflictException("发布包状态已变化，不能更新BOM版本。");
        return await FindReleasePackageAsync(releasePackageId, cancellationToken)
            ?? throw new PdmNotFoundException("发布包不存在。");
    }

    public async Task<ManufacturingBomBaseline> MarkPublishedWithBomBaselineAsync(ReleasePackage package, string publishedPath, DateTimeOffset publishedAt, string actor, CancellationToken cancellationToken)
    {
        var versionIds = RequiredPackageBomVersionIds(package);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var currentState = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT state FROM release_package WHERE id=@PackageId FOR UPDATE", new { PackageId = package.Id }, transaction, cancellationToken: cancellationToken));
        if (currentState != ReleasePackageState.Publishing.ToString()) throw new PdmConflictException("发布包状态已变化，不能生成制造BOM基线。");
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE bom_version SET state='Released',updated_by=@Actor,updated_at=@PublishedAt,released_at=@PublishedAt,row_version=row_version+1 WHERE id IN @VersionIds AND state='InReview'",
            new { VersionIds = versionIds, Actor = actor, PublishedAt = publishedAt.UtcDateTime }, transaction, cancellationToken: cancellationToken));
        var latestSequence = await connection.QuerySingleOrDefaultAsync<int?>(new CommandDefinition(
            "SELECT sequence_no FROM manufacturing_bom_baseline WHERE project_id=@ProjectId ORDER BY sequence_no DESC LIMIT 1 FOR UPDATE",
            new { package.ProjectId }, transaction, cancellationToken: cancellationToken));
        var sequence = (latestSequence ?? 0) + 1;
        var baseline = new ManufacturingBomBaseline(
            Guid.NewGuid(), package.ProjectId, sequence, $"BL-{sequence:D3}", versionIds[0], versionIds[1], versionIds[2],
            package.ChangeNumber ?? package.Number, package.ChangeReason ?? "兼容既有发布流程创建的设变",
            package.EffectiveSerialFrom ?? "未指定", package.EffectiveSerialTo, package.Id, actor, publishedAt);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO manufacturing_bom_baseline(id,project_id,sequence_no,baseline_label,standard_bom_version_id,non_standard_bom_version_id,electrical_bom_version_id,change_number,change_reason,effective_serial_from,effective_serial_to,release_package_id,created_by,created_at) VALUES(@Id,@ProjectId,@Sequence,@Label,@StandardBomVersionId,@NonStandardBomVersionId,@ElectricalBomVersionId,@ChangeNumber,@ChangeReason,@EffectiveSerialFrom,@EffectiveSerialTo,@ReleasePackageId,@CreatedBy,@CreatedAt)",
            new { baseline.Id, baseline.ProjectId, baseline.Sequence, baseline.Label, baseline.StandardBomVersionId, baseline.NonStandardBomVersionId, baseline.ElectricalBomVersionId, baseline.ChangeNumber, baseline.ChangeReason, baseline.EffectiveSerialFrom, baseline.EffectiveSerialTo, baseline.ReleasePackageId, baseline.CreatedBy, CreatedAt = baseline.CreatedAt.UtcDateTime }, transaction, cancellationToken: cancellationToken));
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE release_package SET state='Published',published_at=@PublishedAt,published_path=@PublishedPath,publish_error=NULL,row_version=row_version+1 WHERE id=@PackageId AND state='Publishing'",
            new { PackageId = package.Id, PublishedAt = publishedAt.UtcDateTime, PublishedPath = publishedPath }, transaction, cancellationToken: cancellationToken));
        if (affected != 1) throw new PdmConflictException("发布包状态已变化，不能标记为已发布。");
        await transaction.CommitAsync(cancellationToken);
        return baseline;
    }

    private BomVersion MapBomVersion(BomVersionRow row) => new(
        row.Id, row.ProjectId, Enum.Parse<BomKind>(row.BomKind), row.VersionNumber, row.VersionLabel,
        Enum.Parse<BomVersionState>(row.State), row.BaseVersionId, row.ChangeNumber, row.ChangeReason,
        row.EffectiveSerialFrom, row.EffectiveSerialTo,
        JsonSerializer.Deserialize<List<BomItem>>(row.SnapshotJson, jsonOptions) ?? [],
        row.CreatedBy, AsUtc(row.CreatedAt), row.UpdatedBy, AsUtc(row.UpdatedAt), AsNullableUtc(row.ReleasedAt))
    {
        ValidationRequiredFields = string.IsNullOrWhiteSpace(row.ValidationRuleSnapshotJson)
            ? []
            : JsonSerializer.Deserialize<List<string>>(row.ValidationRuleSnapshotJson, jsonOptions) ?? []
    };

    private static ManufacturingBomBaseline MapManufacturingBomBaseline(ManufacturingBomBaselineRow row) => new(
        row.Id, row.ProjectId, row.SequenceNo, row.BaselineLabel, row.StandardBomVersionId,
        row.NonStandardBomVersionId, row.ElectricalBomVersionId, row.ChangeNumber, row.ChangeReason,
        row.EffectiveSerialFrom, row.EffectiveSerialTo, row.ReleasePackageId, row.CreatedBy, AsUtc(row.CreatedAt));

    private static void EnsureVersionedBomKind(BomKind kind)
    {
        if (kind is not (BomKind.Standard or BomKind.NonStandard or BomKind.Electrical))
            throw new PdmRuleException("只有标准件、非标件和电气BOM支持独立版本控制。");
    }

    private static string BomVersionLabel(BomKind kind, int versionNumber) =>
        $"{(kind == BomKind.Standard ? "S" : kind == BomKind.NonStandard ? "N" : "E")}-B{versionNumber:D2}";

    private static Guid[] RequiredPackageBomVersionIds(ReleasePackage package)
    {
        if (!package.StandardBomVersionId.HasValue || !package.NonStandardBomVersionId.HasValue || !package.ElectricalBomVersionId.HasValue)
            throw new PdmConflictException("发布包没有绑定三套独立BOM版本。");
        return [package.StandardBomVersionId.Value, package.NonStandardBomVersionId.Value, package.ElectricalBomVersionId.Value];
    }

    private const string BomVersionSelect = "SELECT id,project_id,bom_kind,version_number,version_label,state,base_version_id,change_number,change_reason,effective_serial_from,effective_serial_to,snapshot_json,validation_rule_snapshot_json,created_by,created_at,updated_by,updated_at,released_at FROM bom_version";

    private sealed class BomVersionRow
    {
        public Guid Id { get; init; }
        public Guid ProjectId { get; init; }
        public string BomKind { get; init; } = string.Empty;
        public int VersionNumber { get; init; }
        public string VersionLabel { get; init; } = string.Empty;
        public string State { get; init; } = string.Empty;
        public Guid? BaseVersionId { get; init; }
        public string? ChangeNumber { get; init; }
        public string? ChangeReason { get; init; }
        public string? EffectiveSerialFrom { get; init; }
        public string? EffectiveSerialTo { get; init; }
        public string SnapshotJson { get; init; } = "[]";
        public string? ValidationRuleSnapshotJson { get; init; }
        public string CreatedBy { get; init; } = string.Empty;
        public DateTime CreatedAt { get; init; }
        public string UpdatedBy { get; init; } = string.Empty;
        public DateTime UpdatedAt { get; init; }
        public DateTime? ReleasedAt { get; init; }
    }

    private sealed class BomVersionSequenceRow
    {
        public Guid Id { get; init; }
        public int VersionNumber { get; init; }
    }

    private sealed class ManufacturingBomBaselineRow
    {
        public Guid Id { get; init; }
        public Guid ProjectId { get; init; }
        public int SequenceNo { get; init; }
        public string BaselineLabel { get; init; } = string.Empty;
        public Guid StandardBomVersionId { get; init; }
        public Guid NonStandardBomVersionId { get; init; }
        public Guid ElectricalBomVersionId { get; init; }
        public string ChangeNumber { get; init; } = string.Empty;
        public string ChangeReason { get; init; } = string.Empty;
        public string EffectiveSerialFrom { get; init; } = string.Empty;
        public string? EffectiveSerialTo { get; init; }
        public Guid ReleasePackageId { get; init; }
        public string CreatedBy { get; init; } = string.Empty;
        public DateTime CreatedAt { get; init; }
    }
}
