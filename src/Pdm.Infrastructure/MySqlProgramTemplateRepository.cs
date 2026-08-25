using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed class MySqlProgramTemplateRepository(IOptions<PdmDatabaseOptions> options, TimeProvider timeProvider) : IProgramTemplateRepository
{
    private readonly PdmDatabaseOptions settings = options.Value;
    private readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<string> ReserveCodeAsync(ProgramTemplateAssetType assetType, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var type = assetType.ToString();
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT IGNORE INTO program_template_counter(asset_type,current_value) VALUES(@Type,0)",
            new { Type = type }, transaction, cancellationToken: cancellationToken));
        var current = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT current_value FROM program_template_counter WHERE asset_type=@Type FOR UPDATE",
            new { Type = type }, transaction, cancellationToken: cancellationToken));
        var next = current + 1;
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE program_template_counter SET current_value=@Next WHERE asset_type=@Type",
            new { Type = type, Next = next }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return $"{Prefix(assetType)}-{next:D4}";
    }

    public async Task<IReadOnlyList<ProgramTemplate>> ListPublishedAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var ids = await connection.QueryAsync<Guid>(new CommandDefinition(
            "SELECT id FROM program_template WHERE is_archived=0 AND current_published_revision_id IS NOT NULL ORDER BY code",
            cancellationToken: cancellationToken));
        return await LoadTemplatesAsync(connection, ids, cancellationToken);
    }

    public async Task<IReadOnlyList<ProgramTemplate>> ListMineAsync(string actor, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var ids = await connection.QueryAsync<Guid>(new CommandDefinition(
            """
            SELECT template.id
            FROM program_template template
            WHERE EXISTS (SELECT 1 FROM program_template_revision revision WHERE revision.template_id=template.id AND revision.created_by=@Actor)
            ORDER BY template.code
            """, new { Actor = actor }, cancellationToken: cancellationToken));
        return await LoadTemplatesAsync(connection, ids, cancellationToken);
    }

    public async Task<ProgramTemplate?> FindAsync(Guid templateId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await LoadTemplateAsync(connection, null, templateId, cancellationToken);
    }

    public async Task<ProgramTemplateRevision?> FindRevisionAsync(Guid revisionId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<RevisionRow>(new CommandDefinition(
            RevisionSelect + " WHERE revision.id=@RevisionId", new { RevisionId = revisionId }, cancellationToken: cancellationToken));
        if (row is null) return null;
        var parameters = await LoadParametersAsync(connection, null, [revisionId], cancellationToken);
        return MapRevision(row, parameters.GetValueOrDefault(revisionId) ?? []);
    }

    public async Task<ProgramTemplate> CreateAsync(ProgramTemplate template, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO program_template(id,code,asset_type,origin_company_id,current_published_revision_id,is_archived,created_by,created_at)
            VALUES(@Id,@Code,@AssetType,@OriginCompanyId,NULL,0,@CreatedBy,@CreatedAt)
            """, new
            {
                template.Id, template.Code, AssetType = template.AssetType.ToString(), template.OriginCompanyId,
                template.CreatedBy, CreatedAt = template.CreatedAt.UtcDateTime
            }, transaction, cancellationToken: cancellationToken));
        foreach (var revision in template.Revisions) await InsertRevisionAsync(connection, transaction, revision, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await LoadTemplateAsync(connection, null, template.Id, cancellationToken)
            ?? throw new InvalidOperationException("程序模板创建后无法读取。");
    }

    public async Task<ProgramTemplateRevision> CreateRevisionAsync(ProgramTemplateRevision revision, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await InsertRevisionAsync(connection, transaction, revision, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await FindRevisionAsync(revision.Id, cancellationToken)
            ?? throw new InvalidOperationException("程序模板候选版本创建后无法读取。");
    }

    public async Task<ProgramTemplateRevision> UpdateDraftAsync(ProgramTemplateRevision revision, long expectedRowVersion, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE program_template_revision
            SET name=@Name,category=@Category,description=@Description,vendor=@Vendor,platform=@Platform,
                software_version=@SoftwareVersion,applicable_series=@ApplicableSeries,tags_json=@TagsJson,
                change_note=@ChangeNote,row_version=row_version+1
            WHERE id=@Id AND state='Draft' AND row_version=@ExpectedRowVersion
            """, new
            {
                revision.Id, revision.Name, revision.Category, revision.Description, revision.Vendor, revision.Platform,
                revision.SoftwareVersion, revision.ApplicableSeries, TagsJson = JsonSerializer.Serialize(revision.Tags, jsonOptions),
                revision.ChangeNote, ExpectedRowVersion = expectedRowVersion
            }, transaction, cancellationToken: cancellationToken));
        if (affected != 1) throw new PdmConflictException("程序模板草稿已被其他用户更新，请刷新后重试。");
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM program_template_parameter WHERE revision_id=@RevisionId",
            new { RevisionId = revision.Id }, transaction, cancellationToken: cancellationToken));
        await InsertParametersAsync(connection, transaction, revision.Parameters, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await FindRevisionAsync(revision.Id, cancellationToken)
            ?? throw new InvalidOperationException("程序模板草稿更新后无法读取。");
    }

    public async Task<ProgramTemplateRevision> AttachFileAsync(Guid revisionId, StoredProgramTemplateFile file, long expectedRowVersion, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var sql = file.Kind == ProgramTemplateAttachmentKind.Package
            ? """
              UPDATE program_template_revision
              SET package_file_name=@FileName,package_storage_path=@RelativePath,package_file_length=@Length,package_sha256=@Sha256,row_version=row_version+1
              WHERE id=@RevisionId AND state='Draft' AND row_version=@ExpectedRowVersion
              """
            : """
              UPDATE program_template_revision
              SET evidence_file_name=@FileName,evidence_storage_path=@RelativePath,evidence_file_length=@Length,evidence_sha256=@Sha256,row_version=row_version+1
              WHERE id=@RevisionId AND state='Draft' AND row_version=@ExpectedRowVersion
              """;
        var affected = await connection.ExecuteAsync(new CommandDefinition(sql, new
        {
            RevisionId = revisionId, FileName = file.OriginalFileName, file.RelativePath, file.Length, file.Sha256,
            ExpectedRowVersion = expectedRowVersion
        }, cancellationToken: cancellationToken));
        if (affected != 1) throw new PdmConflictException("程序模板草稿已被其他用户更新，请刷新后重试。");
        return await FindRevisionAsync(revisionId, cancellationToken)
            ?? throw new InvalidOperationException("程序模板文件关联后无法读取。");
    }

    public async Task<ProgramTemplateRevision> SubmitAsync(Guid revisionId, ProgramTemplateApprovalTask reviewTask, ProgramTemplateApprovalTask approvalTask, long expectedRowVersion, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE program_template_revision SET state='PendingReview',submitted_at=@SubmittedAt,row_version=row_version+1
            WHERE id=@RevisionId AND state='Draft' AND row_version=@ExpectedRowVersion
            """, new { RevisionId = revisionId, SubmittedAt = reviewTask.CreatedAt.UtcDateTime, ExpectedRowVersion = expectedRowVersion }, transaction, cancellationToken: cancellationToken));
        if (affected != 1) throw new PdmConflictException("程序模板草稿状态已变化，请刷新后重试。");
        await InsertTaskAsync(connection, transaction, reviewTask, cancellationToken);
        await InsertTaskAsync(connection, transaction, approvalTask, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await FindRevisionAsync(revisionId, cancellationToken)
            ?? throw new InvalidOperationException("程序模板提交后无法读取。");
    }

    public async Task<ProgramTemplateApprovalTask?> FindTaskAsync(Guid taskId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<TaskRow>(new CommandDefinition(
            TaskSelect + " WHERE task.id=@TaskId", new { TaskId = taskId }, cancellationToken: cancellationToken));
        return row is null ? null : MapTask(row);
    }

    public async Task<IReadOnlyList<ProgramTemplateApprovalTask>> ListRevisionTasksAsync(Guid revisionId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<TaskRow>(new CommandDefinition(
            TaskSelect + " WHERE task.revision_id=@RevisionId ORDER BY task.stage",
            new { RevisionId = revisionId }, cancellationToken: cancellationToken));
        return rows.Select(MapTask).ToArray();
    }

    public async Task<IReadOnlyList<ProgramTemplateApprovalTask>> ListTasksAsync(string actor, string roleCode, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<TaskRow>(new CommandDefinition(
            TaskSelect + """
             INNER JOIN program_template_revision revision ON revision.id=task.revision_id
             WHERE task.decision IS NULL AND (
                (task.stage='Review' AND revision.state='PendingReview' AND task.assignee=@Actor)
                OR (task.stage='Approval' AND revision.state='PendingApproval' AND task.assignee_role_code=@RoleCode))
             ORDER BY task.created_at
            """, new { Actor = actor, RoleCode = roleCode }, cancellationToken: cancellationToken));
        return rows.Select(MapTask).ToArray();
    }

    public async Task<ProgramTemplateDecisionResult> DecideAsync(Guid taskId, string actor, ProgramTemplateApprovalDecision decision, string? comment, IReadOnlyList<string> checklistItems, long expectedRowVersion, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var taskRow = await connection.QuerySingleOrDefaultAsync<TaskRow>(new CommandDefinition(
            TaskSelect + " WHERE task.id=@TaskId FOR UPDATE", new { TaskId = taskId }, transaction, cancellationToken: cancellationToken))
            ?? throw new PdmNotFoundException("程序模板审批任务不存在。");
        var task = MapTask(taskRow);
        if (task.RowVersion != expectedRowVersion || task.Decision is not null) throw new PdmConflictException("程序模板审批任务已被其他用户处理。");
        var revisionRow = await connection.QuerySingleOrDefaultAsync<RevisionRow>(new CommandDefinition(
            RevisionSelect + " WHERE revision.id=@RevisionId FOR UPDATE", new { RevisionId = task.RevisionId }, transaction, cancellationToken: cancellationToken))
            ?? throw new PdmNotFoundException("程序模板候选版本不存在。");
        var expectedState = task.Stage == ProgramTemplateApprovalStage.Review ? "PendingReview" : "PendingApproval";
        if (!string.Equals(revisionRow.State, expectedState, StringComparison.Ordinal)) throw new PdmConflictException("程序模板审批状态已变化。");
        var now = timeProvider.GetUtcNow();
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE program_template_approval_task
            SET decision=@Decision,decision_by=@Actor,comment=@Comment,checklist_json=@ChecklistJson,decided_at=@Now,row_version=row_version+1
            WHERE id=@TaskId AND decision IS NULL AND row_version=@ExpectedRowVersion
            """, new
            {
                TaskId = taskId, Decision = decision.ToString(), Actor = actor, Comment = comment,
                ChecklistJson = JsonSerializer.Serialize(checklistItems, jsonOptions), Now = now.UtcDateTime, ExpectedRowVersion = expectedRowVersion
            }, transaction, cancellationToken: cancellationToken));

        var targetState = decision == ProgramTemplateApprovalDecision.Rejected
            ? ProgramTemplateRevisionState.Rejected
            : task.Stage == ProgramTemplateApprovalStage.Review
                ? ProgramTemplateRevisionState.PendingApproval
                : ProgramTemplateRevisionState.Published;
        if (targetState == ProgramTemplateRevisionState.Published)
        {
            var currentPublishedId = await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
                "SELECT current_published_revision_id FROM program_template WHERE id=@TemplateId FOR UPDATE",
                new { revisionRow.TemplateId }, transaction, cancellationToken: cancellationToken));
            if (currentPublishedId is Guid previousId)
                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE program_template_revision SET state='Superseded',row_version=row_version+1 WHERE id=@PreviousId AND state='Published'",
                    new { PreviousId = previousId }, transaction, cancellationToken: cancellationToken));
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE program_template SET current_published_revision_id=@RevisionId,is_archived=0 WHERE id=@TemplateId",
                new { RevisionId = task.RevisionId, revisionRow.TemplateId }, transaction, cancellationToken: cancellationToken));
        }
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE program_template_revision
            SET state=@State,published_at=CASE WHEN @State='Published' THEN @Now ELSE published_at END,row_version=row_version+1
            WHERE id=@RevisionId AND state=@ExpectedState
            """, new { State = targetState.ToString(), Now = now.UtcDateTime, RevisionId = task.RevisionId, ExpectedState = expectedState }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return new ProgramTemplateDecisionResult(
            await FindRevisionAsync(task.RevisionId, cancellationToken) ?? throw new InvalidOperationException("审批后无法读取程序模板版本。"),
            await FindTaskAsync(taskId, cancellationToken) ?? throw new InvalidOperationException("审批后无法读取程序模板任务。"));
    }

    public async Task<ProgramTemplate> SetArchivedAsync(Guid templateId, bool archived, string actor, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE program_template SET is_archived=@Archived WHERE id=@TemplateId",
            new { TemplateId = templateId, Archived = archived }, transaction, cancellationToken: cancellationToken));
        if (affected != 1) throw new PdmNotFoundException("程序模板不存在。");
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE program_template_revision revision
            INNER JOIN program_template template ON template.current_published_revision_id=revision.id
            SET revision.state=@State,revision.row_version=revision.row_version+1
            WHERE template.id=@TemplateId
            """, new { TemplateId = templateId, State = archived ? "Archived" : "Published" }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return await FindAsync(templateId, cancellationToken) ?? throw new InvalidOperationException("程序模板状态更新后无法读取。");
    }

    private async Task<MySqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new MySqlConnection(settings.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private async Task<IReadOnlyList<ProgramTemplate>> LoadTemplatesAsync(MySqlConnection connection, IEnumerable<Guid> ids, CancellationToken cancellationToken)
    {
        var result = new List<ProgramTemplate>();
        foreach (var id in ids)
        {
            var template = await LoadTemplateAsync(connection, null, id, cancellationToken);
            if (template is not null) result.Add(template);
        }
        return result;
    }

    private async Task<ProgramTemplate?> LoadTemplateAsync(MySqlConnection connection, MySqlTransaction? transaction, Guid templateId, CancellationToken cancellationToken)
    {
        var template = await connection.QuerySingleOrDefaultAsync<TemplateRow>(new CommandDefinition(
            """
            SELECT template.id,template.code,template.asset_type AssetType,template.origin_company_id OriginCompanyId,
                   organization.name OriginCompanyName,template.current_published_revision_id CurrentPublishedRevisionId,
                   template.is_archived IsArchived,template.created_by CreatedBy,template.created_at CreatedAt
            FROM program_template template
            LEFT JOIN project_organization organization ON organization.id=template.origin_company_id
            WHERE template.id=@TemplateId
            """, new { TemplateId = templateId }, transaction, cancellationToken: cancellationToken));
        if (template is null) return null;
        var revisionRows = (await connection.QueryAsync<RevisionRow>(new CommandDefinition(
            RevisionSelect + " WHERE revision.template_id=@TemplateId ORDER BY revision.version_major DESC,revision.version_minor DESC,revision.version_patch DESC,revision.attempt_number DESC",
            new { TemplateId = templateId }, transaction, cancellationToken: cancellationToken))).ToArray();
        var parameters = await LoadParametersAsync(connection, transaction, revisionRows.Select(item => item.Id).ToArray(), cancellationToken);
        return new ProgramTemplate(
            template.Id, template.Code, Enum.Parse<ProgramTemplateAssetType>(template.AssetType), template.OriginCompanyId,
            template.OriginCompanyName, template.CurrentPublishedRevisionId, template.IsArchived, template.CreatedBy,
            template.CreatedAt, revisionRows.Select(row => MapRevision(row, parameters.GetValueOrDefault(row.Id) ?? [])).ToArray());
    }

    private async Task<Dictionary<Guid, IReadOnlyList<ProgramTemplateParameter>>> LoadParametersAsync(MySqlConnection connection, MySqlTransaction? transaction, Guid[] revisionIds, CancellationToken cancellationToken)
    {
        if (revisionIds.Length == 0) return [];
        var rows = await connection.QueryAsync<ParameterRow>(new CommandDefinition(
            """
            SELECT id,revision_id RevisionId,direction,sort_order SortOrder,name,data_type DataType,
                   default_value DefaultValue,unit,description
            FROM program_template_parameter WHERE revision_id IN @RevisionIds ORDER BY revision_id,sort_order
            """, new { RevisionIds = revisionIds }, transaction, cancellationToken: cancellationToken));
        return rows.GroupBy(item => item.RevisionId).ToDictionary(
            group => group.Key,
            group => (IReadOnlyList<ProgramTemplateParameter>)group.Select(item => new ProgramTemplateParameter(
                item.Id, item.RevisionId, Enum.Parse<ProgramTemplateParameterDirection>(item.Direction), item.SortOrder,
                item.Name, item.DataType, item.DefaultValue, item.Unit, item.Description)).ToArray());
    }

    private async Task InsertRevisionAsync(MySqlConnection connection, MySqlTransaction transaction, ProgramTemplateRevision revision, CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO program_template_revision(
                id,template_id,version_major,version_minor,version_patch,attempt_number,state,name,category,description,
                vendor,platform,software_version,applicable_series,tags_json,change_note,
                package_file_name,package_storage_path,package_file_length,package_sha256,
                evidence_file_name,evidence_storage_path,evidence_file_length,evidence_sha256,
                created_by,created_at,submitted_at,published_at,row_version)
            VALUES(
                @Id,@TemplateId,@VersionMajor,@VersionMinor,@VersionPatch,@AttemptNumber,@State,@Name,@Category,@Description,
                @Vendor,@Platform,@SoftwareVersion,@ApplicableSeries,@TagsJson,@ChangeNote,
                @PackageFileName,@PackageStoragePath,@PackageFileLength,@PackageSha256,
                @EvidenceFileName,@EvidenceStoragePath,@EvidenceFileLength,@EvidenceSha256,
                @CreatedBy,@CreatedAt,@SubmittedAt,@PublishedAt,@RowVersion)
            """, new
            {
                revision.Id, revision.TemplateId, revision.VersionMajor, revision.VersionMinor, revision.VersionPatch,
                revision.AttemptNumber, State = revision.State.ToString(), revision.Name, revision.Category, revision.Description,
                revision.Vendor, revision.Platform, revision.SoftwareVersion, revision.ApplicableSeries,
                TagsJson = JsonSerializer.Serialize(revision.Tags, jsonOptions), revision.ChangeNote,
                revision.PackageFileName, revision.PackageStoragePath, revision.PackageFileLength, revision.PackageSha256,
                revision.EvidenceFileName, revision.EvidenceStoragePath, revision.EvidenceFileLength, revision.EvidenceSha256,
                revision.CreatedBy, CreatedAt = revision.CreatedAt.UtcDateTime, SubmittedAt = revision.SubmittedAt?.UtcDateTime,
                PublishedAt = revision.PublishedAt?.UtcDateTime, revision.RowVersion
            }, transaction, cancellationToken: cancellationToken));
        await InsertParametersAsync(connection, transaction, revision.Parameters, cancellationToken);
    }

    private static Task InsertParametersAsync(MySqlConnection connection, MySqlTransaction transaction, IReadOnlyList<ProgramTemplateParameter> parameters, CancellationToken cancellationToken)
    {
        if (parameters.Count == 0) return Task.CompletedTask;
        return connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO program_template_parameter(id,revision_id,direction,sort_order,name,data_type,default_value,unit,description)
            VALUES(@Id,@RevisionId,@Direction,@SortOrder,@Name,@DataType,@DefaultValue,@Unit,@Description)
            """, parameters.Select(item => new
            {
                item.Id, item.RevisionId, Direction = item.Direction.ToString(), item.SortOrder,
                item.Name, item.DataType, item.DefaultValue, item.Unit, item.Description
            }), transaction, cancellationToken: cancellationToken));
    }

    private Task InsertTaskAsync(MySqlConnection connection, MySqlTransaction transaction, ProgramTemplateApprovalTask task, CancellationToken cancellationToken) =>
        connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO program_template_approval_task(
                id,revision_id,stage,assignee,assignee_role_code,decision,decision_by,comment,checklist_json,created_at,decided_at,row_version)
            VALUES(@Id,@RevisionId,@Stage,@Assignee,@AssigneeRoleCode,NULL,NULL,NULL,@ChecklistJson,@CreatedAt,NULL,@RowVersion)
            """, new
            {
                task.Id, task.RevisionId, Stage = task.Stage.ToString(), task.Assignee, task.AssigneeRoleCode,
                ChecklistJson = JsonSerializer.Serialize(task.ChecklistItems, jsonOptions), CreatedAt = task.CreatedAt.UtcDateTime, task.RowVersion
            }, transaction, cancellationToken: cancellationToken));

    private ProgramTemplateRevision MapRevision(RevisionRow row, IReadOnlyList<ProgramTemplateParameter> parameters) => new(
        row.Id, row.TemplateId, checked((int)row.VersionMajor), checked((int)row.VersionMinor), checked((int)row.VersionPatch), checked((int)row.AttemptNumber),
        Enum.Parse<ProgramTemplateRevisionState>(row.State), row.Name, row.Category, row.Description, row.Vendor,
        row.Platform, row.SoftwareVersion, row.ApplicableSeries,
        JsonSerializer.Deserialize<string[]>(row.TagsJson, jsonOptions) ?? [], row.ChangeNote,
        row.PackageFileName, row.PackageStoragePath, row.PackageFileLength, row.PackageSha256,
        row.EvidenceFileName, row.EvidenceStoragePath, row.EvidenceFileLength, row.EvidenceSha256,
        row.CreatedBy, row.CreatedAt, row.SubmittedAt, row.PublishedAt, row.RowVersion, parameters);

    private ProgramTemplateApprovalTask MapTask(TaskRow row) => new(
        row.Id, row.RevisionId, Enum.Parse<ProgramTemplateApprovalStage>(row.Stage), row.Assignee, row.AssigneeRoleCode,
        string.IsNullOrWhiteSpace(row.Decision) ? null : Enum.Parse<ProgramTemplateApprovalDecision>(row.Decision),
        row.DecisionBy, row.Comment, JsonSerializer.Deserialize<string[]>(row.ChecklistJson, jsonOptions) ?? [],
        row.CreatedAt, row.DecidedAt, row.RowVersion);

    private static string Prefix(ProgramTemplateAssetType type) => type switch
    {
        ProgramTemplateAssetType.PlcFunctionBlock => "PT-FB",
        ProgramTemplateAssetType.PlcProgram => "PT-PLC",
        _ => "PT-HMI"
    };

    private const string RevisionSelect = """
        SELECT revision.id,revision.template_id TemplateId,revision.version_major VersionMajor,
               revision.version_minor VersionMinor,revision.version_patch VersionPatch,revision.attempt_number AttemptNumber,
               revision.state,revision.name,revision.category,revision.description,revision.vendor,revision.platform,
               revision.software_version SoftwareVersion,revision.applicable_series ApplicableSeries,
               revision.tags_json TagsJson,revision.change_note ChangeNote,revision.package_file_name PackageFileName,
               revision.package_storage_path PackageStoragePath,revision.package_file_length PackageFileLength,
               revision.package_sha256 PackageSha256,revision.evidence_file_name EvidenceFileName,
               revision.evidence_storage_path EvidenceStoragePath,revision.evidence_file_length EvidenceFileLength,
               revision.evidence_sha256 EvidenceSha256,revision.created_by CreatedBy,revision.created_at CreatedAt,
               revision.submitted_at SubmittedAt,revision.published_at PublishedAt,revision.row_version RowVersion
        FROM program_template_revision revision
        """;

    private const string TaskSelect = """
        SELECT task.id,task.revision_id RevisionId,task.stage,task.assignee,
               task.assignee_role_code AssigneeRoleCode,task.decision,task.decision_by DecisionBy,
               task.comment,task.checklist_json ChecklistJson,task.created_at CreatedAt,
               task.decided_at DecidedAt,task.row_version RowVersion
        FROM program_template_approval_task task
        """;

    private sealed record TemplateRow(
        Guid Id, string Code, string AssetType, Guid? OriginCompanyId, string? OriginCompanyName,
        Guid? CurrentPublishedRevisionId, bool IsArchived, string CreatedBy, DateTime CreatedAt);

    private sealed record RevisionRow(
        Guid Id, Guid TemplateId, uint VersionMajor, uint VersionMinor, uint VersionPatch, uint AttemptNumber,
        string State, string Name, string Category, string Description, string Vendor, string Platform,
        string SoftwareVersion, string ApplicableSeries, string TagsJson, string ChangeNote,
        string? PackageFileName, string? PackageStoragePath, long? PackageFileLength, string? PackageSha256,
        string? EvidenceFileName, string? EvidenceStoragePath, long? EvidenceFileLength, string? EvidenceSha256,
        string CreatedBy, DateTime CreatedAt, DateTime? SubmittedAt, DateTime? PublishedAt, long RowVersion);

    private sealed record ParameterRow(
        Guid Id, Guid RevisionId, string Direction, int SortOrder, string Name, string DataType,
        string? DefaultValue, string? Unit, string? Description);

    private sealed record TaskRow(
        Guid Id, Guid RevisionId, string Stage, string? Assignee, string? AssigneeRoleCode, string? Decision,
        string? DecisionBy, string? Comment, string ChecklistJson, DateTime CreatedAt, DateTime? DecidedAt, long RowVersion);
}
