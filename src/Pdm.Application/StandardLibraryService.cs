using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed class StandardLibraryService(
    IStandardLibraryRepository standardLibrary,
    IMaterialRepository materials,
    IPdmRepository repository,
    TimeProvider timeProvider)
{
    public async Task<IReadOnlyList<StandardLibraryCategory>> ListCategoriesAsync(
        bool includeInactive, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequireAsync(actor, role, includeInactive ? PermissionCodes.StandardLibraryManage : PermissionCodes.StandardLibraryView, cancellationToken);
        return await standardLibrary.ListCategoriesAsync(includeInactive, cancellationToken);
    }

    public async Task<StandardLibraryCategory> SaveCategoryAsync(
        Guid? categoryId,
        SaveStandardLibraryCategoryCommand command,
        string actor,
        UserRole role,
        CancellationToken cancellationToken)
    {
        await RequireAsync(actor, role, PermissionCodes.StandardLibraryManage, cancellationToken);
        var name = command.Name?.Trim() ?? string.Empty;
        if (name.Length is < 1 or > 150) throw new PdmRuleException("标准分类名称必须为1至150个字符。");
        var all = await standardLibrary.ListCategoriesAsync(true, cancellationToken);
        if (command.ParentId is not null && all.All(item => item.Id != command.ParentId)) throw new PdmNotFoundException("上级标准分类不存在。");
        if (categoryId is not null && command.ParentId == categoryId) throw new PdmRuleException("标准分类不能将自己设为上级。");
        if (all.Any(item => item.Id != categoryId && item.ParentId == command.ParentId && string.Equals(item.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)))
            throw new PdmConflictException("同一上级下已存在同名标准分类。");
        ValidateHierarchy(all, categoryId, command.ParentId);

        var now = timeProvider.GetUtcNow();
        StandardLibraryCategory saved;
        if (categoryId is null)
        {
            saved = await standardLibrary.CreateCategoryAsync(new(
                Guid.NewGuid(), name, command.ParentId, command.SortOrder, command.IsActive,
                actor, now, actor, now, 1), cancellationToken);
        }
        else
        {
            if (command.ExpectedRowVersion is null) throw new PdmRuleException("修改标准分类必须提供数据版本。");
            var existing = all.FirstOrDefault(item => item.Id == categoryId) ?? throw new PdmNotFoundException("标准分类不存在。");
            saved = await standardLibrary.UpdateCategoryAsync(existing with
            {
                Name = name,
                ParentId = command.ParentId,
                SortOrder = command.SortOrder,
                IsActive = command.IsActive,
                UpdatedBy = actor,
                UpdatedAt = now
            }, command.ExpectedRowVersion.Value, cancellationToken);
        }
        await AuditAsync(actor, categoryId is null ? "standard-library.category.create" : "standard-library.category.update", saved.Id,
            $"标准分类：{saved.Name}", cancellationToken);
        return saved;
    }

    public async Task DeleteCategoryAsync(Guid categoryId, long expectedRowVersion, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequireAsync(actor, role, PermissionCodes.StandardLibraryManage, cancellationToken);
        await standardLibrary.DeleteCategoryAsync(categoryId, expectedRowVersion, cancellationToken);
        await AuditAsync(actor, "standard-library.category.delete", categoryId, "删除空标准分类", cancellationToken);
    }

    public async Task<StandardLibraryMaterialPage> ListMaterialsAsync(
        StandardLibraryQuery query,
        string actor,
        UserRole role,
        CancellationToken cancellationToken)
    {
        await RequireAsync(actor, role, PermissionCodes.StandardLibraryView, cancellationToken);
        var categories = await standardLibrary.ListCategoriesAsync(true, cancellationToken);
        var memberships = await standardLibrary.ListMembershipsAsync(cancellationToken);
        var scope = query.CategoryId is null ? null : DescendantIds(categories, query.CategoryId.Value);
        var scoped = memberships.Where(item => scope is null || scope.Contains(item.CategoryId)).ToArray();
        var categoryById = categories.ToDictionary(item => item.Id);
        var normalized = query.Query?.Trim();
        var rows = new List<StandardLibraryMaterial>();
        foreach (var group in scoped.GroupBy(item => item.MaterialId))
        {
            var material = await materials.FindMaterialAsync(group.Key, cancellationToken);
            if (material is null || material.IsArchived || material.ApprovalStatus != MaterialApprovalStatus.Approved || !material.U9SyncConfirmed) continue;
            if (query.RecommendedOnly && !material.IsRecommended) continue;
            if (!string.IsNullOrWhiteSpace(query.Brand) && !string.Equals(material.Brand?.Trim(), query.Brand.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrWhiteSpace(normalized) && !new[] { material.Name, material.MaterialCode, material.Specification, material.Brand }
                    .Any(value => value?.Contains(normalized, StringComparison.OrdinalIgnoreCase) == true)) continue;
            var rowCategories = group.Select(item => categoryById.GetValueOrDefault(item.CategoryId)).Where(item => item is not null).Cast<StandardLibraryCategory>()
                .OrderBy(item => item.SortOrder).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            var cover = material.CoverImageAttachmentId is null
                ? null
                : await materials.FindMaterialAttachmentAsync(material.CoverImageAttachmentId.Value, cancellationToken);
            rows.Add(new(material, rowCategories, cover));
        }

        var ordered = rows.OrderByDescending(item => item.Material.IsRecommended)
            .ThenByDescending(item => item.Material.ReferenceCount)
            .ThenBy(item => item.Material.MaterialCode, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);
        return new(ordered.Skip((page - 1) * pageSize).Take(pageSize).ToArray(), ordered.Length, page, pageSize);
    }

    public async Task AddMaterialsAsync(AddStandardLibraryMaterialsCommand command, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequireAsync(actor, role, PermissionCodes.StandardLibraryManage, cancellationToken);
        var categoryIds = command.CategoryIds.Distinct().ToArray();
        var materialIds = command.MaterialIds.Distinct().ToArray();
        if (categoryIds.Length == 0 || materialIds.Length == 0) throw new PdmRuleException("请选择标准分类和料品。");
        var categories = await standardLibrary.ListCategoriesAsync(false, cancellationToken);
        if (categoryIds.Any(id => categories.All(item => item.Id != id))) throw new PdmRuleException("只能加入已启用的标准分类。");
        foreach (var materialId in materialIds)
        {
            var material = await materials.FindMaterialAsync(materialId, cancellationToken) ?? throw new PdmNotFoundException("待加入料品不存在。");
            if (material.IsArchived || material.ApprovalStatus != MaterialApprovalStatus.Approved || !material.U9SyncConfirmed)
                throw new PdmRuleException($"料品 {material.MaterialCode} 尚未满足“未停用、已批准、U9C已确认”的入库条件。");
        }
        var now = timeProvider.GetUtcNow();
        await standardLibrary.AddMembershipsAsync(
            (from categoryId in categoryIds from materialId in materialIds select new StandardLibraryMembership(categoryId, materialId, actor, now)).ToArray(),
            cancellationToken);
        await AuditAsync(actor, "standard-library.membership.add", Guid.Empty, $"加入 {materialIds.Length} 个料品到 {categoryIds.Length} 个标准分类", cancellationToken);
    }

    public async Task RemoveMaterialAsync(Guid categoryId, Guid materialId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequireAsync(actor, role, PermissionCodes.StandardLibraryManage, cancellationToken);
        await standardLibrary.RemoveMembershipAsync(categoryId, materialId, cancellationToken);
        await AuditAsync(actor, "standard-library.membership.remove", materialId, $"从标准分类 {categoryId} 移出料品", cancellationToken);
    }

    public async Task<PdmMaterial> SetRecommendedAsync(Guid materialId, bool isRecommended, long expectedRowVersion, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequireAsync(actor, role, PermissionCodes.StandardLibraryManage, cancellationToken);
        var material = await materials.FindMaterialAsync(materialId, cancellationToken) ?? throw new PdmNotFoundException("料品主档不存在。");
        if (material.IsArchived) throw new PdmRuleException("已停用料品不能设置推荐属性。");
        var saved = await materials.UpdatePlmMetadataAsync(material with { IsRecommended = isRecommended, UpdatedBy = actor, UpdatedAt = timeProvider.GetUtcNow() }, expectedRowVersion, cancellationToken);
        await AuditAsync(actor, "standard-library.material.recommend", materialId, isRecommended ? "设为推荐料品" : "取消推荐料品", cancellationToken);
        return saved;
    }

    private static void ValidateHierarchy(IReadOnlyList<StandardLibraryCategory> categories, Guid? categoryId, Guid? parentId)
    {
        var byId = categories.ToDictionary(item => item.Id);
        var visited = new HashSet<Guid>();
        var current = parentId;
        var depth = 1;
        while (current is not null)
        {
            if (!visited.Add(current.Value) || current == categoryId) throw new PdmRuleException("标准分类层级不能形成循环。");
            depth++;
            if (depth > 10) throw new PdmRuleException("标准分类最多支持10级。");
            current = byId.GetValueOrDefault(current.Value)?.ParentId;
        }
        if (categoryId is not null)
        {
            var descendants = categories.Where(item => item.ParentId is not null).GroupBy(item => item.ParentId!.Value).ToDictionary(group => group.Key, group => group.Select(item => item.Id).ToArray());
            var subtreeDepth = SubtreeDepth(categoryId.Value, descendants, new HashSet<Guid>());
            if (depth + subtreeDepth - 1 > 10) throw new PdmRuleException("标准分类最多支持10级。");
        }
    }

    private static int SubtreeDepth(Guid id, IReadOnlyDictionary<Guid, Guid[]> children, HashSet<Guid> path)
    {
        if (!path.Add(id)) throw new PdmRuleException("标准分类层级不能形成循环。");
        var depth = children.GetValueOrDefault(id)?.Select(child => SubtreeDepth(child, children, path)).DefaultIfEmpty(0).Max() ?? 0;
        path.Remove(id);
        return depth + 1;
    }

    private static HashSet<Guid> DescendantIds(IReadOnlyList<StandardLibraryCategory> categories, Guid root)
    {
        var result = new HashSet<Guid> { root };
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var category in categories.Where(item => item.ParentId is not null && result.Contains(item.ParentId.Value)))
                changed |= result.Add(category.Id);
        }
        return result;
    }

    private async Task RequireAsync(string actor, UserRole role, string permission, CancellationToken cancellationToken)
    {
        if (!await repository.HasUserPermissionAsync(actor, role, permission, cancellationToken)) throw new UnauthorizedAccessException("当前角色无权执行此操作。");
    }

    private Task AuditAsync(string actor, string action, Guid id, string summary, CancellationToken cancellationToken) =>
        repository.AppendAuditAsync(new AuditEntry(Guid.NewGuid(), timeProvider.GetUtcNow(), actor, action, nameof(StandardLibraryCategory), id.ToString(), summary), cancellationToken);
}
