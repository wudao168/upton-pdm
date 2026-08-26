using System.Collections.Concurrent;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed class InMemoryMaterialRepository : IMaterialRepository
{
    private readonly object gate = new();
    private readonly ConcurrentDictionary<Guid, PdmMaterial> materials = new();
    private readonly ConcurrentDictionary<Guid, MaterialSyncTask> tasks = new();
    private readonly ConcurrentDictionary<Guid, MaterialAttachment> attachments = new();
    private readonly ConcurrentDictionary<Guid, Guid> bomLinks = new();
    private readonly ConcurrentDictionary<Guid, MaterialCodeApplication> applications = new();
    private readonly ConcurrentDictionary<MaterialKind, MaterialCategoryRule> rules = new();
    private readonly ConcurrentDictionary<string, MaterialCategory> categories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> materialCodeCounters = new(StringComparer.OrdinalIgnoreCase);
    private U9MaterialIntegrationConfiguration configuration = new(
        "http://10.7.7.188/U9", "01", "7", "pdm", "PDM", string.Empty,
        U9MaterialContract.CreatePath, U9MaterialContract.QueryPath, false, "system", DateTimeOffset.UnixEpoch,
        UnitCodeMappings: new Dictionary<string, string>());

    public InMemoryMaterialRepository(TimeProvider timeProvider)
    {
        var now = timeProvider.GetUtcNow();
        rules[MaterialKind.Electrical] = new(MaterialKind.Electrical, "0101", "电气外购件", MaterialSupplyMode.Purchase, true, "system", now);
        rules[MaterialKind.Standard] = new(MaterialKind.Standard, "0102", "机械外购件", MaterialSupplyMode.Purchase, true, "system", now);
        rules[MaterialKind.NonStandard] = new(MaterialKind.NonStandard, "0204", "非标机加件", MaterialSupplyMode.Manufacture, true, "system", now);
        categories["01"] = Category("01", "原材料", null, null, false, now, 10);
        categories["0101"] = Category("0101", "电气外购件", "01", MaterialKind.Electrical, true, now, 11);
        categories["0102"] = Category("0102", "机械外购件", "01", MaterialKind.Standard, true, now, 12);
        categories["0104"] = Category("0104", "生产辅料", "01", null, false, now, 14);
        categories["02"] = Category("02", "半成品", null, null, false, now, 20);
        categories["0201"] = Category("0201", "夹具", "02", MaterialKind.Product, true, now, 21);
        categories["0204"] = Category("0204", "非标机加件", "02", MaterialKind.NonStandard, true, now, 24);
        categories["03"] = Category("03", "产成品", null, null, false, now, 30);
        categories["0301"] = Category("0301", "产线", "03", MaterialKind.Product, true, now, 31);
        categories["0302"] = Category("0302", "设备", "03", MaterialKind.Product, true, now, 32);
    }

    public Task<IReadOnlyList<PdmMaterial>> ListMaterialsAsync(string? query, string? categoryCode, bool includeArchived, int limit, CancellationToken cancellationToken)
    {
        var normalized = query?.Trim();
        var result = materials.Values
            .Where(item => includeArchived || !item.IsArchived)
            .Where(item => string.IsNullOrWhiteSpace(categoryCode) || string.Equals(item.CategoryCode, categoryCode.Trim(), StringComparison.OrdinalIgnoreCase))
            .Where(item => string.IsNullOrWhiteSpace(normalized) || new[] { item.MaterialCode, item.Name, item.Specification, item.Material, item.Brand }
                .Any(value => value?.Contains(normalized, StringComparison.OrdinalIgnoreCase) == true))
            .OrderByDescending(item => item.IsRecommended)
            .ThenByDescending(item => (item.SourceBomItemId is null ? 0 : 1) + bomLinks.Values.Count(value => value == item.Id))
            .ThenBy(item => item.MaterialCode, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(limit, 1, 500))
            .Select(item => item with
            {
                ReferenceCount = (item.SourceBomItemId is null ? 0 : 1) + bomLinks.Values.Count(value => value == item.Id),
                Model3DAttachmentCount = attachments.Values.Count(value => value.MaterialId == item.Id && value.Kind == MaterialAttachmentKind.Model3D),
                DocumentAttachmentCount = attachments.Values.Count(value => value.MaterialId == item.Id && value.Kind == MaterialAttachmentKind.Document)
            })
            .ToArray();
        return Task.FromResult<IReadOnlyList<PdmMaterial>>(result);
    }

    public Task<PdmMaterial?> FindMaterialAsync(Guid materialId, CancellationToken cancellationToken) =>
        Task.FromResult(materials.TryGetValue(materialId, out var material) ? WithCounts(material) : null);

    public Task<PdmMaterial?> FindMaterialByCodeAsync(string materialCode, CancellationToken cancellationToken) =>
        Task.FromResult(materials.Values.FirstOrDefault(item => item.MaterialCode.Equals(materialCode, StringComparison.OrdinalIgnoreCase)));

    public Task<PdmMaterial?> FindMaterialBySourceBomItemAsync(Guid bomItemId, CancellationToken cancellationToken) =>
        Task.FromResult(materials.Values.FirstOrDefault(item => item.SourceBomItemId == bomItemId));

    public Task<IReadOnlyList<MaterialAttachment>> ListMaterialAttachmentsAsync(Guid materialId, MaterialAttachmentKind? kind, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<MaterialAttachment>>(attachments.Values
            .Where(item => item.MaterialId == materialId && (kind is null || item.Kind == kind))
            .OrderByDescending(item => item.UploadedAt)
            .ToArray());

    public Task<MaterialAttachment?> FindMaterialAttachmentAsync(Guid attachmentId, CancellationToken cancellationToken) =>
        Task.FromResult(attachments.GetValueOrDefault(attachmentId));

    public Task<MaterialAttachment> CreateMaterialAttachmentAsync(MaterialAttachment attachment, CancellationToken cancellationToken)
    {
        if (!materials.ContainsKey(attachment.MaterialId)) throw new PdmNotFoundException("料品主档不存在。");
        if (!attachments.TryAdd(attachment.Id, attachment)) throw new PdmConflictException("料品附件已经存在。");
        return Task.FromResult(attachment);
    }

    public Task<IReadOnlyList<PdmMaterial>> FindApprovedMaterialsBySpecificationAsync(string specification, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PdmMaterial>>(materials.Values
            .Where(item => item.ApprovalStatus == MaterialApprovalStatus.Approved && !item.IsArchived)
            .Where(item => string.Equals(item.Specification?.Trim(), specification.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.MaterialCode, StringComparer.OrdinalIgnoreCase).ToArray());

    public Task<IReadOnlyList<PdmMaterial>> FindApprovedMaterialsByBrandAndSpecificationAsync(string brand, string specification, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PdmMaterial>>(materials.Values
            .Where(item => item.ApprovalStatus == MaterialApprovalStatus.Approved && !item.IsArchived)
            .Where(item => string.Equals(item.Brand?.Trim(), brand.Trim(), StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.Specification?.Trim(), specification.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.MaterialCode, StringComparer.OrdinalIgnoreCase).ToArray());

    public Task<IReadOnlyList<MaterialCodeApplication>> ListMaterialCodeApplicationsAsync(Guid? projectId, MaterialCodeApplicationStatus? status, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<MaterialCodeApplication>>(applications.Values
            .Where(item => projectId is null || item.ProjectId == projectId)
            .Where(item => status is null || item.Status == status)
            .OrderByDescending(item => item.RequestedAt).ToArray());

    public Task<MaterialCodeApplication?> FindMaterialCodeApplicationAsync(Guid applicationId, CancellationToken cancellationToken) =>
        Task.FromResult(applications.GetValueOrDefault(applicationId));

    public Task<MaterialCodeApplication?> FindPendingMaterialCodeApplicationByBomItemAsync(Guid bomItemId, CancellationToken cancellationToken) =>
        Task.FromResult(applications.Values.Where(item => item.BomItemId == bomItemId && item.Status == MaterialCodeApplicationStatus.Pending)
            .OrderByDescending(item => item.RequestedAt).FirstOrDefault());

    public Task<MaterialCodeApplication> CreateMaterialCodeApplicationAsync(MaterialCodeApplication application, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (applications.Values.Any(item => item.Status == MaterialCodeApplicationStatus.Pending
                && ((application.BomItemId is not null && item.BomItemId == application.BomItemId)
                    || (application.BomHeaderKind is not null && item.ProjectId == application.ProjectId && item.BomHeaderKind == application.BomHeaderKind))))
                throw new PdmConflictException("该BOM物料已有待审批的料号申请。");
            applications[application.Id] = application;
            return Task.FromResult(application);
        }
    }

    public Task<MaterialCodeApplication> DecideMaterialCodeApplicationAsync(Guid applicationId, long expectedRowVersion, MaterialCodeApplicationStatus status, string actor, string? comment, Guid? materialId, string? materialCode, DateTimeOffset decidedAt, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!applications.TryGetValue(applicationId, out var current)) throw new PdmNotFoundException("料号申请不存在。");
            if (current.Status != MaterialCodeApplicationStatus.Pending || current.RowVersion != expectedRowVersion)
                throw new PdmConflictException("料号申请已由其他标准化人员处理，请刷新后重试。");
            var saved = current with { Status = status, DecidedBy = actor, DecidedAt = decidedAt, DecisionComment = comment, MaterialId = materialId, MaterialCode = materialCode, RowVersion = current.RowVersion + 1 };
            applications[applicationId] = saved;
            return Task.FromResult(saved);
        }
    }

    public Task<bool> HasMaterialReferencesAsync(Guid materialId, CancellationToken cancellationToken) =>
        Task.FromResult(materials.TryGetValue(materialId, out var material)
            && (material.SourceBomItemId is not null || bomLinks.Values.Any(value => value == materialId) || attachments.Values.Any(value => value.MaterialId == materialId)));

    public Task<int> CountMaterialReferencesAsync(Guid materialId, CancellationToken cancellationToken)
    {
        var sourceReference = materials.TryGetValue(materialId, out var material) && material.SourceBomItemId is not null ? 1 : 0;
        return Task.FromResult(sourceReference + bomLinks.Values.Count(value => value == materialId) + attachments.Values.Count(value => value.MaterialId == materialId));
    }

    public Task<string> ReserveNextMaterialCodeAsync(MaterialCategory category, long minimumCurrentSequence, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var currentValue = Math.Max(materialCodeCounters.GetValueOrDefault(category.CounterScope), minimumCurrentSequence);
            var maximum = MaximumSequence(category.SequenceLength);
            if (currentValue >= maximum) throw new PdmRuleException($"分类 {category.Code} 的物料编码流水已用尽。");
            var nextValue = currentValue + 1;
            materialCodeCounters[category.CounterScope] = nextValue;
            return Task.FromResult($"{category.NumberPrefix}{nextValue.ToString($"D{category.SequenceLength}")}");
        }
    }

    public Task<PdmMaterial> CreateMaterialAsync(PdmMaterial material, MaterialCategory category, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (string.IsNullOrWhiteSpace(material.MaterialCode)) throw new PdmRuleException("物料编码尚未预留。");
            if (materials.Values.Any(item => item.MaterialCode.Equals(material.MaterialCode, StringComparison.OrdinalIgnoreCase)))
                throw new PdmConflictException("预留的PLM物料编码已被占用，请重试。");
            var saved = material with { CategoryCode = category.Code };
            materials[saved.Id] = saved;
            return Task.FromResult(saved);
        }
    }

    public Task<PdmMaterial> UpsertU9MaterialAsync(PdmMaterial material, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (material.SourceSystem != MaterialDataSource.U9C || material.MasterOwner != MaterialMasterOwner.U9C)
                throw new PdmRuleException("U9C导入料品必须标记为U9C来源和U9C主控。");
            var existing = materials.Values.FirstOrDefault(item => item.MaterialCode.Equals(material.MaterialCode, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                materials[material.Id] = material;
                return Task.FromResult(material);
            }
            if (existing.MasterOwner != MaterialMasterOwner.U9C) return Task.FromResult(existing);
            var refreshed = material with
            {
                Id = existing.Id,
                CreatedBy = existing.CreatedBy,
                CreatedAt = existing.CreatedAt,
                RowVersion = existing.RowVersion + 1,
                IsArchived = existing.IsArchived,
                ArchivedBy = existing.ArchivedBy,
                ArchivedAt = existing.ArchivedAt
            };
            materials[existing.Id] = refreshed;
            return Task.FromResult(refreshed);
        }
    }

    public Task<PdmMaterial> UpdateMaterialAsync(PdmMaterial material, long expectedRowVersion, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!materials.TryGetValue(material.Id, out var existing)) throw new PdmNotFoundException("物料主档不存在。");
            if (existing.RowVersion != expectedRowVersion) throw new PdmConflictException("物料主档已被其他用户修改，请刷新后重试。");
            var saved = material with { MaterialCode = existing.MaterialCode, RowVersion = expectedRowVersion + 1 };
            materials[material.Id] = saved;
            return Task.FromResult(saved);
        }
    }

    public Task<PdmMaterial> UpdatePlmMetadataAsync(PdmMaterial material, long expectedRowVersion, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!materials.TryGetValue(material.Id, out var existing)) throw new PdmNotFoundException("物料主档不存在。");
            if (existing.RowVersion != expectedRowVersion) throw new PdmConflictException("物料主档已被其他用户修改，请刷新后重试。");
            var saved = existing with
            {
                SelectionAdvice = material.SelectionAdvice,
                ReferencePrice = material.ReferencePrice,
                Model3DLink = material.Model3DLink,
                DocumentLink = material.DocumentLink,
                IsRecommended = material.IsRecommended,
                CoverImageAttachmentId = material.CoverImageAttachmentId,
                UpdatedBy = material.UpdatedBy,
                UpdatedAt = material.UpdatedAt,
                RowVersion = expectedRowVersion + 1
            };
            materials[material.Id] = saved;
            return Task.FromResult(saved);
        }
    }

    public Task LinkBomItemAsync(Guid bomItemId, Guid materialId, string actor, DateTimeOffset linkedAt, CancellationToken cancellationToken)
    {
        bomLinks[bomItemId] = materialId;
        return Task.CompletedTask;
    }

    public Task UnlinkBomItemAsync(Guid bomItemId, CancellationToken cancellationToken)
    {
        bomLinks.TryRemove(bomItemId, out _);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<MaterialCategory>> ListCategoriesAsync(bool includeHidden, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<MaterialCategory>>(categories.Values
            .Where(category => includeHidden || category.IsVisible)
            .Select(WithCurrentSequence)
            .OrderBy(category => category.SortOrder)
            .ThenBy(category => category.Code, StringComparer.OrdinalIgnoreCase)
            .ToArray());

    public Task<MaterialCategory?> FindCategoryAsync(string categoryCode, CancellationToken cancellationToken) =>
        Task.FromResult(categories.TryGetValue(categoryCode.Trim(), out var category) ? WithCurrentSequence(category) : null);

    public Task<MaterialCategory> SaveCategoryAsync(MaterialCategory category, long? expectedRowVersion, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (category.ParentCode is not null && !categories.ContainsKey(category.ParentCode))
                throw new PdmRuleException("上级料品分类不存在。");
            if (categories.TryGetValue(category.Code, out var existing))
            {
                if (expectedRowVersion is null || expectedRowVersion != existing.RowVersion)
                    throw new PdmConflictException("料品分类已被其他用户修改，请刷新后重试。");
                category = category with { RowVersion = existing.RowVersion + 1 };
            }
            else if (expectedRowVersion is not null)
            {
                throw new PdmNotFoundException("料品分类不存在。");
            }
            categories[category.Code] = category with { CurrentSequence = 0 };
            return Task.FromResult(WithCurrentSequence(category));
        }
    }

    public Task<MaterialCategory> AdvanceCategoryCounterAsync(MaterialCategory category, long minimumValue, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var current = materialCodeCounters.GetValueOrDefault(category.CounterScope);
            materialCodeCounters[category.CounterScope] = Math.Max(current, minimumValue);
            return Task.FromResult(WithCurrentSequence(category));
        }
    }

    public Task<IReadOnlyList<MaterialCategoryRule>> ListCategoryRulesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<MaterialCategoryRule>>(rules.Values.OrderBy(rule => rule.U9CategoryCode).ToArray());

    public Task<MaterialCategoryRule?> FindCategoryRuleAsync(MaterialKind kind, CancellationToken cancellationToken) =>
        Task.FromResult(rules.GetValueOrDefault(kind));

    public Task<MaterialCategoryRule> SaveCategoryRuleAsync(MaterialCategoryRule rule, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (rules.Values.Any(item => item.PdmKind != rule.PdmKind && item.U9CategoryCode.Equals(rule.U9CategoryCode, StringComparison.OrdinalIgnoreCase)))
                throw new PdmConflictException("U9C料品分类编码已映射到其他PLM分类。");
            rules[rule.PdmKind] = rule;
            if (categories.TryGetValue(rule.U9CategoryCode, out var category))
            {
                categories[rule.U9CategoryCode] = category with
                {
                    Name = rule.U9CategoryName,
                    PdmKind = rule.PdmKind,
                    DefaultSupplyMode = rule.DefaultSupplyMode,
                    AllowCreate = rule.IsEnabled,
                    UpdatedBy = rule.UpdatedBy,
                    UpdatedAt = rule.UpdatedAt,
                    RowVersion = category.RowVersion + 1
                };
            }
            return Task.FromResult(rule);
        }
    }

    public Task<(PdmMaterial Material, MaterialSyncTask Task)> UpdateAndEnqueueAsync(
        PdmMaterial material,
        long expectedRowVersion,
        MaterialSyncTask task,
        AuditEntry audit,
        CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!materials.TryGetValue(material.Id, out var existing)) throw new PdmNotFoundException("物料主档不存在。");
            if (existing.RowVersion != expectedRowVersion) throw new PdmConflictException("物料主档已被其他用户修改，请刷新后重试。");
            if (existing.ApprovalStatus != MaterialApprovalStatus.Approved
                || existing.SyncStatus == MaterialSyncStatus.Pending
                || existing.IsArchived
                || tasks.Values.Any(item => item.MaterialId == material.Id && item.Status == MaterialSyncStatus.Pending))
                throw new PdmRuleException("料品不是可变更状态，请刷新后重试。");
            if (tasks.Values.Any(item => item.MaterialId == task.MaterialId && item.Operation == task.Operation && item.PayloadSha256 == task.PayloadSha256))
                throw new PdmConflictException("相同内容的U9C同步任务已经存在。");
            foreach (var obsolete in tasks.Values.Where(item => item.MaterialId == material.Id && item.Status != MaterialSyncStatus.Succeeded).ToArray())
            {
                tasks[obsolete.Id] = obsolete with
                {
                    Status = MaterialSyncStatus.Superseded,
                    NextAttemptAt = null,
                    LastError = "料品已编辑，旧请求已废止。",
                    UpdatedAt = audit.OccurredAt
                };
            }
            var saved = material with
            {
                MaterialCode = existing.MaterialCode,
                ApprovalStatus = existing.ApprovalStatus,
                ApprovedBy = existing.ApprovedBy,
                ApprovedAt = existing.ApprovedAt,
                U9CategoryCode = material.CategoryCode,
                U9ItemId = existing.U9ItemId,
                U9ItemCode = existing.U9ItemCode,
                SyncStatus = MaterialSyncStatus.PreviewReady,
                RowVersion = existing.RowVersion + 1
            };
            materials[saved.Id] = saved;
            tasks[task.Id] = task;
            return Task.FromResult((saved, task));
        }
    }

    public Task<PdmMaterial> ArchiveMaterialAsync(Guid materialId, long expectedRowVersion, string actor, DateTimeOffset archivedAt, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!materials.TryGetValue(materialId, out var existing)) throw new PdmNotFoundException("物料主档不存在。");
            if (existing.RowVersion != expectedRowVersion) throw new PdmConflictException("物料主档已被其他用户修改，请刷新后重试。");
            if (existing.IsArchived) throw new PdmRuleException("料品已经归档。");
            var archived = existing with
            {
                IsArchived = true,
                ArchivedBy = actor,
                ArchivedAt = archivedAt,
                UpdatedBy = actor,
                UpdatedAt = archivedAt,
                RowVersion = existing.RowVersion + 1
            };
            materials[materialId] = archived;
            return Task.FromResult(archived);
        }
    }

    public Task<PdmMaterial> DeleteLocalMaterialAsync(Guid materialId, long expectedRowVersion, bool u9AbsenceConfirmed, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!u9AbsenceConfirmed) throw new PdmRuleException("尚未实时确认U9C不存在，不能删除料品。");
            if (!materials.TryGetValue(materialId, out var existing)) throw new PdmNotFoundException("物料主档不存在。");
            if (existing.RowVersion != expectedRowVersion) throw new PdmConflictException("物料主档已被其他用户修改，请刷新后重试。");
            if (existing.SourceSystem != MaterialDataSource.Pdm || existing.MasterOwner != MaterialMasterOwner.Pdm)
                throw new PdmRuleException("只有PLM来源且PLM主控的料品可以删除。");
            if (existing.SourceBomItemId is not null || bomLinks.Values.Any(value => value == materialId))
                throw new PdmRuleException("料品已被BOM引用或来源于BOM，不能删除；可改为停用。");
            if (tasks.Values.Any(task => task.MaterialId == materialId && task.Status == MaterialSyncStatus.Pending))
                throw new PdmRuleException("U9C同步请求正在执行，结果确认前不能删除。");
            foreach (var task in tasks.Values.Where(task => task.MaterialId == materialId).ToArray()) tasks.TryRemove(task.Id, out _);
            materials.TryRemove(materialId, out _);
            return Task.FromResult(existing);
        }
    }

    public Task<(PdmMaterial Material, MaterialSyncTask Task)> ApproveAndEnqueueAsync(PdmMaterial material, long expectedRowVersion, string u9CategoryCode, MaterialSyncTask task, AuditEntry audit, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!materials.TryGetValue(material.Id, out var current)) throw new PdmNotFoundException("物料主档不存在。");
            if (current.RowVersion != expectedRowVersion) throw new PdmConflictException("物料主档已被其他用户修改，请刷新后重试。");
            if (current.ApprovalStatus != MaterialApprovalStatus.Draft) throw new PdmRuleException("物料主档已经批准。");
            if (materials.Values.Any(existing => existing.Id != material.Id
                && existing.MaterialCode.Equals(material.MaterialCode, StringComparison.OrdinalIgnoreCase)))
                throw new PdmConflictException("PLM物料编码已存在。");
            if (tasks.Values.Any(existing => existing.MaterialId == task.MaterialId && existing.Operation == task.Operation && existing.PayloadSha256 == task.PayloadSha256))
                throw new PdmConflictException("相同内容的U9C同步任务已经存在。");
            var approved = material with
            {
                ApprovalStatus = MaterialApprovalStatus.Approved,
                ApprovedBy = audit.Actor,
                ApprovedAt = audit.OccurredAt,
                U9CategoryCode = u9CategoryCode,
                SyncStatus = MaterialSyncStatus.PreviewReady,
                UpdatedBy = audit.Actor,
                UpdatedAt = audit.OccurredAt,
                RowVersion = current.RowVersion + 1
            };
            materials[material.Id] = approved;
            tasks[task.Id] = task;
            return Task.FromResult((approved, task));
        }
    }

    public Task<IReadOnlyList<MaterialSyncTask>> ListSyncTasksAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<MaterialSyncTask>>(tasks.Values.OrderByDescending(task => task.CreatedAt).Select(EnrichTask).ToArray());

    public Task<MaterialSyncTask?> FindSyncTaskAsync(Guid taskId, CancellationToken cancellationToken) =>
        Task.FromResult(tasks.TryGetValue(taskId, out var task) ? EnrichTask(task) : null);

    private MaterialSyncTask EnrichTask(MaterialSyncTask task)
    {
        var material = materials.GetValueOrDefault(task.MaterialId);
        var application = applications.Values
            .Where(item => item.MaterialId == task.MaterialId)
            .OrderByDescending(item => item.RequestedAt)
            .FirstOrDefault();
        return task with
        {
            MaterialCode = material?.MaterialCode,
            MaterialName = material?.Name,
            CategoryCode = material?.CategoryCode,
            ProjectId = application?.ProjectId,
            ProjectCode = application?.ProjectCode,
            ProjectName = application?.ProjectName,
            BomHeaderKind = application?.BomHeaderKind,
            RequestedBy = application?.RequestedBy ?? material?.CreatedBy,
            RequestedAt = application?.RequestedAt ?? material?.CreatedAt
        };
    }

    public Task<MaterialSyncTask> RetrySyncTaskAsync(
        Guid taskId,
        string payloadJson,
        string payloadSha256,
        DateTimeOffset retriedAt,
        CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!tasks.TryGetValue(taskId, out var task)) throw new PdmNotFoundException("U9C同步任务不存在。");
            if (task.Status is not (MaterialSyncStatus.PreviewReady or MaterialSyncStatus.Failed or MaterialSyncStatus.NeedsReview))
                throw new PdmRuleException(task.Status == MaterialSyncStatus.Superseded
                    ? "料品已编辑，旧同步任务已废止，请使用最新请求。"
                    : "当前状态的同步任务不能重试。");
            var retried = task with
            {
                Status = MaterialSyncStatus.PreviewReady,
                PayloadJson = payloadJson,
                PayloadSha256 = payloadSha256,
                NextAttemptAt = null,
                LastError = null,
                ResponsePreview = null,
                UpdatedAt = retriedAt
            };
            tasks[taskId] = retried;
            return Task.FromResult(retried);
        }
    }

    public Task<MaterialSyncTask> BeginSyncTaskAsync(Guid taskId, DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!tasks.TryGetValue(taskId, out var task)) throw new PdmNotFoundException("U9C同步任务不存在。");
            if (task.Status is not (MaterialSyncStatus.PreviewReady or MaterialSyncStatus.Failed or MaterialSyncStatus.NeedsReview))
                throw new PdmRuleException(task.Status switch
                {
                    MaterialSyncStatus.Succeeded => "已成功的同步任务不能重复执行。",
                    MaterialSyncStatus.Superseded => "料品已编辑，旧同步任务已废止，请使用最新请求。",
                    _ => "U9C同步任务正在执行。"
                });
            var started = task with
            {
                Status = MaterialSyncStatus.Pending,
                AttemptCount = task.AttemptCount + 1,
                NextAttemptAt = null,
                LastError = null,
                UpdatedAt = startedAt
            };
            tasks[taskId] = started;
            if (!materials.TryGetValue(task.MaterialId, out var material)) throw new PdmNotFoundException("同步任务对应的物料主档不存在。");
            materials[material.Id] = material with { SyncStatus = MaterialSyncStatus.Pending };
            return Task.FromResult(started);
        }
    }

    public Task<(PdmMaterial Material, MaterialSyncTask Task)> CompleteSyncTaskAsync(
        Guid taskId,
        string? u9ItemId,
        string u9ItemCode,
        string responsePreview,
        AuditEntry audit,
        CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!tasks.TryGetValue(taskId, out var task)) throw new PdmNotFoundException("U9C同步任务不存在。");
            if (task.Status != MaterialSyncStatus.Pending) throw new PdmRuleException("只有执行中的U9C同步任务才能完成。");
            if (!materials.TryGetValue(task.MaterialId, out var material)) throw new PdmNotFoundException("同步任务对应的物料主档不存在。");
            var completedTask = task with
            {
                Status = MaterialSyncStatus.Succeeded,
                LastError = null,
                ResponsePreview = responsePreview,
                U9ItemId = u9ItemId,
                U9ItemCode = u9ItemCode,
                UpdatedAt = audit.OccurredAt
            };
            var completedMaterial = material with
            {
                U9ItemId = u9ItemId,
                U9ItemCode = u9ItemCode,
                SyncStatus = MaterialSyncStatus.Succeeded,
                U9SyncConfirmed = true,
                UpdatedBy = audit.Actor,
                UpdatedAt = audit.OccurredAt,
                RowVersion = material.RowVersion + 1
            };
            tasks[taskId] = completedTask;
            materials[material.Id] = completedMaterial;
            return Task.FromResult((completedMaterial, completedTask));
        }
    }

    public Task<MaterialSyncTask> FailSyncTaskAsync(
        Guid taskId,
        MaterialSyncStatus status,
        string error,
        string? responsePreview,
        AuditEntry audit,
        CancellationToken cancellationToken)
    {
        if (status is not (MaterialSyncStatus.Failed or MaterialSyncStatus.NeedsReview))
            throw new ArgumentOutOfRangeException(nameof(status));
        lock (gate)
        {
            if (!tasks.TryGetValue(taskId, out var task)) throw new PdmNotFoundException("U9C同步任务不存在。");
            if (task.Status != MaterialSyncStatus.Pending) throw new PdmRuleException("只有执行中的U9C同步任务才能记录失败。");
            if (!materials.TryGetValue(task.MaterialId, out var material)) throw new PdmNotFoundException("同步任务对应的物料主档不存在。");
            var failed = task with
            {
                Status = status,
                LastError = error,
                ResponsePreview = responsePreview,
                UpdatedAt = audit.OccurredAt
            };
            tasks[taskId] = failed;
            materials[material.Id] = material with
            {
                SyncStatus = status,
                UpdatedBy = audit.Actor,
                UpdatedAt = audit.OccurredAt,
                RowVersion = material.RowVersion + 1
            };
            return Task.FromResult(failed);
        }
    }

    public Task<U9MaterialIntegrationConfiguration> GetIntegrationConfigurationAsync(CancellationToken cancellationToken) => Task.FromResult(configuration);

    public Task<U9MaterialIntegrationConfiguration> SaveIntegrationConfigurationAsync(U9MaterialIntegrationConfiguration value, CancellationToken cancellationToken)
    {
        configuration = value;
        return Task.FromResult(configuration);
    }

    private PdmMaterial WithCounts(PdmMaterial material) => material with
    {
        ReferenceCount = (material.SourceBomItemId is null ? 0 : 1) + bomLinks.Values.Count(value => value == material.Id),
        Model3DAttachmentCount = attachments.Values.Count(value => value.MaterialId == material.Id && value.Kind == MaterialAttachmentKind.Model3D),
        DocumentAttachmentCount = attachments.Values.Count(value => value.MaterialId == material.Id && value.Kind == MaterialAttachmentKind.Document)
    };

    private static MaterialCategory Category(string code, string name, string? parentCode, MaterialKind? kind, bool allowCreate, DateTimeOffset now, int sortOrder) =>
        new(code, name, parentCode, null, kind, kind is MaterialKind.NonStandard or MaterialKind.Product ? MaterialSupplyMode.Manufacture : MaterialSupplyMode.Purchase,
            allowCreate, true, true, code, 7, code, sortOrder, "system", now, 1);

    private static long MaximumSequence(int sequenceLength) =>
        checked((long)Math.Pow(10, sequenceLength) - 1);

    private MaterialCategory WithCurrentSequence(MaterialCategory category)
    {
        lock (gate)
        {
            return category with { CurrentSequence = materialCodeCounters.GetValueOrDefault(category.CounterScope) };
        }
    }
}
