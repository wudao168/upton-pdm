using Dapper;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed class MySqlMaterialRelationRepository : IMaterialRelationRepository
{
    private readonly string connectionString;

    public MySqlMaterialRelationRepository(IOptions<PdmDatabaseOptions> options)
    {
        connectionString = options.Value.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("PLM MySQL连接字符串未配置。 ");
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    public async Task<IReadOnlyList<MaterialRelationTemplate>> ListTemplatesAsync(bool includeDraft, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var rows = (await connection.QueryAsync<RelationRow>(new CommandDefinition("""
            SELECT t.id TemplateId,t.main_material_id MainMaterialId,mm.material_code MainMaterialCode,mm.name MainMaterialName,
                   t.name TemplateName,t.is_archived IsArchived,t.updated_by TemplateUpdatedBy,t.updated_at TemplateUpdatedAt,t.row_version TemplateRowVersion,
                   r.id RevisionId,r.version_no RevisionVersion,r.revision_state RevisionState,r.change_note ChangeNote,
                   r.created_by RevisionCreatedBy,r.created_at RevisionCreatedAt,r.published_by PublishedBy,r.published_at PublishedAt,r.row_version RevisionRowVersion,
                   g.id GroupId,g.name GroupName,g.is_required IsRequired,g.selection_mode SelectionMode,g.min_selection MinSelection,
                   g.max_selection MaxSelection,g.auto_select_unique AutoSelectUnique,g.sort_order GroupSortOrder,
                   o.id OptionId,o.material_id OptionMaterialId,om.material_code OptionMaterialCode,om.name OptionMaterialName,
                   om.material_kind OptionMaterialKind,om.unit_code OptionUnitCode,o.quantity_mode QuantityMode,o.quantity_per_set QuantityPerSet,
                   o.is_default IsDefault,o.sort_order OptionSortOrder
            FROM material_relation_template t
            INNER JOIN material_master mm ON mm.id=t.main_material_id
            LEFT JOIN material_relation_revision r ON r.template_id=t.id AND r.revision_state IN ('Published','Draft')
            LEFT JOIN material_relation_group g ON g.revision_id=r.id
            LEFT JOIN material_relation_option o ON o.group_id=g.id
            LEFT JOIN material_master om ON om.id=o.material_id
            WHERE (@IncludeDraft=1 OR r.revision_state='Published')
            ORDER BY mm.material_code,r.version_no,g.sort_order,o.sort_order
            """, new { IncludeDraft = includeDraft }, cancellationToken: cancellationToken))).ToArray();
        return MapTemplates(rows, includeDraft);
    }

    public async Task<MaterialRelationTemplate> SaveDraftAsync(Guid? templateId, SaveMaterialRelationTemplateCommand command, string mainMaterialCode, string mainMaterialName, IReadOnlyList<MaterialRelationGroup> groups, string actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var effectiveTemplateId = templateId ?? Guid.NewGuid();
        var header = await connection.QuerySingleOrDefaultAsync<TemplateLockRow>(new CommandDefinition(
            "SELECT id Id,main_material_id MainMaterialId,row_version RowVersion FROM material_relation_template WHERE id=@Id FOR UPDATE",
            new { Id = effectiveTemplateId }, transaction, cancellationToken: cancellationToken));
        if (templateId.HasValue && header is null) throw new PdmNotFoundException("配套模板不存在。");
        if (header is not null && header.MainMaterialId != command.MainMaterialId) throw new PdmRuleException("已建模板不能更换主物料。");
        if (header is null)
        {
            var duplicate = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM material_relation_template WHERE main_material_id=@MainMaterialId", new { command.MainMaterialId }, transaction, cancellationToken: cancellationToken));
            if (duplicate > 0) throw new PdmConflictException("该主物料已存在配套模板，请刷新后编辑现有模板。");
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO material_relation_template(id,main_material_id,name,is_archived,created_by,created_at,updated_by,updated_at,row_version)
                VALUES(@Id,@MainMaterialId,@Name,0,@Actor,@Now,@Actor,@Now,1)
                """, new { Id = effectiveTemplateId, command.MainMaterialId, command.Name, Actor = actor, Now = now.UtcDateTime }, transaction, cancellationToken: cancellationToken));
        }

        var draft = await connection.QuerySingleOrDefaultAsync<RevisionLockRow>(new CommandDefinition(
            "SELECT id Id,version_no Version,row_version RowVersion FROM material_relation_revision WHERE template_id=@TemplateId AND revision_state='Draft' FOR UPDATE",
            new { TemplateId = effectiveTemplateId }, transaction, cancellationToken: cancellationToken));
        Guid revisionId;
        if (draft is null)
        {
            var version = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COALESCE(MAX(version_no),0)+1 FROM material_relation_revision WHERE template_id=@TemplateId",
                new { TemplateId = effectiveTemplateId }, transaction, cancellationToken: cancellationToken));
            revisionId = Guid.NewGuid();
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO material_relation_revision(id,template_id,version_no,revision_state,change_note,created_by,created_at,row_version)
                VALUES(@Id,@TemplateId,@Version,'Draft',@ChangeNote,@Actor,@Now,1)
                """, new { Id = revisionId, TemplateId = effectiveTemplateId, Version = version, command.ChangeNote, Actor = actor, Now = now.UtcDateTime }, transaction, cancellationToken: cancellationToken));
        }
        else
        {
            if (command.ExpectedRevisionRowVersion.HasValue && draft.RowVersion != command.ExpectedRevisionRowVersion.Value)
                throw new PdmConflictException("配套模板草稿已被其他用户修改，请刷新后重试。");
            revisionId = draft.Id;
            await connection.ExecuteAsync(new CommandDefinition("DELETE FROM material_relation_group WHERE revision_id=@RevisionId", new { RevisionId = revisionId }, transaction, cancellationToken: cancellationToken));
            await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE material_relation_revision SET change_note=@ChangeNote,row_version=row_version+1 WHERE id=@RevisionId
                """, new { RevisionId = revisionId, command.ChangeNote }, transaction, cancellationToken: cancellationToken));
        }

        foreach (var group in groups)
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO material_relation_group(id,revision_id,name,is_required,selection_mode,min_selection,max_selection,auto_select_unique,sort_order)
                VALUES(@Id,@RevisionId,@Name,@IsRequired,@SelectionMode,@MinSelection,@MaxSelection,@AutoSelectUnique,@SortOrder)
                """, new { group.Id, RevisionId = revisionId, group.Name, group.IsRequired, SelectionMode = group.SelectionMode.ToString(), group.MinSelection, group.MaxSelection, group.AutoSelectUnique, group.SortOrder }, transaction, cancellationToken: cancellationToken));
            foreach (var option in group.Options)
                await connection.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO material_relation_option(id,group_id,material_id,quantity_mode,quantity_per_set,is_default,sort_order)
                    VALUES(@Id,@GroupId,@MaterialId,@QuantityMode,@QuantityPerSet,@IsDefault,@SortOrder)
                    """, new { option.Id, GroupId = group.Id, option.MaterialId, QuantityMode = option.QuantityMode.ToString(), option.QuantityPerSet, option.IsDefault, option.SortOrder }, transaction, cancellationToken: cancellationToken));
        }
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE material_relation_template SET name=@Name,updated_by=@Actor,updated_at=@Now,row_version=row_version+1 WHERE id=@Id
            """, new { Id = effectiveTemplateId, command.Name, Actor = actor, Now = now.UtcDateTime }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return (await ListTemplatesAsync(true, cancellationToken)).Single(item => item.Id == effectiveTemplateId);
    }

    public async Task<MaterialRelationTemplate> PublishAsync(Guid templateId, Guid revisionId, long expectedRowVersion, string actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var affected = await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE material_relation_revision SET revision_state='Superseded',row_version=row_version+1
            WHERE template_id=@TemplateId AND revision_state='Published'
            """, new { TemplateId = templateId }, transaction, cancellationToken: cancellationToken));
        _ = affected;
        affected = await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE material_relation_revision
            SET revision_state='Published',published_by=@Actor,published_at=@Now,row_version=row_version+1
            WHERE id=@RevisionId AND template_id=@TemplateId AND revision_state='Draft' AND row_version=@ExpectedRowVersion
            """, new { RevisionId = revisionId, TemplateId = templateId, ExpectedRowVersion = expectedRowVersion, Actor = actor, Now = now.UtcDateTime }, transaction, cancellationToken: cancellationToken));
        if (affected != 1) throw new PdmConflictException("配套模板草稿已被其他用户修改，请刷新后重试。");
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE material_relation_template SET updated_by=@Actor,updated_at=@Now,row_version=row_version+1 WHERE id=@TemplateId
            """, new { TemplateId = templateId, Actor = actor, Now = now.UtcDateTime }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return (await ListTemplatesAsync(true, cancellationToken)).Single(item => item.Id == templateId);
    }

    public async Task<IReadOnlyList<MaterialRelationSelection>> ListSelectionsAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<SelectionRow>(new CommandDefinition("""
            SELECT project_id ProjectId,main_bom_item_id MainBomItemId,accessory_bom_item_id AccessoryBomItemId,
                   revision_id RevisionId,group_id GroupId,option_id OptionId,expected_quantity ExpectedQuantity,
                   updated_by UpdatedBy,updated_at UpdatedAt
            FROM material_relation_selection WHERE project_id=@ProjectId
            """, new { ProjectId = projectId }, cancellationToken: cancellationToken));
        return rows.Select(item => new MaterialRelationSelection(item.ProjectId, item.MainBomItemId, item.AccessoryBomItemId, item.RevisionId,
            item.GroupId, item.OptionId, item.ExpectedQuantity, item.UpdatedBy, new DateTimeOffset(item.UpdatedAt, TimeSpan.Zero))).ToArray();
    }

    public async Task ReplaceSelectionsAsync(Guid projectId, Guid mainBomItemId, IReadOnlyList<MaterialRelationSelection> selections, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM material_relation_selection WHERE project_id=@ProjectId AND main_bom_item_id=@MainBomItemId",
            new { ProjectId = projectId, MainBomItemId = mainBomItemId }, transaction, cancellationToken: cancellationToken));
        foreach (var item in selections)
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO material_relation_selection(project_id,main_bom_item_id,accessory_bom_item_id,revision_id,group_id,option_id,expected_quantity,updated_by,updated_at)
                VALUES(@ProjectId,@MainBomItemId,@AccessoryBomItemId,@RevisionId,@GroupId,@OptionId,@ExpectedQuantity,@UpdatedBy,@UpdatedAt)
                """, new { item.ProjectId, item.MainBomItemId, item.AccessoryBomItemId, item.RevisionId, item.GroupId, item.OptionId, item.ExpectedQuantity, item.UpdatedBy, UpdatedAt = item.UpdatedAt.UtcDateTime }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MaterialRelationReview>> ListReviewsAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<ReviewRow>(new CommandDefinition("""
            SELECT project_id ProjectId,main_bom_item_id MainBomItemId,revision_id RevisionId,group_id GroupId,
                   decision Decision,main_quantity MainQuantity,reason Reason,updated_by UpdatedBy,updated_at UpdatedAt,bom_fingerprint BomFingerprint
            FROM material_relation_review WHERE project_id=@ProjectId
            """, new { ProjectId = projectId }, cancellationToken: cancellationToken));
        return rows.Select(item => new MaterialRelationReview(item.ProjectId, item.MainBomItemId, item.RevisionId, item.GroupId,
            Enum.Parse<MaterialRelationReviewDecision>(item.Decision), item.MainQuantity, item.Reason, item.UpdatedBy,
            new DateTimeOffset(item.UpdatedAt, TimeSpan.Zero), item.BomFingerprint)).ToArray();
    }

    public async Task ReplaceReviewsAsync(Guid projectId, Guid mainBomItemId, IReadOnlyList<MaterialRelationReview> reviews, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM material_relation_review WHERE project_id=@ProjectId AND main_bom_item_id=@MainBomItemId",
            new { ProjectId = projectId, MainBomItemId = mainBomItemId }, transaction, cancellationToken: cancellationToken));
        foreach (var item in reviews)
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO material_relation_review(project_id,main_bom_item_id,revision_id,group_id,decision,main_quantity,reason,updated_by,updated_at,bom_fingerprint)
                VALUES(@ProjectId,@MainBomItemId,@RevisionId,@GroupId,@Decision,@MainQuantity,@Reason,@UpdatedBy,@UpdatedAt,@BomFingerprint)
                """, new { item.ProjectId, item.MainBomItemId, item.RevisionId, item.GroupId, Decision = item.Decision.ToString(), item.MainQuantity, item.Reason, item.UpdatedBy, UpdatedAt = item.UpdatedAt.UtcDateTime, item.BomFingerprint }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<MySqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static IReadOnlyList<MaterialRelationTemplate> MapTemplates(IReadOnlyList<RelationRow> rows, bool includeDraft)
    {
        return rows.GroupBy(row => row.TemplateId).Select(templateRows =>
        {
            var header = templateRows.First();
            MaterialRelationRevision? Revision(string state)
            {
                var revisionRows = templateRows.Where(row => string.Equals(row.RevisionState, state, StringComparison.OrdinalIgnoreCase) && row.RevisionId.HasValue).ToArray();
                if (revisionRows.Length == 0) return null;
                var first = revisionRows[0];
                var groups = revisionRows.Where(row => row.GroupId.HasValue).GroupBy(row => row.GroupId!.Value).Select(groupRows =>
                {
                    var group = groupRows.First();
                    var options = groupRows.Where(row => row.OptionId.HasValue).Select(row => new MaterialRelationOption(row.OptionId!.Value, row.OptionMaterialId!.Value,
                        row.OptionMaterialCode!, row.OptionMaterialName!, Enum.Parse<MaterialKind>(row.OptionMaterialKind!), row.OptionUnitCode!,
                        Enum.Parse<MaterialRelationQuantityMode>(row.QuantityMode!), row.QuantityPerSet!.Value, row.IsDefault!.Value, row.OptionSortOrder!.Value)).ToArray();
                    return new MaterialRelationGroup(group.GroupId!.Value, group.GroupName!, group.IsRequired!.Value,
                        Enum.Parse<MaterialRelationSelectionMode>(group.SelectionMode!), group.MinSelection!.Value, group.MaxSelection,
                        group.AutoSelectUnique!.Value, group.GroupSortOrder!.Value, options);
                }).OrderBy(group => group.SortOrder).ToArray();
                return new MaterialRelationRevision(first.RevisionId!.Value, first.RevisionVersion!.Value, Enum.Parse<MaterialRelationRevisionState>(first.RevisionState!),
                    first.ChangeNote, first.RevisionCreatedBy!, new DateTimeOffset(first.RevisionCreatedAt!.Value, TimeSpan.Zero), first.PublishedBy,
                    first.PublishedAt.HasValue ? new DateTimeOffset(first.PublishedAt.Value, TimeSpan.Zero) : null, first.RevisionRowVersion!.Value, groups);
            }
            return new MaterialRelationTemplate(header.TemplateId, header.MainMaterialId, header.MainMaterialCode, header.MainMaterialName, header.TemplateName,
                header.IsArchived, Revision("Published"), includeDraft ? Revision("Draft") : null, header.TemplateUpdatedBy,
                new DateTimeOffset(header.TemplateUpdatedAt, TimeSpan.Zero), header.TemplateRowVersion);
        }).ToArray();
    }

    private sealed record TemplateLockRow(Guid Id, Guid MainMaterialId, long RowVersion);
    private sealed record RevisionLockRow(Guid Id, int Version, long RowVersion);
    private sealed record SelectionRow(Guid ProjectId, Guid MainBomItemId, Guid AccessoryBomItemId, Guid RevisionId, Guid GroupId, Guid OptionId, decimal ExpectedQuantity, string UpdatedBy, DateTime UpdatedAt);
    private sealed record ReviewRow(Guid ProjectId, Guid MainBomItemId, Guid RevisionId, Guid GroupId, string Decision, decimal MainQuantity, string? Reason, string UpdatedBy, DateTime UpdatedAt, string? BomFingerprint);
    private sealed record RelationRow
    {
        public Guid TemplateId { get; init; }
        public Guid MainMaterialId { get; init; }
        public string MainMaterialCode { get; init; } = "";
        public string MainMaterialName { get; init; } = "";
        public string TemplateName { get; init; } = "";
        public bool IsArchived { get; init; }
        public string TemplateUpdatedBy { get; init; } = "";
        public DateTime TemplateUpdatedAt { get; init; }
        public long TemplateRowVersion { get; init; }
        public Guid? RevisionId { get; init; }
        public int? RevisionVersion { get; init; }
        public string? RevisionState { get; init; }
        public string? ChangeNote { get; init; }
        public string? RevisionCreatedBy { get; init; }
        public DateTime? RevisionCreatedAt { get; init; }
        public string? PublishedBy { get; init; }
        public DateTime? PublishedAt { get; init; }
        public long? RevisionRowVersion { get; init; }
        public Guid? GroupId { get; init; }
        public string? GroupName { get; init; }
        public bool? IsRequired { get; init; }
        public string? SelectionMode { get; init; }
        public int? MinSelection { get; init; }
        public int? MaxSelection { get; init; }
        public bool? AutoSelectUnique { get; init; }
        public int? GroupSortOrder { get; init; }
        public Guid? OptionId { get; init; }
        public Guid? OptionMaterialId { get; init; }
        public string? OptionMaterialCode { get; init; }
        public string? OptionMaterialName { get; init; }
        public string? OptionMaterialKind { get; init; }
        public string? OptionUnitCode { get; init; }
        public string? QuantityMode { get; init; }
        public decimal? QuantityPerSet { get; init; }
        public bool? IsDefault { get; init; }
        public int? OptionSortOrder { get; init; }
    }
}
