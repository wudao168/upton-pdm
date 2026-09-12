using Dapper;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed class MySqlValidationPlanRepository : IValidationPlanRepository
{
    private readonly string connectionString;

    public MySqlValidationPlanRepository(IOptions<PdmDatabaseOptions> options)
    {
        connectionString = options.Value.ConnectionString;
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    public async Task<ValidationCheckCatalog> ListCatalogAsync(bool includeInactive, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var categoryRows = await connection.QueryAsync<CategoryRow>(new CommandDefinition(
            CategorySelect + " WHERE (@IncludeInactive OR c.is_active=1) ORDER BY c.sort_order,c.name",
            new { IncludeInactive = includeInactive }, cancellationToken: cancellationToken));
        var categoryIds = categoryRows.Select(item => item.Id).ToArray();
        if (categoryIds.Length == 0) return new([], []);
        var itemRows = await connection.QueryAsync<ItemRow>(new CommandDefinition(
            ItemSelect + " WHERE i.category_id IN @CategoryIds AND (@IncludeInactive OR i.is_active=1) ORDER BY i.sort_order,i.content",
            new { CategoryIds = categoryIds, IncludeInactive = includeInactive }, cancellationToken: cancellationToken));
        return new(categoryRows.Select(row => MapCategory(row)!).ToArray(), itemRows.Select(row => MapItem(row)!).ToArray());
    }

    public async Task<ValidationCheckCategory> CreateCategoryAsync(ValidationCheckCategory category, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO validation_check_category(id,name,sort_order,is_active,note,created_by,created_at,updated_by,updated_at,row_version) VALUES(@Id,@Name,@SortOrder,@IsActive,@Note,@CreatedBy,@CreatedAt,@UpdatedBy,@UpdatedAt,1)",
            ToCategoryParameters(category), cancellationToken: cancellationToken));
        return await FindCategoryAsync(connection, category.Id, cancellationToken) ?? throw new PdmNotFoundException("验证分类保存失败。");
    }

    public async Task<ValidationCheckCategory> UpdateCategoryAsync(ValidationCheckCategory category, long expectedRowVersion, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE validation_check_category SET name=@Name,sort_order=@SortOrder,is_active=@IsActive,note=@Note,updated_by=@UpdatedBy,updated_at=@UpdatedAt,row_version=row_version+1 WHERE id=@Id AND row_version=@ExpectedRowVersion",
            new { category.Id, category.Name, category.SortOrder, category.IsActive, category.Note, category.UpdatedBy, UpdatedAt = category.UpdatedAt.UtcDateTime, ExpectedRowVersion = expectedRowVersion }, cancellationToken: cancellationToken));
        if (affected == 0) throw new PdmConflictException("验证分类已被其他用户修改，请刷新后重试。");
        return await FindCategoryAsync(connection, category.Id, cancellationToken) ?? throw new PdmNotFoundException("验证分类不存在。");
    }

    public async Task DeleteCategoryAsync(Guid categoryId, long expectedRowVersion, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM validation_check_category WHERE id=@CategoryId AND row_version=@ExpectedRowVersion",
            new { CategoryId = categoryId, ExpectedRowVersion = expectedRowVersion }, cancellationToken: cancellationToken));
        if (affected == 0) throw new PdmConflictException("验证分类已变化，请刷新后重试。");
    }

    public async Task<ValidationCheckItem> CreateItemAsync(ValidationCheckItem item, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO validation_check_item(id,category_id,content,default_information_source,sort_order,is_active,note,created_by,created_at,updated_by,updated_at,row_version) VALUES(@Id,@CategoryId,@Content,@DefaultInformationSource,@SortOrder,@IsActive,@Note,@CreatedBy,@CreatedAt,@UpdatedBy,@UpdatedAt,1)",
            ToItemParameters(item), cancellationToken: cancellationToken));
        return await FindItemAsync(connection, item.Id, cancellationToken) ?? throw new PdmNotFoundException("检查项保存失败。");
    }

    public async Task<ValidationCheckItem> UpdateItemAsync(ValidationCheckItem item, long expectedRowVersion, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE validation_check_item SET category_id=@CategoryId,content=@Content,default_information_source=@DefaultInformationSource,sort_order=@SortOrder,is_active=@IsActive,note=@Note,updated_by=@UpdatedBy,updated_at=@UpdatedAt,row_version=row_version+1 WHERE id=@Id AND row_version=@ExpectedRowVersion",
            new { item.Id, item.CategoryId, item.Content, item.DefaultInformationSource, item.SortOrder, item.IsActive, item.Note, item.UpdatedBy, UpdatedAt = item.UpdatedAt.UtcDateTime, ExpectedRowVersion = expectedRowVersion }, cancellationToken: cancellationToken));
        if (affected == 0) throw new PdmConflictException("检查项已被其他用户修改，请刷新后重试。");
        return await FindItemAsync(connection, item.Id, cancellationToken) ?? throw new PdmNotFoundException("检查项不存在。");
    }

    public async Task DeleteItemAsync(Guid itemId, long expectedRowVersion, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM validation_check_item WHERE id=@ItemId AND row_version=@ExpectedRowVersion",
            new { ItemId = itemId, ExpectedRowVersion = expectedRowVersion }, cancellationToken: cancellationToken));
        if (affected == 0) throw new PdmConflictException("检查项已变化，请刷新后重试。");
    }

    public async Task<ProjectValidationPlan?> FindPlanAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var id = await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            "SELECT id FROM project_validation_plan WHERE project_id=@ProjectId ORDER BY revision_number DESC LIMIT 1",
            new { ProjectId = projectId }, cancellationToken: cancellationToken));
        return id is null ? null : await FindPlanByIdAsync(connection, null, id.Value, cancellationToken);
    }

    public async Task<ProjectValidationPlan?> FindPlanByIdAsync(Guid planId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await FindPlanByIdAsync(connection, null, planId, cancellationToken);
    }

    public async Task<ProjectValidationPlan> SavePlanAsync(ProjectValidationPlan plan, long? expectedRowVersion, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var currentVersion = await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT row_version FROM project_validation_plan WHERE id=@Id FOR UPDATE",
            new { plan.Id }, transaction, cancellationToken: cancellationToken));
        if (currentVersion is null)
        {
            if (expectedRowVersion is not null) throw new PdmConflictException("验证计划状态已变化，请刷新后重试。");
            await InsertPlanAsync(connection, transaction, plan, cancellationToken);
        }
        else
        {
            if (expectedRowVersion != currentVersion) throw new PdmConflictException("验证计划已被其他用户修改，请刷新后重试。");
            var affected = await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE project_validation_plan SET prepared_by=@PreparedBy,validation_date=@ValidationDate,updated_by=@UpdatedBy,updated_at=@UpdatedAt,row_version=row_version+1 WHERE id=@Id AND state IN ('Draft','Rejected') AND row_version=@ExpectedRowVersion",
                new { plan.Id, plan.PreparedBy, ValidationDate = ToDateTime(plan.ValidationDate), plan.UpdatedBy, UpdatedAt = plan.UpdatedAt.UtcDateTime, ExpectedRowVersion = expectedRowVersion }, transaction, cancellationToken: cancellationToken));
            if (affected == 0) throw new PdmConflictException("验证计划已被其他用户修改，请刷新后重试。");
            await connection.ExecuteAsync(new CommandDefinition("DELETE FROM project_validation_plan_item WHERE plan_id=@PlanId", new { PlanId = plan.Id }, transaction, cancellationToken: cancellationToken));
        }

        if (plan.Items.Count > 0)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO project_validation_plan_item(id,plan_id,catalog_category_id,catalog_item_id,category_name,validation_content,information_source,validation_date,result,responsible_person,remark,sort_order) VALUES(@Id,@PlanId,@CatalogCategoryId,@CatalogItemId,@CategoryName,@ValidationContent,@InformationSource,@ValidationDate,@Result,@ResponsiblePerson,@Remark,@SortOrder)",
                plan.Items.Select(item => new { item.Id, PlanId = plan.Id, item.CatalogCategoryId, item.CatalogItemId, item.CategoryName, item.ValidationContent, item.InformationSource, ValidationDate = ToDateTime(item.ValidationDate), item.Result, item.ResponsiblePerson, item.Remark, item.SortOrder }),
                transaction, cancellationToken: cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);
        return await FindPlanByIdAsync(connection, null, plan.Id, cancellationToken) ?? throw new PdmNotFoundException("验证计划保存失败。");
    }

    public async Task<ProjectValidationPlan> CreateRevisionAsync(ProjectValidationPlan plan, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var latestRevision = await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT MAX(revision_number) FROM project_validation_plan WHERE project_id=@ProjectId FOR UPDATE",
            new { plan.ProjectId }, transaction, cancellationToken: cancellationToken));
        if ((latestRevision ?? 0) >= plan.RevisionNumber) throw new PdmConflictException("验证计划版本已变化，请刷新后重试。");
        await InsertPlanAsync(connection, transaction, plan, cancellationToken);
        await InsertItemsAsync(connection, transaction, plan, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await FindPlanByIdAsync(connection, null, plan.Id, cancellationToken) ?? throw new PdmNotFoundException("验证计划新版本创建失败。");
    }

    public async Task<ProjectValidationPlan> SubmitAsync(Guid planId, long expectedRowVersion, string workflowCode, int workflowVersion, IReadOnlyList<ValidationPlanApprovalTask> tasks, string actor, DateTimeOffset submittedAt, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE project_validation_plan SET state='PendingApproval',workflow_code=@WorkflowCode,workflow_version=@WorkflowVersion,submitted_by=@Actor,submitted_at=@SubmittedAt,effective_by=NULL,effective_at=NULL,updated_by=@Actor,updated_at=@SubmittedAt,row_version=row_version+1 WHERE id=@PlanId AND state IN ('Draft','Rejected') AND row_version=@ExpectedRowVersion",
            new { PlanId = planId, ExpectedRowVersion = expectedRowVersion, WorkflowCode = workflowCode, WorkflowVersion = workflowVersion, Actor = actor, SubmittedAt = submittedAt.UtcDateTime }, transaction, cancellationToken: cancellationToken));
        if (affected == 0) throw new PdmConflictException("验证计划状态已变化，请刷新后重试。");
        await connection.ExecuteAsync(new CommandDefinition("DELETE FROM validation_plan_approval_task WHERE plan_id=@PlanId", new { PlanId = planId }, transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO validation_plan_approval_task(id,plan_id,step_order,stage,step_name,assignee,created_at) VALUES(@Id,@PlanId,@StepOrder,@Stage,@StepName,@Assignee,@CreatedAt)",
            tasks.Select(task => new { task.Id, task.PlanId, task.StepOrder, Stage = task.Stage.ToString(), task.StepName, task.Assignee, CreatedAt = task.CreatedAt.UtcDateTime }), transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return await FindPlanByIdAsync(connection, null, planId, cancellationToken) ?? throw new PdmNotFoundException("验证计划不存在。");
    }

    public async Task<ProjectValidationPlan> DecideAsync(Guid taskId, string actor, ApprovalDecision decision, string? comment, DateTimeOffset decidedAt, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var task = await connection.QuerySingleOrDefaultAsync<ApprovalTaskRow>(new CommandDefinition(
            "SELECT id,plan_id,step_order,stage,step_name,assignee,decision,decision_by,decision_comment,created_at,decided_at FROM validation_plan_approval_task WHERE id=@TaskId FOR UPDATE",
            new { TaskId = taskId }, transaction, cancellationToken: cancellationToken)) ?? throw new PdmNotFoundException("验证计划审批任务不存在。");
        var plan = await connection.QuerySingleAsync<PlanRow>(new CommandDefinition(PlanSelect + " WHERE id=@PlanId FOR UPDATE", new { PlanId = task.PlanId }, transaction, cancellationToken: cancellationToken));
        if (plan.State != ProjectValidationPlanState.PendingApproval.ToString() || task.Decision is not null) throw new PdmConflictException("验证计划审批状态已变化。");
        var earlierPending = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM validation_plan_approval_task WHERE plan_id=@PlanId AND step_order<@StepOrder AND decision IS NULL",
            new { task.PlanId, task.StepOrder }, transaction, cancellationToken: cancellationToken));
        if (earlierPending > 0) throw new PdmConflictException("当前尚未到达该审批节点。");
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE validation_plan_approval_task SET decision=@Decision,decision_by=@Actor,decision_comment=@Comment,decided_at=@DecidedAt WHERE id=@TaskId AND decision IS NULL",
            new { TaskId = taskId, Decision = decision.ToString(), Actor = actor, Comment = comment, DecidedAt = decidedAt.UtcDateTime }, transaction, cancellationToken: cancellationToken));
        var remaining = decision == ApprovalDecision.Approved
            ? await connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM validation_plan_approval_task WHERE plan_id=@PlanId AND decision IS NULL", new { PlanId = task.PlanId }, transaction, cancellationToken: cancellationToken))
            : 1;
        var nextState = decision == ApprovalDecision.Rejected ? "Rejected" : remaining == 0 ? "Effective" : "PendingApproval";
        if (nextState == "Effective")
            await connection.ExecuteAsync(new CommandDefinition("UPDATE project_validation_plan SET state='Superseded',row_version=row_version+1 WHERE project_id=@ProjectId AND state='Effective' AND id<>@PlanId", new { plan.ProjectId, PlanId = task.PlanId }, transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE project_validation_plan SET state=@State,updated_by=@Actor,updated_at=@DecidedAt,effective_by=@EffectiveBy,effective_at=@EffectiveAt,row_version=row_version+1 WHERE id=@PlanId",
            new { PlanId = task.PlanId, State = nextState, Actor = actor, DecidedAt = decidedAt.UtcDateTime, EffectiveBy = nextState == "Effective" ? actor : null, EffectiveAt = nextState == "Effective" ? decidedAt.UtcDateTime : (DateTime?)null }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return await FindPlanByIdAsync(connection, null, task.PlanId, cancellationToken) ?? throw new PdmNotFoundException("验证计划不存在。");
    }

    public async Task<IReadOnlyList<PendingValidationPlanApprovalTask>> ListPendingApprovalTasksAsync(string actor, bool includeAll, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<PendingTaskRow>(new CommandDefinition(
            """
            SELECT task.id,task.plan_id,plan.project_id,plan.revision_number,task.stage,task.step_name,task.assignee,task.created_at
            FROM validation_plan_approval_task task
            JOIN project_validation_plan plan ON plan.id=task.plan_id
            WHERE plan.state='PendingApproval' AND task.decision IS NULL
              AND (@IncludeAll OR task.assignee=@Actor)
              AND NOT EXISTS (SELECT 1 FROM validation_plan_approval_task earlier WHERE earlier.plan_id=task.plan_id AND earlier.step_order<task.step_order AND earlier.decision IS NULL)
            ORDER BY task.created_at
            """, new { Actor = actor, IncludeAll = includeAll }, cancellationToken: cancellationToken));
        return rows.Select(row => new PendingValidationPlanApprovalTask(row.Id, row.PlanId, row.ProjectId, row.RevisionNumber, Enum.Parse<ApprovalStage>(row.Stage), row.StepName, row.Assignee, Utc(row.CreatedAt))).ToArray();
    }

    public async Task<ValidationPlanAttachment> AddAttachmentAsync(ValidationPlanAttachment attachment, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO validation_plan_attachment(id,plan_id,attachment_kind,original_file_name,file_version,storage_relative_path,file_length,sha256,uploaded_by,uploaded_at) VALUES(@Id,@PlanId,@Kind,@OriginalFileName,@FileVersion,@StorageRelativePath,@FileLength,@Sha256,@UploadedBy,@UploadedAt)",
            new { attachment.Id, attachment.PlanId, Kind = attachment.Kind.ToString(), attachment.OriginalFileName, attachment.FileVersion, attachment.StorageRelativePath, attachment.FileLength, attachment.Sha256, attachment.UploadedBy, UploadedAt = attachment.UploadedAt.UtcDateTime }, transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition("UPDATE project_validation_plan SET row_version=row_version+1 WHERE id=@PlanId", new { attachment.PlanId }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return attachment;
    }

    public async Task<IReadOnlyList<ValidationPlanExecutionRecord>> ListExecutionRecordsAsync(Guid planId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var records = (await connection.QueryAsync<ExecutionRecordRow>(new CommandDefinition(
            "SELECT id,plan_id,source_attachment_id,source_file_name,ocr_text,confirmed_by,confirmed_at FROM validation_plan_execution_record WHERE plan_id=@PlanId ORDER BY confirmed_at DESC",
            new { PlanId = planId }, cancellationToken: cancellationToken))).ToArray();
        if (records.Length == 0) return [];
        var ids = records.Select(item => item.Id).ToArray();
        var items = (await connection.QueryAsync<ExecutionItemRow>(new CommandDefinition(
            "SELECT id,execution_record_id,plan_item_id,match_confidence,source_text,recognized_result,recognized_validation_date,recognized_responsible_person,recognized_remark,result,validation_date,responsible_person,remark FROM validation_plan_execution_item WHERE execution_record_id IN @Ids ORDER BY execution_record_id,id",
            new { Ids = ids }, cancellationToken: cancellationToken))).ToLookup(item => item.ExecutionRecordId);
        return records.Select(record => MapExecutionRecord(record, items[record.Id])).ToArray();
    }

    public async Task<ValidationPlanExecutionRecord> AddExecutionRecordAsync(ValidationPlanExecutionRecord record, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO validation_plan_execution_record(id,plan_id,source_attachment_id,source_file_name,ocr_text,confirmed_by,confirmed_at) VALUES(@Id,@PlanId,@SourceAttachmentId,@SourceFileName,@OcrText,@ConfirmedBy,@ConfirmedAt)",
            new { record.Id, record.PlanId, record.SourceAttachmentId, record.SourceFileName, record.OcrText, record.ConfirmedBy, ConfirmedAt = record.ConfirmedAt.UtcDateTime }, transaction, cancellationToken: cancellationToken));
        if (record.Items.Count > 0)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO validation_plan_execution_item(id,execution_record_id,plan_item_id,match_confidence,source_text,recognized_result,recognized_validation_date,recognized_responsible_person,recognized_remark,result,validation_date,responsible_person,remark) VALUES(@Id,@ExecutionRecordId,@PlanItemId,@MatchConfidence,@SourceText,@RecognizedResult,@RecognizedValidationDate,@RecognizedResponsiblePerson,@RecognizedRemark,@Result,@ValidationDate,@ResponsiblePerson,@Remark)",
                record.Items.Select(item => new { item.Id, item.ExecutionRecordId, item.PlanItemId, item.MatchConfidence, item.SourceText, item.RecognizedResult, RecognizedValidationDate = ToDateTime(item.RecognizedValidationDate), item.RecognizedResponsiblePerson, item.RecognizedRemark, item.Result, ValidationDate = ToDateTime(item.ValidationDate), item.ResponsiblePerson, item.Remark }),
                transaction, cancellationToken: cancellationToken));
        }
        await transaction.CommitAsync(cancellationToken);
        return record;
    }

    private static readonly string CategorySelect =
        "SELECT c.id,c.name,c.sort_order,c.is_active,c.note,c.created_by,c.created_at,c.updated_by,c.updated_at,c.row_version," +
        "(SELECT COUNT(*) FROM validation_check_item i WHERE i.category_id=c.id) item_count," +
        "(SELECT COUNT(*) FROM project_validation_plan_item p WHERE p.catalog_category_id=c.id) reference_count FROM validation_check_category c";

    private static readonly string ItemSelect =
        "SELECT i.id,i.category_id,i.content,i.default_information_source,i.sort_order,i.is_active,i.note,i.created_by,i.created_at,i.updated_by,i.updated_at,i.row_version," +
        "(SELECT COUNT(*) FROM project_validation_plan_item p WHERE p.catalog_item_id=i.id) reference_count FROM validation_check_item i";

    private static async Task<ValidationCheckCategory?> FindCategoryAsync(MySqlConnection connection, Guid id, CancellationToken cancellationToken) =>
        MapCategory(await connection.QuerySingleOrDefaultAsync<CategoryRow>(new CommandDefinition(CategorySelect + " WHERE c.id=@Id", new { Id = id }, cancellationToken: cancellationToken)));

    private static async Task<ValidationCheckItem?> FindItemAsync(MySqlConnection connection, Guid id, CancellationToken cancellationToken) =>
        MapItem(await connection.QuerySingleOrDefaultAsync<ItemRow>(new CommandDefinition(ItemSelect + " WHERE i.id=@Id", new { Id = id }, cancellationToken: cancellationToken)));

    private static async Task<ProjectValidationPlan?> FindPlanByIdAsync(MySqlConnection connection, MySqlTransaction? transaction, Guid planId, CancellationToken cancellationToken)
    {
        var row = await connection.QuerySingleOrDefaultAsync<PlanRow>(new CommandDefinition(
            PlanSelect + " WHERE id=@PlanId",
            new { PlanId = planId }, transaction, cancellationToken: cancellationToken));
        if (row is null) return null;
        var itemRows = await connection.QueryAsync<PlanItemRow>(new CommandDefinition(
            "SELECT id,catalog_category_id,catalog_item_id,category_name,validation_content,information_source,validation_date,result,responsible_person,remark,sort_order FROM project_validation_plan_item WHERE plan_id=@PlanId ORDER BY sort_order,id",
            new { PlanId = row.Id }, transaction, cancellationToken: cancellationToken));
        var taskRows = await connection.QueryAsync<ApprovalTaskRow>(new CommandDefinition(
            "SELECT id,plan_id,step_order,stage,step_name,assignee,decision,decision_by,decision_comment,created_at,decided_at FROM validation_plan_approval_task WHERE plan_id=@PlanId ORDER BY step_order",
            new { PlanId = row.Id }, transaction, cancellationToken: cancellationToken));
        var attachmentRows = await connection.QueryAsync<AttachmentRow>(new CommandDefinition(
            "SELECT id,plan_id,attachment_kind,original_file_name,file_version,storage_relative_path,file_length,sha256,uploaded_by,uploaded_at FROM validation_plan_attachment WHERE plan_id=@PlanId ORDER BY uploaded_at DESC",
            new { PlanId = row.Id }, transaction, cancellationToken: cancellationToken));
        return new(row.Id, row.ProjectId, row.RevisionNumber, Enum.Parse<ProjectValidationPlanState>(row.State), row.PreparedBy, ToDateOnly(row.ValidationDate), itemRows.Select(MapPlanItem).ToArray(),
            taskRows.Select(MapApprovalTask).ToArray(), attachmentRows.Select(MapAttachment).ToArray(), row.CreatedBy, Utc(row.CreatedAt), row.UpdatedBy, Utc(row.UpdatedAt), row.RowVersion)
        {
            WorkflowCode = row.WorkflowCode,
            WorkflowVersion = row.WorkflowVersion,
            SubmittedBy = row.SubmittedBy,
            SubmittedAt = row.SubmittedAt is null ? null : Utc(row.SubmittedAt.Value),
            EffectiveBy = row.EffectiveBy,
            EffectiveAt = row.EffectiveAt is null ? null : Utc(row.EffectiveAt.Value)
        };
    }

    private static Task InsertPlanAsync(MySqlConnection connection, MySqlTransaction transaction, ProjectValidationPlan plan, CancellationToken cancellationToken) =>
        connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO project_validation_plan(id,project_id,revision_number,state,prepared_by,validation_date,workflow_code,workflow_version,submitted_by,submitted_at,effective_by,effective_at,created_by,created_at,updated_by,updated_at,row_version) VALUES(@Id,@ProjectId,@RevisionNumber,@State,@PreparedBy,@ValidationDate,@WorkflowCode,@WorkflowVersion,@SubmittedBy,@SubmittedAt,@EffectiveBy,@EffectiveAt,@CreatedBy,@CreatedAt,@UpdatedBy,@UpdatedAt,1)",
            new { plan.Id, plan.ProjectId, plan.RevisionNumber, State = plan.State.ToString(), plan.PreparedBy, ValidationDate = ToDateTime(plan.ValidationDate), plan.WorkflowCode, plan.WorkflowVersion, plan.SubmittedBy, SubmittedAt = plan.SubmittedAt?.UtcDateTime, plan.EffectiveBy, EffectiveAt = plan.EffectiveAt?.UtcDateTime, plan.CreatedBy, CreatedAt = plan.CreatedAt.UtcDateTime, plan.UpdatedBy, UpdatedAt = plan.UpdatedAt.UtcDateTime }, transaction, cancellationToken: cancellationToken));

    private static Task InsertItemsAsync(MySqlConnection connection, MySqlTransaction transaction, ProjectValidationPlan plan, CancellationToken cancellationToken) =>
        plan.Items.Count == 0 ? Task.CompletedTask : connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO project_validation_plan_item(id,plan_id,catalog_category_id,catalog_item_id,category_name,validation_content,information_source,validation_date,result,responsible_person,remark,sort_order) VALUES(@Id,@PlanId,@CatalogCategoryId,@CatalogItemId,@CategoryName,@ValidationContent,@InformationSource,@ValidationDate,@Result,@ResponsiblePerson,@Remark,@SortOrder)",
            plan.Items.Select(item => new { item.Id, PlanId = plan.Id, item.CatalogCategoryId, item.CatalogItemId, item.CategoryName, item.ValidationContent, item.InformationSource, ValidationDate = ToDateTime(item.ValidationDate), item.Result, item.ResponsiblePerson, item.Remark, item.SortOrder }),
            transaction, cancellationToken: cancellationToken));

    private static object ToCategoryParameters(ValidationCheckCategory category) => new { category.Id, category.Name, category.SortOrder, category.IsActive, category.Note, category.CreatedBy, CreatedAt = category.CreatedAt.UtcDateTime, category.UpdatedBy, UpdatedAt = category.UpdatedAt.UtcDateTime };
    private static object ToItemParameters(ValidationCheckItem item) => new { item.Id, item.CategoryId, item.Content, item.DefaultInformationSource, item.SortOrder, item.IsActive, item.Note, item.CreatedBy, CreatedAt = item.CreatedAt.UtcDateTime, item.UpdatedBy, UpdatedAt = item.UpdatedAt.UtcDateTime };
    private static ValidationCheckCategory? MapCategory(CategoryRow? row) => row is null ? null : new(row.Id, row.Name, row.SortOrder, row.IsActive, row.Note, row.ItemCount, row.ReferenceCount, row.CreatedBy, Utc(row.CreatedAt), row.UpdatedBy, Utc(row.UpdatedAt), row.RowVersion);
    private static ValidationCheckItem? MapItem(ItemRow? row) => row is null ? null : new(row.Id, row.CategoryId, row.Content, row.DefaultInformationSource, row.SortOrder, row.IsActive, row.Note, row.ReferenceCount, row.CreatedBy, Utc(row.CreatedAt), row.UpdatedBy, Utc(row.UpdatedAt), row.RowVersion);
    private static ProjectValidationPlanItem MapPlanItem(PlanItemRow row) => new(row.Id, row.CatalogCategoryId, row.CatalogItemId, row.CategoryName, row.ValidationContent, row.InformationSource, ToDateOnly(row.ValidationDate), row.Result, row.ResponsiblePerson, row.Remark, row.SortOrder);
    private static ValidationPlanApprovalTask MapApprovalTask(ApprovalTaskRow row) => new(row.Id, row.PlanId, row.StepOrder, Enum.Parse<ApprovalStage>(row.Stage), row.StepName, row.Assignee,
        row.Decision is null ? null : Enum.Parse<ApprovalDecision>(row.Decision), row.DecisionBy, row.DecisionComment, Utc(row.CreatedAt), row.DecidedAt is null ? null : Utc(row.DecidedAt.Value));
    private static ValidationPlanAttachment MapAttachment(AttachmentRow row) => new(row.Id, row.PlanId, Enum.Parse<ValidationPlanAttachmentKind>(row.AttachmentKind), row.OriginalFileName, row.FileVersion, row.StorageRelativePath, row.FileLength, row.Sha256, row.UploadedBy, Utc(row.UploadedAt));
    private static ValidationPlanExecutionRecord MapExecutionRecord(ExecutionRecordRow row, IEnumerable<ExecutionItemRow> items) =>
        new(row.Id, row.PlanId, row.SourceAttachmentId, row.SourceFileName, row.OcrText, items.Select(MapExecutionItem).ToArray(), row.ConfirmedBy, Utc(row.ConfirmedAt));
    private static ValidationPlanExecutionItem MapExecutionItem(ExecutionItemRow row) =>
        new(row.Id, row.ExecutionRecordId, row.PlanItemId, row.MatchConfidence, row.SourceText, row.RecognizedResult, ToDateOnly(row.RecognizedValidationDate), row.RecognizedResponsiblePerson, row.RecognizedRemark, row.Result, ToDateOnly(row.ValidationDate), row.ResponsiblePerson, row.Remark);
    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    private static DateTime? ToDateTime(DateOnly? value) => value?.ToDateTime(TimeOnly.MinValue);
    private static DateOnly? ToDateOnly(DateTime? value) => value is null ? null : DateOnly.FromDateTime(value.Value);

    private async Task<MySqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private sealed class CategoryRow
    {
        public Guid Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public int SortOrder { get; init; }
        public bool IsActive { get; init; }
        public string? Note { get; init; }
        public int ItemCount { get; init; }
        public int ReferenceCount { get; init; }
        public string CreatedBy { get; init; } = string.Empty;
        public DateTime CreatedAt { get; init; }
        public string UpdatedBy { get; init; } = string.Empty;
        public DateTime UpdatedAt { get; init; }
        public long RowVersion { get; init; }
    }

    private sealed class ItemRow
    {
        public Guid Id { get; init; }
        public Guid CategoryId { get; init; }
        public string Content { get; init; } = string.Empty;
        public string DefaultInformationSource { get; init; } = string.Empty;
        public int SortOrder { get; init; }
        public bool IsActive { get; init; }
        public string? Note { get; init; }
        public int ReferenceCount { get; init; }
        public string CreatedBy { get; init; } = string.Empty;
        public DateTime CreatedAt { get; init; }
        public string UpdatedBy { get; init; } = string.Empty;
        public DateTime UpdatedAt { get; init; }
        public long RowVersion { get; init; }
    }

    private sealed class PlanRow
    {
        public Guid Id { get; init; }
        public Guid ProjectId { get; init; }
        public int RevisionNumber { get; init; }
        public string State { get; init; } = string.Empty;
        public string? PreparedBy { get; init; }
        public DateTime? ValidationDate { get; init; }
        public string? WorkflowCode { get; init; }
        public int? WorkflowVersion { get; init; }
        public string? SubmittedBy { get; init; }
        public DateTime? SubmittedAt { get; init; }
        public string? EffectiveBy { get; init; }
        public DateTime? EffectiveAt { get; init; }
        public string CreatedBy { get; init; } = string.Empty;
        public DateTime CreatedAt { get; init; }
        public string UpdatedBy { get; init; } = string.Empty;
        public DateTime UpdatedAt { get; init; }
        public long RowVersion { get; init; }
    }

    private sealed class PlanItemRow
    {
        public Guid Id { get; init; }
        public Guid? CatalogCategoryId { get; init; }
        public Guid? CatalogItemId { get; init; }
        public string CategoryName { get; init; } = string.Empty;
        public string ValidationContent { get; init; } = string.Empty;
        public string? InformationSource { get; init; }
        public DateTime? ValidationDate { get; init; }
        public string? Result { get; init; }
        public string? ResponsiblePerson { get; init; }
        public string? Remark { get; init; }
        public int SortOrder { get; init; }
    }

    private sealed class ApprovalTaskRow
    {
        public Guid Id { get; init; }
        public Guid PlanId { get; init; }
        public int StepOrder { get; init; }
        public string Stage { get; init; } = string.Empty;
        public string StepName { get; init; } = string.Empty;
        public string Assignee { get; init; } = string.Empty;
        public string? Decision { get; init; }
        public string? DecisionBy { get; init; }
        public string? DecisionComment { get; init; }
        public DateTime CreatedAt { get; init; }
        public DateTime? DecidedAt { get; init; }
    }

    private sealed class AttachmentRow
    {
        public Guid Id { get; init; }
        public Guid PlanId { get; init; }
        public string AttachmentKind { get; init; } = string.Empty;
        public string OriginalFileName { get; init; } = string.Empty;
        public int FileVersion { get; init; }
        public string StorageRelativePath { get; init; } = string.Empty;
        public long FileLength { get; init; }
        public string Sha256 { get; init; } = string.Empty;
        public string UploadedBy { get; init; } = string.Empty;
        public DateTime UploadedAt { get; init; }
    }

    private sealed class PendingTaskRow
    {
        public Guid Id { get; init; }
        public Guid PlanId { get; init; }
        public Guid ProjectId { get; init; }
        public int RevisionNumber { get; init; }
        public string Stage { get; init; } = string.Empty;
        public string StepName { get; init; } = string.Empty;
        public string Assignee { get; init; } = string.Empty;
        public DateTime CreatedAt { get; init; }
    }

    private sealed class ExecutionRecordRow
    {
        public Guid Id { get; init; }
        public Guid PlanId { get; init; }
        public Guid SourceAttachmentId { get; init; }
        public string SourceFileName { get; init; } = string.Empty;
        public string OcrText { get; init; } = string.Empty;
        public string ConfirmedBy { get; init; } = string.Empty;
        public DateTime ConfirmedAt { get; init; }
    }

    private sealed class ExecutionItemRow
    {
        public Guid Id { get; init; }
        public Guid ExecutionRecordId { get; init; }
        public Guid PlanItemId { get; init; }
        public decimal MatchConfidence { get; init; }
        public string SourceText { get; init; } = string.Empty;
        public string? RecognizedResult { get; init; }
        public DateTime? RecognizedValidationDate { get; init; }
        public string? RecognizedResponsiblePerson { get; init; }
        public string? RecognizedRemark { get; init; }
        public string? Result { get; init; }
        public DateTime? ValidationDate { get; init; }
        public string? ResponsiblePerson { get; init; }
        public string? Remark { get; init; }
    }

    private const string PlanSelect = "SELECT id,project_id,revision_number,state,prepared_by,validation_date,workflow_code,workflow_version,submitted_by,submitted_at,effective_by,effective_at,created_by,created_at,updated_by,updated_at,row_version FROM project_validation_plan";
}
