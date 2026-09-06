using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed class MySqlMaterialRepository : IMaterialRepository
{
    private readonly string connectionString;
    private readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);

    public MySqlMaterialRepository(IOptions<PdmDatabaseOptions> options)
    {
        connectionString = options.Value.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("PLM MySQL连接字符串未配置。 ");
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    public async Task<IReadOnlyList<PdmMaterial>> ListMaterialsAsync(string? query, string? categoryCode, bool includeArchived, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var normalizedQuery = string.IsNullOrWhiteSpace(query) ? null : $"%{query.Trim()}%";
        var normalizedCategory = string.IsNullOrWhiteSpace(categoryCode) ? null : categoryCode.Trim();
        var rows = await connection.QueryAsync<MaterialRow>(new CommandDefinition(
            MaterialSelect +
            " WHERE (@IncludeArchived=1 OR is_archived=0)" +
            " AND (@CategoryCode IS NULL OR category_code=@CategoryCode)" +
            " AND (@Query IS NULL OR material_code LIKE @Query OR name LIKE @Query OR specification LIKE @Query OR material LIKE @Query OR brand LIKE @Query OR purchase_link LIKE @Query)" +
            " ORDER BY is_recommended DESC,reference_count DESC,material_code LIMIT @Limit",
            new { Query = normalizedQuery, CategoryCode = normalizedCategory, IncludeArchived = includeArchived, Limit = Math.Clamp(limit, 1, 500) },
            cancellationToken: cancellationToken));
        return rows.Select(MapMaterial).ToArray();
    }

    public async Task<MaterialPage> ListMaterialPageAsync(
        string? query,
        string? categoryCode,
        string? brand,
        bool includeArchived,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var normalizedQuery = string.IsNullOrWhiteSpace(query) ? null : $"%{query.Trim()}%";
        var normalizedCategory = string.IsNullOrWhiteSpace(categoryCode) ? null : $"{categoryCode.Trim()}%";
        var normalizedBrand = string.IsNullOrWhiteSpace(brand) ? null : brand.Trim();
        var normalizedPageSize = Math.Clamp(pageSize, 1, 200);
        var normalizedPage = Math.Max(page, 1);
        const string filters =
            " WHERE (@IncludeArchived=1 OR is_archived=0)" +
            " AND (@CategoryCode IS NULL OR category_code LIKE @CategoryCode)" +
            " AND (@Brand IS NULL OR brand=@Brand)" +
            " AND (@Query IS NULL OR material_code LIKE @Query OR name LIKE @Query OR specification LIKE @Query OR material LIKE @Query OR brand LIKE @Query OR category_code LIKE @Query OR u9_category_code LIKE @Query OR surface_treatment LIKE @Query OR purchase_link LIKE @Query OR remark LIKE @Query)";
        var parameters = new
        {
            Query = normalizedQuery,
            CategoryCode = normalizedCategory,
            Brand = normalizedBrand,
            IncludeArchived = includeArchived,
            Offset = (normalizedPage - 1) * normalizedPageSize,
            PageSize = normalizedPageSize
        };
        var total = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM material_master" + filters,
            parameters,
            cancellationToken: cancellationToken));
        var rows = await connection.QueryAsync<MaterialRow>(new CommandDefinition(
            MaterialSelect + filters +
            " ORDER BY is_recommended DESC,reference_count DESC,material_code LIMIT @PageSize OFFSET @Offset",
            parameters,
            cancellationToken: cancellationToken));
        return new(rows.Select(MapMaterial).ToArray(), total, normalizedPage, normalizedPageSize);
    }

    public async Task<PdmMaterial?> FindMaterialAsync(Guid materialId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await FindMaterialAsync(connection, null, materialId, cancellationToken);
    }

    public async Task<PdmMaterial?> FindMaterialByCodeAsync(string materialCode, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<MaterialRow>(new CommandDefinition(
            MaterialSelect + " WHERE material_code=@MaterialCode", new { MaterialCode = materialCode }, cancellationToken: cancellationToken));
        return row is null ? null : MapMaterial(row);
    }

    public async Task<IReadOnlyList<PdmMaterial>> FindMaterialsByCodesAsync(IReadOnlyList<string> materialCodes, CancellationToken cancellationToken)
    {
        var requested = materialCodes.Where(code => !string.IsNullOrWhiteSpace(code)).Select(code => code.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (requested.Length == 0) return [];
        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<MaterialRow>(new CommandDefinition(
            MaterialSelect + " WHERE material_code IN @MaterialCodes", new { MaterialCodes = requested }, cancellationToken: cancellationToken));
        return rows.Select(MapMaterial).ToArray();
    }

    public async Task<PdmMaterial?> FindMaterialBySourceBomItemAsync(Guid bomItemId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var row = await connection.QueryFirstOrDefaultAsync<MaterialRow>(new CommandDefinition(
            MaterialSelect + " WHERE source_bom_item_id=@BomItemId ORDER BY created_at LIMIT 1",
            new { BomItemId = bomItemId }, cancellationToken: cancellationToken));
        return row is null ? null : MapMaterial(row);
    }

    public async Task<IReadOnlyList<MaterialAttachment>> ListMaterialAttachmentsAsync(Guid materialId, MaterialAttachmentKind? kind, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<MaterialAttachmentRow>(new CommandDefinition(
            MaterialAttachmentSelect + " WHERE material_id=@MaterialId AND (@Kind IS NULL OR attachment_kind=@Kind) ORDER BY uploaded_at DESC",
            new { MaterialId = materialId, Kind = kind?.ToString() }, cancellationToken: cancellationToken));
        return rows.Select(MapMaterialAttachment).ToArray();
    }

    public async Task<MaterialAttachment?> FindMaterialAttachmentAsync(Guid attachmentId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<MaterialAttachmentRow>(new CommandDefinition(
            MaterialAttachmentSelect + " WHERE id=@AttachmentId", new { AttachmentId = attachmentId }, cancellationToken: cancellationToken));
        return row is null ? null : MapMaterialAttachment(row);
    }

    public async Task<MaterialAttachment> CreateMaterialAttachmentAsync(MaterialAttachment attachment, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO material_attachment(
                    id,material_id,attachment_kind,original_file_name,storage_root,storage_relative_path,file_length,sha256,uploaded_by,uploaded_at)
                VALUES(@Id,@MaterialId,@Kind,@OriginalFileName,@StorageRoot,@StorageRelativePath,@FileLength,@Sha256,@UploadedBy,@UploadedAt)
                """, new
                {
                    attachment.Id,
                    attachment.MaterialId,
                    Kind = attachment.Kind.ToString(),
                    attachment.OriginalFileName,
                    attachment.StorageRoot,
                    attachment.StorageRelativePath,
                    attachment.FileLength,
                    attachment.Sha256,
                    attachment.UploadedBy,
                    UploadedAt = attachment.UploadedAt.UtcDateTime
                }, cancellationToken: cancellationToken));
            return attachment;
        }
        catch (MySqlException exception) when (exception.Number == 1062)
        {
            throw new PdmConflictException("料品附件已经存在。");
        }
    }

    public async Task<IReadOnlyList<PdmMaterial>> FindApprovedMaterialsBySpecificationAsync(string specification, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<MaterialRow>(new CommandDefinition(
            MaterialSelect + " WHERE approval_status='Approved' AND is_archived=0 AND UPPER(TRIM(specification))=UPPER(@Specification) ORDER BY material_code",
            new { Specification = specification.Trim() }, cancellationToken: cancellationToken));
        return rows.Select(MapMaterial).ToArray();
    }

    public async Task<IReadOnlyList<PdmMaterial>> FindApprovedMaterialsByBrandAndSpecificationAsync(string brand, string specification, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<MaterialRow>(new CommandDefinition(
            MaterialSelect + " WHERE approval_status='Approved' AND is_archived=0 AND UPPER(TRIM(brand))=UPPER(@Brand) AND UPPER(TRIM(specification))=UPPER(@Specification) ORDER BY material_code",
            new { Brand = brand.Trim(), Specification = specification.Trim() }, cancellationToken: cancellationToken));
        return rows.Select(MapMaterial).ToArray();
    }

    public async Task<IReadOnlyList<MaterialCodeApplication>> ListMaterialCodeApplicationsAsync(Guid? projectId, MaterialCodeApplicationStatus? status, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<MaterialCodeApplicationRow>(new CommandDefinition(
            MaterialCodeApplicationSelect + " WHERE (@ProjectId IS NULL OR project_id=@ProjectId) AND (@Status IS NULL OR status=@Status) ORDER BY requested_at DESC",
            new { ProjectId = projectId, Status = status?.ToString() }, cancellationToken: cancellationToken));
        var workflow = await LoadApplicationWorkflowAsync(connection, cancellationToken);
        return rows.Select(row => MapMaterialCodeApplication(row, workflow.States, workflow.CompletedBomCodes)).ToArray();
    }

    public async Task<MaterialCodeApplication?> FindMaterialCodeApplicationAsync(Guid applicationId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<MaterialCodeApplicationRow>(new CommandDefinition(
            MaterialCodeApplicationSelect + " WHERE id=@ApplicationId", new { ApplicationId = applicationId }, cancellationToken: cancellationToken));
        if (row is null) return null;
        var workflow = await LoadApplicationWorkflowAsync(connection, cancellationToken);
        return MapMaterialCodeApplication(row, workflow.States, workflow.CompletedBomCodes);
    }

    public async Task<MaterialCodeApplication?> FindPendingMaterialCodeApplicationByBomItemAsync(Guid bomItemId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var row = await connection.QueryFirstOrDefaultAsync<MaterialCodeApplicationRow>(new CommandDefinition(
            MaterialCodeApplicationSelect + " WHERE bom_item_id=@BomItemId AND status='Pending' ORDER BY requested_at DESC LIMIT 1",
            new { BomItemId = bomItemId }, cancellationToken: cancellationToken));
        return row is null ? null : MapMaterialCodeApplication(row, new Dictionary<Guid, ApplicationWorkflowAudit>(), new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    public async Task<MaterialCodeApplication> CreateMaterialCodeApplicationAsync(MaterialCodeApplication application, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO material_code_application(id,project_id,bom_item_id,bom_header_kind,status,requested_by,requested_at,material_id,material_code,row_version) VALUES(@Id,@ProjectId,@BomItemId,@BomHeaderKind,@Status,@RequestedBy,@RequestedAt,@MaterialId,@MaterialCode,@RowVersion)",
            new
            {
                application.Id,
                application.ProjectId,
                application.BomItemId,
                BomHeaderKind = application.BomHeaderKind?.ToString(),
                Status = application.Status.ToString(),
                application.RequestedBy,
                RequestedAt = application.RequestedAt.UtcDateTime,
                application.MaterialId,
                application.MaterialCode,
                application.RowVersion
            }, cancellationToken: cancellationToken));
        return application;
    }

    public async Task<MaterialCodeApplication> DecideMaterialCodeApplicationAsync(Guid applicationId, long expectedRowVersion, MaterialCodeApplicationStatus status, string actor, string? comment, Guid? materialId, string? materialCode, DateTimeOffset decidedAt, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE material_code_application SET status=@Status,decided_by=@Actor,decided_at=@DecidedAt,decision_comment=@Comment,material_id=@MaterialId,material_code=@MaterialCode,row_version=row_version+1 WHERE id=@ApplicationId AND status='Pending' AND row_version=@ExpectedRowVersion",
            new { ApplicationId = applicationId, ExpectedRowVersion = expectedRowVersion, Status = status.ToString(), Actor = actor, DecidedAt = decidedAt.UtcDateTime, Comment = comment, MaterialId = materialId, MaterialCode = materialCode }, cancellationToken: cancellationToken));
        if (affected != 1) throw new PdmConflictException("料号申请已由其他标准化人员处理，请刷新后重试。");
        return await FindMaterialCodeApplicationAsync(applicationId, cancellationToken) ?? throw new PdmNotFoundException("料号申请不存在。");
    }

    public async Task RecordMaterialCodeApplicationWorkflowAsync(
        Guid applicationId,
        MaterialCodeWorkflowState state,
        string actor,
        DateTimeOffset occurredAt,
        string detail,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var exists = await connection.QuerySingleAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM material_code_application WHERE id=@ApplicationId",
            new { ApplicationId = applicationId }, cancellationToken: cancellationToken));
        if (exists != 1) throw new PdmNotFoundException("料号申请不存在。");
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO audit_entry(id,occurred_at,actor,action_name,entity_type,entity_id,detail_json)
            VALUES(@Id,@OccurredAt,@Actor,@ActionName,@EntityType,@EntityId,@DetailJson)
            """,
            new
            {
                Id = Guid.NewGuid(),
                OccurredAt = occurredAt.UtcDateTime,
                Actor = actor,
                ActionName = $"material-code.application.workflow.{state.ToString().ToLowerInvariant()}",
                EntityType = nameof(MaterialCodeApplication),
                EntityId = applicationId.ToString(),
                DetailJson = JsonSerializer.Serialize(new { detail }, jsonOptions)
            }, cancellationToken: cancellationToken));
    }

    public async Task<bool> HasMaterialReferencesAsync(Guid materialId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var referenced = await connection.QuerySingleAsync<int>(new CommandDefinition(
            """
            SELECT CASE WHEN EXISTS(SELECT 1 FROM bom_material_link WHERE material_id=@MaterialId)
                          OR EXISTS(SELECT 1 FROM material_master WHERE id=@MaterialId AND source_bom_item_id IS NOT NULL)
                          OR EXISTS(SELECT 1 FROM material_attachment WHERE material_id=@MaterialId)
                        THEN 1 ELSE 0 END
            """,
            new { MaterialId = materialId }, cancellationToken: cancellationToken));
        return referenced == 1;
    }

    public async Task<int> CountMaterialReferencesAsync(Guid materialId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await connection.QuerySingleAsync<int>(new CommandDefinition(
            """
            SELECT (SELECT COUNT(*) FROM bom_material_link WHERE material_id=@MaterialId)
                 + (SELECT CASE WHEN source_bom_item_id IS NULL THEN 0 ELSE 1 END FROM material_master WHERE id=@MaterialId)
                 + (SELECT COUNT(*) FROM material_attachment WHERE material_id=@MaterialId)
            """,
            new { MaterialId = materialId }, cancellationToken: cancellationToken));
    }

    public async Task<string> ReserveNextMaterialCodeAsync(MaterialCategory category, long minimumCurrentSequence, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO material_code_counter(u9_category_code,current_value,updated_at)
                SELECT @CounterScope,
                       COALESCE(MAX(CASE
                           WHEN material_code LIKE CONCAT(@NumberPrefix,'%')
                            AND CHAR_LENGTH(material_code)=CHAR_LENGTH(@NumberPrefix)+@SequenceLength
                            AND RIGHT(material_code,@SequenceLength) REGEXP '^[0-9]+$'
                           THEN CAST(RIGHT(material_code,@SequenceLength) AS UNSIGNED)
                           ELSE 0 END),0),
                       UTC_TIMESTAMP(6)
                FROM material_master
                ON DUPLICATE KEY UPDATE u9_category_code=u9_category_code
            """, new { category.CounterScope, category.NumberPrefix, category.SequenceLength }, transaction, cancellationToken: cancellationToken));
        var storedValue = await connection.QuerySingleAsync<long>(new CommandDefinition(
            "SELECT current_value FROM material_code_counter WHERE u9_category_code=@CounterScope FOR UPDATE",
            new { category.CounterScope }, transaction, cancellationToken: cancellationToken));
        var currentValue = Math.Max(storedValue, minimumCurrentSequence);
        var maximum = MaximumSequence(category.SequenceLength);
        if (currentValue >= maximum) throw new PdmRuleException($"分类 {category.Code} 的物料编码流水已用尽。");

        var nextValue = currentValue + 1;
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE material_code_counter SET current_value=@NextValue,updated_at=UTC_TIMESTAMP(6) WHERE u9_category_code=@CounterScope",
            new { category.CounterScope, NextValue = nextValue }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return $"{category.NumberPrefix}{nextValue.ToString($"D{category.SequenceLength}")}";
    }

    public async Task<long> GetMaterialCodeStartSequenceAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var value = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT setting_value FROM pdm_system_setting WHERE setting_key='material_code_start_sequence'",
            cancellationToken: cancellationToken));
        return long.TryParse(value, out var parsed) ? parsed : 1_000_000;
    }

    public async Task<long> SaveMaterialCodeStartSequenceAsync(long startSequence, DateTimeOffset updatedAt, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO pdm_system_setting(setting_key,setting_value,updated_at)
            VALUES('material_code_start_sequence',@Value,@UpdatedAt)
            ON DUPLICATE KEY UPDATE setting_value=VALUES(setting_value),updated_at=VALUES(updated_at)
            """,
            new { Value = startSequence.ToString(System.Globalization.CultureInfo.InvariantCulture), UpdatedAt = updatedAt.UtcDateTime },
            cancellationToken: cancellationToken));
        return startSequence;
    }

    public async Task<IReadOnlyList<MaterialDuplicateRule>> GetMaterialDuplicateRulesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var value = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT setting_value FROM pdm_system_setting WHERE setting_key='material_duplicate_rules'",
            cancellationToken: cancellationToken));
        if (string.IsNullOrWhiteSpace(value)) return [];
        try
        {
            return JsonSerializer.Deserialize<MaterialDuplicateRule[]>(value, jsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public async Task<IReadOnlyList<MaterialDuplicateRule>> SaveMaterialDuplicateRulesAsync(IReadOnlyList<MaterialDuplicateRule> rules, DateTimeOffset updatedAt, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO pdm_system_setting(setting_key,setting_value,updated_at)
            VALUES('material_duplicate_rules',@Value,@UpdatedAt)
            ON DUPLICATE KEY UPDATE setting_value=VALUES(setting_value),updated_at=VALUES(updated_at)
            """,
            new { Value = JsonSerializer.Serialize(rules, jsonOptions), UpdatedAt = updatedAt.UtcDateTime },
            cancellationToken: cancellationToken));
        return rules;
    }

    public async Task<PdmMaterial> CreateMaterialAsync(PdmMaterial material, MaterialCategory category, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(material.MaterialCode)) throw new PdmRuleException("物料编码尚未预留。");
        await using var connection = await OpenAsync(cancellationToken);
        try
        {
            var saved = material with { CategoryCode = category.Code };
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO material_master(
                    id,material_code,name,material_kind,supply_mode,unit_code,specification,material,remark,brand,surface_treatment,purchase_link,selection_advice,reference_price,model_3d_link,document_link,is_recommended,
                    weight,weight_unit,source_bom_item_id,approval_status,approved_by,approved_at,u9_category_code,u9_item_id,u9_item_code,u9_sync_confirmed,
                    source_system,master_owner,last_u9_synced_at,sync_status,created_by,created_at,updated_by,updated_at,row_version,category_code,is_archived,archived_by,archived_at)
                VALUES(
                    @Id,@MaterialCode,@Name,@MaterialKind,@SupplyMode,@UnitCode,@Specification,@Material,@Remark,@Brand,@SurfaceTreatment,@PurchaseLink,@SelectionAdvice,@ReferencePrice,@Model3DLink,@DocumentLink,@IsRecommended,
                    @Weight,@WeightUnit,@SourceBomItemId,@ApprovalStatus,@ApprovedBy,@ApprovedAt,@U9CategoryCode,@U9ItemId,@U9ItemCode,@U9SyncConfirmed,
                    @SourceSystem,@MasterOwner,@LastU9SyncedAt,@SyncStatus,@CreatedBy,@CreatedAt,@UpdatedBy,@UpdatedAt,@RowVersion,@CategoryCode,@IsArchived,@ArchivedBy,@ArchivedAt)
                """, MaterialParameters(saved), cancellationToken: cancellationToken));
            return saved;
        }
        catch (MySqlException exception) when (exception.Number == 1062)
        {
            throw new PdmConflictException("预留的PLM物料编码已被占用，请重试。");
        }
    }

    public async Task<PdmMaterial> UpsertU9MaterialAsync(PdmMaterial material, CancellationToken cancellationToken)
    {
        if (material.SourceSystem != MaterialDataSource.U9C || material.MasterOwner != MaterialMasterOwner.U9C)
            throw new PdmRuleException("U9C导入料品必须标记为U9C来源和U9C主控。");

        await using var connection = await OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO material_master(
                id,material_code,name,material_kind,supply_mode,unit_code,specification,material,remark,brand,surface_treatment,purchase_link,selection_advice,reference_price,model_3d_link,document_link,is_recommended,
                weight,weight_unit,source_bom_item_id,approval_status,approved_by,approved_at,u9_category_code,u9_item_id,u9_item_code,u9_sync_confirmed,
                source_system,master_owner,last_u9_synced_at,sync_status,created_by,created_at,updated_by,updated_at,row_version,category_code,is_archived,archived_by,archived_at)
            VALUES(
                @Id,@MaterialCode,@Name,@MaterialKind,@SupplyMode,@UnitCode,@Specification,@Material,@Remark,@Brand,@SurfaceTreatment,@PurchaseLink,@SelectionAdvice,@ReferencePrice,@Model3DLink,@DocumentLink,@IsRecommended,
                @Weight,@WeightUnit,@SourceBomItemId,@ApprovalStatus,@ApprovedBy,@ApprovedAt,@U9CategoryCode,@U9ItemId,@U9ItemCode,@U9SyncConfirmed,
                @SourceSystem,@MasterOwner,@LastU9SyncedAt,@SyncStatus,@CreatedBy,@CreatedAt,@UpdatedBy,@UpdatedAt,@RowVersion,@CategoryCode,@IsArchived,@ArchivedBy,@ArchivedAt)
            ON DUPLICATE KEY UPDATE
                name=IF(master_owner='U9C',VALUES(name),name),
                material_kind=IF(master_owner='U9C',VALUES(material_kind),material_kind),
                supply_mode=IF(master_owner='U9C',VALUES(supply_mode),supply_mode),
                unit_code=IF(master_owner='U9C',VALUES(unit_code),unit_code),
                specification=IF(master_owner='U9C',VALUES(specification),specification),
                material=IF(master_owner='U9C',VALUES(material),material),
                remark=IF(master_owner='U9C',VALUES(remark),remark),
                brand=IF(master_owner='U9C',VALUES(brand),brand),
                surface_treatment=IF(master_owner='U9C',VALUES(surface_treatment),surface_treatment),
                purchase_link=IF(master_owner='U9C',VALUES(purchase_link),purchase_link),
                weight=IF(master_owner='U9C',VALUES(weight),weight),
                weight_unit=IF(master_owner='U9C',VALUES(weight_unit),weight_unit),
                u9_category_code=IF(master_owner='U9C',VALUES(u9_category_code),u9_category_code),
                u9_item_id=IF(master_owner='U9C',VALUES(u9_item_id),u9_item_id),
                u9_item_code=IF(master_owner='U9C',VALUES(u9_item_code),u9_item_code),
                u9_sync_confirmed=IF(master_owner='U9C',1,u9_sync_confirmed),
                source_system=IF(master_owner='U9C','U9C',source_system),
                last_u9_synced_at=IF(master_owner='U9C',VALUES(last_u9_synced_at),last_u9_synced_at),
                sync_status=IF(master_owner='U9C','Succeeded',sync_status),
                updated_by=IF(master_owner='U9C',VALUES(updated_by),updated_by),
                updated_at=IF(master_owner='U9C',VALUES(updated_at),updated_at),
                row_version=IF(master_owner='U9C',row_version+1,row_version),
                category_code=IF(master_owner='U9C',VALUES(category_code),category_code),
                is_archived=IF(master_owner='U9C',0,is_archived),
                archived_by=IF(master_owner='U9C',NULL,archived_by),
                archived_at=IF(master_owner='U9C',NULL,archived_at)
            """, MaterialParameters(material), cancellationToken: cancellationToken));
        return await FindMaterialByCodeAsync(material.MaterialCode, cancellationToken)
            ?? throw new PdmRuleException("U9C料品导入后未能回读PLM主档。");
    }

    public async Task<int> ArchiveMissingU9MaterialsAsync(
        string categoryCode,
        DateTimeOffset synchronizedSince,
        string actor,
        DateTimeOffset archivedAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE material_master
            SET is_archived=1,archived_by=@Actor,archived_at=@ArchivedAt,
                updated_by=@Actor,updated_at=@ArchivedAt,row_version=row_version+1
            WHERE master_owner='U9C' AND category_code=@CategoryCode AND is_archived=0
              AND (last_u9_synced_at IS NULL OR last_u9_synced_at<@SynchronizedSince)
            """,
            new
            {
                CategoryCode = categoryCode,
                Actor = actor,
                ArchivedAt = archivedAt.UtcDateTime,
                SynchronizedSince = synchronizedSince.UtcDateTime
            },
            cancellationToken: cancellationToken));
    }

    public async Task MarkU9MaterialsObservedAsync(
        string categoryCode,
        IReadOnlyCollection<string> materialCodes,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        var normalizedCodes = materialCodes
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedCodes.Length == 0) return;

        await using var connection = await OpenAsync(cancellationToken);
        foreach (var codeBatch in normalizedCodes.Chunk(500))
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE material_master
                SET last_u9_synced_at=@ObservedAt
                WHERE master_owner='U9C' AND category_code=@CategoryCode AND material_code IN @MaterialCodes
                """,
                new
                {
                    CategoryCode = categoryCode,
                    MaterialCodes = codeBatch,
                    ObservedAt = observedAt.UtcDateTime
                },
                cancellationToken: cancellationToken));
        }
    }

    public async Task<PdmMaterial> UpdateMaterialAsync(PdmMaterial material, long expectedRowVersion, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        try
        {
            var affected = await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE material_master AS current_material
                SET name=@Name,material_kind=@MaterialKind,supply_mode=@SupplyMode,unit_code=@UnitCode,
                    specification=@Specification,material=@Material,remark=@Remark,brand=@Brand,surface_treatment=@SurfaceTreatment,purchase_link=@PurchaseLink,
                    selection_advice=@SelectionAdvice,reference_price=@ReferencePrice,model_3d_link=@Model3DLink,document_link=@DocumentLink,is_recommended=@IsRecommended,
                    weight=@Weight,weight_unit=@WeightUnit,category_code=@CategoryCode,updated_by=@UpdatedBy,updated_at=@UpdatedAt,row_version=row_version+1
                WHERE id=@Id AND row_version=@ExpectedRowVersion AND approval_status='Draft' AND is_archived=0
                """, new
                {
                    material.Id,
                    material.Name,
                    MaterialKind = material.Kind.ToString(),
                    SupplyMode = material.SupplyMode.ToString(),
                    material.UnitCode,
                    material.Specification,
                    material.Material,
                    material.Remark,
                    material.Brand,
                    material.SurfaceTreatment,
                    material.PurchaseLink,
                    material.SelectionAdvice,
                    material.ReferencePrice,
                    material.Model3DLink,
                    material.DocumentLink,
                    material.IsRecommended,
                    material.Weight,
                    material.WeightUnit,
                    material.CategoryCode,
                    material.UpdatedBy,
                    UpdatedAt = material.UpdatedAt.UtcDateTime,
                    ExpectedRowVersion = expectedRowVersion
                }, cancellationToken: cancellationToken));
            if (affected != 1) throw new PdmConflictException("物料主档已被批准或被其他用户修改，请刷新后重试。");
            return (await FindMaterialAsync(connection, null, material.Id, cancellationToken))!;
        }
        catch (MySqlException exception) when (exception.Number == 1062)
        {
            throw new PdmConflictException("PLM物料编码已存在。");
        }
    }

    public async Task<PdmMaterial> UpdatePlmMetadataAsync(PdmMaterial material, long expectedRowVersion, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE material_master
            SET selection_advice=@SelectionAdvice,reference_price=@ReferencePrice,model_3d_link=@Model3DLink,
                document_link=@DocumentLink,is_recommended=@IsRecommended,cover_image_attachment_id=@CoverImageAttachmentId,
                updated_by=@UpdatedBy,updated_at=@UpdatedAt,row_version=row_version+1
            WHERE id=@Id AND row_version=@ExpectedRowVersion AND is_archived=0
            """, new
            {
                material.Id,
                material.SelectionAdvice,
                material.ReferencePrice,
                material.Model3DLink,
                material.DocumentLink,
                material.IsRecommended,
                material.CoverImageAttachmentId,
                material.UpdatedBy,
                UpdatedAt = material.UpdatedAt.UtcDateTime,
                ExpectedRowVersion = expectedRowVersion
            }, cancellationToken: cancellationToken));
        if (affected != 1) throw new PdmConflictException("料品已停用或被其他用户修改，请刷新后重试。");
        return (await FindMaterialAsync(connection, null, material.Id, cancellationToken))!;
    }

    public async Task<(PdmMaterial Material, MaterialSyncTask Task)> UpdateAndEnqueueAsync(
        PdmMaterial material,
        long expectedRowVersion,
        MaterialSyncTask task,
        AuditEntry audit,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var affected = await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE material_master
                SET name=@Name,material_kind=@MaterialKind,supply_mode=@SupplyMode,unit_code=@UnitCode,
                    specification=@Specification,material=@Material,remark=@Remark,brand=@Brand,surface_treatment=@SurfaceTreatment,purchase_link=@PurchaseLink,
                    selection_advice=@SelectionAdvice,reference_price=@ReferencePrice,model_3d_link=@Model3DLink,document_link=@DocumentLink,is_recommended=@IsRecommended,
                    weight=@Weight,weight_unit=@WeightUnit,category_code=@CategoryCode,u9_category_code=@CategoryCode,
                    sync_status='PreviewReady',updated_by=@UpdatedBy,updated_at=@UpdatedAt,row_version=row_version+1
                WHERE id=@Id AND row_version=@ExpectedRowVersion AND approval_status='Approved'
                  AND sync_status<>'Pending' AND is_archived=0
                  AND NOT EXISTS(
                      SELECT 1 FROM u9_material_sync_task AS active_task
                      WHERE active_task.material_id=current_material.id AND active_task.status='Pending'
                  )
                """, new
                {
                    material.Id,
                    material.Name,
                    MaterialKind = material.Kind.ToString(),
                    SupplyMode = material.SupplyMode.ToString(),
                    material.UnitCode,
                    material.Specification,
                    material.Material,
                    material.Remark,
                    material.Brand,
                    material.SurfaceTreatment,
                    material.PurchaseLink,
                    material.SelectionAdvice,
                    material.ReferencePrice,
                    material.Model3DLink,
                    material.DocumentLink,
                    material.IsRecommended,
                    material.Weight,
                    material.WeightUnit,
                    material.CategoryCode,
                    material.UpdatedBy,
                    UpdatedAt = material.UpdatedAt.UtcDateTime,
                    ExpectedRowVersion = expectedRowVersion
                }, transaction, cancellationToken: cancellationToken));
            if (affected != 1) throw new PdmConflictException("料品不是可变更状态或已被其他用户修改，请刷新后重试。");
            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE u9_material_sync_task
                SET status='Superseded',next_attempt_at=NULL,last_error='料品已编辑，旧请求已废止。',updated_at=@UpdatedAt
                WHERE material_id=@MaterialId AND status<>'Succeeded'
                """, new { MaterialId = material.Id, UpdatedAt = audit.OccurredAt.UtcDateTime }, transaction, cancellationToken: cancellationToken));
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO u9_material_sync_task(
                    id,material_id,operation,status,correlation_id,payload_json,payload_sha256,attempt_count,next_attempt_at,last_error,
                    response_preview,u9_item_id,u9_item_code,created_at,updated_at)
                VALUES(@Id,@MaterialId,@Operation,@Status,@CorrelationId,@PayloadJson,@PayloadSha256,@AttemptCount,@NextAttemptAt,@LastError,
                    @ResponsePreview,@U9ItemId,@U9ItemCode,@CreatedAt,@UpdatedAt)
                """, TaskParameters(task), transaction, cancellationToken: cancellationToken));
            await InsertAuditAsync(connection, transaction, audit, cancellationToken);
            var saved = (await FindMaterialAsync(connection, transaction, material.Id, cancellationToken))!;
            await transaction.CommitAsync(cancellationToken);
            return (saved, task);
        }
        catch (MySqlException exception) when (exception.Number == 1062)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new PdmConflictException("相同内容的U9C同步任务已经存在。");
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<PdmMaterial> ArchiveMaterialAsync(Guid materialId, long expectedRowVersion, string actor, DateTimeOffset archivedAt, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE material_master
            SET is_archived=1,archived_by=@Actor,archived_at=@ArchivedAt,updated_by=@Actor,updated_at=@ArchivedAt,row_version=row_version+1
            WHERE id=@MaterialId AND row_version=@ExpectedRowVersion AND is_archived=0
            """, new { MaterialId = materialId, ExpectedRowVersion = expectedRowVersion, Actor = actor, ArchivedAt = archivedAt.UtcDateTime }, cancellationToken: cancellationToken));
        if (affected != 1) throw new PdmConflictException("料品已归档或已被其他用户修改，请刷新后重试。");
        return (await FindMaterialAsync(materialId, cancellationToken))!;
    }

    public async Task<PdmMaterial> ReactivateMaterialAsync(Guid materialId, long expectedRowVersion, string actor, DateTimeOffset reactivatedAt, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE material_master
            SET is_archived=0,archived_by=NULL,archived_at=NULL,updated_by=@Actor,updated_at=@ReactivatedAt,row_version=row_version+1
            WHERE id=@MaterialId AND row_version=@ExpectedRowVersion AND is_archived=1
            """, new { MaterialId = materialId, ExpectedRowVersion = expectedRowVersion, Actor = actor, ReactivatedAt = reactivatedAt.UtcDateTime }, cancellationToken: cancellationToken));
        if (affected != 1) throw new PdmConflictException("料品已启用或已被其他用户修改，请刷新后重试。");
        return (await FindMaterialAsync(materialId, cancellationToken))!;
    }

    public async Task<PdmMaterial> DeleteLocalMaterialAsync(Guid materialId, long expectedRowVersion, bool u9AbsenceConfirmed, CancellationToken cancellationToken)
    {
        if (!u9AbsenceConfirmed) throw new PdmRuleException("尚未实时确认U9C不存在，不能删除料品。");
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var existing = await FindMaterialAsync(connection, transaction, materialId, cancellationToken)
                ?? throw new PdmNotFoundException("物料主档不存在。");
            if (existing.RowVersion != expectedRowVersion)
                throw new PdmConflictException("物料主档已被其他用户修改，请刷新后重试。");
            if (existing.SourceSystem != MaterialDataSource.Pdm || existing.MasterOwner != MaterialMasterOwner.Pdm)
                throw new PdmRuleException("只有PLM来源且PLM主控的料品可以删除。");
            var referenced = await connection.QuerySingleAsync<int>(new CommandDefinition(
                """
                SELECT CASE WHEN EXISTS(SELECT 1 FROM bom_material_link WHERE material_id=@MaterialId)
                                  OR EXISTS(SELECT 1 FROM material_master WHERE id=@MaterialId AND source_bom_item_id IS NOT NULL)
                                THEN 1 ELSE 0 END
                """,
                new { MaterialId = materialId }, transaction, cancellationToken: cancellationToken));
            if (referenced == 1)
                throw new PdmRuleException("料品已被BOM引用或来源于BOM，不能删除；可改为停用。");
            var pendingTaskId = await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
                "SELECT id FROM u9_material_sync_task WHERE material_id=@MaterialId AND status='Pending' LIMIT 1 FOR UPDATE",
                new { MaterialId = materialId }, transaction, cancellationToken: cancellationToken));
            if (pendingTaskId is not null)
                throw new PdmRuleException("U9C同步请求正在执行，结果确认前不能删除。");

            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM u9_material_sync_task WHERE material_id=@MaterialId",
                new { MaterialId = materialId }, transaction, cancellationToken: cancellationToken));
            var affected = await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM material_master WHERE id=@MaterialId AND row_version=@ExpectedRowVersion",
                new { MaterialId = materialId, ExpectedRowVersion = expectedRowVersion }, transaction, cancellationToken: cancellationToken));
            if (affected != 1) throw new PdmConflictException("料品同步状态或数据版本已变化，请刷新后重试。");
            await transaction.CommitAsync(cancellationToken);
            return existing;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task LinkBomItemAsync(Guid bomItemId, Guid materialId, string actor, DateTimeOffset linkedAt, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO bom_material_link(bom_item_id,material_id,linked_by,linked_at)
            VALUES(@BomItemId,@MaterialId,@Actor,@LinkedAt)
            ON DUPLICATE KEY UPDATE material_id=VALUES(material_id),linked_by=VALUES(linked_by),linked_at=VALUES(linked_at)
            """, new { BomItemId = bomItemId, MaterialId = materialId, Actor = actor, LinkedAt = linkedAt.UtcDateTime }, cancellationToken: cancellationToken));
    }

    public async Task UnlinkBomItemAsync(Guid bomItemId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM bom_material_link WHERE bom_item_id=@BomItemId",
            new { BomItemId = bomItemId }, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<MaterialCategory>> ListCategoriesAsync(bool includeHidden, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<CategoryRow>(new CommandDefinition(
            CategorySelect + " WHERE (@IncludeHidden=1 OR mc.is_visible=1) ORDER BY mc.sort_order,mc.category_code",
            new { IncludeHidden = includeHidden }, cancellationToken: cancellationToken));
        return rows.Select(MapCategory).ToArray();
    }

    public async Task<MaterialCategory?> FindCategoryAsync(string categoryCode, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<CategoryRow>(new CommandDefinition(
            CategorySelect + " WHERE mc.category_code=@CategoryCode",
            new { CategoryCode = categoryCode.Trim() }, cancellationToken: cancellationToken));
        return row is null ? null : MapCategory(row);
    }

    public async Task<MaterialCategory> SaveCategoryAsync(MaterialCategory category, long? expectedRowVersion, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        try
        {
            if (expectedRowVersion is null)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO material_category(
                        category_code,category_name,parent_code,u9_category_id,pdm_kind,default_supply_mode,allow_create,is_visible,is_active,
                        number_prefix,sequence_length,counter_scope,sort_order,updated_by,updated_at,row_version)
                    VALUES(
                        @Code,@Name,@ParentCode,@U9CategoryId,@PdmKind,@DefaultSupplyMode,@AllowCreate,@IsVisible,@IsActive,
                        @NumberPrefix,@SequenceLength,@CounterScope,@SortOrder,@UpdatedBy,@UpdatedAt,1)
                    """, CategoryParameters(category), cancellationToken: cancellationToken));
            }
            else
            {
                var affected = await connection.ExecuteAsync(new CommandDefinition(
                    """
                    UPDATE material_category
                    SET category_name=@Name,parent_code=@ParentCode,u9_category_id=@U9CategoryId,pdm_kind=@PdmKind,
                        default_supply_mode=@DefaultSupplyMode,allow_create=@AllowCreate,is_visible=@IsVisible,is_active=@IsActive,
                        number_prefix=@NumberPrefix,sequence_length=@SequenceLength,counter_scope=@CounterScope,sort_order=@SortOrder,
                        updated_by=@UpdatedBy,updated_at=@UpdatedAt,row_version=row_version+1
                    WHERE category_code=@Code AND row_version=@ExpectedRowVersion
                    """, new
                    {
                        category.Code,
                        category.Name,
                        category.ParentCode,
                        category.U9CategoryId,
                        PdmKind = category.PdmKind?.ToString(),
                        DefaultSupplyMode = category.DefaultSupplyMode.ToString(),
                        category.AllowCreate,
                        category.IsVisible,
                        category.IsActive,
                        category.NumberPrefix,
                        category.SequenceLength,
                        category.CounterScope,
                        category.SortOrder,
                        category.UpdatedBy,
                        UpdatedAt = category.UpdatedAt.UtcDateTime,
                        ExpectedRowVersion = expectedRowVersion.Value
                    }, cancellationToken: cancellationToken));
                if (affected != 1) throw new PdmConflictException("料品分类已被其他用户修改，请刷新后重试。");
            }
            return (await FindCategoryAsync(category.Code, cancellationToken))!;
        }
        catch (MySqlException exception) when (exception.Number == 1062)
        {
            throw new PdmConflictException("料品分类编码或U9C分类ID已存在。");
        }
        catch (MySqlException exception) when (exception.Number == 1452)
        {
            throw new PdmRuleException("上级料品分类不存在。");
        }
    }

    public async Task<MaterialCategory> AdvanceCategoryCounterAsync(MaterialCategory category, long minimumValue, CancellationToken cancellationToken)
    {
        var maximum = checked((long)Math.Pow(10, category.SequenceLength) - 1);
        if (minimumValue < 0 || minimumValue > maximum)
            throw new PdmRuleException($"分类 {category.Code} 的流水必须在0到{maximum}之间。");
        await using var connection = await OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO material_code_counter(u9_category_code,current_value,updated_at)
            VALUES(@CounterScope,@MinimumValue,UTC_TIMESTAMP(6))
            ON DUPLICATE KEY UPDATE current_value=GREATEST(current_value,VALUES(current_value)),updated_at=UTC_TIMESTAMP(6)
            """, new { category.CounterScope, MinimumValue = minimumValue }, cancellationToken: cancellationToken));
        return (await FindCategoryAsync(category.Code, cancellationToken))!;
    }

    public async Task<IReadOnlyList<MaterialCategoryRule>> ListCategoryRulesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<CategoryRuleRow>(new CommandDefinition(
            "SELECT pdm_kind,u9_category_code,u9_category_name,default_supply_mode,is_enabled,updated_by,updated_at FROM material_category_rule ORDER BY u9_category_code",
            cancellationToken: cancellationToken));
        return rows.Select(MapRule).ToArray();
    }

    public async Task<MaterialCategoryRule?> FindCategoryRuleAsync(MaterialKind kind, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<CategoryRuleRow>(new CommandDefinition(
            "SELECT pdm_kind,u9_category_code,u9_category_name,default_supply_mode,is_enabled,updated_by,updated_at FROM material_category_rule WHERE pdm_kind=@PdmKind",
            new { PdmKind = kind.ToString() }, cancellationToken: cancellationToken));
        return row is null ? null : MapRule(row);
    }

    public async Task<MaterialCategoryRule> SaveCategoryRuleAsync(MaterialCategoryRule rule, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO material_category_rule(pdm_kind,u9_category_code,u9_category_name,default_supply_mode,is_enabled,updated_by,updated_at)
                VALUES(@PdmKind,@U9CategoryCode,@U9CategoryName,@DefaultSupplyMode,@IsEnabled,@UpdatedBy,@UpdatedAt)
                ON DUPLICATE KEY UPDATE u9_category_code=VALUES(u9_category_code),u9_category_name=VALUES(u9_category_name),
                    default_supply_mode=VALUES(default_supply_mode),is_enabled=VALUES(is_enabled),updated_by=VALUES(updated_by),updated_at=VALUES(updated_at)
                """, new
                {
                    PdmKind = rule.PdmKind.ToString(),
                    rule.U9CategoryCode,
                    rule.U9CategoryName,
                    DefaultSupplyMode = rule.DefaultSupplyMode.ToString(),
                    rule.IsEnabled,
                    rule.UpdatedBy,
                    UpdatedAt = rule.UpdatedAt.UtcDateTime
                }, cancellationToken: cancellationToken));
            return rule;
        }
        catch (MySqlException exception) when (exception.Number == 1062)
        {
            throw new PdmConflictException("U9C料品分类编码已映射到其他PLM分类。");
        }
    }

    public async Task<(PdmMaterial Material, MaterialSyncTask Task)> ApproveAndEnqueueAsync(
        PdmMaterial material, long expectedRowVersion, string u9CategoryCode, MaterialSyncTask task, AuditEntry audit, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var affected = await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE material_master
                SET material_code=@MaterialCode,approval_status='Approved',approved_by=@Actor,approved_at=@OccurredAt,u9_category_code=@U9CategoryCode,
                    sync_status='PreviewReady',updated_by=@Actor,updated_at=@OccurredAt,row_version=row_version+1
                WHERE id=@MaterialId AND row_version=@ExpectedRowVersion AND approval_status='Draft'
                """, new
                {
                    MaterialId = material.Id,
                    material.MaterialCode,
                    ExpectedRowVersion = expectedRowVersion,
                    U9CategoryCode = u9CategoryCode,
                    Actor = audit.Actor,
                    OccurredAt = audit.OccurredAt.UtcDateTime
                }, transaction, cancellationToken: cancellationToken));
            if (affected != 1) throw new PdmConflictException("物料主档已被批准或被其他用户修改，请刷新后重试。");
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO u9_material_sync_task(
                    id,material_id,operation,status,correlation_id,payload_json,payload_sha256,attempt_count,next_attempt_at,last_error,
                    response_preview,u9_item_id,u9_item_code,created_at,updated_at)
                VALUES(@Id,@MaterialId,@Operation,@Status,@CorrelationId,@PayloadJson,@PayloadSha256,@AttemptCount,@NextAttemptAt,@LastError,
                    @ResponsePreview,@U9ItemId,@U9ItemCode,@CreatedAt,@UpdatedAt)
                """, TaskParameters(task), transaction, cancellationToken: cancellationToken));
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO audit_entry(id,occurred_at,actor,action_name,entity_type,entity_id,detail_json)
                VALUES(@Id,@OccurredAt,@Actor,@Action,@EntityType,@EntityId,@DetailJson)
                """, new
                {
                    audit.Id,
                    OccurredAt = audit.OccurredAt.UtcDateTime,
                    audit.Actor,
                    audit.Action,
                    audit.EntityType,
                    audit.EntityId,
                    DetailJson = JsonSerializer.Serialize(new { detail = audit.Detail }, jsonOptions)
                }, transaction, cancellationToken: cancellationToken));
            var saved = (await FindMaterialAsync(connection, transaction, material.Id, cancellationToken))!;
            await transaction.CommitAsync(cancellationToken);
            return (saved, task);
        }
        catch (MySqlException exception) when (exception.Number == 1062)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new PdmConflictException("PLM物料编码或相同内容的U9C同步任务已经存在。");
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<IReadOnlyList<MaterialSyncTask>> ListSyncTasksAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<SyncTaskRow>(new CommandDefinition(SyncTaskSelect + " ORDER BY created_at DESC", cancellationToken: cancellationToken));
        return rows.Select(MapTask).ToArray();
    }

    public async Task<MaterialSyncTask?> FindSyncTaskAsync(Guid taskId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<SyncTaskRow>(new CommandDefinition(SyncTaskSelect + " WHERE id=@TaskId", new { TaskId = taskId }, cancellationToken: cancellationToken));
        return row is null ? null : MapTask(row);
    }

    public async Task<MaterialSyncTask> RetrySyncTaskAsync(
        Guid taskId,
        string payloadJson,
        string payloadSha256,
        DateTimeOffset retriedAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE u9_material_sync_task
            SET status='PreviewReady',payload_json=@PayloadJson,payload_sha256=@PayloadSha256,
                next_attempt_at=NULL,last_error=NULL,response_preview=NULL,updated_at=@RetriedAt
            WHERE id=@TaskId AND status IN ('PreviewReady','Failed','NeedsReview')
            """, new { TaskId = taskId, PayloadJson = payloadJson, PayloadSha256 = payloadSha256, RetriedAt = retriedAt.UtcDateTime }, cancellationToken: cancellationToken));
        if (affected != 1)
        {
            var existing = await FindSyncTaskAsync(taskId, cancellationToken);
            if (existing is null) throw new PdmNotFoundException("U9C同步任务不存在。");
            throw new PdmRuleException(existing.Status == MaterialSyncStatus.Superseded
                ? "料品已编辑，旧同步任务已废止，请使用最新请求。"
                : "当前状态的同步任务不能重试。");
        }
        return (await FindSyncTaskAsync(taskId, cancellationToken))!;
    }

    public async Task<MaterialSyncTask> ScheduleSyncTaskAsync(Guid taskId, DateTimeOffset dueAt, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE u9_material_sync_task SET next_attempt_at=@DueAt,updated_at=@DueAt WHERE id=@TaskId AND status='PreviewReady'",
            new { TaskId = taskId, DueAt = dueAt.UtcDateTime }, cancellationToken: cancellationToken));
        if (affected != 1)
        {
            var existing = await FindSyncTaskAsync(taskId, cancellationToken);
            if (existing is null) throw new PdmNotFoundException("U9C同步任务不存在。");
            throw new PdmRuleException("只有待执行的U9C同步任务才能进入后台队列。");
        }
        return (await FindSyncTaskAsync(taskId, cancellationToken))!;
    }

    public async Task<MaterialSyncBatch> CreateSyncBatchAsync(MaterialSyncBatch batch, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var taskIds = batch.Items.Select(item => item.TaskId).ToArray();
            var tasks = (await connection.QueryAsync<(Guid Id, string Status)>(new CommandDefinition(
                "SELECT id,status FROM u9_material_sync_task WHERE id IN @TaskIds FOR UPDATE",
                new { TaskIds = taskIds }, transaction, cancellationToken: cancellationToken))).ToArray();
            if (tasks.Length != taskIds.Length)
                throw new PdmRuleException("部分U9C同步任务不存在，请刷新后重试。");
            if (tasks.Any(task => task.Status is not ("PreviewReady" or "Failed" or "NeedsReview")))
                throw new PdmRuleException("选中的任务状态已经变化，请刷新后重新选择待执行项。");
            var duplicate = await connection.QuerySingleAsync<int>(new CommandDefinition(
                """
                SELECT COUNT(*)
                FROM u9_material_sync_batch_item item
                INNER JOIN u9_material_sync_batch batch ON batch.id=item.batch_id
                WHERE item.task_id IN @TaskIds AND item.status IN ('Queued','Running')
                  AND batch.status IN ('Queued','Running')
                """, new { TaskIds = taskIds }, transaction, cancellationToken: cancellationToken));
            if (duplicate > 0) throw new PdmConflictException("选中的任务已存在于运行中的批次，请刷新后查看进度。");

            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO u9_material_sync_batch(
                    id,status,requested_by,requested_role,total_count,completed_count,succeeded_count,waiting_count,failed_count,
                    current_task_id,current_material_code,last_error,created_at,started_at,completed_at)
                VALUES(@Id,@Status,@RequestedBy,@RequestedRole,@TotalCount,@CompletedCount,@SucceededCount,@WaitingCount,@FailedCount,
                    @CurrentTaskId,@CurrentMaterialCode,@LastError,@CreatedAt,@StartedAt,@CompletedAt)
                """, BatchParameters(batch), transaction, cancellationToken: cancellationToken));
            foreach (var item in batch.Items)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO u9_material_sync_batch_item(
                        id,batch_id,task_id,ordinal_no,status,message,started_at,completed_at,lease_expires_at)
                    VALUES(@Id,@BatchId,@TaskId,@Ordinal,@Status,@Message,@StartedAt,@CompletedAt,@LeaseExpiresAt)
                    """, BatchItemParameters(item), transaction, cancellationToken: cancellationToken));
            }
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            if (transaction.Connection is not null) await transaction.RollbackAsync(cancellationToken);
            throw;
        }
        return (await FindSyncBatchAsync(batch.Id, cancellationToken))!;
    }

    public async Task<MaterialSyncBatch?> FindSyncBatchAsync(Guid batchId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<SyncBatchRow>(new CommandDefinition(
            "SELECT * FROM u9_material_sync_batch WHERE id=@BatchId",
            new { BatchId = batchId }, cancellationToken: cancellationToken));
        if (row is null) return null;
        var items = await connection.QueryAsync<SyncBatchItemRow>(new CommandDefinition(
            "SELECT * FROM u9_material_sync_batch_item WHERE batch_id=@BatchId ORDER BY ordinal_no",
            new { BatchId = batchId }, cancellationToken: cancellationToken));
        return MapBatch(row, items);
    }

    public async Task<IReadOnlyList<MaterialSyncBatch>> ListRecentSyncBatchesAsync(string actor, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var rows = (await connection.QueryAsync<SyncBatchRow>(new CommandDefinition(
            "SELECT * FROM u9_material_sync_batch WHERE requested_by=@Actor ORDER BY created_at DESC LIMIT @Limit",
            new { Actor = actor, Limit = Math.Clamp(limit, 1, 50) }, cancellationToken: cancellationToken))).ToArray();
        var results = new List<MaterialSyncBatch>(rows.Length);
        foreach (var row in rows)
        {
            var items = await connection.QueryAsync<SyncBatchItemRow>(new CommandDefinition(
                "SELECT * FROM u9_material_sync_batch_item WHERE batch_id=@BatchId ORDER BY ordinal_no",
                new { BatchId = row.Id }, cancellationToken: cancellationToken));
            results.Add(MapBatch(row, items));
        }
        return results;
    }

    public async Task<MaterialSyncBatchClaim?> ClaimNextSyncBatchItemAsync(
        DateTimeOffset now,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var item = await connection.QueryFirstOrDefaultAsync<SyncBatchItemRow>(new CommandDefinition(
                """
                SELECT item.*
                FROM u9_material_sync_batch_item item
                INNER JOIN u9_material_sync_batch batch ON batch.id=item.batch_id
                WHERE batch.status IN ('Queued','Running')
                  AND (item.status='Queued' OR (item.status='Running' AND item.lease_expires_at<=@Now))
                ORDER BY batch.created_at,item.ordinal_no
                LIMIT 1 FOR UPDATE
                """, new { Now = now.UtcDateTime }, transaction, cancellationToken: cancellationToken));
            if (item is null)
            {
                await transaction.CommitAsync(cancellationToken);
                return null;
            }

            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE u9_material_sync_batch_item
                SET status='Running',started_at=COALESCE(started_at,@Now),completed_at=NULL,message=NULL,lease_expires_at=@LeaseExpiresAt
                WHERE id=@ItemId
                """, new { ItemId = item.Id, Now = now.UtcDateTime, LeaseExpiresAt = leaseExpiresAt.UtcDateTime }, transaction, cancellationToken: cancellationToken));
            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE u9_material_sync_batch batch
                INNER JOIN u9_material_sync_task task ON task.id=@TaskId
                INNER JOIN material_master material ON material.id=task.material_id
                SET batch.status='Running',batch.started_at=COALESCE(batch.started_at,@Now),
                    batch.current_task_id=@TaskId,batch.current_material_code=material.material_code,batch.last_error=NULL
                WHERE batch.id=@BatchId
                """, new { item.BatchId, item.TaskId, Now = now.UtcDateTime }, transaction, cancellationToken: cancellationToken));
            var batchRow = await connection.QuerySingleAsync<SyncBatchRow>(new CommandDefinition(
                "SELECT * FROM u9_material_sync_batch WHERE id=@BatchId",
                new { item.BatchId }, transaction, cancellationToken: cancellationToken));
            var claimedItem = await connection.QuerySingleAsync<SyncBatchItemRow>(new CommandDefinition(
                "SELECT * FROM u9_material_sync_batch_item WHERE id=@ItemId",
                new { ItemId = item.Id }, transaction, cancellationToken: cancellationToken));
            var allItems = await connection.QueryAsync<SyncBatchItemRow>(new CommandDefinition(
                "SELECT * FROM u9_material_sync_batch_item WHERE batch_id=@BatchId ORDER BY ordinal_no",
                new { item.BatchId }, transaction, cancellationToken: cancellationToken));
            await transaction.CommitAsync(cancellationToken);
            return new MaterialSyncBatchClaim(MapBatch(batchRow, allItems), MapBatchItem(claimedItem));
        }
        catch
        {
            if (transaction.Connection is not null) await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<MaterialSyncBatch> CompleteSyncBatchItemAsync(
        Guid batchId,
        Guid itemId,
        MaterialSyncBatchItemStatus status,
        string? message,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken)
    {
        if (status is not (MaterialSyncBatchItemStatus.Succeeded or MaterialSyncBatchItemStatus.Waiting or MaterialSyncBatchItemStatus.Failed))
            throw new ArgumentOutOfRangeException(nameof(status));
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var affected = await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE u9_material_sync_batch_item
                SET status=@Status,message=@Message,completed_at=@CompletedAt,lease_expires_at=NULL
                WHERE id=@ItemId AND batch_id=@BatchId AND status='Running'
                """, new { ItemId = itemId, BatchId = batchId, Status = status.ToString(), Message = Truncate(message, 2000), CompletedAt = completedAt.UtcDateTime }, transaction, cancellationToken: cancellationToken));
            if (affected != 1) throw new PdmConflictException("U9C批量同步明细状态已变化。");

            var counts = await connection.QuerySingleAsync<SyncBatchCountsRow>(new CommandDefinition(
                """
                SELECT COUNT(*) total_count,
                    SUM(status IN ('Succeeded','Waiting','Failed')) completed_count,
                    SUM(status='Succeeded') succeeded_count,
                    SUM(status='Waiting') waiting_count,
                    SUM(status='Failed') failed_count
                FROM u9_material_sync_batch_item WHERE batch_id=@BatchId
                """, new { BatchId = batchId }, transaction, cancellationToken: cancellationToken));
            var isComplete = counts.CompletedCount == counts.TotalCount;
            var batchStatus = !isComplete ? MaterialSyncBatchStatus.Running
                : counts.FailedCount == counts.TotalCount ? MaterialSyncBatchStatus.Failed
                : counts.FailedCount > 0 || counts.WaitingCount > 0 ? MaterialSyncBatchStatus.PartiallySucceeded
                : MaterialSyncBatchStatus.Succeeded;
            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE u9_material_sync_batch
                SET status=@Status,completed_count=@CompletedCount,succeeded_count=@SucceededCount,
                    waiting_count=@WaitingCount,failed_count=@FailedCount,current_task_id=NULL,current_material_code=NULL,
                    last_error=@LastError,completed_at=@CompletedAt
                WHERE id=@BatchId
                """, new
                {
                    BatchId = batchId,
                    Status = batchStatus.ToString(),
                    counts.CompletedCount,
                    counts.SucceededCount,
                    counts.WaitingCount,
                    counts.FailedCount,
                    LastError = status == MaterialSyncBatchItemStatus.Failed ? Truncate(message, 2000) : null,
                    CompletedAt = isComplete ? completedAt.UtcDateTime : (DateTime?)null
                }, transaction, cancellationToken: cancellationToken));
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            if (transaction.Connection is not null) await transaction.RollbackAsync(cancellationToken);
            throw;
        }
        return (await FindSyncBatchAsync(batchId, cancellationToken))!;
    }

    public async Task<(PdmMaterial Material, MaterialSyncTask Task)> ReassignMaterialCodeAndEnqueueAsync(
        PdmMaterial material,
        long expectedRowVersion,
        string previousMaterialCode,
        MaterialSyncTask previousTask,
        MaterialSyncTask replacementTask,
        AuditEntry audit,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var materialAffected = await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE material_master
                SET material_code=@MaterialCode,sync_status='PreviewReady',updated_by=@Actor,updated_at=@OccurredAt,row_version=row_version+1
                WHERE id=@MaterialId AND row_version=@ExpectedRowVersion AND material_code=@PreviousMaterialCode
                  AND approval_status='Approved' AND u9_sync_confirmed=0 AND is_archived=0
                """, new
                {
                    MaterialId = material.Id,
                    material.MaterialCode,
                    ExpectedRowVersion = expectedRowVersion,
                    PreviousMaterialCode = previousMaterialCode,
                    audit.Actor,
                    OccurredAt = audit.OccurredAt.UtcDateTime
                }, transaction, cancellationToken: cancellationToken));
            if (materialAffected != 1)
                throw new PdmConflictException("料品状态或料号已经变化，不能自动换号。");

            var taskAffected = await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE u9_material_sync_task
                SET status='Superseded',next_attempt_at=NULL,last_error=@LastError,updated_at=@OccurredAt
                WHERE id=@TaskId AND material_id=@MaterialId AND status IN ('PreviewReady','Failed','NeedsReview')
                """, new
                {
                    TaskId = previousTask.Id,
                    MaterialId = material.Id,
                    LastError = $"U9C已占用料号 {previousMaterialCode}，已自动换号。",
                    OccurredAt = audit.OccurredAt.UtcDateTime
                }, transaction, cancellationToken: cancellationToken));
            if (taskAffected != 1)
                throw new PdmConflictException("原U9C同步任务状态已经变化，不能自动换号。");

            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE material_code_application
                SET material_code=@MaterialCode,row_version=row_version+1
                WHERE material_id=@MaterialId AND status='Approved'
                """, new { MaterialId = material.Id, material.MaterialCode }, transaction, cancellationToken: cancellationToken));
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO u9_material_sync_task(
                    id,material_id,operation,status,correlation_id,payload_json,payload_sha256,attempt_count,next_attempt_at,last_error,
                    response_preview,u9_item_id,u9_item_code,created_at,updated_at)
                VALUES(@Id,@MaterialId,@Operation,@Status,@CorrelationId,@PayloadJson,@PayloadSha256,@AttemptCount,@NextAttemptAt,@LastError,
                    @ResponsePreview,@U9ItemId,@U9ItemCode,@CreatedAt,@UpdatedAt)
                """, TaskParameters(replacementTask), transaction, cancellationToken: cancellationToken));
            await InsertAuditAsync(connection, transaction, audit, cancellationToken);
            var saved = (await FindMaterialAsync(connection, transaction, material.Id, cancellationToken))!;
            var row = await connection.QuerySingleAsync<SyncTaskRow>(new CommandDefinition(
                SyncTaskSelect + " WHERE id=@TaskId", new { TaskId = replacementTask.Id }, transaction, cancellationToken: cancellationToken));
            await transaction.CommitAsync(cancellationToken);
            return (saved, MapTask(row));
        }
        catch (MySqlException exception) when (exception.Number == 1062)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new PdmConflictException("重新分配的PLM料号已经被占用，请重试。");
        }
        catch
        {
            if (transaction.Connection is not null) await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<MaterialSyncTask> BeginSyncTaskAsync(Guid taskId, DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var materialId = await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
            "SELECT material_id FROM u9_material_sync_task WHERE id=@TaskId",
            new { TaskId = taskId }, cancellationToken: cancellationToken));
        if (materialId is null) throw new PdmNotFoundException("U9C同步任务不存在。");

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var materialAffected = await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE material_master
                SET sync_status='Pending'
                WHERE id=@MaterialId AND approval_status='Approved' AND sync_status<>'Pending' AND is_archived=0
                """, new { MaterialId = materialId.Value }, transaction, cancellationToken: cancellationToken));
            var taskAffected = materialAffected == 1
                ? await connection.ExecuteAsync(new CommandDefinition(
                    """
                    UPDATE u9_material_sync_task
                    SET status='Pending',attempt_count=attempt_count+1,next_attempt_at=NULL,last_error=NULL,updated_at=@StartedAt
                    WHERE id=@TaskId AND status IN ('PreviewReady','Failed','NeedsReview')
                    """, new { TaskId = taskId, StartedAt = startedAt.UtcDateTime }, transaction, cancellationToken: cancellationToken))
                : 0;
            if (materialAffected != 1 || taskAffected != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                var existing = await FindSyncTaskAsync(taskId, cancellationToken);
                if (existing is null) throw new PdmNotFoundException("U9C同步任务不存在。");
                throw new PdmRuleException(existing.Status switch
                {
                    MaterialSyncStatus.Succeeded => "已成功的同步任务不能重复执行。",
                    MaterialSyncStatus.Superseded => "料品已编辑，旧同步任务已废止，请使用最新请求。",
                    _ => "U9C同步任务正在执行。"
                });
            }
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            if (transaction.Connection is not null) await transaction.RollbackAsync(cancellationToken);
            throw;
        }
        return (await FindSyncTaskAsync(taskId, cancellationToken))!;
    }

    public async Task<(PdmMaterial Material, MaterialSyncTask Task)> CompleteSyncTaskAsync(
        Guid taskId,
        string? u9ItemId,
        string u9ItemCode,
        string responsePreview,
        AuditEntry audit,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var materialId = await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
                "SELECT material_id FROM u9_material_sync_task WHERE id=@TaskId",
                new { TaskId = taskId }, transaction, cancellationToken: cancellationToken));
            if (materialId is null) throw new PdmNotFoundException("U9C同步任务不存在。");
            var affected = await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE u9_material_sync_task
                SET status='Succeeded',last_error=NULL,response_preview=@ResponsePreview,u9_item_id=@U9ItemId,
                    u9_item_code=@U9ItemCode,updated_at=@OccurredAt
                WHERE id=@TaskId AND status='Pending'
                """, new
                {
                    TaskId = taskId,
                    ResponsePreview = responsePreview,
                    U9ItemId = u9ItemId,
                    U9ItemCode = u9ItemCode,
                    OccurredAt = audit.OccurredAt.UtcDateTime
                }, transaction, cancellationToken: cancellationToken));
            if (affected != 1) throw new PdmConflictException("U9C同步任务状态已变化，请刷新后重试。");
            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE material_master
                SET u9_item_id=@U9ItemId,u9_item_code=@U9ItemCode,u9_sync_confirmed=1,sync_status='Succeeded',updated_by=@Actor,
                    updated_at=@OccurredAt,row_version=row_version+1
                WHERE id=@MaterialId
                """, new
                {
                    MaterialId = materialId.Value,
                    U9ItemId = u9ItemId,
                    U9ItemCode = u9ItemCode,
                    audit.Actor,
                    OccurredAt = audit.OccurredAt.UtcDateTime
                }, transaction, cancellationToken: cancellationToken));
            await InsertAuditAsync(connection, transaction, audit, cancellationToken);
            var material = (await FindMaterialAsync(connection, transaction, materialId.Value, cancellationToken))!;
            var row = await connection.QuerySingleAsync<SyncTaskRow>(new CommandDefinition(
                SyncTaskSelect + " WHERE id=@TaskId", new { TaskId = taskId }, transaction, cancellationToken: cancellationToken));
            await transaction.CommitAsync(cancellationToken);
            return (material, MapTask(row));
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<MaterialSyncTask> FailSyncTaskAsync(
        Guid taskId,
        MaterialSyncStatus status,
        string error,
        string? responsePreview,
        AuditEntry audit,
        CancellationToken cancellationToken)
    {
        if (status is not (MaterialSyncStatus.Failed or MaterialSyncStatus.NeedsReview))
            throw new ArgumentOutOfRangeException(nameof(status));
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var materialId = await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
                "SELECT material_id FROM u9_material_sync_task WHERE id=@TaskId",
                new { TaskId = taskId }, transaction, cancellationToken: cancellationToken));
            if (materialId is null) throw new PdmNotFoundException("U9C同步任务不存在。");
            var affected = await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE u9_material_sync_task
                SET status=@Status,last_error=@Error,response_preview=@ResponsePreview,updated_at=@OccurredAt
                WHERE id=@TaskId AND status='Pending'
                """, new
                {
                    TaskId = taskId,
                    Status = status.ToString(),
                    Error = error,
                    ResponsePreview = responsePreview,
                    OccurredAt = audit.OccurredAt.UtcDateTime
                }, transaction, cancellationToken: cancellationToken));
            if (affected != 1) throw new PdmConflictException("U9C同步任务状态已变化，请刷新后重试。");
            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE material_master
                SET sync_status=@Status,updated_by=@Actor,updated_at=@OccurredAt,row_version=row_version+1
                WHERE id=@MaterialId
                """, new
                {
                    MaterialId = materialId.Value,
                    Status = status.ToString(),
                    audit.Actor,
                    OccurredAt = audit.OccurredAt.UtcDateTime
                }, transaction, cancellationToken: cancellationToken));
            await InsertAuditAsync(connection, transaction, audit, cancellationToken);
            var row = await connection.QuerySingleAsync<SyncTaskRow>(new CommandDefinition(
                SyncTaskSelect + " WHERE id=@TaskId", new { TaskId = taskId }, transaction, cancellationToken: cancellationToken));
            await transaction.CommitAsync(cancellationToken);
            return MapTask(row);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<U9MaterialFullSyncRun?> GetLatestU9MaterialFullSyncRunAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var row = await connection.QueryFirstOrDefaultAsync<FullSyncRunRow>(new CommandDefinition(
            "SELECT * FROM u9_material_full_sync_run ORDER BY started_at DESC LIMIT 1",
            cancellationToken: cancellationToken));
        return row is null ? null : MapFullSyncRun(row);
    }

    public async Task<U9MaterialFullSyncRun> SaveU9MaterialFullSyncRunAsync(U9MaterialFullSyncRun run, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO u9_material_full_sync_run(
                id,trigger_kind,status,category_codes_json,category_results_json,category_count,
                completed_category_count,discovered_count,created_count,refreshed_count,skipped_count,
                failed_category_count,last_error,started_at,completed_at)
            VALUES(
                @Id,@TriggerKind,@Status,@CategoryCodesJson,@CategoryResultsJson,@CategoryCount,
                @CompletedCategoryCount,@DiscoveredCount,@CreatedCount,@RefreshedCount,@SkippedCount,
                @FailedCategoryCount,@LastError,@StartedAt,@CompletedAt)
            ON DUPLICATE KEY UPDATE
                status=VALUES(status),category_codes_json=VALUES(category_codes_json),
                category_results_json=VALUES(category_results_json),category_count=VALUES(category_count),
                completed_category_count=VALUES(completed_category_count),discovered_count=VALUES(discovered_count),
                created_count=VALUES(created_count),refreshed_count=VALUES(refreshed_count),
                skipped_count=VALUES(skipped_count),failed_category_count=VALUES(failed_category_count),
                last_error=VALUES(last_error),completed_at=VALUES(completed_at)
            """, new
            {
                Id = run.Id.ToString("D"),
                run.TriggerKind,
                Status = run.Status.ToString(),
                CategoryCodesJson = JsonSerializer.Serialize(run.CategoryCodes, jsonOptions),
                CategoryResultsJson = JsonSerializer.Serialize(run.CategoryResults, jsonOptions),
                run.CategoryCount,
                run.CompletedCategoryCount,
                run.DiscoveredCount,
                run.CreatedCount,
                run.RefreshedCount,
                run.SkippedCount,
                run.FailedCategoryCount,
                run.LastError,
                StartedAt = run.StartedAt.UtcDateTime,
                CompletedAt = run.CompletedAt?.UtcDateTime
            }, cancellationToken: cancellationToken));
        return run;
    }

    public async Task<U9MaterialIntegrationConfiguration> GetIntegrationConfigurationAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var row = await connection.QuerySingleAsync<IntegrationRow>(new CommandDefinition(
            """
            SELECT base_url,enterprise_code,organization_code,user_code,client_id,client_secret_ciphertext,
                   item_create_path,item_query_path,item_modify_path,item_delete_path,unit_code_mapping_json,
                   customer_query_path,bom_create_path,bom_query_path,bom_modify_path,bom_delete_path,
                   bom_batch_unapprove_path,bom_bip_query_page_path,
                   write_enabled,updated_by,updated_at
            FROM u9_material_integration_setting WHERE id=1
            """, cancellationToken: cancellationToken));
        return MapConfiguration(row);
    }

    public async Task<U9MaterialIntegrationConfiguration> SaveIntegrationConfigurationAsync(U9MaterialIntegrationConfiguration configuration, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE u9_material_integration_setting
            SET base_url=@BaseUrl,enterprise_code=@EnterpriseCode,organization_code=@OrganizationCode,user_code=@UserCode,
                client_id=@ClientId,client_secret_ciphertext=@ClientSecretCiphertext,item_create_path=@ItemCreatePath,
                item_query_path=@ItemQueryPath,item_modify_path=@ItemModifyPath,item_delete_path=@ItemDeletePath,
                customer_query_path=@CustomerQueryPath,bom_create_path=@BomCreatePath,bom_query_path=@BomQueryPath,
                bom_modify_path=@BomModifyPath,bom_delete_path=@BomDeletePath,
                bom_batch_unapprove_path=@BomBatchUnapprovePath,bom_bip_query_page_path=@BomBipQueryPagePath,
                unit_code_mapping_json=@UnitCodeMappingJson,write_enabled=@WriteEnabled,updated_by=@UpdatedBy,updated_at=@UpdatedAt
            WHERE id=1
            """, new
            {
                configuration.BaseUrl,
                configuration.EnterpriseCode,
                configuration.OrganizationCode,
                configuration.UserCode,
                configuration.ClientId,
                configuration.ClientSecretCiphertext,
                configuration.ItemCreatePath,
                configuration.ItemQueryPath,
                configuration.ItemModifyPath,
                configuration.ItemDeletePath,
                configuration.CustomerQueryPath,
                configuration.BomCreatePath,
                configuration.BomQueryPath,
                configuration.BomModifyPath,
                configuration.BomDeletePath,
                configuration.BomBatchUnapprovePath,
                configuration.BomBipQueryPagePath,
                UnitCodeMappingJson = JsonSerializer.Serialize(configuration.UnitCodeMappings ?? new Dictionary<string, string>(), jsonOptions),
                configuration.WriteEnabled,
                configuration.UpdatedBy,
                UpdatedAt = configuration.UpdatedAt?.UtcDateTime
            }, cancellationToken: cancellationToken));
        return configuration;
    }

    private async Task<MySqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private async Task InsertAuditAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        AuditEntry audit,
        CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO audit_entry(id,occurred_at,actor,action_name,entity_type,entity_id,detail_json)
            VALUES(@Id,@OccurredAt,@Actor,@Action,@EntityType,@EntityId,@DetailJson)
            """, new
            {
                audit.Id,
                OccurredAt = audit.OccurredAt.UtcDateTime,
                audit.Actor,
                audit.Action,
                audit.EntityType,
                audit.EntityId,
                DetailJson = JsonSerializer.Serialize(new { detail = audit.Detail }, jsonOptions)
            }, transaction, cancellationToken: cancellationToken));
    }

    private static async Task<PdmMaterial?> FindMaterialAsync(MySqlConnection connection, MySqlTransaction? transaction, Guid materialId, CancellationToken cancellationToken)
    {
        var row = await connection.QuerySingleOrDefaultAsync<MaterialRow>(new CommandDefinition(
            MaterialSelect + " WHERE id=@MaterialId", new { MaterialId = materialId }, transaction, cancellationToken: cancellationToken));
        return row is null ? null : MapMaterial(row);
    }

    private static object MaterialParameters(PdmMaterial material) => new
    {
        material.Id,
        material.MaterialCode,
        material.Name,
        MaterialKind = material.Kind.ToString(),
        SupplyMode = material.SupplyMode.ToString(),
        material.UnitCode,
        material.Specification,
        material.Material,
        material.Remark,
        material.Brand,
        material.SurfaceTreatment,
        material.PurchaseLink,
        material.SelectionAdvice,
        material.ReferencePrice,
        material.Model3DLink,
        material.DocumentLink,
        material.IsRecommended,
        material.Weight,
        material.WeightUnit,
        material.SourceBomItemId,
        ApprovalStatus = material.ApprovalStatus.ToString(),
        material.ApprovedBy,
        ApprovedAt = material.ApprovedAt?.UtcDateTime,
        material.U9CategoryCode,
        material.U9ItemId,
        material.U9ItemCode,
        material.U9SyncConfirmed,
        SourceSystem = material.SourceSystem.ToString(),
        MasterOwner = material.MasterOwner.ToString(),
        LastU9SyncedAt = material.LastU9SyncedAt?.UtcDateTime,
        SyncStatus = material.SyncStatus.ToString(),
        material.CreatedBy,
        CreatedAt = material.CreatedAt.UtcDateTime,
        material.UpdatedBy,
        UpdatedAt = material.UpdatedAt.UtcDateTime,
        material.RowVersion,
        material.CategoryCode,
        material.IsArchived,
        material.ArchivedBy,
        ArchivedAt = material.ArchivedAt?.UtcDateTime
    };

    private static object CategoryParameters(MaterialCategory category) => new
    {
        category.Code,
        category.Name,
        category.ParentCode,
        category.U9CategoryId,
        PdmKind = category.PdmKind?.ToString(),
        DefaultSupplyMode = category.DefaultSupplyMode.ToString(),
        category.AllowCreate,
        category.IsVisible,
        category.IsActive,
        category.NumberPrefix,
        category.SequenceLength,
        category.CounterScope,
        category.SortOrder,
        category.UpdatedBy,
        UpdatedAt = category.UpdatedAt.UtcDateTime
    };

    private static object TaskParameters(MaterialSyncTask task) => new
    {
        task.Id,
        task.MaterialId,
        Operation = task.Operation.ToString(),
        Status = task.Status.ToString(),
        task.CorrelationId,
        task.PayloadJson,
        task.PayloadSha256,
        task.AttemptCount,
        NextAttemptAt = task.NextAttemptAt?.UtcDateTime,
        task.LastError,
        task.ResponsePreview,
        task.U9ItemId,
        task.U9ItemCode,
        CreatedAt = task.CreatedAt.UtcDateTime,
        UpdatedAt = task.UpdatedAt.UtcDateTime
    };

    private static object BatchParameters(MaterialSyncBatch batch) => new
    {
        batch.Id,
        Status = batch.Status.ToString(),
        batch.RequestedBy,
        RequestedRole = batch.RequestedRole.ToString(),
        batch.TotalCount,
        batch.CompletedCount,
        batch.SucceededCount,
        batch.WaitingCount,
        batch.FailedCount,
        batch.CurrentTaskId,
        batch.CurrentMaterialCode,
        batch.LastError,
        CreatedAt = batch.CreatedAt.UtcDateTime,
        StartedAt = batch.StartedAt?.UtcDateTime,
        CompletedAt = batch.CompletedAt?.UtcDateTime
    };

    private static object BatchItemParameters(MaterialSyncBatchItem item) => new
    {
        item.Id,
        item.BatchId,
        item.TaskId,
        item.Ordinal,
        Status = item.Status.ToString(),
        item.Message,
        StartedAt = item.StartedAt?.UtcDateTime,
        CompletedAt = item.CompletedAt?.UtcDateTime,
        LeaseExpiresAt = item.LeaseExpiresAt?.UtcDateTime
    };

    private static string? Truncate(string? value, int maximumLength) =>
        string.IsNullOrEmpty(value) || value.Length <= maximumLength ? value : value[..maximumLength];

    private static PdmMaterial MapMaterial(MaterialRow row) => new(
        row.Id, row.MaterialCode, row.Name, Enum.Parse<MaterialKind>(row.MaterialKind), Enum.Parse<MaterialSupplyMode>(row.SupplyMode),
        row.UnitCode, row.Specification, row.Material, row.Remark, row.Brand, row.SurfaceTreatment, row.Weight, row.WeightUnit,
        row.SourceBomItemId, Enum.Parse<MaterialApprovalStatus>(row.ApprovalStatus), row.ApprovedBy, Utc(row.ApprovedAt),
        row.U9CategoryCode, row.U9ItemId, row.U9ItemCode, Enum.Parse<MaterialSyncStatus>(row.SyncStatus), row.CreatedBy, Utc(row.CreatedAt)!.Value,
        row.UpdatedBy, Utc(row.UpdatedAt)!.Value, row.RowVersion, row.CategoryCode, row.IsArchived, row.ArchivedBy, Utc(row.ArchivedAt),
        row.U9SyncConfirmed, Enum.Parse<MaterialDataSource>(row.SourceSystem), Enum.Parse<MaterialMasterOwner>(row.MasterOwner), Utc(row.LastU9SyncedAt),
        row.PurchaseLink, row.ReferenceCount, row.SelectionAdvice, row.ReferencePrice, row.Model3DLink, row.DocumentLink, row.IsRecommended, row.CoverImageAttachmentId)
        {
            Model3DAttachmentCount = row.Model3DAttachmentCount,
            DocumentAttachmentCount = row.DocumentAttachmentCount
        };

    private static MaterialAttachment MapMaterialAttachment(MaterialAttachmentRow row) => new(
        row.Id, row.MaterialId, Enum.Parse<MaterialAttachmentKind>(row.AttachmentKind), row.OriginalFileName,
        row.StorageRoot, row.StorageRelativePath, row.FileLength, row.Sha256, row.UploadedBy, Utc(row.UploadedAt)!.Value);

    private static MaterialCategory MapCategory(CategoryRow row) => new(
        row.CategoryCode,
        row.CategoryName,
        row.ParentCode,
        row.U9CategoryId,
        string.IsNullOrWhiteSpace(row.PdmKind) ? null : Enum.Parse<MaterialKind>(row.PdmKind),
        Enum.Parse<MaterialSupplyMode>(row.DefaultSupplyMode),
        row.AllowCreate,
        row.IsVisible,
        row.IsActive,
        row.NumberPrefix,
        row.SequenceLength,
        row.CounterScope,
        row.SortOrder,
        row.UpdatedBy,
        Utc(row.UpdatedAt)!.Value,
        row.RowVersion,
        row.CurrentSequence);

    private static MaterialCategoryRule MapRule(CategoryRuleRow row) => new(
        Enum.Parse<MaterialKind>(row.PdmKind), row.U9CategoryCode, row.U9CategoryName, Enum.Parse<MaterialSupplyMode>(row.DefaultSupplyMode),
        row.IsEnabled, row.UpdatedBy, Utc(row.UpdatedAt)!.Value);

    private static MaterialSyncTask MapTask(SyncTaskRow row) => new(
        row.Id, row.MaterialId, Enum.Parse<MaterialSyncOperation>(row.Operation), Enum.Parse<MaterialSyncStatus>(row.Status), row.CorrelationId,
        row.PayloadJson, row.PayloadSha256, row.AttemptCount, Utc(row.NextAttemptAt), row.LastError, row.ResponsePreview, row.U9ItemId, row.U9ItemCode,
        Utc(row.CreatedAt)!.Value, Utc(row.UpdatedAt)!.Value)
        {
            MaterialCode = row.MaterialCode,
            MaterialName = row.MaterialName,
            CategoryCode = row.CategoryCode,
            ProjectId = row.ProjectId,
            ProjectCode = row.ProjectCode,
            ProjectName = row.ProjectName,
            BomHeaderKind = string.IsNullOrWhiteSpace(row.BomHeaderKind) ? null : Enum.Parse<ProjectBomHeaderKind>(row.BomHeaderKind),
            RequestedBy = row.RequestedBy,
            RequestedAt = Utc(row.RequestedAt)
        };

    private static MaterialSyncBatch MapBatch(SyncBatchRow row, IEnumerable<SyncBatchItemRow> items) => new(
        row.Id,
        Enum.Parse<MaterialSyncBatchStatus>(row.Status),
        row.RequestedBy,
        Enum.Parse<UserRole>(row.RequestedRole),
        row.TotalCount,
        row.CompletedCount,
        row.SucceededCount,
        row.WaitingCount,
        row.FailedCount,
        row.CurrentTaskId,
        row.CurrentMaterialCode,
        row.LastError,
        Utc(row.CreatedAt)!.Value,
        Utc(row.StartedAt),
        Utc(row.CompletedAt),
        items.Select(MapBatchItem).ToArray());

    private static MaterialSyncBatchItem MapBatchItem(SyncBatchItemRow row) => new(
        row.Id,
        row.BatchId,
        row.TaskId,
        row.OrdinalNo,
        Enum.Parse<MaterialSyncBatchItemStatus>(row.Status),
        row.Message,
        Utc(row.StartedAt),
        Utc(row.CompletedAt),
        Utc(row.LeaseExpiresAt));

    private static MaterialCodeApplication MapMaterialCodeApplication(
        MaterialCodeApplicationRow row,
        IReadOnlyDictionary<Guid, ApplicationWorkflowAudit> workflowStates,
        IReadOnlySet<string> completedBomCodes)
    {
        workflowStates.TryGetValue(row.Id, out var workflow);
        var workflowState = Enum.TryParse<MaterialCodeWorkflowState>(workflow?.State, true, out var recordedState)
            ? recordedState
            : MaterialCodeWorkflowState.PendingApproval;
        MaterialSyncStatus? taskStatus = string.IsNullOrWhiteSpace(row.SyncStatus) ? null : Enum.Parse<MaterialSyncStatus>(row.SyncStatus);
        var state = Enum.Parse<MaterialCodeApplicationStatus>(row.Status) switch
        {
            MaterialCodeApplicationStatus.Pending => MaterialCodeWorkflowState.PendingApproval,
            MaterialCodeApplicationStatus.Rejected => MaterialCodeWorkflowState.Rejected,
            _ when workflowState == MaterialCodeWorkflowState.Completed => MaterialCodeWorkflowState.Completed,
            _ when !row.U9SyncConfirmed => taskStatus is MaterialSyncStatus.Failed or MaterialSyncStatus.NeedsReview
                ? MaterialCodeWorkflowState.MaterialSyncFailed
                : MaterialCodeWorkflowState.PendingMaterialSync,
            _ when string.IsNullOrWhiteSpace(row.BomHeaderKind) => MaterialCodeWorkflowState.Completed,
            _ when workflowState is MaterialCodeWorkflowState.PendingBomSync or MaterialCodeWorkflowState.BomSyncFailed => workflowState,
            _ when completedBomCodes.Contains($"{row.MaterialCode ?? row.RequestedMaterialCode}/A1") => MaterialCodeWorkflowState.Completed,
            _ => MaterialCodeWorkflowState.PendingBomSync
        };
        return new(
        row.Id, row.ProjectId, row.BomItemId, Enum.Parse<MaterialCodeApplicationStatus>(row.Status), row.RequestedBy,
        Utc(row.RequestedAt)!.Value, row.DecidedBy, Utc(row.DecidedAt), row.DecisionComment, row.MaterialId, row.MaterialCode, row.RowVersion,
        string.IsNullOrWhiteSpace(row.BomHeaderKind) ? null : Enum.Parse<ProjectBomHeaderKind>(row.BomHeaderKind))
        {
            BomItemName = row.BomItemName,
            ApplicationName = row.ApplicationName,
            ProjectCode = row.ProjectCode,
            ProjectName = row.ProjectName,
            CategoryCode = row.CategoryCode,
            RequestedMaterialCode = row.RequestedMaterialCode,
            Specification = row.Specification,
            Brand = row.Brand,
            Remark = row.Remark,
            WorkflowState = state,
            SyncTaskId = row.SyncTaskId,
            SyncStatus = taskStatus,
            SyncError = row.SyncError,
            WorkflowMessage = workflow?.Detail ?? row.SyncError
        };
    }

    private static async Task<ApplicationWorkflowContext> LoadApplicationWorkflowAsync(
        MySqlConnection connection,
        CancellationToken cancellationToken)
    {
        var auditRows = await connection.QueryAsync<ApplicationWorkflowAuditRow>(new CommandDefinition(
            """
            SELECT entity_id,action_name,
                   JSON_UNQUOTE(JSON_EXTRACT(detail_json,'$.detail')) detail,
                   occurred_at
            FROM audit_entry
            WHERE action_name LIKE 'material-code.application.workflow.%'
            ORDER BY occurred_at DESC
            """, cancellationToken: cancellationToken));
        var states = new Dictionary<Guid, ApplicationWorkflowAudit>();
        foreach (var row in auditRows)
        {
            if (!Guid.TryParse(row.EntityId, out var applicationId) || states.ContainsKey(applicationId)) continue;
            states[applicationId] = new(row.ActionName[(row.ActionName.LastIndexOf('.') + 1)..], row.Detail);
        }
        var completedBomCodes = (await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT entity_id FROM audit_entry WHERE action_name IN ('u9.bom.create','u9.bom.modify')",
            cancellationToken: cancellationToken))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new(states, completedBomCodes);
    }

    private static U9MaterialIntegrationConfiguration MapConfiguration(IntegrationRow row) => new(
        row.BaseUrl, row.EnterpriseCode, row.OrganizationCode, row.UserCode, row.ClientId, row.ClientSecretCiphertext,
        row.ItemCreatePath, row.ItemQueryPath, row.WriteEnabled, row.UpdatedBy, Utc(row.UpdatedAt), row.ItemModifyPath, row.ItemDeletePath,
        DeserializeUnitCodeMappings(row.UnitCodeMappingJson), row.CustomerQueryPath, row.BomCreatePath, row.BomQueryPath,
        row.BomModifyPath, row.BomDeletePath, row.BomBatchUnapprovePath, row.BomBipQueryPagePath);

    private U9MaterialFullSyncRun MapFullSyncRun(FullSyncRunRow row) => new(
        Guid.Parse(row.Id),
        row.TriggerKind,
        Enum.Parse<U9MaterialFullSyncStatus>(row.Status),
        JsonSerializer.Deserialize<string[]>(row.CategoryCodesJson, jsonOptions) ?? [],
        JsonSerializer.Deserialize<U9MaterialFullSyncCategoryResult[]>(row.CategoryResultsJson, jsonOptions) ?? [],
        row.CategoryCount,
        row.CompletedCategoryCount,
        row.DiscoveredCount,
        row.CreatedCount,
        row.RefreshedCount,
        row.SkippedCount,
        row.FailedCategoryCount,
        row.LastError,
        Utc(row.StartedAt)!.Value,
        Utc(row.CompletedAt));

    private static IReadOnlyDictionary<string, string> DeserializeUnitCodeMappings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, string>();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                ?? new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }

    private static DateTimeOffset? Utc(DateTime? value) => value is null ? null : new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc));

    private const string MaterialSelect = """
        SELECT id,material_code,name,material_kind,supply_mode,unit_code,specification,material,remark,brand,surface_treatment,purchase_link,selection_advice,reference_price,model_3d_link,document_link,is_recommended,cover_image_attachment_id,
               weight,weight_unit,source_bom_item_id,approval_status,approved_by,approved_at,u9_category_code,u9_item_id,u9_item_code,u9_sync_confirmed,
               source_system,master_owner,last_u9_synced_at,sync_status,created_by,created_at,updated_by,updated_at,row_version,category_code,is_archived,archived_by,archived_at,
                (SELECT COUNT(*) FROM bom_material_link AS material_link WHERE material_link.material_id=material_master.id)
                    + CASE WHEN source_bom_item_id IS NULL THEN 0 ELSE 1 END AS reference_count,
                (SELECT COUNT(*) FROM material_attachment AS attachment WHERE attachment.material_id=material_master.id AND attachment.attachment_kind='Model3D') AS model3d_attachment_count,
                (SELECT COUNT(*) FROM material_attachment AS attachment WHERE attachment.material_id=material_master.id AND attachment.attachment_kind='Document') AS document_attachment_count
        FROM material_master
        """;

    private const string MaterialAttachmentSelect = """
        SELECT id,material_id,attachment_kind,original_file_name,storage_root,storage_relative_path,file_length,sha256,uploaded_by,uploaded_at
        FROM material_attachment
        """;

    private const string CategorySelect = """
        SELECT mc.category_code,mc.category_name,mc.parent_code,mc.u9_category_id,mc.pdm_kind,mc.default_supply_mode,
               mc.allow_create,mc.is_visible,mc.is_active,mc.number_prefix,mc.sequence_length,mc.counter_scope,mc.sort_order,
               mc.updated_by,mc.updated_at,mc.row_version,COALESCE(counter.current_value,0) AS current_sequence
        FROM material_category AS mc
        LEFT JOIN material_code_counter AS counter ON counter.u9_category_code=mc.counter_scope
        """;

    private const string SyncTaskSelect = """
        SELECT id,material_id,operation,status,correlation_id,payload_json,payload_sha256,attempt_count,next_attempt_at,last_error,
               response_preview,u9_item_id,u9_item_code,created_at,updated_at,
               (SELECT material_code FROM material_master WHERE material_master.id=u9_material_sync_task.material_id) material_code,
               (SELECT name FROM material_master WHERE material_master.id=u9_material_sync_task.material_id) material_name,
               (SELECT category_code FROM material_master WHERE material_master.id=u9_material_sync_task.material_id) category_code,
               (SELECT project_id FROM material_code_application WHERE material_code_application.material_id=u9_material_sync_task.material_id ORDER BY requested_at DESC LIMIT 1) project_id,
               (SELECT project.code FROM project WHERE project.id=(SELECT project_id FROM material_code_application WHERE material_code_application.material_id=u9_material_sync_task.material_id ORDER BY requested_at DESC LIMIT 1)) project_code,
               (SELECT project.name FROM project WHERE project.id=(SELECT project_id FROM material_code_application WHERE material_code_application.material_id=u9_material_sync_task.material_id ORDER BY requested_at DESC LIMIT 1)) project_name,
               (SELECT bom_header_kind FROM material_code_application WHERE material_code_application.material_id=u9_material_sync_task.material_id ORDER BY requested_at DESC LIMIT 1) bom_header_kind,
               COALESCE(
                   (SELECT requested_by FROM material_code_application WHERE material_code_application.material_id=u9_material_sync_task.material_id ORDER BY requested_at DESC LIMIT 1),
                   (SELECT created_by FROM material_master WHERE material_master.id=u9_material_sync_task.material_id)) requested_by,
               COALESCE(
                   (SELECT requested_at FROM material_code_application WHERE material_code_application.material_id=u9_material_sync_task.material_id ORDER BY requested_at DESC LIMIT 1),
                   (SELECT created_at FROM material_master WHERE material_master.id=u9_material_sync_task.material_id)) requested_at
        FROM u9_material_sync_task
        """;

    private const string MaterialCodeApplicationSelect = """
        SELECT id,project_id,bom_item_id,bom_header_kind,status,requested_by,requested_at,decided_by,decided_at,decision_comment,material_id,material_code,row_version,
               (SELECT code FROM project WHERE project.id=material_code_application.project_id) project_code,
               (SELECT name FROM project WHERE project.id=material_code_application.project_id) project_name,
               (SELECT name FROM bom_item WHERE bom_item.id=material_code_application.bom_item_id) bom_item_name,
               COALESCE((SELECT name FROM bom_item WHERE bom_item.id=material_code_application.bom_item_id),
                        (SELECT name FROM material_master WHERE material_master.id=material_code_application.material_id)) application_name,
               (SELECT category_code FROM material_master WHERE material_master.id=material_code_application.material_id) category_code,
                (SELECT material_code FROM material_master WHERE material_master.id=material_code_application.material_id) requested_material_code,
               COALESCE((SELECT u9_sync_confirmed FROM material_master WHERE material_master.id=material_code_application.material_id),0) u9_sync_confirmed,
               (SELECT id FROM u9_material_sync_task WHERE u9_material_sync_task.material_id=material_code_application.material_id AND status<>'Superseded' ORDER BY created_at DESC LIMIT 1) sync_task_id,
               (SELECT status FROM u9_material_sync_task WHERE u9_material_sync_task.material_id=material_code_application.material_id AND status<>'Superseded' ORDER BY created_at DESC LIMIT 1) sync_status,
               (SELECT last_error FROM u9_material_sync_task WHERE u9_material_sync_task.material_id=material_code_application.material_id AND status<>'Superseded' ORDER BY created_at DESC LIMIT 1) sync_error,
               (SELECT specification FROM bom_item WHERE bom_item.id=material_code_application.bom_item_id) specification,
               (SELECT brand FROM bom_item WHERE bom_item.id=material_code_application.bom_item_id) brand,
               (SELECT remark FROM bom_item WHERE bom_item.id=material_code_application.bom_item_id) remark
        FROM material_code_application
        """;

    private sealed class MaterialRow
    {
        public Guid Id { get; init; }
        public string MaterialCode { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string MaterialKind { get; init; } = string.Empty;
        public string SupplyMode { get; init; } = string.Empty;
        public string UnitCode { get; init; } = string.Empty;
        public string? Specification { get; init; }
        public string? Material { get; init; }
        public string? Remark { get; init; }
        public string? Brand { get; init; }
        public string? SurfaceTreatment { get; init; }
        public string? PurchaseLink { get; init; }
        public string? SelectionAdvice { get; init; }
        public decimal? ReferencePrice { get; init; }
        public string? Model3DLink { get; init; }
        public string? DocumentLink { get; init; }
        public bool IsRecommended { get; init; }
        public Guid? CoverImageAttachmentId { get; init; }
        public int ReferenceCount { get; init; }
        public int Model3DAttachmentCount { get; init; }
        public int DocumentAttachmentCount { get; init; }
        public decimal? Weight { get; init; }
        public string? WeightUnit { get; init; }
        public Guid? SourceBomItemId { get; init; }
        public string ApprovalStatus { get; init; } = string.Empty;
        public string? ApprovedBy { get; init; }
        public DateTime? ApprovedAt { get; init; }
        public string? U9CategoryCode { get; init; }
        public string? U9ItemId { get; init; }
        public string? U9ItemCode { get; init; }
        public bool U9SyncConfirmed { get; init; }
        public string SourceSystem { get; init; } = nameof(MaterialDataSource.Pdm);
        public string MasterOwner { get; init; } = nameof(MaterialMasterOwner.Pdm);
        public DateTime? LastU9SyncedAt { get; init; }
        public string SyncStatus { get; init; } = string.Empty;
        public string CreatedBy { get; init; } = string.Empty;
        public DateTime CreatedAt { get; init; }
        public string UpdatedBy { get; init; } = string.Empty;
        public DateTime UpdatedAt { get; init; }
        public long RowVersion { get; init; }
        public string? CategoryCode { get; init; }
        public bool IsArchived { get; init; }
        public string? ArchivedBy { get; init; }
        public DateTime? ArchivedAt { get; init; }
    }

    private sealed class MaterialAttachmentRow
    {
        public Guid Id { get; init; }
        public Guid MaterialId { get; init; }
        public string AttachmentKind { get; init; } = string.Empty;
        public string OriginalFileName { get; init; } = string.Empty;
        public string StorageRoot { get; init; } = string.Empty;
        public string StorageRelativePath { get; init; } = string.Empty;
        public long FileLength { get; init; }
        public string Sha256 { get; init; } = string.Empty;
        public string UploadedBy { get; init; } = string.Empty;
        public DateTime UploadedAt { get; init; }
    }

    private sealed class CategoryRow
    {
        public string CategoryCode { get; init; } = string.Empty;
        public string CategoryName { get; init; } = string.Empty;
        public string? ParentCode { get; init; }
        public string? U9CategoryId { get; init; }
        public string? PdmKind { get; init; }
        public string DefaultSupplyMode { get; init; } = string.Empty;
        public bool AllowCreate { get; init; }
        public bool IsVisible { get; init; }
        public bool IsActive { get; init; }
        public string NumberPrefix { get; init; } = string.Empty;
        public int SequenceLength { get; init; }
        public string CounterScope { get; init; } = string.Empty;
        public int SortOrder { get; init; }
        public string UpdatedBy { get; init; } = string.Empty;
        public DateTime UpdatedAt { get; init; }
        public long RowVersion { get; init; }
        public long CurrentSequence { get; init; }
    }

    private sealed class CategoryRuleRow
    {
        public string PdmKind { get; init; } = string.Empty;
        public string U9CategoryCode { get; init; } = string.Empty;
        public string U9CategoryName { get; init; } = string.Empty;
        public string DefaultSupplyMode { get; init; } = string.Empty;
        public bool IsEnabled { get; init; }
        public string UpdatedBy { get; init; } = string.Empty;
        public DateTime UpdatedAt { get; init; }
    }

    private sealed class SyncTaskRow
    {
        public Guid Id { get; init; }
        public Guid MaterialId { get; init; }
        public string Operation { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public string CorrelationId { get; init; } = string.Empty;
        public string PayloadJson { get; init; } = string.Empty;
        public string PayloadSha256 { get; init; } = string.Empty;
        public int AttemptCount { get; init; }
        public DateTime? NextAttemptAt { get; init; }
        public string? LastError { get; init; }
        public string? ResponsePreview { get; init; }
        public string? U9ItemId { get; init; }
        public string? U9ItemCode { get; init; }
        public DateTime CreatedAt { get; init; }
        public DateTime UpdatedAt { get; init; }
        public string? MaterialCode { get; init; }
        public string? MaterialName { get; init; }
        public string? CategoryCode { get; init; }
        public Guid? ProjectId { get; init; }
        public string? ProjectCode { get; init; }
        public string? ProjectName { get; init; }
        public string? BomHeaderKind { get; init; }
        public string? RequestedBy { get; init; }
        public DateTime? RequestedAt { get; init; }
    }

    private sealed class SyncBatchRow
    {
        public Guid Id { get; init; }
        public string Status { get; init; } = string.Empty;
        public string RequestedBy { get; init; } = string.Empty;
        public string RequestedRole { get; init; } = string.Empty;
        public int TotalCount { get; init; }
        public int CompletedCount { get; init; }
        public int SucceededCount { get; init; }
        public int WaitingCount { get; init; }
        public int FailedCount { get; init; }
        public Guid? CurrentTaskId { get; init; }
        public string? CurrentMaterialCode { get; init; }
        public string? LastError { get; init; }
        public DateTime CreatedAt { get; init; }
        public DateTime? StartedAt { get; init; }
        public DateTime? CompletedAt { get; init; }
    }

    private sealed class SyncBatchItemRow
    {
        public Guid Id { get; init; }
        public Guid BatchId { get; init; }
        public Guid TaskId { get; init; }
        public int OrdinalNo { get; init; }
        public string Status { get; init; } = string.Empty;
        public string? Message { get; init; }
        public DateTime? StartedAt { get; init; }
        public DateTime? CompletedAt { get; init; }
        public DateTime? LeaseExpiresAt { get; init; }
    }

    private sealed class SyncBatchCountsRow
    {
        public int TotalCount { get; init; }
        public int CompletedCount { get; init; }
        public int SucceededCount { get; init; }
        public int WaitingCount { get; init; }
        public int FailedCount { get; init; }
    }

    private sealed class IntegrationRow
    {
        public string BaseUrl { get; init; } = string.Empty;
        public string EnterpriseCode { get; init; } = string.Empty;
        public string OrganizationCode { get; init; } = string.Empty;
        public string UserCode { get; init; } = string.Empty;
        public string ClientId { get; init; } = string.Empty;
        public string ClientSecretCiphertext { get; init; } = string.Empty;
        public string ItemCreatePath { get; init; } = string.Empty;
        public string ItemQueryPath { get; init; } = string.Empty;
        public string ItemModifyPath { get; init; } = string.Empty;
        public string ItemDeletePath { get; init; } = string.Empty;
        public string CustomerQueryPath { get; init; } = U9MaterialContract.CustomerReferencePath;
        public string BomCreatePath { get; init; } = U9BomContract.CreatePath;
        public string BomQueryPath { get; init; } = U9BomContract.QueryPath;
        public string BomModifyPath { get; init; } = U9BomContract.ModifyPath;
        public string BomDeletePath { get; init; } = U9BomContract.DeletePath;
        public string BomBatchUnapprovePath { get; init; } = U9BomContract.BatchUnApprovePath;
        public string BomBipQueryPagePath { get; init; } = U9BomContract.BipQueryPagePath;
        public string UnitCodeMappingJson { get; init; } = "{}";
        public bool WriteEnabled { get; init; }
        public string? UpdatedBy { get; init; }
        public DateTime? UpdatedAt { get; init; }
    }

    private sealed class FullSyncRunRow
    {
        public string Id { get; init; } = string.Empty;
        public string TriggerKind { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public string CategoryCodesJson { get; init; } = "[]";
        public string CategoryResultsJson { get; init; } = "[]";
        public int CategoryCount { get; init; }
        public int CompletedCategoryCount { get; init; }
        public int DiscoveredCount { get; init; }
        public int CreatedCount { get; init; }
        public int RefreshedCount { get; init; }
        public int SkippedCount { get; init; }
        public int FailedCategoryCount { get; init; }
        public string? LastError { get; init; }
        public DateTime StartedAt { get; init; }
        public DateTime? CompletedAt { get; init; }
    }

    private sealed class MaterialCodeApplicationRow
    {
        public Guid Id { get; init; }
        public Guid ProjectId { get; init; }
        public Guid? BomItemId { get; init; }
        public string? BomHeaderKind { get; init; }
        public string Status { get; init; } = string.Empty;
        public string RequestedBy { get; init; } = string.Empty;
        public DateTime RequestedAt { get; init; }
        public string? DecidedBy { get; init; }
        public DateTime? DecidedAt { get; init; }
        public string? DecisionComment { get; init; }
        public Guid? MaterialId { get; init; }
        public string? MaterialCode { get; init; }
        public long RowVersion { get; init; }
        public string? BomItemName { get; init; }
        public string? ApplicationName { get; init; }
        public string? ProjectCode { get; init; }
        public string? ProjectName { get; init; }
        public string? CategoryCode { get; init; }
        public string? RequestedMaterialCode { get; init; }
        public bool U9SyncConfirmed { get; init; }
        public Guid? SyncTaskId { get; init; }
        public string? SyncStatus { get; init; }
        public string? SyncError { get; init; }
        public string? Specification { get; init; }
        public string? Brand { get; init; }
        public string? Remark { get; init; }
    }

    private sealed class ApplicationWorkflowAuditRow
    {
        public string EntityId { get; init; } = string.Empty;
        public string ActionName { get; init; } = string.Empty;
        public string? Detail { get; init; }
        public DateTime OccurredAt { get; init; }
    }

    private sealed record ApplicationWorkflowAudit(string State, string? Detail);

    private sealed record ApplicationWorkflowContext(
        IReadOnlyDictionary<Guid, ApplicationWorkflowAudit> States,
        IReadOnlySet<string> CompletedBomCodes);

    private static long MaximumSequence(int sequenceLength) =>
        checked((long)Math.Pow(10, sequenceLength) - 1);
}
