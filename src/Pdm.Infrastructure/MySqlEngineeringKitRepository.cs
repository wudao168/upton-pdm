using Dapper;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed class MySqlEngineeringKitRepository : IEngineeringKitRepository
{
    private readonly string connectionString;

    public MySqlEngineeringKitRepository(IOptions<PdmDatabaseOptions> options)
    {
        connectionString = options.Value.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("PLM MySQL连接字符串未配置。");
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    public async Task<IReadOnlyList<EngineeringKit>> ListAsync(bool releasedOnly, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await LoadAsync(connection, releasedOnly ? "WHERE k.current_released_revision_id IS NOT NULL" : string.Empty, null, cancellationToken);
    }

    public async Task<EngineeringKit?> FindAsync(Guid kitId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return (await LoadAsync(connection, "WHERE k.id=@KitId", new { KitId = kitId }, cancellationToken)).SingleOrDefault();
    }

    public async Task<EngineeringKit> SaveDraftAsync(EngineeringKit kit, EngineeringKitRevision revision, long? expectedRowVersion, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (expectedRowVersion is null)
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO engineering_kit(id,kit_code,name,description,current_released_revision_id,created_by,created_at,updated_by,updated_at,row_version)
                VALUES(@Id,NULL,@Name,@Description,NULL,@CreatedBy,@CreatedAt,@UpdatedBy,@UpdatedAt,1)
                """, new { kit.Id, kit.Name, kit.Description, kit.CreatedBy, CreatedAt = kit.CreatedAt.UtcDateTime, kit.UpdatedBy, UpdatedAt = kit.UpdatedAt.UtcDateTime }, transaction, cancellationToken: cancellationToken));
        }
        else
        {
            var affected = await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE engineering_kit
                SET name=@Name,description=@Description,updated_by=@UpdatedBy,updated_at=@UpdatedAt,row_version=row_version+1
                WHERE id=@Id AND row_version=@ExpectedRowVersion
                """, new { kit.Id, kit.Name, kit.Description, kit.UpdatedBy, UpdatedAt = kit.UpdatedAt.UtcDateTime, ExpectedRowVersion = expectedRowVersion.Value }, transaction, cancellationToken: cancellationToken));
            if (affected == 0) throw new PdmConflictException("套件已被其他用户修改，请刷新后重试。");
        }

        var existingDraft = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM engineering_kit_revision WHERE id=@RevisionId AND revision_state='Draft'",
            new { RevisionId = revision.Id }, transaction, cancellationToken: cancellationToken));
        if (existingDraft == 0)
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO engineering_kit_revision(id,kit_id,version_no,revision_state,change_note,created_by,created_at,published_by,published_at)
                VALUES(@Id,@KitId,@VersionNumber,'Draft',@ChangeNote,@CreatedBy,@CreatedAt,NULL,NULL)
                """, new { revision.Id, revision.KitId, revision.VersionNumber, revision.ChangeNote, revision.CreatedBy, CreatedAt = revision.CreatedAt.UtcDateTime }, transaction, cancellationToken: cancellationToken));
        }
        else
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE engineering_kit_revision SET change_note=@ChangeNote WHERE id=@Id AND revision_state='Draft'",
                new { revision.Id, revision.ChangeNote }, transaction, cancellationToken: cancellationToken));
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM engineering_kit_component WHERE revision_id=@RevisionId", new { RevisionId = revision.Id }, transaction, cancellationToken: cancellationToken));
        }

        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO engineering_kit_component(id,revision_id,material_id,quantity,is_optional,sort_order)
            VALUES(@Id,@RevisionId,@MaterialId,@Quantity,@IsOptional,@SortOrder)
            """, revision.Components, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return (await FindAsync(kit.Id, cancellationToken))!;
    }

    public async Task<EngineeringKit> PublishAsync(Guid kitId, long expectedRowVersion, string actor, DateTimeOffset publishedAt, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var kit = await connection.QuerySingleOrDefaultAsync<KitRow>(new CommandDefinition(
            KitSelect + " WHERE k.id=@KitId FOR UPDATE", new { KitId = kitId }, transaction, cancellationToken: cancellationToken))
            ?? throw new PdmNotFoundException("套件不存在。");
        if (kit.RowVersion != expectedRowVersion) throw new PdmConflictException("套件已被其他用户修改，请刷新后重试。");
        var draft = await connection.QuerySingleOrDefaultAsync<RevisionRow>(new CommandDefinition(
            RevisionSelect + " WHERE r.kit_id=@KitId AND r.revision_state='Draft' ORDER BY r.version_no DESC LIMIT 1 FOR UPDATE",
            new { KitId = kitId }, transaction, cancellationToken: cancellationToken)) ?? throw new PdmRuleException("套件没有待发布草稿。");

        var code = kit.KitCode;
        if (string.IsNullOrWhiteSpace(code))
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT IGNORE INTO engineering_kit_counter(counter_id,current_sequence) VALUES(1,0)", transaction: transaction, cancellationToken: cancellationToken));
            var current = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT current_sequence FROM engineering_kit_counter WHERE counter_id=1 FOR UPDATE", transaction: transaction, cancellationToken: cancellationToken));
            var next = checked(current + 1);
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE engineering_kit_counter SET current_sequence=@Next WHERE counter_id=1", new { Next = next }, transaction, cancellationToken: cancellationToken));
            code = $"UKIT-{next:D6}";
        }

        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE engineering_kit_revision SET revision_state='Released',published_by=@Actor,published_at=@PublishedAt WHERE id=@RevisionId
            """, new { Actor = actor, PublishedAt = publishedAt.UtcDateTime, RevisionId = draft.Id }, transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE engineering_kit SET kit_code=@Code,current_released_revision_id=@RevisionId,updated_by=@Actor,updated_at=@PublishedAt,row_version=row_version+1
            WHERE id=@KitId
            """, new { Code = code, RevisionId = draft.Id, Actor = actor, PublishedAt = publishedAt.UtcDateTime, KitId = kitId }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return (await FindAsync(kitId, cancellationToken))!;
    }

    private static async Task<IReadOnlyList<EngineeringKit>> LoadAsync(MySqlConnection connection, string where, object? parameters, CancellationToken cancellationToken)
    {
        var kits = (await connection.QueryAsync<KitRow>(new CommandDefinition(
            $"{KitSelect} {where} ORDER BY k.kit_code IS NULL,k.kit_code,k.name", parameters, cancellationToken: cancellationToken))).ToArray();
        if (kits.Length == 0) return [];
        var ids = kits.Select(item => item.Id).ToArray();
        var revisions = (await connection.QueryAsync<RevisionRow>(new CommandDefinition(
            RevisionSelect + " WHERE r.kit_id IN @Ids ORDER BY r.kit_id,r.version_no", new { Ids = ids }, cancellationToken: cancellationToken))).ToArray();
        var revisionIds = revisions.Select(item => item.Id).ToArray();
        var components = revisionIds.Length == 0 ? [] : (await connection.QueryAsync<ComponentRow>(new CommandDefinition("""
            SELECT c.id,c.revision_id,c.material_id,m.material_code,m.name material_name,c.quantity,m.unit_code unit,c.is_optional,c.sort_order
            FROM engineering_kit_component c
            INNER JOIN material_master m ON m.id=c.material_id
            WHERE c.revision_id IN @RevisionIds
            ORDER BY c.revision_id,c.sort_order
            """, new { RevisionIds = revisionIds }, cancellationToken: cancellationToken))).ToArray();
        var componentsByRevision = components.GroupBy(item => item.RevisionId).ToDictionary(group => group.Key, group => (IReadOnlyList<EngineeringKitComponent>)group.Select(MapComponent).ToArray());
        var revisionsByKit = revisions.GroupBy(item => item.KitId).ToDictionary(group => group.Key, group => (IReadOnlyList<EngineeringKitRevision>)group.Select(item => MapRevision(item, componentsByRevision.GetValueOrDefault(item.Id) ?? [])).ToArray());
        return kits.Select(item => MapKit(item, revisionsByKit.GetValueOrDefault(item.Id) ?? [])).ToArray();
    }

    private async Task<MySqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static EngineeringKit MapKit(KitRow row, IReadOnlyList<EngineeringKitRevision> revisions) =>
        new(row.Id, row.KitCode, row.Name, row.Description, row.CurrentReleasedRevisionId, revisions,
            row.CreatedBy, Utc(row.CreatedAt), row.UpdatedBy, Utc(row.UpdatedAt), row.RowVersion);

    private static EngineeringKitRevision MapRevision(RevisionRow row, IReadOnlyList<EngineeringKitComponent> components) =>
        new(row.Id, row.KitId, row.VersionNo, Enum.Parse<EngineeringKitRevisionState>(row.RevisionState), row.ChangeNote,
            components, row.CreatedBy, Utc(row.CreatedAt), row.PublishedBy, row.PublishedAt is null ? null : Utc(row.PublishedAt.Value));

    private static EngineeringKitComponent MapComponent(ComponentRow row) =>
        new(row.Id, row.RevisionId, row.MaterialId, row.MaterialCode, row.MaterialName, row.Quantity, row.Unit, row.IsOptional, row.SortOrder);

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private const string KitSelect = """
        SELECT k.id,k.kit_code,k.name,k.description,k.current_released_revision_id,k.created_by,k.created_at,k.updated_by,k.updated_at,k.row_version
        FROM engineering_kit k
        """;

    private const string RevisionSelect = """
        SELECT r.id,r.kit_id,r.version_no,r.revision_state,r.change_note,r.created_by,r.created_at,r.published_by,r.published_at
        FROM engineering_kit_revision r
        """;

    private sealed class KitRow
    {
        public Guid Id { get; init; }
        public string? KitCode { get; init; }
        public string Name { get; init; } = string.Empty;
        public string? Description { get; init; }
        public Guid? CurrentReleasedRevisionId { get; init; }
        public string CreatedBy { get; init; } = string.Empty;
        public DateTime CreatedAt { get; init; }
        public string UpdatedBy { get; init; } = string.Empty;
        public DateTime UpdatedAt { get; init; }
        public long RowVersion { get; init; }
    }

    private sealed class RevisionRow
    {
        public Guid Id { get; init; }
        public Guid KitId { get; init; }
        public int VersionNo { get; init; }
        public string RevisionState { get; init; } = string.Empty;
        public string? ChangeNote { get; init; }
        public string CreatedBy { get; init; } = string.Empty;
        public DateTime CreatedAt { get; init; }
        public string? PublishedBy { get; init; }
        public DateTime? PublishedAt { get; init; }
    }

    private sealed class ComponentRow
    {
        public Guid Id { get; init; }
        public Guid RevisionId { get; init; }
        public Guid MaterialId { get; init; }
        public string MaterialCode { get; init; } = string.Empty;
        public string MaterialName { get; init; } = string.Empty;
        public decimal Quantity { get; init; }
        public string Unit { get; init; } = string.Empty;
        public bool IsOptional { get; init; }
        public int SortOrder { get; init; }
    }
}
