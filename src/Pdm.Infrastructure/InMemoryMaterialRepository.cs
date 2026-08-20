using System.Collections.Concurrent;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed class InMemoryMaterialRepository : IMaterialRepository
{
    private readonly object gate = new();
    private readonly ConcurrentDictionary<Guid, PdmMaterial> materials = new();
    private readonly ConcurrentDictionary<Guid, MaterialSyncTask> tasks = new();
    private readonly ConcurrentDictionary<Guid, Guid> bomLinks = new();
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
        categories["0204"] = Category("0204", "非标机加件", "02", MaterialKind.NonStandard, true, now, 24);
    }

    public Task<IReadOnlyList<PdmMaterial>> ListMaterialsAsync(string? query, string? categoryCode, bool includeArchived, int limit, CancellationToken cancellationToken)
    {
        var normalized = query?.Trim();
        var result = materials.Values
            .Where(item => includeArchived || !item.IsArchived)
            .Where(item => string.IsNullOrWhiteSpace(categoryCode) || string.Equals(item.CategoryCode, categoryCode.Trim(), StringComparison.OrdinalIgnoreCase))
            .Where(item => string.IsNullOrWhiteSpace(normalized) || new[] { item.MaterialCode, item.Name, item.Specification, item.Material, item.Brand }
                .Any(value => value?.Contains(normalized, StringComparison.OrdinalIgnoreCase) == true))
            .OrderBy(item => item.MaterialCode, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(limit, 1, 500))
            .ToArray();
        return Task.FromResult<IReadOnlyList<PdmMaterial>>(result);
    }

    public Task<PdmMaterial?> FindMaterialAsync(Guid materialId, CancellationToken cancellationToken) =>
        Task.FromResult(materials.GetValueOrDefault(materialId));

    public Task<PdmMaterial?> FindMaterialByCodeAsync(string materialCode, CancellationToken cancellationToken) =>
        Task.FromResult(materials.Values.FirstOrDefault(item => item.MaterialCode.Equals(materialCode, StringComparison.OrdinalIgnoreCase)));

    public Task<PdmMaterial?> FindMaterialBySourceBomItemAsync(Guid bomItemId, CancellationToken cancellationToken) =>
        Task.FromResult(materials.Values.FirstOrDefault(item => item.SourceBomItemId == bomItemId));

    public Task<bool> HasMaterialReferencesAsync(Guid materialId, CancellationToken cancellationToken) =>
        Task.FromResult(materials.TryGetValue(materialId, out var material)
            && (material.SourceBomItemId is not null || bomLinks.Values.Any(value => value == materialId)));

    public Task<int> CountMaterialReferencesAsync(Guid materialId, CancellationToken cancellationToken)
    {
        var sourceReference = materials.TryGetValue(materialId, out var material) && material.SourceBomItemId is not null ? 1 : 0;
        return Task.FromResult(sourceReference + bomLinks.Values.Count(value => value == materialId));
    }

    public Task<string> ReserveNextMaterialCodeAsync(MaterialCategory category, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var currentValue = materialCodeCounters.GetValueOrDefault(category.CounterScope);
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
                throw new PdmConflictException("预留的PDM物料编码已被占用，请重试。");
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

    public Task LinkBomItemAsync(Guid bomItemId, Guid materialId, string actor, DateTimeOffset linkedAt, CancellationToken cancellationToken)
    {
        bomLinks[bomItemId] = materialId;
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
                throw new PdmConflictException("U9C料品分类编码已映射到其他PDM分类。");
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
                throw new PdmRuleException("只有PDM来源且PDM主控的料品可以删除。");
            if (existing.SourceBomItemId is not null || bomLinks.Values.Any(value => value == materialId))
                throw new PdmRuleException("料品已被BOM引用或来源于BOM，不能删除；可改为停用。");
            if (tasks.Values.Any(task => task.MaterialId == materialId && task.Status == MaterialSyncStatus.Pending))
                throw new PdmRuleException("U9C同步请求正在执行，结果确认前不能删除。");
            foreach (var task in tasks.Values.Where(task => task.MaterialId == materialId).ToArray()) tasks.TryRemove(task.Id, out _);
            materials.TryRemove(materialId, out _);
            return Task.FromResult(existing);
        }
    }

    public Task<(PdmMaterial Material, MaterialSyncTask Task)> ApproveAndEnqueueAsync(Guid materialId, long expectedRowVersion, string u9CategoryCode, MaterialSyncTask task, AuditEntry audit, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!materials.TryGetValue(materialId, out var material)) throw new PdmNotFoundException("物料主档不存在。");
            if (material.RowVersion != expectedRowVersion) throw new PdmConflictException("物料主档已被其他用户修改，请刷新后重试。");
            if (material.ApprovalStatus != MaterialApprovalStatus.Draft) throw new PdmRuleException("物料主档已经批准。");
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
                RowVersion = material.RowVersion + 1
            };
            materials[materialId] = approved;
            tasks[task.Id] = task;
            return Task.FromResult((approved, task));
        }
    }

    public Task<IReadOnlyList<MaterialSyncTask>> ListSyncTasksAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<MaterialSyncTask>>(tasks.Values.OrderByDescending(task => task.CreatedAt).ToArray());

    public Task<MaterialSyncTask?> FindSyncTaskAsync(Guid taskId, CancellationToken cancellationToken) =>
        Task.FromResult(tasks.GetValueOrDefault(taskId));

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

    private static MaterialCategory Category(string code, string name, string? parentCode, MaterialKind? kind, bool allowCreate, DateTimeOffset now, int sortOrder) =>
        new(code, name, parentCode, null, kind, kind == MaterialKind.NonStandard ? MaterialSupplyMode.Manufacture : MaterialSupplyMode.Purchase,
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
