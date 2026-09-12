using System.Text;
using System.Text.RegularExpressions;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed class ValidationPlanService(
    IValidationPlanRepository validationPlans,
    IPdmRepository repository,
    IFileStorage storage,
    IValidationPlanTextRecognitionService recognition,
    TimeProvider timeProvider)
{
    private static readonly HashSet<string> InformationSources =
        ["技术协议", "技术方案", "内部评审", "客户评审"];
    private static readonly HashSet<string> PlanDocumentExtensions = [".pdf", ".png", ".jpg", ".jpeg", ".xlsx", ".xls"];
    private static readonly HashSet<string> EvidenceExtensions = [".pdf", ".png", ".jpg", ".jpeg", ".xlsx", ".xls", ".csv", ".zip"];

    public async Task<ValidationCheckCatalog> ListCatalogAsync(bool includeInactive, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, includeInactive ? PermissionCodes.ValidationCatalogManage : PermissionCodes.ProjectContentView, cancellationToken);
        return await validationPlans.ListCatalogAsync(includeInactive, cancellationToken);
    }

    public async Task<ValidationCheckCategory> SaveCategoryAsync(Guid? categoryId, SaveValidationCheckCategoryCommand command, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.ValidationCatalogManage, cancellationToken);
        var name = Required(command.Name, 150, "分类名称");
        var note = Optional(command.Note, 500, "分类备注");
        var catalog = await validationPlans.ListCatalogAsync(true, cancellationToken);
        if (catalog.Categories.Any(item => item.Id != categoryId && string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new PdmConflictException("已存在同名验证分类。");

        var now = timeProvider.GetUtcNow();
        ValidationCheckCategory saved;
        if (categoryId is null)
        {
            saved = await validationPlans.CreateCategoryAsync(new(
                Guid.NewGuid(), name, command.SortOrder, command.IsActive, note, 0, 0,
                actor, now, actor, now, 1), cancellationToken);
        }
        else
        {
            if (command.ExpectedRowVersion is null) throw new PdmRuleException("修改验证分类必须提供数据版本。");
            var current = catalog.Categories.FirstOrDefault(item => item.Id == categoryId)
                ?? throw new PdmNotFoundException("验证分类不存在。");
            saved = await validationPlans.UpdateCategoryAsync(current with
            {
                Name = name,
                SortOrder = command.SortOrder,
                IsActive = command.IsActive,
                Note = note,
                UpdatedBy = actor,
                UpdatedAt = now
            }, command.ExpectedRowVersion.Value, cancellationToken);
        }

        await AuditAsync(actor, categoryId is null ? "validation-catalog.category.create" : "validation-catalog.category.update", nameof(ValidationCheckCategory), saved.Id, $"验证分类：{saved.Name}", cancellationToken);
        return saved;
    }

    public async Task DeleteCategoryAsync(Guid categoryId, long expectedRowVersion, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.ValidationCatalogManage, cancellationToken);
        var catalog = await validationPlans.ListCatalogAsync(true, cancellationToken);
        var current = catalog.Categories.FirstOrDefault(item => item.Id == categoryId)
            ?? throw new PdmNotFoundException("验证分类不存在。");
        if (current.ItemCount > 0 || current.ReferenceCount > 0) throw new PdmConflictException("已包含检查项或已被项目引用的分类不能删除，请停用。");
        await validationPlans.DeleteCategoryAsync(categoryId, expectedRowVersion, cancellationToken);
        await AuditAsync(actor, "validation-catalog.category.delete", nameof(ValidationCheckCategory), categoryId, $"删除验证分类：{current.Name}", cancellationToken);
    }

    public async Task<ValidationCheckItem> SaveItemAsync(Guid? itemId, SaveValidationCheckItemCommand command, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.ValidationCatalogManage, cancellationToken);
        var content = Required(command.Content, 1500, "检查项内容");
        var source = NormalizeInformationSource(command.DefaultInformationSource);
        var note = Optional(command.Note, 500, "检查项备注");
        var catalog = await validationPlans.ListCatalogAsync(true, cancellationToken);
        if (catalog.Categories.All(item => item.Id != command.CategoryId)) throw new PdmNotFoundException("验证分类不存在。");
        if (catalog.Items.Any(item => item.Id != itemId && item.CategoryId == command.CategoryId && string.Equals(item.Content, content, StringComparison.OrdinalIgnoreCase)))
            throw new PdmConflictException("该分类中已存在相同检查项。");

        var now = timeProvider.GetUtcNow();
        ValidationCheckItem saved;
        if (itemId is null)
        {
            saved = await validationPlans.CreateItemAsync(new(
                Guid.NewGuid(), command.CategoryId, content, source ?? "内部评审", command.SortOrder,
                command.IsActive, note, 0, actor, now, actor, now, 1), cancellationToken);
        }
        else
        {
            if (command.ExpectedRowVersion is null) throw new PdmRuleException("修改检查项必须提供数据版本。");
            var current = catalog.Items.FirstOrDefault(item => item.Id == itemId)
                ?? throw new PdmNotFoundException("检查项不存在。");
            saved = await validationPlans.UpdateItemAsync(current with
            {
                CategoryId = command.CategoryId,
                Content = content,
                DefaultInformationSource = source ?? "内部评审",
                SortOrder = command.SortOrder,
                IsActive = command.IsActive,
                Note = note,
                UpdatedBy = actor,
                UpdatedAt = now
            }, command.ExpectedRowVersion.Value, cancellationToken);
        }

        await AuditAsync(actor, itemId is null ? "validation-catalog.item.create" : "validation-catalog.item.update", nameof(ValidationCheckItem), saved.Id, $"验证检查项：{content}", cancellationToken);
        return saved;
    }

    public async Task DeleteItemAsync(Guid itemId, long expectedRowVersion, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.ValidationCatalogManage, cancellationToken);
        var catalog = await validationPlans.ListCatalogAsync(true, cancellationToken);
        var current = catalog.Items.FirstOrDefault(item => item.Id == itemId)
            ?? throw new PdmNotFoundException("检查项不存在。");
        if (current.ReferenceCount > 0) throw new PdmConflictException("已被项目引用的检查项不能删除，请停用。");
        await validationPlans.DeleteItemAsync(itemId, expectedRowVersion, cancellationToken);
        await AuditAsync(actor, "validation-catalog.item.delete", nameof(ValidationCheckItem), itemId, $"删除验证检查项：{current.Content}", cancellationToken);
    }

    public async Task<ProjectValidationPlan?> GetPlanAsync(Guid projectId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequireProjectReadAsync(projectId, actor, role, cancellationToken);
        return await validationPlans.FindPlanAsync(projectId, cancellationToken);
    }

    public async Task<ProjectValidationPlan> SavePlanAsync(Guid projectId, SaveProjectValidationPlanCommand command, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequireProjectReadAsync(projectId, actor, role, cancellationToken);
        await RequirePermissionAsync(actor, role, PermissionCodes.ValidationPlanEdit, cancellationToken);
        var project = await repository.FindProjectAsync(projectId, cancellationToken) ?? throw new PdmNotFoundException("项目不存在。");
        var current = await validationPlans.FindPlanAsync(projectId, cancellationToken);
        if (current?.State == ProjectValidationPlanState.PendingApproval) throw new PdmConflictException("验证计划正在审批，不能修改。");
        if (current?.State is ProjectValidationPlanState.Effective or ProjectValidationPlanState.Superseded) throw new PdmConflictException("已生效验证计划不可修改，请先创建新版本。");
        if (current is not null && command.ExpectedRowVersion is null) throw new PdmRuleException("修改验证计划必须提供数据版本。");
        if (command.Items.Count > 500) throw new PdmRuleException("单个项目验证计划最多支持500条检查项。");
        var duplicate = command.Items.Where(item => item.CatalogItemId.HasValue).GroupBy(item => item.CatalogItemId).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null) throw new PdmConflictException("同一个检查项不能重复加入验证计划。");

        var catalog = await validationPlans.ListCatalogAsync(true, cancellationToken);
        var itemById = catalog.Items.ToDictionary(item => item.Id);
        var categoryById = catalog.Categories.ToDictionary(item => item.Id);
        var currentByCatalogId = current?.Items.Where(item => item.CatalogItemId.HasValue).ToDictionary(item => item.CatalogItemId!.Value) ?? [];
        var items = new List<ProjectValidationPlanItem>(command.Items.Count);
        foreach (var input in command.Items.OrderBy(item => item.SortOrder))
        {
            if (!input.CatalogItemId.HasValue)
            {
                items.Add(new(Guid.NewGuid(), null, null, "人工项", Required(input.ValidationContent, 1500, "验证内容"),
                    NormalizeInformationSource(input.InformationSource), input.ValidationDate,
                    Optional(input.Result, 1500, "验证结果"), Optional(input.ResponsiblePerson, 100, "责任人"),
                    Optional(input.Remark, 1000, "备注"), input.SortOrder));
                continue;
            }
            if (!itemById.TryGetValue(input.CatalogItemId.Value, out var catalogItem)) throw new PdmNotFoundException("所选检查项不存在。");
            if (!categoryById.TryGetValue(catalogItem.CategoryId, out var category)) throw new PdmNotFoundException("所选检查项分类不存在。");
            if ((!catalogItem.IsActive || !category.IsActive) && !currentByCatalogId.ContainsKey(catalogItem.Id))
                throw new PdmRuleException("停用的分类或检查项不能新加入验证计划。");
            var snapshot = currentByCatalogId.GetValueOrDefault(catalogItem.Id);
            items.Add(new(
                snapshot?.Id ?? Guid.NewGuid(), category.Id, catalogItem.Id,
                snapshot?.CategoryName ?? category.Name,
                snapshot?.ValidationContent ?? catalogItem.Content,
                NormalizeInformationSource(input.InformationSource), input.ValidationDate,
                Optional(input.Result, 1500, "验证结果"), Optional(input.ResponsiblePerson, 100, "责任人"),
                Optional(input.Remark, 1000, "备注"), input.SortOrder));
        }

        var now = timeProvider.GetUtcNow();
        var plan = new ProjectValidationPlan(
            current?.Id ?? Guid.NewGuid(), project.Id, current?.RevisionNumber ?? 1, current?.State ?? ProjectValidationPlanState.Draft,
            Optional(command.PreparedBy, 100, "编制人"), command.ValidationDate,
            items, current?.ApprovalTasks ?? [], current?.Attachments ?? [], current?.CreatedBy ?? actor, current?.CreatedAt ?? now, actor, now, current?.RowVersion ?? 1)
        {
            WorkflowCode = current?.WorkflowCode,
            WorkflowVersion = current?.WorkflowVersion,
            SubmittedBy = current?.SubmittedBy,
            SubmittedAt = current?.SubmittedAt,
            EffectiveBy = current?.EffectiveBy,
            EffectiveAt = current?.EffectiveAt
        };
        var saved = await validationPlans.SavePlanAsync(plan, command.ExpectedRowVersion, cancellationToken);
        await AuditAsync(actor, "project.validation-plan.save", nameof(ProjectValidationPlan), saved.Id, $"保存项目验证计划：{project.Code} · {saved.Items.Count}项", cancellationToken);
        return saved;
    }

    public async Task<ProjectValidationPlan> CreateRevisionAsync(Guid projectId, long expectedRowVersion, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequireProjectReadAsync(projectId, actor, role, cancellationToken);
        await RequirePermissionAsync(actor, role, PermissionCodes.ValidationPlanEdit, cancellationToken);
        var current = await validationPlans.FindPlanAsync(projectId, cancellationToken) ?? throw new PdmNotFoundException("项目尚未建立验证计划。");
        if (current.RowVersion != expectedRowVersion) throw new PdmConflictException("验证计划已变化，请刷新后重试。");
        if (current.State != ProjectValidationPlanState.Effective) throw new PdmConflictException("只有已生效验证计划可以创建新版本。");
        var now = timeProvider.GetUtcNow();
        var revision = new ProjectValidationPlan(Guid.NewGuid(), current.ProjectId, current.RevisionNumber + 1, ProjectValidationPlanState.Draft,
            current.PreparedBy, current.ValidationDate, current.Items.Select(item => item with { Id = Guid.NewGuid() }).ToArray(), [], [], actor, now, actor, now, 1);
        var saved = await validationPlans.CreateRevisionAsync(revision, cancellationToken);
        await AuditAsync(actor, "project.validation-plan.revision.create", nameof(ProjectValidationPlan), saved.Id, $"创建验证计划R{saved.RevisionNumber}", cancellationToken);
        return saved;
    }

    public async Task<ProjectValidationPlan> SubmitAsync(Guid projectId, long expectedRowVersion, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequireProjectReadAsync(projectId, actor, role, cancellationToken);
        await RequirePermissionAsync(actor, role, PermissionCodes.ValidationPlanEdit, cancellationToken);
        var project = await repository.FindProjectAsync(projectId, cancellationToken) ?? throw new PdmNotFoundException("项目不存在。");
        var current = await validationPlans.FindPlanAsync(projectId, cancellationToken) ?? throw new PdmNotFoundException("项目尚未建立验证计划。");
        if (current.RowVersion != expectedRowVersion || current.State is not (ProjectValidationPlanState.Draft or ProjectValidationPlanState.Rejected)) throw new PdmConflictException("验证计划状态已变化，请刷新后重试。");
        if (current.Items.Count == 0) throw new PdmRuleException("验证计划至少需要一个检查项才能提交审批。");
        if (string.IsNullOrWhiteSpace(current.PreparedBy) || current.ValidationDate is null) throw new PdmRuleException("请填写编制人和计划日期后再提交审批。");
        var settings = await repository.GetSystemSettingsAsync(cancellationToken);
        var workflow = settings.ApprovalWorkflows.ValidationPlan ?? ReleaseApprovalSettings.Default.ValidationPlan!;
        var now = timeProvider.GetUtcNow();
        var tasks = await BuildApprovalTasksAsync(current.Id, workflow, project, actor, now, cancellationToken);
        var saved = await validationPlans.SubmitAsync(current.Id, expectedRowVersion, workflow.Code, workflow.Version, tasks, actor, now, cancellationToken);
        await AuditAsync(actor, "project.validation-plan.submit", nameof(ProjectValidationPlan), saved.Id, $"提交验证计划R{saved.RevisionNumber}审批；模板v{workflow.Version}", cancellationToken);
        return saved;
    }

    public async Task<ProjectValidationPlan> DecideAsync(Guid taskId, ApprovalDecision decision, string? comment, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.ApprovalDecide, cancellationToken);
        var includeAll = string.Equals(actor, "admin", StringComparison.OrdinalIgnoreCase);
        var task = (await validationPlans.ListPendingApprovalTasksAsync(actor, includeAll, cancellationToken)).FirstOrDefault(item => item.Id == taskId)
            ?? throw new PdmRuleException("只能处理当前分配给自己的验证计划审批任务。");
        var saved = await validationPlans.DecideAsync(taskId, actor, decision, Optional(comment, 500, "审批意见"), timeProvider.GetUtcNow(), cancellationToken);
        await AuditAsync(actor, "project.validation-plan.approval.decide", nameof(ValidationPlanApprovalTask), taskId, $"R{saved.RevisionNumber}；{decision}", cancellationToken);
        return saved;
    }

    public async Task<IReadOnlyList<PendingValidationPlanApprovalTask>> ListMyApprovalTasksAsync(string actor, UserRole role, CancellationToken cancellationToken)
    {
        var includeAll = string.Equals(actor, "admin", StringComparison.OrdinalIgnoreCase)
            && await repository.HasUserPermissionAsync(actor, role, PermissionCodes.ApprovalDecide, cancellationToken);
        return await validationPlans.ListPendingApprovalTasksAsync(actor, includeAll, cancellationToken);
    }

    public async Task<UploadSession> StartAttachmentUploadAsync(Guid planId, ValidationPlanAttachmentKind kind, string fileName, long totalLength, string sha256, string actor, UserRole role, CancellationToken cancellationToken)
    {
        var plan = await RequireAttachmentAccessAsync(planId, kind, actor, role, cancellationToken);
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        var allowed = kind == ValidationPlanAttachmentKind.PlanDocument ? PlanDocumentExtensions : EvidenceExtensions;
        if (!allowed.Contains(extension)) throw new PdmRuleException(kind == ValidationPlanAttachmentKind.PlanDocument
            ? "上传验证计划仅支持PDF、图片或Excel文件。" : "佐证附件仅支持PDF、图片、Excel、CSV或ZIP文件。");
        return await storage.StartUploadAsync(plan.ProjectId, fileName, totalLength, sha256, cancellationToken);
    }

    public async Task<ValidationPlanAttachment> CompleteAttachmentUploadAsync(Guid planId, ValidationPlanAttachmentKind kind, Guid sessionId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        var plan = await RequireAttachmentAccessAsync(planId, kind, actor, role, cancellationToken);
        var session = await storage.GetUploadSessionAsync(sessionId, cancellationToken);
        if (session.ProjectId != plan.ProjectId) throw new PdmConflictException("上传会话与验证计划项目不匹配。");
        var fileVersion = plan.Attachments.Where(item => item.Kind == kind && string.Equals(item.OriginalFileName, session.FileName, StringComparison.OrdinalIgnoreCase)).Select(item => item.FileVersion).DefaultIfEmpty(0).Max() + 1;
        var id = Guid.NewGuid();
        var extension = Path.GetExtension(session.FileName);
        var baseName = Path.GetFileNameWithoutExtension(session.FileName);
        var targetName = $"{baseName}-V{fileVersion:D2}-{id:N}{extension}";
        var relativePath = Path.Combine("验收资料", "验证计划", ".versions", $"R{plan.RevisionNumber:D3}", kind.ToString(), targetName);
        var stored = await storage.CompleteUploadAsync(sessionId, relativePath, cancellationToken);
        var attachment = new ValidationPlanAttachment(id, plan.Id, kind, session.FileName, fileVersion, stored.RelativePath, stored.Length, stored.Sha256, actor, stored.StoredAt);
        var saved = await validationPlans.AddAttachmentAsync(attachment, cancellationToken);
        await AuditAsync(actor, "project.validation-plan.attachment.upload", nameof(ValidationPlanAttachment), saved.Id, $"{kind}；{saved.OriginalFileName}；V{saved.FileVersion}；SHA-256 {saved.Sha256}", cancellationToken);
        return saved;
    }

    public async Task<ValidationPlanAttachmentDownload> PrepareAttachmentDownloadAsync(Guid attachmentId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        var projects = await repository.ListProjectsForUserAsync(actor, role, cancellationToken);
        foreach (var project in projects)
        {
            var plan = await validationPlans.FindPlanAsync(project.Id, cancellationToken);
            var attachment = plan?.Attachments.FirstOrDefault(item => item.Id == attachmentId);
            if (attachment is null) continue;
            var fullPath = Path.GetFullPath(Path.Combine(project.VaultLocation, attachment.StorageRelativePath));
            var root = Path.GetFullPath(project.VaultLocation).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new PdmRuleException("验证计划附件路径无效。");
            return new(attachment, await storage.OpenReadAsync(fullPath, cancellationToken));
        }
        throw new PdmNotFoundException("验证计划附件不存在或无权访问。");
    }

    public async Task<ValidationPlanRecognitionDraft> RecognizeAttachmentAsync(Guid attachmentId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        var (plan, project, attachment) = await RequireAttachmentAsync(attachmentId, actor, role, cancellationToken);
        await RequirePermissionAsync(actor, role, PermissionCodes.ValidationPlanEdit, cancellationToken);
        if (plan.State != ProjectValidationPlanState.Effective) throw new PdmConflictException("验证计划审批完成并生效后才能识别上传文件。");
        if (attachment.Kind != ValidationPlanAttachmentKind.PlanDocument) throw new PdmRuleException("仅验证计划文件支持自动识别结果。");
        var extension = Path.GetExtension(attachment.OriginalFileName).ToLowerInvariant();
        if (extension is not (".pdf" or ".png" or ".jpg" or ".jpeg")) throw new PdmRuleException("自动识别仅支持PDF、PNG、JPG或JPEG文件。");
        var fullPath = ResolveAttachmentPath(project, attachment);
        var text = await recognition.RecognizeAsync(fullPath, cancellationToken);
        var draft = new ValidationPlanRecognitionDraft(plan.Id, attachment.Id, attachment.OriginalFileName, timeProvider.GetUtcNow(), text, BuildRecognitionCandidates(plan.Items, text));
        await AuditAsync(actor, "project.validation-plan.attachment.recognize", nameof(ValidationPlanAttachment), attachment.Id, $"识别验证计划文件；匹配{draft.Candidates.Count(item => item.MatchStatus != "Unmatched")}/{draft.Candidates.Count}项", cancellationToken);
        return draft;
    }

    public async Task<IReadOnlyList<ValidationPlanExecutionRecord>> ListExecutionRecordsAsync(Guid planId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        var plan = await validationPlans.FindPlanByIdAsync(planId, cancellationToken) ?? throw new PdmNotFoundException("验证计划不存在。");
        await RequireProjectReadAsync(plan.ProjectId, actor, role, cancellationToken);
        return await validationPlans.ListExecutionRecordsAsync(planId, cancellationToken);
    }

    public async Task<ValidationPlanExecutionRecord> ConfirmExecutionRecordAsync(Guid planId, ConfirmValidationPlanExecutionCommand command, string actor, UserRole role, CancellationToken cancellationToken)
    {
        var plan = await validationPlans.FindPlanByIdAsync(planId, cancellationToken) ?? throw new PdmNotFoundException("验证计划不存在。");
        await RequireProjectReadAsync(plan.ProjectId, actor, role, cancellationToken);
        await RequirePermissionAsync(actor, role, PermissionCodes.ValidationPlanEdit, cancellationToken);
        if (plan.State != ProjectValidationPlanState.Effective) throw new PdmConflictException("验证计划审批完成并生效后才能确认识别结果。");
        var attachment = plan.Attachments.FirstOrDefault(item => item.Id == command.SourceAttachmentId)
            ?? throw new PdmNotFoundException("识别来源文件不存在。");
        if (attachment.Kind != ValidationPlanAttachmentKind.PlanDocument) throw new PdmRuleException("识别来源必须是验证计划文件。");
        if (command.Items.Count == 0) throw new PdmRuleException("请至少选择一项识别结果进行确认。");
        if (command.Items.Count > plan.Items.Count) throw new PdmRuleException("识别结果数量超过验证计划检查项数量。");
        var duplicate = command.Items.GroupBy(item => item.PlanItemId).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null) throw new PdmConflictException("同一检查项不能重复写入执行记录。");
        var planItemIds = plan.Items.Select(item => item.Id).ToHashSet();
        var recordId = Guid.NewGuid();
        var items = command.Items.Select(item =>
        {
            if (!planItemIds.Contains(item.PlanItemId)) throw new PdmRuleException("识别结果包含不属于当前验证计划的检查项。");
            if (item.MatchConfidence is < 0 or > 1) throw new PdmRuleException("识别匹配置信度无效。");
            return new ValidationPlanExecutionItem(
                Guid.NewGuid(), recordId, item.PlanItemId, decimal.Round(item.MatchConfidence, 4),
                Optional(item.SourceText, 2000, "OCR来源文字") ?? string.Empty,
                Optional(item.RecognizedResult, 1500, "识别结果"), item.RecognizedValidationDate,
                Optional(item.RecognizedResponsiblePerson, 100, "识别责任人"), Optional(item.RecognizedRemark, 1000, "识别备注"),
                Optional(item.Result, 1500, "确认结果"), item.ValidationDate,
                Optional(item.ResponsiblePerson, 100, "确认责任人"), Optional(item.Remark, 1000, "确认备注"));
        }).ToArray();
        var record = new ValidationPlanExecutionRecord(recordId, plan.Id, attachment.Id, attachment.OriginalFileName,
            Required(command.OcrText, 100_000, "OCR原文"), items, actor, timeProvider.GetUtcNow());
        var saved = await validationPlans.AddExecutionRecordAsync(record, cancellationToken);
        await AuditAsync(actor, "project.validation-plan.execution.confirm", nameof(ValidationPlanExecutionRecord), saved.Id, $"确认验证执行记录；{attachment.OriginalFileName}；{saved.Items.Count}项", cancellationToken);
        return saved;
    }

    public async Task<ValidationPlanExportData> PrepareExportAsync(Guid projectId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequireProjectReadAsync(projectId, actor, role, cancellationToken);
        var project = await repository.FindProjectAsync(projectId, cancellationToken) ?? throw new PdmNotFoundException("项目不存在。");
        var plan = await validationPlans.FindPlanAsync(projectId, cancellationToken) ?? throw new PdmNotFoundException("项目尚未建立验证计划。");
        await AuditAsync(actor, "project.validation-plan.export", nameof(ProjectValidationPlan), plan.Id, $"导出项目验证计划：{project.Code} · {plan.Items.Count}项", cancellationToken);
        return new(project, plan, timeProvider.GetUtcNow());
    }

    private async Task RequireProjectReadAsync(Guid projectId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        if (!await repository.HasProjectContentReadAccessAsync(projectId, actor, role, cancellationToken))
            throw new UnauthorizedAccessException("无权访问该项目内容。");
    }

    private async Task RequirePermissionAsync(string actor, UserRole role, string permission, CancellationToken cancellationToken)
    {
        if (!await repository.HasUserPermissionAsync(actor, role, permission, cancellationToken))
            throw new UnauthorizedAccessException("当前角色无权执行此操作。");
    }

    private async Task<ProjectValidationPlan> RequireAttachmentAccessAsync(Guid planId, ValidationPlanAttachmentKind kind, string actor, UserRole role, CancellationToken cancellationToken)
    {
        var plan = await validationPlans.FindPlanByIdAsync(planId, cancellationToken) ?? throw new PdmNotFoundException("验证计划不存在。");
        await RequireProjectReadAsync(plan.ProjectId, actor, role, cancellationToken);
        await RequirePermissionAsync(actor, role, PermissionCodes.ValidationPlanEdit, cancellationToken);
        if (plan.State != ProjectValidationPlanState.Effective)
            throw new PdmConflictException("验证计划审批完成并生效后才能上传验证计划或附件。");
        return plan;
    }

    private async Task<(ProjectValidationPlan Plan, Project Project, ValidationPlanAttachment Attachment)> RequireAttachmentAsync(Guid attachmentId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        var projects = await repository.ListProjectsForUserAsync(actor, role, cancellationToken);
        foreach (var project in projects)
        {
            var plan = await validationPlans.FindPlanAsync(project.Id, cancellationToken);
            var attachment = plan?.Attachments.FirstOrDefault(item => item.Id == attachmentId);
            if (plan is not null && attachment is not null) return (plan, project, attachment);
        }
        throw new PdmNotFoundException("验证计划附件不存在或无权访问。");
    }

    private static string ResolveAttachmentPath(Project project, ValidationPlanAttachment attachment)
    {
        var fullPath = Path.GetFullPath(Path.Combine(project.VaultLocation, attachment.StorageRelativePath));
        var root = Path.GetFullPath(project.VaultLocation).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new PdmRuleException("验证计划附件路径无效。");
        return fullPath;
    }

    private static IReadOnlyList<ValidationPlanRecognitionCandidate> BuildRecognitionCandidates(IReadOnlyList<ProjectValidationPlanItem> items, string text)
    {
        var normalized = NormalizeWithMap(text);
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return items.OrderBy(item => item.SortOrder).Select(item =>
        {
            var target = Normalize(item.ValidationContent);
            var exactIndex = target.Length == 0 ? -1 : normalized.Text.IndexOf(target, StringComparison.OrdinalIgnoreCase);
            string source;
            decimal confidence;
            if (exactIndex >= 0)
            {
                var rawStart = normalized.RawIndexes[exactIndex];
                var mappedEnd = Math.Min(normalized.RawIndexes.Count - 1, exactIndex + target.Length + 260);
                var rawEnd = normalized.RawIndexes[mappedEnd];
                source = text.Substring(rawStart, Math.Min(1000, Math.Max(1, rawEnd - rawStart + 1))).Trim();
                confidence = 0.98m;
            }
            else
            {
                var best = lines.Select(line => new { Line = line, Score = Similarity(target, Normalize(line)) })
                    .OrderByDescending(value => value.Score).FirstOrDefault();
                source = best?.Line.Length > 1000 ? best.Line[..1000] : best?.Line ?? string.Empty;
                confidence = decimal.Round((decimal)(best?.Score ?? 0), 4);
            }
            var result = ExtractValue(source, "结果|结论", 1500) ?? ExtractResultKeyword(source);
            var date = ExtractDate(source);
            var person = ExtractValue(source, "责任人|验证人|确认人", 100);
            var remark = ExtractValue(source, "备注|说明", 1000);
            var status = confidence >= 0.75m && result is not null ? "Matched" : confidence >= 0.45m ? "Review" : "Unmatched";
            return new ValidationPlanRecognitionCandidate(item.Id, item.CategoryName, item.ValidationContent, confidence, status, result, date, person, remark, source);
        }).ToArray();
    }

    private static (string Text, IReadOnlyList<int> RawIndexes) NormalizeWithMap(string value)
    {
        var text = new StringBuilder(value.Length);
        var indexes = new List<int>(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            if (!char.IsLetterOrDigit(value[index])) continue;
            text.Append(char.ToLowerInvariant(value[index]));
            indexes.Add(index);
        }
        return (text.ToString(), indexes);
    }

    private static string Normalize(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static double Similarity(string left, string right)
    {
        if (left.Length == 0 || right.Length == 0) return 0;
        if (right.Contains(left, StringComparison.OrdinalIgnoreCase)) return 1;
        var leftPairs = Enumerable.Range(0, Math.Max(1, left.Length - 1)).Select(index => left.Substring(index, Math.Min(2, left.Length - index))).ToHashSet();
        var rightPairs = Enumerable.Range(0, Math.Max(1, right.Length - 1)).Select(index => right.Substring(index, Math.Min(2, right.Length - index))).ToHashSet();
        return leftPairs.Count == 0 ? 0 : (double)leftPairs.Count(rightPairs.Contains) / leftPairs.Count;
    }

    private static string? ExtractValue(string source, string labelPattern, int maxLength)
    {
        var match = Regex.Match(source, $@"(?:{labelPattern})\s*[:：]?\s*(.{{1,{Math.Min(maxLength, 120)}}}?)(?=\s+(?:结果|结论|验证日期|日期|责任人|验证人|确认人|备注|说明)\s*[:：]|[\r\n|｜]|$)", RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        var value = match.Groups[1].Value.Trim().Trim(',', '，', ';', '；');
        return value.Length == 0 ? null : value.Length <= maxLength ? value : value[..maxLength];
    }

    private static string? ExtractResultKeyword(string source)
    {
        var match = Regex.Match(source, @"(?:^|[\s:：,，;；])(不合格|未通过|NG|合格|通过|OK)(?:$|[\s,，;；])", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() : null;
    }

    private static DateOnly? ExtractDate(string source)
    {
        var match = Regex.Match(source, @"(?<!\d)(20\d{2})[年/\-.](\d{1,2})[月/\-.](\d{1,2})日?(?!\d)");
        if (!match.Success) return null;
        return DateOnly.TryParse($"{match.Groups[1].Value}-{match.Groups[2].Value}-{match.Groups[3].Value}", out var date) ? date : null;
    }

    private async Task<IReadOnlyList<ValidationPlanApprovalTask>> BuildApprovalTasksAsync(Guid planId, ApprovalWorkflowTemplate workflow, Project project, string actor, DateTimeOffset createdAt, CancellationToken cancellationToken)
    {
        var users = await repository.ListUsersAsync(cancellationToken);
        var directory = await repository.GetOrganizationDirectoryAsync(cancellationToken);
        var primaryMembership = directory.Memberships.FirstOrDefault(item => item.IsPrimary && string.Equals(item.Username, actor, StringComparison.OrdinalIgnoreCase));
        var primaryUnit = primaryMembership is null ? null : directory.Units.FirstOrDefault(item => item.Id == primaryMembership.UnitId && item.IsActive);
        string? UnitManager(ApprovalAssigneeSource source)
        {
            var unit = primaryUnit;
            if (unit is null) return null;
            if (source == ApprovalAssigneeSource.ParentUnitManager)
            {
                if (unit.ParentUnitId is not Guid parentId) return null;
                unit = directory.Units.FirstOrDefault(item => item.Id == parentId && item.IsActive);
            }
            return unit is null ? null : directory.Managers.FirstOrDefault(item => item.UnitId == unit.Id)?.PrimaryManager;
        }
        var result = new List<ValidationPlanApprovalTask>();
        for (var index = 0; index < workflow.Steps.Count; index++)
        {
            var step = workflow.Steps[index];
            var assignee = step.AssigneeSource switch
            {
                ApprovalAssigneeSource.Submitter => actor,
                ApprovalAssigneeSource.ProjectDesignLead => project.DesignLead,
                ApprovalAssigneeSource.FixedUser => step.FixedAssignee,
                ApprovalAssigneeSource.PrimaryUnitManager or ApprovalAssigneeSource.ParentUnitManager => UnitManager(step.AssigneeSource),
                _ => null
            };
            if (string.IsNullOrWhiteSpace(assignee)) throw new PdmRuleException($"审批模板“{workflow.Name}”的“{step.Name}”尚未配置审批人。");
            if (!users.Any(user => user.IsActive && string.Equals(user.Username, assignee, StringComparison.OrdinalIgnoreCase))) throw new PdmRuleException($"审批节点“{step.Name}”的账号{assignee}不存在或已停用。");
            result.Add(new ValidationPlanApprovalTask(Guid.NewGuid(), planId, index + 1, step.Stage, step.Name, assignee, null, null, null, createdAt, null));
        }
        return result;
    }

    private static string Required(string? value, int maxLength, string field)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0 || normalized.Length > maxLength) throw new PdmRuleException($"{field}必须为1至{maxLength}个字符。");
        return normalized;
    }

    private static string? Optional(string? value, int maxLength, string field)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized)) return null;
        if (normalized.Length > maxLength) throw new PdmRuleException($"{field}不能超过{maxLength}个字符。");
        return normalized;
    }

    private static string? NormalizeInformationSource(string? value)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized)) return null;
        if (!InformationSources.Contains(normalized)) throw new PdmRuleException("信息来源必须为技术协议、技术方案、内部评审或客户评审。");
        return normalized;
    }

    private Task AuditAsync(string actor, string action, string entityType, Guid id, string details, CancellationToken cancellationToken) =>
        repository.AppendAuditAsync(new AuditEntry(Guid.NewGuid(), timeProvider.GetUtcNow(), actor, action, entityType, id.ToString(), details), cancellationToken);
}
