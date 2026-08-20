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
        if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("PDM MySQL连接字符串未配置。 ");
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
            " ORDER BY material_code LIMIT @Limit",
            new { Query = normalizedQuery, CategoryCode = normalizedCategory, IncludeArchived = includeArchived, Limit = Math.Clamp(limit, 1, 500) },
            cancellationToken: cancellationToken));
        return rows.Select(MapMaterial).ToArray();
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

    public async Task<PdmMaterial?> FindMaterialBySourceBomItemAsync(Guid bomItemId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var row = await connection.QueryFirstOrDefaultAsync<MaterialRow>(new CommandDefinition(
            MaterialSelect + " WHERE source_bom_item_id=@BomItemId ORDER BY created_at LIMIT 1",
            new { BomItemId = bomItemId }, cancellationToken: cancellationToken));
        return row is null ? null : MapMaterial(row);
    }

    public async Task<bool> HasMaterialReferencesAsync(Guid materialId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var referenced = await connection.QuerySingleAsync<int>(new CommandDefinition(
            """
            SELECT CASE WHEN EXISTS(SELECT 1 FROM bom_material_link WHERE material_id=@MaterialId)
                          OR EXISTS(SELECT 1 FROM material_master WHERE id=@MaterialId AND source_bom_item_id IS NOT NULL)
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
            """,
            new { MaterialId = materialId }, cancellationToken: cancellationToken));
    }

    public async Task<string> ReserveNextMaterialCodeAsync(MaterialCategory category, CancellationToken cancellationToken)
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
        var currentValue = await connection.QuerySingleAsync<long>(new CommandDefinition(
            "SELECT current_value FROM material_code_counter WHERE u9_category_code=@CounterScope FOR UPDATE",
            new { category.CounterScope }, transaction, cancellationToken: cancellationToken));
        var maximum = MaximumSequence(category.SequenceLength);
        if (currentValue >= maximum) throw new PdmRuleException($"分类 {category.Code} 的物料编码流水已用尽。");

        var nextValue = currentValue + 1;
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE material_code_counter SET current_value=@NextValue,updated_at=UTC_TIMESTAMP(6) WHERE u9_category_code=@CounterScope",
            new { category.CounterScope, NextValue = nextValue }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return $"{category.NumberPrefix}{nextValue.ToString($"D{category.SequenceLength}")}";
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
                    id,material_code,name,material_kind,supply_mode,unit_code,specification,material,remark,brand,surface_treatment,purchase_link,
                    weight,weight_unit,source_bom_item_id,approval_status,approved_by,approved_at,u9_category_code,u9_item_id,u9_item_code,u9_sync_confirmed,
                    source_system,master_owner,last_u9_synced_at,sync_status,created_by,created_at,updated_by,updated_at,row_version,category_code,is_archived,archived_by,archived_at)
                VALUES(
                    @Id,@MaterialCode,@Name,@MaterialKind,@SupplyMode,@UnitCode,@Specification,@Material,@Remark,@Brand,@SurfaceTreatment,@PurchaseLink,
                    @Weight,@WeightUnit,@SourceBomItemId,@ApprovalStatus,@ApprovedBy,@ApprovedAt,@U9CategoryCode,@U9ItemId,@U9ItemCode,@U9SyncConfirmed,
                    @SourceSystem,@MasterOwner,@LastU9SyncedAt,@SyncStatus,@CreatedBy,@CreatedAt,@UpdatedBy,@UpdatedAt,@RowVersion,@CategoryCode,@IsArchived,@ArchivedBy,@ArchivedAt)
                """, MaterialParameters(saved), cancellationToken: cancellationToken));
            return saved;
        }
        catch (MySqlException exception) when (exception.Number == 1062)
        {
            throw new PdmConflictException("预留的PDM物料编码已被占用，请重试。");
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
                id,material_code,name,material_kind,supply_mode,unit_code,specification,material,remark,brand,surface_treatment,purchase_link,
                weight,weight_unit,source_bom_item_id,approval_status,approved_by,approved_at,u9_category_code,u9_item_id,u9_item_code,u9_sync_confirmed,
                source_system,master_owner,last_u9_synced_at,sync_status,created_by,created_at,updated_by,updated_at,row_version,category_code,is_archived,archived_by,archived_at)
            VALUES(
                @Id,@MaterialCode,@Name,@MaterialKind,@SupplyMode,@UnitCode,@Specification,@Material,@Remark,@Brand,@SurfaceTreatment,@PurchaseLink,
                @Weight,@WeightUnit,@SourceBomItemId,@ApprovalStatus,@ApprovedBy,@ApprovedAt,@U9CategoryCode,@U9ItemId,@U9ItemCode,@U9SyncConfirmed,
                @SourceSystem,@MasterOwner,@LastU9SyncedAt,@SyncStatus,@CreatedBy,@CreatedAt,@UpdatedBy,@UpdatedAt,@RowVersion,@CategoryCode,@IsArchived,@ArchivedBy,@ArchivedAt)
            ON DUPLICATE KEY UPDATE
                name=IF(master_owner='U9C',VALUES(name),name),
                material_kind=IF(master_owner='U9C',VALUES(material_kind),material_kind),
                supply_mode=IF(master_owner='U9C',VALUES(supply_mode),supply_mode),
                unit_code=IF(master_owner='U9C',VALUES(unit_code),unit_code),
                specification=IF(master_owner='U9C',VALUES(specification),specification),
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
                category_code=IF(master_owner='U9C',VALUES(category_code),category_code)
            """, MaterialParameters(material), cancellationToken: cancellationToken));
        return await FindMaterialByCodeAsync(material.MaterialCode, cancellationToken)
            ?? throw new PdmRuleException("U9C料品导入后未能回读PDM主档。");
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
            throw new PdmConflictException("PDM物料编码已存在。");
        }
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
                throw new PdmRuleException("只有PDM来源且PDM主控的料品可以删除。");
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
            throw new PdmConflictException("U9C料品分类编码已映射到其他PDM分类。");
        }
    }

    public async Task<(PdmMaterial Material, MaterialSyncTask Task)> ApproveAndEnqueueAsync(
        Guid materialId, long expectedRowVersion, string u9CategoryCode, MaterialSyncTask task, AuditEntry audit, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var affected = await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE material_master
                SET approval_status='Approved',approved_by=@Actor,approved_at=@OccurredAt,u9_category_code=@U9CategoryCode,
                    sync_status='PreviewReady',updated_by=@Actor,updated_at=@OccurredAt,row_version=row_version+1
                WHERE id=@MaterialId AND row_version=@ExpectedRowVersion AND approval_status='Draft'
                """, new
                {
                    MaterialId = materialId,
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
            var material = (await FindMaterialAsync(connection, transaction, materialId, cancellationToken))!;
            await transaction.CommitAsync(cancellationToken);
            return (material, task);
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

    public async Task<U9MaterialIntegrationConfiguration> GetIntegrationConfigurationAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var row = await connection.QuerySingleAsync<IntegrationRow>(new CommandDefinition(
            """
            SELECT base_url,enterprise_code,organization_code,user_code,client_id,client_secret_ciphertext,
                   item_create_path,item_query_path,item_modify_path,item_delete_path,unit_code_mapping_json,
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

    private static PdmMaterial MapMaterial(MaterialRow row) => new(
        row.Id, row.MaterialCode, row.Name, Enum.Parse<MaterialKind>(row.MaterialKind), Enum.Parse<MaterialSupplyMode>(row.SupplyMode),
        row.UnitCode, row.Specification, row.Material, row.Remark, row.Brand, row.SurfaceTreatment, row.Weight, row.WeightUnit,
        row.SourceBomItemId, Enum.Parse<MaterialApprovalStatus>(row.ApprovalStatus), row.ApprovedBy, Utc(row.ApprovedAt),
        row.U9CategoryCode, row.U9ItemId, row.U9ItemCode, Enum.Parse<MaterialSyncStatus>(row.SyncStatus), row.CreatedBy, Utc(row.CreatedAt)!.Value,
        row.UpdatedBy, Utc(row.UpdatedAt)!.Value, row.RowVersion, row.CategoryCode, row.IsArchived, row.ArchivedBy, Utc(row.ArchivedAt),
        row.U9SyncConfirmed, Enum.Parse<MaterialDataSource>(row.SourceSystem), Enum.Parse<MaterialMasterOwner>(row.MasterOwner), Utc(row.LastU9SyncedAt),
        row.PurchaseLink);

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
        Utc(row.CreatedAt)!.Value, Utc(row.UpdatedAt)!.Value);

    private static U9MaterialIntegrationConfiguration MapConfiguration(IntegrationRow row) => new(
        row.BaseUrl, row.EnterpriseCode, row.OrganizationCode, row.UserCode, row.ClientId, row.ClientSecretCiphertext,
        row.ItemCreatePath, row.ItemQueryPath, row.WriteEnabled, row.UpdatedBy, Utc(row.UpdatedAt), row.ItemModifyPath, row.ItemDeletePath,
        DeserializeUnitCodeMappings(row.UnitCodeMappingJson));

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
        SELECT id,material_code,name,material_kind,supply_mode,unit_code,specification,material,remark,brand,surface_treatment,purchase_link,
               weight,weight_unit,source_bom_item_id,approval_status,approved_by,approved_at,u9_category_code,u9_item_id,u9_item_code,u9_sync_confirmed,
               source_system,master_owner,last_u9_synced_at,sync_status,created_by,created_at,updated_by,updated_at,row_version,category_code,is_archived,archived_by,archived_at
        FROM material_master
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
               response_preview,u9_item_id,u9_item_code,created_at,updated_at
        FROM u9_material_sync_task
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
        public string UnitCodeMappingJson { get; init; } = "{}";
        public bool WriteEnabled { get; init; }
        public string? UpdatedBy { get; init; }
        public DateTime? UpdatedAt { get; init; }
    }

    private static long MaximumSequence(int sequenceLength) =>
        checked((long)Math.Pow(10, sequenceLength) - 1);
}
