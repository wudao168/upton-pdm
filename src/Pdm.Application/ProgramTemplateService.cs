using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed class ProgramTemplateService(
    IProgramTemplateRepository templates,
    IPdmRepository pdmRepository,
    IProgramTemplateStorage storage,
    TimeProvider timeProvider)
{
    public async Task<IReadOnlyList<ProgramTemplate>> ListPublishedAsync(string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.ProgramTemplateView, cancellationToken);
        return await templates.ListPublishedAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ProgramTemplate>> ListMineAsync(string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.ProgramTemplateSubmit, cancellationToken);
        return await templates.ListMineAsync(actor, cancellationToken);
    }

    /// <summary>程序模板维护选项（分类、厂商、平台）：有查看权限即可读取，用于上传/编辑时选择。</summary>
    public async Task<ProgramTemplateOptionCatalog> GetOptionCatalogAsync(string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.ProgramTemplateView, cancellationToken);
        var settings = await pdmRepository.GetSystemSettingsAsync(cancellationToken);
        return settings.ProgramTemplateOptions;
    }

    /// <summary>维护程序模板选项：只有管理权限可以修改，保存后供上传与编辑草稿时选择。</summary>
    public async Task<ProgramTemplateOptionCatalog> SaveOptionCatalogAsync(ProgramTemplateOptionCatalog catalog, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.ProgramTemplateManage, cancellationToken);
        var normalized = NormalizeOptionCatalog(catalog);
        var settings = await pdmRepository.GetSystemSettingsAsync(cancellationToken);
        await pdmRepository.UpdateSystemSettingsAsync(settings with { ProgramTemplateOptions = normalized }, cancellationToken);
        await AuditAsync(actor, "program-template.options.update", nameof(ProgramTemplateOptionCatalog), Guid.Empty,
            $"分类{normalized.Categories.Count}项、厂商{normalized.Vendors.Count}项、平台{normalized.Platforms.Count}项", cancellationToken);
        return normalized;
    }

    private static ProgramTemplateOptionCatalog NormalizeOptionCatalog(ProgramTemplateOptionCatalog? catalog) => new(
        NormalizeOptionValues(catalog?.Categories, "分类"),
        NormalizeOptionValues(catalog?.Vendors, "厂商"),
        NormalizeOptionValues(catalog?.Platforms, "平台"),
        (catalog?.DisabledAssetTypes ?? [])
            .Where(value => Enum.TryParse<ProgramTemplateAssetType>(value?.Trim(), out _))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray());

    private static IReadOnlyList<string> NormalizeOptionValues(IReadOnlyList<string>? values, string label)
    {
        var normalized = new List<string>();
        foreach (var value in values ?? [])
        {
            var trimmed = value?.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;
            if (trimmed.Length > 60) throw new PdmRuleException($"{label}选项不能超过60个字符。");
            if (!normalized.Contains(trimmed, StringComparer.OrdinalIgnoreCase)) normalized.Add(trimmed);
        }
        if (normalized.Count > 200) throw new PdmRuleException($"{label}选项最多维护200项。");
        return normalized;
    }

    public async Task<ProgramTemplate> FindAsync(Guid templateId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.ProgramTemplateView, cancellationToken);
        var template = await templates.FindAsync(templateId, cancellationToken)
            ?? throw new PdmNotFoundException("程序模板不存在。");
        if (template.CurrentPublishedRevisionId is null
            && !string.Equals(template.CreatedBy, actor, StringComparison.OrdinalIgnoreCase)
            && !await HasPermissionAsync(actor, role, PermissionCodes.ProgramTemplateManage, cancellationToken))
        {
            var visibleTaskRevisionIds = new HashSet<Guid>();
            var canApproveDraft = await HasPermissionAsync(actor, role, PermissionCodes.ProgramTemplateApprove, cancellationToken);
            foreach (var roleCode in CurrentRoleCodes(role))
                foreach (var task in await templates.ListTasksAsync(actor, roleCode, canApproveDraft, cancellationToken)) visibleTaskRevisionIds.Add(task.RevisionId);
            if (!template.Revisions.Any(item => visibleTaskRevisionIds.Contains(item.Id)))
                throw new UnauthorizedAccessException("无权查看尚未发布的程序模板。");
        }
        return template;
    }

    public async Task<ProgramTemplate> CreateAsync(CreateProgramTemplateCommand command, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.ProgramTemplateSubmit, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var templateId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var revision = BuildRevision(templateId, revisionId, 1, 0, 0, 1, command, actor, now);
        var template = new ProgramTemplate(
            templateId,
            await templates.ReserveCodeAsync(command.AssetType, cancellationToken),
            command.AssetType,
            TenantContext.CompanyId,
            null,
            null,
            false,
            actor,
            now,
            [revision]);
        var created = await templates.CreateAsync(template, cancellationToken);
        await AuditAsync(actor, "program-template.create", nameof(ProgramTemplate), created.Id, created.Code, cancellationToken);
        return created;
    }

    public async Task<ProgramTemplateRevision> CreateRevisionAsync(
        Guid templateId,
        ProgramTemplateVersionBump bump,
        string actor,
        UserRole role,
        CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.ProgramTemplateSubmit, cancellationToken);
        var template = await templates.FindAsync(templateId, cancellationToken)
            ?? throw new PdmNotFoundException("程序模板不存在。");
        if (template.IsArchived) throw new PdmRuleException("已停用模板不能创建新版本。");
        if (template.Revisions.Any(item => item.State is ProgramTemplateRevisionState.Draft or ProgramTemplateRevisionState.PendingReview or ProgramTemplateRevisionState.PendingApproval))
            throw new PdmConflictException("模板已有正在维护或审批的候选版本。");
        var published = template.Revisions.FirstOrDefault(item => item.Id == template.CurrentPublishedRevisionId);
        var rejectedInitial = published is null
            ? template.Revisions.Where(item => item.State == ProgramTemplateRevisionState.Rejected)
                .OrderByDescending(item => item.CreatedAt).ThenByDescending(item => item.AttemptNumber).FirstOrDefault()
            : null;
        if (published is null && rejectedInitial is null)
            throw new PdmRuleException("模板尚无已发布版本，请继续维护首版草稿。");
        if (rejectedInitial is not null && !string.Equals(rejectedInitial.CreatedBy, actor, StringComparison.OrdinalIgnoreCase)
            && !await HasPermissionAsync(actor, role, PermissionCodes.ProgramTemplateManage, cancellationToken))
            throw new UnauthorizedAccessException("只能由原上传人根据退回意见创建修改稿。");

        var (major, minor, patch) = rejectedInitial is not null
            ? (rejectedInitial.VersionMajor, rejectedInitial.VersionMinor, rejectedInitial.VersionPatch)
            : bump switch
            {
                ProgramTemplateVersionBump.Major => (published!.VersionMajor + 1, 0, 0),
                ProgramTemplateVersionBump.Minor => (published!.VersionMajor, published.VersionMinor + 1, 0),
                _ => (published!.VersionMajor, published.VersionMinor, published.VersionPatch + 1)
            };
        var previousAttempt = template.Revisions
            .Where(item => item.VersionMajor == major && item.VersionMinor == minor && item.VersionPatch == patch)
            .OrderByDescending(item => item.AttemptNumber)
            .FirstOrDefault();
        var source = previousAttempt?.State == ProgramTemplateRevisionState.Rejected ? previousAttempt : published!;
        var revisionId = Guid.NewGuid();
        var now = timeProvider.GetUtcNow();
        var revision = new ProgramTemplateRevision(
            revisionId, template.Id, major, minor, patch, (previousAttempt?.AttemptNumber ?? 0) + 1,
            ProgramTemplateRevisionState.Draft, source.Name, source.Category, source.Description, source.Vendor,
            source.Platform, source.SoftwareVersion, source.ApplicableSeries, source.Tags.ToArray(), string.Empty,
            null, null, null, null, null, null, null, null, actor, now, null, null, 1,
            source.Parameters.Select(item => item with { Id = Guid.NewGuid(), RevisionId = revisionId }).ToArray());
        var created = await templates.CreateRevisionAsync(revision, cancellationToken);
        await AuditAsync(actor, "program-template.revision.create", nameof(ProgramTemplateRevision), created.Id, $"{template.Code} {created.VersionLabel}", cancellationToken);
        return created;
    }

    public async Task<ProgramTemplateRevision> UpdateDraftAsync(
        Guid revisionId,
        UpdateProgramTemplateDraftCommand command,
        string actor,
        UserRole role,
        CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.ProgramTemplateSubmit, cancellationToken);
        var current = await RequireEditableDraftAsync(revisionId, actor, role, cancellationToken);
        var template = await templates.FindAsync(current.TemplateId, cancellationToken)
            ?? throw new PdmNotFoundException("程序模板不存在。");
        var parameters = NormalizeDraftParameters(revisionId, command.Parameters);
        var updated = current with
        {
            Name = Optional(command.Name, 200),
            Category = Optional(command.Category, 100),
            Description = Optional(command.Description, 2000),
            Vendor = Optional(command.Vendor, 100),
            Platform = Optional(command.Platform, 100),
            SoftwareVersion = Optional(command.SoftwareVersion, 100),
            ApplicableSeries = Optional(command.ApplicableSeries, 300),
            Tags = NormalizeTags(command.Tags),
            ChangeNote = Optional(command.ChangeNote, 1000),
            Parameters = parameters
        };
        var saved = await templates.UpdateDraftAsync(updated, command.ExpectedRowVersion, cancellationToken);
        await AuditAsync(actor, "program-template.draft.update", nameof(ProgramTemplateRevision), saved.Id, saved.VersionLabel, cancellationToken);
        return saved;
    }

    public async Task DeleteDraftAsync(
        Guid revisionId,
        long expectedRowVersion,
        string actor,
        UserRole role,
        CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.ProgramTemplateSubmit, cancellationToken);
        var revision = await RequireEditableDraftAsync(revisionId, actor, role, cancellationToken);
        var template = await templates.FindAsync(revision.TemplateId, cancellationToken)
            ?? throw new PdmNotFoundException("程序模板不存在。");
        await templates.DeleteDraftAsync(revisionId, expectedRowVersion, cancellationToken);
        foreach (var file in StoredFiles(revision)) await storage.DiscardAsync(file, cancellationToken);
        await AuditAsync(actor, "program-template.draft.delete", nameof(ProgramTemplateRevision), revisionId, $"{template.Code} {revision.VersionLabel}", cancellationToken);
    }

    public async Task<ProgramTemplateUploadSession> StartUploadAsync(
        Guid revisionId,
        ProgramTemplateAttachmentKind kind,
        string fileName,
        long totalLength,
        string sha256,
        string actor,
        UserRole role,
        CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.ProgramTemplateSubmit, cancellationToken);
        var revision = await RequireEditableDraftAsync(revisionId, actor, role, cancellationToken);
        var template = await templates.FindAsync(revision.TemplateId, cancellationToken)
            ?? throw new PdmNotFoundException("程序模板不存在。");
        return await storage.StartUploadAsync(revision.Id, template.Code, kind, fileName, totalLength, sha256, actor, cancellationToken);
    }

    public Task<ProgramTemplateUploadSession> WriteChunkAsync(Guid sessionId, int chunkIndex, Stream content, string actor, CancellationToken cancellationToken) =>
        storage.WriteChunkAsync(sessionId, chunkIndex, content, actor, cancellationToken);

    public async Task<ProgramTemplateRevision> CompleteUploadAsync(
        Guid sessionId,
        long expectedRowVersion,
        string actor,
        UserRole role,
        CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.ProgramTemplateSubmit, cancellationToken);
        var file = await storage.CompleteUploadAsync(sessionId, actor, cancellationToken);
        try
        {
            await RequireEditableDraftAsync(file.RevisionId, actor, role, cancellationToken);
            var revision = await templates.AttachFileAsync(file.RevisionId, file, expectedRowVersion, cancellationToken);
            await AuditAsync(actor, "program-template.file.upload", nameof(ProgramTemplateRevision), revision.Id, $"{file.Kind} {file.OriginalFileName} {file.Sha256}", cancellationToken);
            return revision;
        }
        catch
        {
            await storage.DiscardAsync(file, cancellationToken);
            throw;
        }
    }

    public async Task<ProgramTemplateRevision> SubmitAsync(
        Guid revisionId,
        long expectedRowVersion,
        string actor,
        UserRole role,
        CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.ProgramTemplateSubmit, cancellationToken);
        var revision = await RequireEditableDraftAsync(revisionId, actor, role, cancellationToken);
        var template = await templates.FindAsync(revision.TemplateId, cancellationToken)
            ?? throw new PdmNotFoundException("程序模板不存在。");
        ValidateSubmission(template.AssetType, revision);

        var directory = await pdmRepository.GetOrganizationDirectoryAsync(cancellationToken);
        var primaryMembership = directory.Memberships.FirstOrDefault(item => item.IsPrimary && string.Equals(item.Username, actor, StringComparison.OrdinalIgnoreCase))
            ?? throw new PdmRuleException("上传人尚未配置主组织，无法确定电气审核人。");
        var reviewer = directory.Managers.FirstOrDefault(item => item.UnitId == primaryMembership.UnitId)?.PrimaryManager?.Trim();
        if (string.IsNullOrWhiteSpace(reviewer)) throw new PdmRuleException("上传人所属组织尚未配置主负责人，无法提交审核。");
        if (string.Equals(reviewer, actor, StringComparison.OrdinalIgnoreCase)) throw new PdmRuleException("上传人不能审核自己的程序模板，请调整组织主负责人。");
        var reviewerAccount = await pdmRepository.FindUserAsync(reviewer, cancellationToken)
            ?? throw new PdmRuleException("组织主负责人账号不存在。");
        if (!reviewerAccount.IsActive || !await HasPermissionAsync(reviewerAccount.Username, reviewerAccount.Role, PermissionCodes.ProgramTemplateReview, cancellationToken))
            throw new PdmRuleException("组织主负责人尚无程序模板审核权限。");

        var eligibleApprovers = new List<UserAccount>();
        foreach (var user in await pdmRepository.ListUsersAsync(cancellationToken))
        {
            if (!user.IsActive) continue;
            if (string.Equals(user.Username, actor, StringComparison.OrdinalIgnoreCase) || string.Equals(user.Username, reviewer, StringComparison.OrdinalIgnoreCase)) continue;
            // 批准池按“批准程序模板”权限判定：基础角色为标准化主管的自定义角色、被单独授权的人员都可进入，
            // 不再要求角色代码恰好等于 Approver（否则集团内只有极少数账号可用，容易整池为空）。
            if (await HasPermissionAsync(user.Username, user.Role, PermissionCodes.ProgramTemplateApprove, cancellationToken)) eligibleApprovers.Add(user);
        }
        if (eligibleApprovers.Count == 0)
            throw new PdmRuleException("集团内没有可用的标准化主管批准人：请在“角色权限设置”为至少一个启用账号授予“批准程序模板”权限，并确保该账号不是提交人本人、也不是提交人所属部门的主负责人。");

        var now = timeProvider.GetUtcNow();
        var reviewTask = new ProgramTemplateApprovalTask(
            Guid.NewGuid(), revision.Id, ProgramTemplateApprovalStage.Review, reviewer, null, null, null, null, [], now, null, 1);
        var approvalTask = new ProgramTemplateApprovalTask(
            Guid.NewGuid(), revision.Id, ProgramTemplateApprovalStage.Approval, null, UserRole.Approver.ToString(), null, null, null, [], now, null, 1);
        var submitted = await templates.SubmitAsync(revision.Id, reviewTask, approvalTask, expectedRowVersion, cancellationToken);
        await AuditAsync(actor, "program-template.submit", nameof(ProgramTemplateRevision), submitted.Id, $"{template.Code} {submitted.VersionLabel} -> {reviewer}", cancellationToken);
        return submitted;
    }

    public async Task<IReadOnlyList<ProgramTemplateApprovalTask>> ListMyTasksAsync(string actor, UserRole role, CancellationToken cancellationToken)
    {
        var tasks = new Dictionary<Guid, ProgramTemplateApprovalTask>();
        var canApprove = await HasPermissionAsync(actor, role, PermissionCodes.ProgramTemplateApprove, cancellationToken);
        foreach (var roleCode in CurrentRoleCodes(role))
            foreach (var task in await templates.ListTasksAsync(actor, roleCode, canApprove, cancellationToken)) tasks[task.Id] = task;
        return tasks.Values.OrderBy(task => task.CreatedAt).ToArray();
    }

    public async Task<ProgramTemplateDecisionResult> DecideAsync(
        Guid taskId,
        ProgramTemplateDecisionCommand command,
        string actor,
        UserRole role,
        CancellationToken cancellationToken)
    {
        var task = await templates.FindTaskAsync(taskId, cancellationToken)
            ?? throw new PdmNotFoundException("程序模板审批任务不存在。");
        var revision = await templates.FindRevisionAsync(task.RevisionId, cancellationToken)
            ?? throw new PdmNotFoundException("程序模板候选版本不存在。");
        var template = await templates.FindAsync(revision.TemplateId, cancellationToken)
            ?? throw new PdmNotFoundException("程序模板不存在。");
        if (task.Decision is not null) throw new PdmConflictException("程序模板审批任务已经处理。");
        if (string.Equals(revision.CreatedBy, actor, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("上传人不能审核或批准自己的程序模板。");

        if (task.Stage == ProgramTemplateApprovalStage.Review)
        {
            await RequirePermissionAsync(actor, role, PermissionCodes.ProgramTemplateReview, cancellationToken);
            if (!string.Equals(task.Assignee, actor, StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("该审核任务未分配给当前用户。");
            if (command.Decision == ProgramTemplateApprovalDecision.Approved)
            {
                var required = ProgramTemplateChecklist.For(template.AssetType);
                if (required.Except(command.ChecklistItems, StringComparer.Ordinal).Any()) throw new PdmRuleException("必须完成全部标准化检查项后才能通过审核。");
            }
        }
        else
        {
            await RequirePermissionAsync(actor, role, PermissionCodes.ProgramTemplateApprove, cancellationToken);
            var review = (await templates.ListRevisionTasksAsync(revision.Id, cancellationToken)).FirstOrDefault(item => item.Stage == ProgramTemplateApprovalStage.Review);
            if (review?.Decision != ProgramTemplateApprovalDecision.Approved) throw new PdmConflictException("程序模板尚未通过电气组织审核。");
            if (string.Equals(review.DecisionBy, actor, StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("审核人不能同时执行最终批准。");
        }

        if (command.Decision == ProgramTemplateApprovalDecision.Rejected && string.IsNullOrWhiteSpace(command.Comment))
            throw new PdmRuleException("退回程序模板必须填写意见。");
        var result = await templates.DecideAsync(task.Id, actor, command.Decision, Optional(command.Comment, 1000), command.ChecklistItems, command.ExpectedRowVersion, cancellationToken);
        await AuditAsync(actor, "program-template.decision", nameof(ProgramTemplateApprovalTask), task.Id, $"{task.Stage} {command.Decision} {result.Revision.VersionLabel}", cancellationToken);
        return result;
    }

    private static IReadOnlyList<string> CurrentRoleCodes(UserRole fallbackRole) =>
        TenantContext.Current?.EffectiveRoleCodes ?? [fallbackRole.ToString()];

    public async Task<ProgramTemplateDownload> OpenPublishedDownloadAsync(Guid templateId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.ProgramTemplateView, cancellationToken);
        var template = await templates.FindAsync(templateId, cancellationToken)
            ?? throw new PdmNotFoundException("程序模板不存在。");
        if (template.IsArchived || template.CurrentPublishedRevisionId is not Guid revisionId)
            throw new PdmNotFoundException("程序模板当前没有可下载的发布版本。");
        var revision = template.Revisions.FirstOrDefault(item => item.Id == revisionId)
            ?? throw new PdmNotFoundException("程序模板发布版本不存在。");
        if (revision.PackageStoragePath is null || revision.PackageFileName is null || revision.PackageFileLength is null || revision.PackageSha256 is null)
            throw new PdmConflictException("程序模板发布包记录不完整。");
        await storage.VerifyAsync(revision.PackageStoragePath, revision.PackageFileLength.Value, revision.PackageSha256, cancellationToken);
        var content = await storage.OpenReadAsync(revision.PackageStoragePath, cancellationToken);
        await AuditAsync(actor, "program-template.download", nameof(ProgramTemplateRevision), revision.Id, $"{template.Code} {revision.VersionLabel} {revision.PackageSha256}", cancellationToken);
        return new(revision.PackageFileName, revision.PackageFileLength.Value, revision.PackageSha256, content);
    }

    public async Task<ProgramTemplate> SetArchivedAsync(Guid templateId, bool archived, string reason, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.ProgramTemplateManage, cancellationToken);
        reason = Required(reason, archived ? "停用原因" : "恢复原因", 1000);
        var updated = await templates.SetArchivedAsync(templateId, archived, actor, cancellationToken);
        await AuditAsync(actor, archived ? "program-template.archive" : "program-template.restore", nameof(ProgramTemplate), templateId, reason, cancellationToken);
        return updated;
    }

    private async Task<ProgramTemplateRevision> RequireEditableDraftAsync(Guid revisionId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        var revision = await templates.FindRevisionAsync(revisionId, cancellationToken)
            ?? throw new PdmNotFoundException("程序模板候选版本不存在。");
        if (revision.State != ProgramTemplateRevisionState.Draft) throw new PdmConflictException("只有草稿版本可以修改、上传文件或删除。");
        if (!string.Equals(revision.CreatedBy, actor, StringComparison.OrdinalIgnoreCase)
            && !await HasPermissionAsync(actor, role, PermissionCodes.ProgramTemplateManage, cancellationToken))
            throw new UnauthorizedAccessException("只能维护本人创建的程序模板草稿。");
        return revision;
    }

    private static IEnumerable<StoredProgramTemplateFile> StoredFiles(ProgramTemplateRevision revision)
    {
        if (revision.PackageFileName is not null && revision.PackageStoragePath is not null && revision.PackageFileLength is long packageLength && revision.PackageSha256 is not null)
            yield return new StoredProgramTemplateFile(revision.Id, ProgramTemplateAttachmentKind.Package, revision.PackageFileName, revision.PackageStoragePath, packageLength, revision.PackageSha256, revision.CreatedAt);
        if (revision.EvidenceFileName is not null && revision.EvidenceStoragePath is not null && revision.EvidenceFileLength is long evidenceLength && revision.EvidenceSha256 is not null)
            yield return new StoredProgramTemplateFile(revision.Id, ProgramTemplateAttachmentKind.TestEvidence, revision.EvidenceFileName, revision.EvidenceStoragePath, evidenceLength, revision.EvidenceSha256, revision.CreatedAt);
    }

    private static ProgramTemplateRevision BuildRevision(
        Guid templateId,
        Guid revisionId,
        int major,
        int minor,
        int patch,
        int attempt,
        CreateProgramTemplateCommand command,
        string actor,
        DateTimeOffset now)
    {
        var parameters = NormalizeDraftParameters(revisionId, command.Parameters);
        return new ProgramTemplateRevision(
            revisionId, templateId, major, minor, patch, attempt, ProgramTemplateRevisionState.Draft,
            Optional(command.Name, 200), Optional(command.Category, 100), Optional(command.Description, 2000),
            Optional(command.Vendor, 100), Optional(command.Platform, 100), Optional(command.SoftwareVersion, 100),
            Optional(command.ApplicableSeries, 300), NormalizeTags(command.Tags), Optional(command.ChangeNote, 1000),
            null, null, null, null, null, null, null, null, actor, now, null, null, 1, parameters);
    }

    private static IReadOnlyList<ProgramTemplateParameter> NormalizeDraftParameters(
        Guid revisionId,
        IReadOnlyList<ProgramTemplateParameterInput>? inputs)
    {
        inputs ??= [];
        return inputs.Select((item, index) => new ProgramTemplateParameter(
            Guid.NewGuid(), revisionId, item.Direction, index,
            Optional(item.Name, 100), Optional(item.DataType, 100),
            Optional(item.DefaultValue, 200), Optional(item.Unit, 50), Optional(item.Description, 500))).ToArray();
    }

    private static void ValidateSubmission(ProgramTemplateAssetType assetType, ProgramTemplateRevision revision)
    {
        Required(revision.Name, "模板名称", 200);
        Required(revision.Description, "功能说明", 2000);
        Required(revision.Vendor, "厂商", 100);
        Required(revision.Platform, "平台", 100);
        Required(revision.SoftwareVersion, "软件版本", 100);
        Required(revision.ChangeNote, "版本说明", 1000);
        if (revision.PackageStoragePath is null || revision.PackageSha256 is null) throw new PdmRuleException("请先上传ZIP或RAR程序包。");
        if (revision.EvidenceStoragePath is null || revision.EvidenceSha256 is null) throw new PdmRuleException("请先上传离线测试证据。");
        if (assetType == ProgramTemplateAssetType.PlcFunctionBlock && revision.Parameters.Count == 0)
            throw new PdmRuleException("PLC功能块必须维护接口定义。");
        foreach (var parameter in revision.Parameters)
        {
            Required(parameter.Name, "接口名称", 100);
            Required(parameter.DataType, "数据类型", 100);
        }
        var duplicate = revision.Parameters.GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null) throw new PdmRuleException($"接口名称不能重复：{duplicate.Key}。");
    }

    private async Task RequirePermissionAsync(string actor, UserRole role, string permission, CancellationToken cancellationToken)
    {
        if (!await HasPermissionAsync(actor, role, permission, cancellationToken))
            throw new UnauthorizedAccessException("当前用户没有执行此操作的权限。");
    }

    private Task<bool> HasPermissionAsync(string actor, UserRole role, string permission, CancellationToken cancellationToken) =>
        pdmRepository.HasUserPermissionAsync(actor, role, permission, cancellationToken);

    private Task AuditAsync(string actor, string action, string entityType, Guid entityId, string detail, CancellationToken cancellationToken) =>
        pdmRepository.AppendAuditAsync(new AuditEntry(Guid.NewGuid(), timeProvider.GetUtcNow(), actor, action, entityType, entityId.ToString(), detail), cancellationToken);

    private static string Required(string? value, string label, int maxLength)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized)) throw new PdmRuleException($"{label}不能为空。");
        if (normalized.Length > maxLength) throw new PdmRuleException($"{label}不能超过{maxLength}个字符。");
        return normalized;
    }

    private static string Optional(string? value, int maxLength)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length > maxLength) throw new PdmRuleException($"内容不能超过{maxLength}个字符。");
        return normalized;
    }

    private static IReadOnlyList<string> NormalizeTags(IReadOnlyList<string>? tags) =>
        (tags ?? []).Select(tag => tag.Trim()).Where(tag => tag.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToArray();
}
