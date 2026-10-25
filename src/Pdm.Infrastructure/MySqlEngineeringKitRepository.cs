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

    public async Task<EngineeringKit> SetActiveAsync(Guid kitId, bool isActive, long expectedRowVersion, string actor, DateTimeOffset updatedAt, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var affected = await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE engineering_kit SET is_active=@IsActive,updated_by=@Actor,updated_at=@UpdatedAt,row_version=row_version+1
            WHERE id=@KitId AND row_version=@ExpectedRowVersion
            """, new { KitId = kitId, IsActive = isActive, ExpectedRowVersion = expectedRowVersion, Actor = actor, UpdatedAt = updatedAt.UtcDateTime }, cancellationToken: cancellationToken));
        if (affected == 0) throw new PdmConflictException("套件不存在或已被其他用户修改，请刷新后重试。");
        return (await FindAsync(kitId, cancellationToken))!;
    }

    public async Task<EngineeringKit> UpdateLibraryMetadataAsync(EngineeringKit kit, long expectedRowVersion, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var affected = await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE engineering_kit SET description=@Description,selection_advice=@SelectionAdvice,reference_price=@ReferencePrice,
                is_recommended=@IsRecommended,cover_image_attachment_id=@CoverImageAttachmentId,updated_by=@UpdatedBy,updated_at=@UpdatedAt,row_version=row_version+1
            WHERE id=@Id AND row_version=@ExpectedRowVersion AND is_active=1 AND current_released_revision_id IS NOT NULL
            """, new { kit.Id, kit.Description, kit.SelectionAdvice, kit.ReferencePrice, kit.IsRecommended, kit.CoverImageAttachmentId,
                kit.UpdatedBy, UpdatedAt = kit.UpdatedAt.UtcDateTime, ExpectedRowVersion = expectedRowVersion }, cancellationToken: cancellationToken));
        if (affected != 1) throw new PdmConflictException("套件已被其他用户修改或停用，请刷新后重试。");
        return (await FindAsync(kit.Id, cancellationToken))!;
    }

    public async Task<EngineeringKit> SaveDraftAsync(EngineeringKit kit, EngineeringKitRevision revision, long? expectedRowVersion, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (expectedRowVersion is null)
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO engineering_kit(id,kit_code,kit_model,model_mode,standard_name,standard_code,category_name,category_code,name,brand,description,current_released_revision_id,created_by,created_at,updated_by,updated_at,row_version)
                VALUES(@Id,NULL,@KitModel,@ModelMode,@StandardName,@StandardCode,@CategoryName,@CategoryCode,@Name,@Brand,@Description,NULL,@CreatedBy,@CreatedAt,@UpdatedBy,@UpdatedAt,1)
                """, new { kit.Id, KitModel = kit.Model, ModelMode = kit.ModelMode.ToString(), kit.StandardName, kit.StandardCode, kit.CategoryName, kit.CategoryCode, kit.Name, kit.Brand, kit.Description, kit.CreatedBy, CreatedAt = kit.CreatedAt.UtcDateTime, kit.UpdatedBy, UpdatedAt = kit.UpdatedAt.UtcDateTime }, transaction, cancellationToken: cancellationToken));
        }
        else
        {
            var affected = await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE engineering_kit
                SET kit_model=@KitModel,model_mode=@ModelMode,standard_name=@StandardName,standard_code=@StandardCode,category_name=@CategoryName,category_code=@CategoryCode,
                    name=@Name,brand=@Brand,description=@Description,updated_by=@UpdatedBy,updated_at=@UpdatedAt,row_version=row_version+1
                WHERE id=@Id AND row_version=@ExpectedRowVersion
                """, new { kit.Id, KitModel = kit.Model, ModelMode = kit.ModelMode.ToString(), kit.StandardName, kit.StandardCode, kit.CategoryName, kit.CategoryCode, kit.Name, kit.Brand, kit.Description, kit.UpdatedBy, UpdatedAt = kit.UpdatedAt.UtcDateTime, ExpectedRowVersion = expectedRowVersion.Value }, transaction, cancellationToken: cancellationToken));
            if (affected == 0) throw new PdmConflictException("套件已被其他用户修改，请刷新后重试。");
        }

        var existingDraft = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM engineering_kit_revision WHERE id=@RevisionId AND revision_state='Draft'",
            new { RevisionId = revision.Id }, transaction, cancellationToken: cancellationToken));
        if (existingDraft == 0)
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO engineering_kit_revision(id,kit_id,version_no,revision_state,change_note,created_by,created_at,published_by,published_at,standard_library_category_id)
                VALUES(@Id,@KitId,@VersionNumber,'Draft',@ChangeNote,@CreatedBy,@CreatedAt,NULL,NULL,@StandardLibraryCategoryId)
                """, new { revision.Id, revision.KitId, revision.VersionNumber, revision.ChangeNote, revision.StandardLibraryCategoryId, revision.CreatedBy, CreatedAt = revision.CreatedAt.UtcDateTime }, transaction, cancellationToken: cancellationToken));
        }
        else
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE engineering_kit_revision SET change_note=@ChangeNote,standard_library_category_id=@StandardLibraryCategoryId WHERE id=@Id AND revision_state='Draft'",
                new { revision.Id, revision.ChangeNote, revision.StandardLibraryCategoryId }, transaction, cancellationToken: cancellationToken));
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

    public async Task<EngineeringKit> PublishAsync(Guid kitId, long expectedRowVersion, string actor, DateTimeOffset publishedAt, string? modelCategoryCode, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var kit = await connection.QuerySingleOrDefaultAsync<KitRow>(new CommandDefinition(
            KitSelect + " WHERE k.id=@KitId FOR UPDATE", new { KitId = kitId }, transaction, cancellationToken: cancellationToken))
            ?? throw new PdmNotFoundException("套件不存在。");
        if (kit.RowVersion != expectedRowVersion) throw new PdmConflictException("套件已被其他用户修改，请刷新后重试。");
        if (!kit.IsActive) throw new PdmRuleException("套件已停用，请恢复启用后再发布。");
        var draft = await connection.QuerySingleOrDefaultAsync<RevisionRow>(new CommandDefinition(
            RevisionSelect + " WHERE r.kit_id=@KitId AND r.revision_state='Draft' ORDER BY r.version_no DESC LIMIT 1 FOR UPDATE",
            new { KitId = kitId }, transaction, cancellationToken: cancellationToken)) ?? throw new PdmRuleException("套件没有待发布草稿。");

        var code = kit.KitCode;
        if (string.IsNullOrWhiteSpace(code))
        {
            var current = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT current_sequence FROM engineering_kit_counter WHERE counter_id=1 FOR UPDATE", transaction: transaction, cancellationToken: cancellationToken));
            var next = checked(current + 1);
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE engineering_kit_counter SET current_sequence=@Next WHERE counter_id=1", new { Next = next }, transaction, cancellationToken: cancellationToken));
            code = $"UKIT-{next:D6}";
        }
        // 首次发布生成型号，后续版本和分类移动均保留原型号。
        var model = kit.KitModel?.Trim();
        if (string.IsNullOrWhiteSpace(model))
        {
            EngineeringKitNumbering.Model(modelCategoryCode, 1);
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO engineering_kit_model_counter(category_code,current_sequence) VALUES(@CategoryCode,0) ON DUPLICATE KEY UPDATE current_sequence=current_sequence",
                new { CategoryCode = modelCategoryCode }, transaction, cancellationToken: cancellationToken));
            var sequence = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT current_sequence FROM engineering_kit_model_counter WHERE category_code=@CategoryCode FOR UPDATE",
                new { CategoryCode = modelCategoryCode }, transaction, cancellationToken: cancellationToken));
            model = EngineeringKitNumbering.Model(modelCategoryCode, checked(sequence + 1));
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE engineering_kit_model_counter SET current_sequence=@Next WHERE category_code=@CategoryCode",
                new { Next = sequence + 1, CategoryCode = modelCategoryCode }, transaction, cancellationToken: cancellationToken));
        }

        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE engineering_kit_revision SET revision_state='Released',published_by=@Actor,published_at=@PublishedAt WHERE id=@RevisionId
            """, new { Actor = actor, PublishedAt = publishedAt.UtcDateTime, RevisionId = draft.Id }, transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE engineering_kit SET kit_code=@Code,kit_model=@Model,category_code=@CategoryCode,current_released_revision_id=@RevisionId,standard_library_category_id=@StandardLibraryCategoryId,updated_by=@Actor,updated_at=@PublishedAt,row_version=row_version+1
            WHERE id=@KitId
            """, new { Code = code, Model = model, CategoryCode = modelCategoryCode, RevisionId = draft.Id, draft.StandardLibraryCategoryId, Actor = actor, PublishedAt = publishedAt.UtcDateTime, KitId = kitId }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return (await FindAsync(kitId, cancellationToken))!;
    }

    public async Task<EngineeringKit> MoveCategoryAsync(Guid kitId, Guid categoryId, long expectedRowVersion, string actor, DateTimeOffset updatedAt, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var affected = await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE engineering_kit SET standard_library_category_id=@CategoryId,updated_by=@Actor,updated_at=@UpdatedAt,row_version=row_version+1
            WHERE id=@KitId AND row_version=@ExpectedRowVersion AND current_released_revision_id IS NOT NULL
            """, new { KitId = kitId, CategoryId = categoryId, ExpectedRowVersion = expectedRowVersion, Actor = actor, UpdatedAt = updatedAt.UtcDateTime }, cancellationToken: cancellationToken));
        if (affected == 0) throw new PdmConflictException("套件未发布或已被其他用户修改，请刷新后重试。");
        return (await FindAsync(kitId, cancellationToken))!;
    }

    public async Task<IReadOnlyList<MaterialAttachment>> ListLibraryAttachmentsAsync(Guid kitId, MaterialAttachmentKind? kind, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<AttachmentRow>(new CommandDefinition(AttachmentSelect +
            " WHERE kit_id=@KitId AND (@Kind IS NULL OR attachment_kind=@Kind) ORDER BY uploaded_at DESC,id",
            new { KitId = kitId, Kind = kind?.ToString() }, cancellationToken: cancellationToken));
        return rows.Select(MapAttachment).ToArray();
    }

    public async Task<MaterialAttachment?> FindLibraryAttachmentAsync(Guid attachmentId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<AttachmentRow>(new CommandDefinition(
            AttachmentSelect + " WHERE id=@Id", new { Id = attachmentId }, cancellationToken: cancellationToken));
        return row is null ? null : MapAttachment(row);
    }

    public async Task<MaterialAttachment> CreateLibraryAttachmentAsync(MaterialAttachment attachment, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var kit = await connection.QuerySingleOrDefaultAsync<KitRow>(new CommandDefinition(
            "SELECT id,is_active,current_released_revision_id FROM engineering_kit WHERE id=@Id FOR UPDATE",
            new { Id = attachment.MaterialId }, transaction, cancellationToken: cancellationToken)) ?? throw new PdmNotFoundException("套件不存在。");
        if (!kit.IsActive || kit.CurrentReleasedRevisionId is null) throw new PdmConflictException("套件已停用或尚未发布，附件未入库。");
        if (attachment.Kind == MaterialAttachmentKind.CoverImage && await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM engineering_kit_attachment WHERE kit_id=@Id AND attachment_kind='CoverImage'",
                new { Id = attachment.MaterialId }, transaction, cancellationToken: cancellationToken)) >= 3)
            throw new PdmRuleException("每个套件最多上传3张图片。");
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO engineering_kit_attachment(id,kit_id,attachment_kind,original_file_name,storage_root,storage_relative_path,file_length,sha256,uploaded_by,uploaded_at)
            VALUES(@Id,@MaterialId,@Kind,@OriginalFileName,@StorageRoot,@StorageRelativePath,@FileLength,@Sha256,@UploadedBy,@UploadedAt)
            """, new { attachment.Id, attachment.MaterialId, Kind = attachment.Kind.ToString(), attachment.OriginalFileName,
                attachment.StorageRoot, attachment.StorageRelativePath, attachment.FileLength, attachment.Sha256, attachment.UploadedBy,
                UploadedAt = attachment.UploadedAt.UtcDateTime }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return attachment;
    }

    public async Task<bool> DeleteLibraryAttachmentAsync(Guid kitId, Guid attachmentId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await connection.ExecuteAsync(new CommandDefinition("""
            DELETE attachment FROM engineering_kit_attachment AS attachment
            INNER JOIN engineering_kit AS kit ON kit.id=attachment.kit_id
            WHERE attachment.id=@AttachmentId AND attachment.kit_id=@KitId
              AND kit.is_active=1 AND kit.current_released_revision_id IS NOT NULL
              AND attachment.attachment_kind IN ('Model3D','Document')
            """, new { KitId = kitId, AttachmentId = attachmentId }, cancellationToken: cancellationToken)) == 1;
    }

    private const string AttachmentSelect = "SELECT id,kit_id material_id,attachment_kind,original_file_name,storage_root,storage_relative_path,file_length,sha256,uploaded_by,uploaded_at FROM engineering_kit_attachment";
    private static MaterialAttachment MapAttachment(AttachmentRow row) => new(row.Id, row.MaterialId,
        Enum.Parse<MaterialAttachmentKind>(row.AttachmentKind), row.OriginalFileName, row.StorageRoot, row.StorageRelativePath,
        row.FileLength, row.Sha256, row.UploadedBy, Utc(row.UploadedAt));
    private sealed class AttachmentRow
    {
        public Guid Id { get; init; }
        public Guid MaterialId { get; init; }
        public string AttachmentKind { get; init; } = "";
        public string OriginalFileName { get; init; } = "";
        public string StorageRoot { get; init; } = "";
        public string StorageRelativePath { get; init; } = "";
        public long FileLength { get; init; }
        public string Sha256 { get; init; } = "";
        public string UploadedBy { get; init; } = "";
        public DateTime UploadedAt { get; init; }
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
            SELECT c.id,c.revision_id,c.material_id,m.material_code,m.name material_name,c.quantity,m.unit_code unit,c.is_optional,c.sort_order,m.specification,m.brand,m.remark
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
        new(row.Id, row.KitCode, row.KitModel, row.Name, row.Brand, row.Description, row.CurrentReleasedRevisionId, revisions,
            row.CreatedBy, Utc(row.CreatedAt), row.UpdatedBy, Utc(row.UpdatedAt), row.RowVersion,
            Enum.TryParse<EngineeringKitModelMode>(row.ModelMode, true, out var mode) ? mode : EngineeringKitModelMode.Auto,
            row.StandardName, row.StandardCode, row.CategoryName, row.CategoryCode, row.StandardLibraryCategoryId)
        { IsActive = row.IsActive, SelectionAdvice = row.SelectionAdvice, ReferencePrice = row.ReferencePrice, IsRecommended = row.IsRecommended,
            CoverImageAttachmentId = row.CoverImageAttachmentId, ReferenceCount = row.ReferenceCount, Model3DAttachmentCount = row.Model3DAttachmentCount, DocumentAttachmentCount = row.DocumentAttachmentCount };

    private static EngineeringKitRevision MapRevision(RevisionRow row, IReadOnlyList<EngineeringKitComponent> components) =>
        new(row.Id, row.KitId, row.VersionNo, Enum.Parse<EngineeringKitRevisionState>(row.RevisionState), row.ChangeNote,
            components, row.CreatedBy, Utc(row.CreatedAt), row.PublishedBy, row.PublishedAt is null ? null : Utc(row.PublishedAt.Value), row.StandardLibraryCategoryId);

    private static EngineeringKitComponent MapComponent(ComponentRow row) =>
        new(row.Id, row.RevisionId, row.MaterialId, row.MaterialCode, row.MaterialName, row.Quantity, row.Unit, row.IsOptional, row.SortOrder, row.Specification, row.Brand, row.Remark);

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private const string KitSelect = """
        SELECT k.id,k.is_active,k.kit_code,k.kit_model,k.model_mode,k.standard_name,k.standard_code,k.category_name,k.category_code,k.name,k.brand,k.description,k.selection_advice,k.reference_price,k.is_recommended,k.cover_image_attachment_id,k.current_released_revision_id,k.created_by,k.created_at,k.updated_by,k.updated_at,k.row_version,k.standard_library_category_id,
            (SELECT COUNT(DISTINCT b.project_id,COALESCE(b.engineering_kit_reference_id,b.id)) FROM bom_item b WHERE b.engineering_kit_id=k.id) reference_count,
            (SELECT COUNT(*) FROM engineering_kit_attachment a WHERE a.kit_id=k.id AND a.attachment_kind='Model3D') model3d_attachment_count,
            (SELECT COUNT(*) FROM engineering_kit_attachment a WHERE a.kit_id=k.id AND a.attachment_kind='Document') document_attachment_count
        FROM engineering_kit k
        """;

    private const string RevisionSelect = """
        SELECT r.id,r.kit_id,r.version_no,r.revision_state,r.change_note,r.created_by,r.created_at,r.published_by,r.published_at,r.standard_library_category_id
        FROM engineering_kit_revision r
        """;

    private sealed class KitRow
    {
        public bool IsActive { get; init; }
        public string? SelectionAdvice { get; init; }
        public decimal? ReferencePrice { get; init; }
        public bool IsRecommended { get; init; }
        public Guid? CoverImageAttachmentId { get; init; }
        public Guid Id { get; init; }
        public int ReferenceCount { get; init; }
        public int Model3DAttachmentCount { get; init; }
        public int DocumentAttachmentCount { get; init; }
        public string? KitCode { get; init; }
        public string? KitModel { get; init; }
        public string? ModelMode { get; init; }
        public string? StandardName { get; init; }
        public string? StandardCode { get; init; }
        public string? CategoryName { get; init; }
        public string? CategoryCode { get; init; }
        public Guid? StandardLibraryCategoryId { get; init; }
        public string Name { get; init; } = string.Empty;
        public string Brand { get; init; } = string.Empty;
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
        public Guid? StandardLibraryCategoryId { get; init; }
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
        public string? Specification { get; init; }
        public string? Brand { get; init; }
        public string? Remark { get; init; }
    }
}
