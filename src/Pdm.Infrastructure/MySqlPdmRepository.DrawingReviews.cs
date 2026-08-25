using System.Data.Common;
using Dapper;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed partial class MySqlPdmRepository
{
    public async Task<IReadOnlyList<DrawingReviewPackage>> ListDrawingReviewPackagesAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var ids = await connection.QueryAsync<Guid>(new CommandDefinition(
            "SELECT id FROM drawing_review_package WHERE project_id=@ProjectId ORDER BY created_at DESC",
            new { ProjectId = projectId }, cancellationToken: cancellationToken));
        var packages = new List<DrawingReviewPackage>();
        foreach (var id in ids)
            packages.Add(await LoadDrawingReviewPackageAsync(connection, null, id, cancellationToken)
                ?? throw new PdmNotFoundException("图纸审核单不存在。"));
        return packages;
    }

    public async Task<IReadOnlySet<Guid>> ListActiveDrawingReviewDocumentIdsAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var ids = await connection.QueryAsync<Guid>(new CommandDefinition(
            """
            SELECT item.model_document_id
            FROM drawing_review_item item
            JOIN drawing_review_package package ON package.id=item.package_id
            WHERE package.project_id=@ProjectId AND package.state IN ('InReview','WritingProperties')
            UNION
            SELECT item.drawing_document_id
            FROM drawing_review_item item
            JOIN drawing_review_package package ON package.id=item.package_id
            WHERE package.project_id=@ProjectId AND package.state IN ('InReview','WritingProperties') AND item.drawing_document_id IS NOT NULL
            """,
            new { ProjectId = projectId }, cancellationToken: cancellationToken));
        return ids.ToHashSet();
    }

    public async Task<bool> IsDocumentUnderActiveDrawingReviewAsync(Guid documentId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await IsDocumentUnderActiveDrawingReviewAsync(connection, null, documentId, cancellationToken);
    }

    public async Task<bool> IsActiveDrawingReviewWritebackAsync(Guid documentId, Guid writebackId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await IsActiveDrawingReviewWritebackAsync(connection, null, documentId, writebackId, cancellationToken);
    }

    public async Task<DrawingReviewPackage?> FindDrawingReviewPackageAsync(Guid packageId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await LoadDrawingReviewPackageAsync(connection, null, packageId, cancellationToken);
    }

    public async Task<DrawingReviewPackage> CreateDrawingReviewPackageAsync(DrawingReviewPackage package, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var documentIds = package.Items
            .SelectMany(item => item.DrawingDocumentId.HasValue
                ? new[] { item.ModelDocumentId, item.DrawingDocumentId.Value }
                : new[] { item.ModelDocumentId })
            .Distinct()
            .OrderBy(id => id)
            .ToArray();
        var lockedDocuments = (await connection.QueryAsync<DrawingReviewDocumentLockRow>(new CommandDefinition(
            "SELECT id,checked_out_by FROM document WHERE id IN @DocumentIds ORDER BY id FOR UPDATE",
            new { DocumentIds = documentIds }, transaction, cancellationToken: cancellationToken))).ToArray();
        if (lockedDocuments.Length != documentIds.Length)
            throw new PdmNotFoundException("待审核图档不存在，请刷新后重试。");
        if (lockedDocuments.Any(document => !string.IsNullOrWhiteSpace(document.CheckedOutBy)))
            throw new PdmConflictException("待审核图档仍处于签出编辑状态，请先提交存档或放弃编辑。");
        var activeReviewCount = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            SELECT COUNT(*)
            FROM drawing_review_item item
            JOIN drawing_review_package package ON package.id=item.package_id
            WHERE package.state IN ('InReview','WritingProperties')
              AND (item.model_document_id IN @DocumentIds OR item.drawing_document_id IN @DocumentIds)
            """,
            new { DocumentIds = documentIds }, transaction, cancellationToken: cancellationToken));
        if (activeReviewCount > 0)
            throw new PdmConflictException("待审核图档已经处于图纸审核中，请刷新后重试。");
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO drawing_review_package(id,project_id,review_number,state,created_by,created_at,approved_at) VALUES(@Id,@ProjectId,@Number,@State,@CreatedBy,@CreatedAt,@ApprovedAt)",
            new { package.Id, package.ProjectId, package.Number, State = package.State.ToString(), package.CreatedBy, CreatedAt = package.CreatedAt.UtcDateTime, ApprovedAt = package.ApprovedAt?.UtcDateTime },
            transaction, cancellationToken: cancellationToken));
        foreach (var item in package.Items)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO drawing_review_item(
                    id,package_id,bom_item_id,drawing_number,item_name,source_configuration,
                    model_document_id,model_version_id,model_revision,model_sha256,model_created_by,
                    drawing_document_id,drawing_version_id,drawing_revision,drawing_sha256,drawing_created_by,
                    model_state,drawing_state)
                VALUES(
                    @Id,@PackageId,@BomItemId,@DrawingNumber,@Name,@Configuration,
                    @ModelDocumentId,@ModelVersionId,@ModelRevision,@ModelSha256,@ModelCreatedBy,
                    @DrawingDocumentId,@DrawingVersionId,@DrawingRevision,@DrawingSha256,@DrawingCreatedBy,
                    @ModelState,@DrawingState)
                """,
                new
                {
                    item.Id,
                    item.PackageId,
                    item.BomItemId,
                    item.DrawingNumber,
                    item.Name,
                    item.Configuration,
                    item.ModelDocumentId,
                    item.ModelVersionId,
                    item.ModelRevision,
                    item.ModelSha256,
                    item.ModelCreatedBy,
                    item.DrawingDocumentId,
                    item.DrawingVersionId,
                    item.DrawingRevision,
                    item.DrawingSha256,
                    item.DrawingCreatedBy,
                    ModelState = item.ModelState.ToString(),
                    DrawingState = item.DrawingState.ToString()
                }, transaction, cancellationToken: cancellationToken));
        }
        await transaction.CommitAsync(cancellationToken);
        return package;
    }

    public async Task<DrawingReviewPackage> WithdrawDrawingReviewPackageAsync(Guid packageId, string actor, DateTimeOffset withdrawnAt, string reason, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var state = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT state FROM drawing_review_package WHERE id=@PackageId FOR UPDATE",
            new { PackageId = packageId }, transaction, cancellationToken: cancellationToken));
        if (state is null) throw new PdmNotFoundException("图纸审核单不存在。");
        if (state != DrawingReviewPackageState.InReview.ToString())
            throw new PdmConflictException("只有审核中的图纸审核单可以撤销。");
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE drawing_review_package SET state='Withdrawn',withdrawn_by=@Actor,withdrawn_at=@WithdrawnAt,withdrawal_reason=@Reason WHERE id=@PackageId",
            new { PackageId = packageId, Actor = actor, WithdrawnAt = withdrawnAt.UtcDateTime, Reason = reason },
            transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return await FindDrawingReviewPackageAsync(packageId, cancellationToken)
            ?? throw new PdmNotFoundException("图纸审核单不存在。");
    }

    private static async Task<bool> IsDocumentUnderActiveDrawingReviewAsync(
        DbConnection connection,
        DbTransaction? transaction,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        var count = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            SELECT COUNT(*)
            FROM drawing_review_item item
            JOIN drawing_review_package package ON package.id=item.package_id
            WHERE package.state IN ('InReview','WritingProperties')
              AND (item.model_document_id=@DocumentId OR item.drawing_document_id=@DocumentId)
            """,
            new { DocumentId = documentId }, transaction, cancellationToken: cancellationToken));
        return count > 0;
    }

    private static async Task<bool> IsActiveDrawingReviewWritebackAsync(
        DbConnection connection,
        DbTransaction? transaction,
        Guid documentId,
        Guid writebackId,
        CancellationToken cancellationToken)
    {
        var count = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            SELECT COUNT(*)
            FROM drawing_review_item item
            JOIN drawing_review_package package ON package.id=item.package_id
            JOIN cad_property_writeback writeback ON writeback.id=@WritebackId
            WHERE package.state='WritingProperties'
              AND writeback.source_document_id=@DocumentId
              AND writeback.status='InProgress'
              AND ((item.model_document_id=@DocumentId AND item.model_writeback_id=@WritebackId)
                   OR (item.drawing_document_id=@DocumentId AND item.drawing_writeback_id=@WritebackId))
            """,
            new { DocumentId = documentId, WritebackId = writebackId }, transaction, cancellationToken: cancellationToken));
        return count > 0;
    }

    public async Task<DrawingReviewPackage> AddDrawingReviewMarkupAsync(DrawingReviewMarkup markup, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO drawing_review_markup(
                id,package_id,item_id,target,view_name,normalized_x,normalized_y,markup_text,severity,state,created_by,created_at)
            SELECT @Id,item.package_id,item.id,@Target,@ViewName,@NormalizedX,@NormalizedY,@Text,@Severity,@State,@CreatedBy,@CreatedAt
            FROM drawing_review_item item
            JOIN drawing_review_package package ON package.id=item.package_id
            WHERE item.id=@ItemId AND item.package_id=@PackageId AND package.state IN ('InReview','WritingProperties')
            """,
            new
            {
                markup.Id,
                markup.PackageId,
                markup.ItemId,
                Target = markup.Target.ToString(),
                markup.ViewName,
                markup.NormalizedX,
                markup.NormalizedY,
                markup.Text,
                Severity = markup.Severity.ToString(),
                State = markup.State.ToString(),
                markup.CreatedBy,
                CreatedAt = markup.CreatedAt.UtcDateTime
            }, cancellationToken: cancellationToken));
        if (affected != 1) throw new PdmConflictException("图纸审核项不存在或审核单已关闭。");
        return await FindDrawingReviewPackageAsync(markup.PackageId, cancellationToken)
            ?? throw new PdmNotFoundException("图纸审核单不存在。");
    }

    public async Task<DrawingReviewPackage> ResolveDrawingReviewMarkupAsync(Guid markupId, string actor, DateTimeOffset resolvedAt, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<DrawingReviewMarkupLinkRow>(new CommandDefinition(
            "SELECT package_id FROM drawing_review_markup WHERE id=@MarkupId",
            new { MarkupId = markupId }, cancellationToken: cancellationToken));
        if (row is null) throw new PdmNotFoundException("图纸批注不存在。");
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE drawing_review_markup SET state='Resolved',resolved_by=@Actor,resolved_at=@ResolvedAt WHERE id=@MarkupId AND state='Open'",
            new { MarkupId = markupId, Actor = actor, ResolvedAt = resolvedAt.UtcDateTime }, cancellationToken: cancellationToken));
        if (affected != 1) throw new PdmConflictException("图纸批注已经关闭。");
        return await FindDrawingReviewPackageAsync(row.PackageId, cancellationToken)
            ?? throw new PdmNotFoundException("图纸审核单不存在。");
    }

    public async Task<DrawingReviewPackage> DecideDrawingReviewTargetAsync(
        Guid itemId,
        DrawingReviewTarget target,
        DrawingReviewTargetState state,
        string reviewer,
        string reviewerName,
        DateTimeOffset reviewedAt,
        string? comment,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var link = await connection.QuerySingleOrDefaultAsync<DrawingReviewItemLinkRow>(new CommandDefinition(
            "SELECT item.package_id,package.state package_state FROM drawing_review_item item JOIN drawing_review_package package ON package.id=item.package_id WHERE item.id=@ItemId FOR UPDATE",
            new { ItemId = itemId }, transaction, cancellationToken: cancellationToken));
        if (link is null) throw new PdmNotFoundException("图纸审核项不存在。");
        if (link.PackageState != DrawingReviewPackageState.InReview.ToString())
            throw new PdmConflictException("当前图纸审核单不允许继续审核。");
        var sql = target == DrawingReviewTarget.Model3D
            ? "UPDATE drawing_review_item SET model_state=@State,model_reviewer=@Reviewer,model_reviewer_name=@ReviewerName,model_reviewed_at=@ReviewedAt,model_comment=@Comment WHERE id=@ItemId AND model_state='Pending'"
            : "UPDATE drawing_review_item SET drawing_state=@State,drawing_reviewer=@Reviewer,drawing_reviewer_name=@ReviewerName,drawing_reviewed_at=@ReviewedAt,drawing_comment=@Comment WHERE id=@ItemId AND drawing_state='Pending'";
        var affected = await connection.ExecuteAsync(new CommandDefinition(sql,
            new { ItemId = itemId, State = state.ToString(), Reviewer = reviewer, ReviewerName = reviewerName, ReviewedAt = reviewedAt.UtcDateTime, Comment = comment },
            transaction, cancellationToken: cancellationToken));
        if (affected != 1) throw new PdmConflictException("该3D或2D图档已经完成审核，请刷新后重试。");
        if (state == DrawingReviewTargetState.ChangesRequested)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE drawing_review_package SET state='ChangesRequested' WHERE id=@PackageId",
                new { link.PackageId }, transaction, cancellationToken: cancellationToken));
        }
        await transaction.CommitAsync(cancellationToken);
        return await FindDrawingReviewPackageAsync(link.PackageId, cancellationToken)
            ?? throw new PdmNotFoundException("图纸审核单不存在。");
    }

    public async Task<DrawingReviewPackage> QueueDrawingReviewWritebacksAsync(Guid packageId, IReadOnlyList<DrawingReviewWritebackRequest> requests, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var packageState = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT state FROM drawing_review_package WHERE id=@PackageId FOR UPDATE",
            new { PackageId = packageId }, transaction, cancellationToken: cancellationToken));
        if (packageState is null) throw new PdmNotFoundException("图纸审核单不存在。");
        if (packageState != DrawingReviewPackageState.InReview.ToString())
            throw new PdmConflictException("当前图纸审核单不能生成属性写回任务。");
        var itemCount = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM drawing_review_item WHERE package_id=@PackageId",
            new { PackageId = packageId }, transaction, cancellationToken: cancellationToken));
        if (itemCount != requests.Count) throw new PdmRuleException("图纸审核属性写回必须覆盖审核单中的全部图档。");
        foreach (var request in requests)
        {
            await InsertDrawingReviewWritebackAsync(connection, transaction, request.Model, cancellationToken);
            if (request.Drawing is not null)
                await InsertDrawingReviewWritebackAsync(connection, transaction, request.Drawing, cancellationToken);
            var affected = await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE drawing_review_item
                SET model_writeback_id=@ModelWritebackId,drawing_writeback_id=@DrawingWritebackId
                WHERE id=@ItemId AND package_id=@PackageId AND model_state='Approved' AND model_writeback_id IS NULL
                  AND ((drawing_state='Approved' AND @DrawingWritebackId IS NOT NULL AND drawing_writeback_id IS NULL)
                    OR (drawing_state='NotRequired' AND @DrawingWritebackId IS NULL))
                """,
                new { request.ItemId, PackageId = packageId, ModelWritebackId = request.Model.Id, DrawingWritebackId = request.Drawing?.Id }, transaction, cancellationToken: cancellationToken));
            if (affected != 1) throw new PdmConflictException("图纸审核结果已变化，不能生成属性写回任务。");
        }
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE drawing_review_package SET state='WritingProperties' WHERE id=@PackageId",
            new { PackageId = packageId }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return await FindDrawingReviewPackageAsync(packageId, cancellationToken)
            ?? throw new PdmNotFoundException("图纸审核单不存在。");
    }

    public async Task<DrawingReviewPackage?> RecordDrawingReviewWritebackResultAsync(Guid writebackId, Guid? resultVersionId, bool succeeded, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var link = await connection.QuerySingleOrDefaultAsync<DrawingReviewWritebackLinkRow>(new CommandDefinition(
            """
            SELECT id item_id,package_id,
                   CASE WHEN model_writeback_id=@WritebackId THEN 'Model3D' ELSE 'Drawing2D' END target
            FROM drawing_review_item
            WHERE model_writeback_id=@WritebackId OR drawing_writeback_id=@WritebackId
            LIMIT 1 FOR UPDATE
            """,
            new { WritebackId = writebackId }, transaction, cancellationToken: cancellationToken));
        if (link is null) return null;
        if (!succeeded)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE drawing_review_package SET state='Stale',approved_at=NULL WHERE id=@PackageId",
                new { link.PackageId }, transaction, cancellationToken: cancellationToken));
        }
        else
        {
            if (!resultVersionId.HasValue) throw new PdmRuleException("图纸审核属性写回缺少结果版本。");
            var sql = link.Target == DrawingReviewTarget.Model3D.ToString()
                ? "UPDATE drawing_review_item SET model_state='Marked',model_result_version_id=@ResultVersionId WHERE id=@ItemId"
                : "UPDATE drawing_review_item SET drawing_state='Marked',drawing_result_version_id=@ResultVersionId WHERE id=@ItemId";
            await connection.ExecuteAsync(new CommandDefinition(sql,
                new { link.ItemId, ResultVersionId = resultVersionId.Value }, transaction, cancellationToken: cancellationToken));
            var remaining = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM drawing_review_item WHERE package_id=@PackageId AND (model_state<>'Marked' OR drawing_state NOT IN ('Marked','NotRequired'))",
                new { link.PackageId }, transaction, cancellationToken: cancellationToken));
            await connection.ExecuteAsync(new CommandDefinition(
                remaining == 0
                    ? "UPDATE drawing_review_package SET state='Approved',approved_at=@Now WHERE id=@PackageId"
                    : "UPDATE drawing_review_package SET state='WritingProperties',approved_at=NULL WHERE id=@PackageId",
                new { link.PackageId, Now = timeProvider.GetUtcNow().UtcDateTime }, transaction, cancellationToken: cancellationToken));
        }
        await transaction.CommitAsync(cancellationToken);
        return await FindDrawingReviewPackageAsync(link.PackageId, cancellationToken);
    }

    public async Task<ReleasePackage?> FindReleasePackageByApprovalTaskAsync(Guid taskId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<ReleasePackageLinkRow>(new CommandDefinition(
            "SELECT release_package_id id FROM approval_task WHERE id=@TaskId",
            new { TaskId = taskId }, cancellationToken: cancellationToken));
        return row is null ? null : await FindReleasePackageAsync(row.Id, cancellationToken);
    }

    private async Task InsertDrawingReviewWritebackAsync(System.Data.Common.DbConnection connection, System.Data.Common.DbTransaction transaction, CadPropertyWriteback request, CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO cad_property_writeback(id,project_id,bom_item_id,source_document_id,source_configuration,expected_version_id,expected_revision,property_payload,status,requested_by,requested_at) VALUES(@Id,@ProjectId,@BomItemId,@SourceDocumentId,@SourceConfiguration,@ExpectedVersionId,@ExpectedRevision,@PropertyPayload,@Status,@RequestedBy,@RequestedAt)",
            new
            {
                request.Id,
                request.ProjectId,
                request.BomItemId,
                request.SourceDocumentId,
                request.SourceConfiguration,
                request.ExpectedVersionId,
                request.ExpectedRevision,
                PropertyPayload = System.Text.Json.JsonSerializer.Serialize(request.Properties, jsonOptions),
                Status = request.Status.ToString(),
                request.RequestedBy,
                RequestedAt = request.RequestedAt.UtcDateTime
            }, transaction, cancellationToken: cancellationToken));
    }

    private async Task<DrawingReviewPackage?> LoadDrawingReviewPackageAsync(System.Data.Common.DbConnection connection, System.Data.Common.DbTransaction? transaction, Guid packageId, CancellationToken cancellationToken)
    {
        var package = await connection.QuerySingleOrDefaultAsync<DrawingReviewPackageRow>(new CommandDefinition(
            "SELECT id,project_id,review_number,state,created_by,created_at,approved_at,withdrawn_by,withdrawn_at,withdrawal_reason FROM drawing_review_package WHERE id=@PackageId",
            new { PackageId = packageId }, transaction, cancellationToken: cancellationToken));
        if (package is null) return null;
        var items = await connection.QueryAsync<DrawingReviewItemRow>(new CommandDefinition(
            """
            SELECT id,package_id,bom_item_id,drawing_number,item_name,source_configuration,
                   model_document_id,model_version_id,model_revision,model_sha256,model_created_by,
                   drawing_document_id,drawing_version_id,drawing_revision,drawing_sha256,drawing_created_by,
                   model_state,model_reviewer,model_reviewer_name,model_reviewed_at,model_comment,
                   drawing_state,drawing_reviewer,drawing_reviewer_name,drawing_reviewed_at,drawing_comment,
                   model_writeback_id,drawing_writeback_id,model_result_version_id,drawing_result_version_id
            FROM drawing_review_item WHERE package_id=@PackageId ORDER BY drawing_number,item_name
            """,
            new { PackageId = packageId }, transaction, cancellationToken: cancellationToken));
        var markups = await connection.QueryAsync<DrawingReviewMarkupRow>(new CommandDefinition(
            """
            SELECT id,package_id,item_id,target,view_name,normalized_x,normalized_y,markup_text,severity,state,created_by,created_at,resolved_by,resolved_at
            FROM drawing_review_markup WHERE package_id=@PackageId ORDER BY created_at
            """,
            new { PackageId = packageId }, transaction, cancellationToken: cancellationToken));
        return new DrawingReviewPackage
        {
            Id = package.Id,
            ProjectId = package.ProjectId,
            Number = package.ReviewNumber,
            State = Enum.Parse<DrawingReviewPackageState>(package.State),
            CreatedBy = package.CreatedBy,
            CreatedAt = AsUtc(package.CreatedAt),
            ApprovedAt = AsNullableUtc(package.ApprovedAt),
            WithdrawnBy = package.WithdrawnBy,
            WithdrawnAt = AsNullableUtc(package.WithdrawnAt),
            WithdrawalReason = package.WithdrawalReason,
            Items = items.Select(MapDrawingReviewItem).ToArray(),
            Markups = markups.Select(MapDrawingReviewMarkup).ToArray()
        };
    }

    private static DrawingReviewItem MapDrawingReviewItem(DrawingReviewItemRow row) => new()
    {
        Id = row.Id,
        PackageId = row.PackageId,
        BomItemId = row.BomItemId,
        DrawingNumber = row.DrawingNumber,
        Name = row.ItemName,
        Configuration = row.SourceConfiguration,
        ModelDocumentId = row.ModelDocumentId,
        ModelVersionId = row.ModelVersionId,
        ModelRevision = row.ModelRevision,
        ModelSha256 = row.ModelSha256,
        ModelCreatedBy = row.ModelCreatedBy,
        DrawingDocumentId = row.DrawingDocumentId,
        DrawingVersionId = row.DrawingVersionId,
        DrawingRevision = row.DrawingRevision,
        DrawingSha256 = row.DrawingSha256,
        DrawingCreatedBy = row.DrawingCreatedBy,
        ModelState = Enum.Parse<DrawingReviewTargetState>(row.ModelState),
        ModelReviewer = row.ModelReviewer,
        ModelReviewerName = row.ModelReviewerName,
        ModelReviewedAt = AsNullableUtc(row.ModelReviewedAt),
        ModelComment = row.ModelComment,
        DrawingState = Enum.Parse<DrawingReviewTargetState>(row.DrawingState),
        DrawingReviewer = row.DrawingReviewer,
        DrawingReviewerName = row.DrawingReviewerName,
        DrawingReviewedAt = AsNullableUtc(row.DrawingReviewedAt),
        DrawingComment = row.DrawingComment,
        ModelWritebackId = row.ModelWritebackId,
        DrawingWritebackId = row.DrawingWritebackId,
        ModelResultVersionId = row.ModelResultVersionId,
        DrawingResultVersionId = row.DrawingResultVersionId
    };

    private static DrawingReviewMarkup MapDrawingReviewMarkup(DrawingReviewMarkupRow row) => new()
    {
        Id = row.Id,
        PackageId = row.PackageId,
        ItemId = row.ItemId,
        Target = Enum.Parse<DrawingReviewTarget>(row.Target),
        ViewName = row.ViewName,
        NormalizedX = row.NormalizedX,
        NormalizedY = row.NormalizedY,
        Text = row.MarkupText,
        Severity = Enum.Parse<DrawingReviewMarkupSeverity>(row.Severity),
        State = Enum.Parse<DrawingReviewMarkupState>(row.State),
        CreatedBy = row.CreatedBy,
        CreatedAt = AsUtc(row.CreatedAt),
        ResolvedBy = row.ResolvedBy,
        ResolvedAt = AsNullableUtc(row.ResolvedAt)
    };

    private sealed class DrawingReviewPackageRow
    {
        public Guid Id { get; init; }
        public Guid ProjectId { get; init; }
        public string ReviewNumber { get; init; } = string.Empty;
        public string State { get; init; } = string.Empty;
        public string CreatedBy { get; init; } = string.Empty;
        public DateTime CreatedAt { get; init; }
        public DateTime? ApprovedAt { get; init; }
        public string? WithdrawnBy { get; init; }
        public DateTime? WithdrawnAt { get; init; }
        public string? WithdrawalReason { get; init; }
    }

    private sealed class DrawingReviewItemRow
    {
        public Guid Id { get; init; }
        public Guid PackageId { get; init; }
        public Guid BomItemId { get; init; }
        public string DrawingNumber { get; init; } = string.Empty;
        public string ItemName { get; init; } = string.Empty;
        public string? SourceConfiguration { get; init; }
        public Guid ModelDocumentId { get; init; }
        public Guid ModelVersionId { get; init; }
        public string ModelRevision { get; init; } = string.Empty;
        public string ModelSha256 { get; init; } = string.Empty;
        public string ModelCreatedBy { get; init; } = string.Empty;
        public Guid? DrawingDocumentId { get; init; }
        public Guid? DrawingVersionId { get; init; }
        public string? DrawingRevision { get; init; }
        public string? DrawingSha256 { get; init; }
        public string? DrawingCreatedBy { get; init; }
        public string ModelState { get; init; } = string.Empty;
        public string? ModelReviewer { get; init; }
        public string? ModelReviewerName { get; init; }
        public DateTime? ModelReviewedAt { get; init; }
        public string? ModelComment { get; init; }
        public string DrawingState { get; init; } = string.Empty;
        public string? DrawingReviewer { get; init; }
        public string? DrawingReviewerName { get; init; }
        public DateTime? DrawingReviewedAt { get; init; }
        public string? DrawingComment { get; init; }
        public Guid? ModelWritebackId { get; init; }
        public Guid? DrawingWritebackId { get; init; }
        public Guid? ModelResultVersionId { get; init; }
        public Guid? DrawingResultVersionId { get; init; }
    }

    private sealed class DrawingReviewMarkupRow
    {
        public Guid Id { get; init; }
        public Guid PackageId { get; init; }
        public Guid ItemId { get; init; }
        public string Target { get; init; } = string.Empty;
        public string? ViewName { get; init; }
        public decimal? NormalizedX { get; init; }
        public decimal? NormalizedY { get; init; }
        public string MarkupText { get; init; } = string.Empty;
        public string Severity { get; init; } = string.Empty;
        public string State { get; init; } = string.Empty;
        public string CreatedBy { get; init; } = string.Empty;
        public DateTime CreatedAt { get; init; }
        public string? ResolvedBy { get; init; }
        public DateTime? ResolvedAt { get; init; }
    }

    private sealed class DrawingReviewMarkupLinkRow
    {
        public Guid PackageId { get; init; }
    }

    private sealed class DrawingReviewItemLinkRow
    {
        public Guid PackageId { get; init; }
        public string PackageState { get; init; } = string.Empty;
    }

    private sealed class DrawingReviewWritebackLinkRow
    {
        public Guid ItemId { get; init; }
        public Guid PackageId { get; init; }
        public string Target { get; init; } = string.Empty;
    }

    private sealed class DrawingReviewDocumentLockRow
    {
        public Guid Id { get; init; }
        public string? CheckedOutBy { get; init; }
    }

    private sealed class ReleasePackageLinkRow
    {
        public Guid Id { get; init; }
    }
}
