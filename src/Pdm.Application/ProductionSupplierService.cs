using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed record ProductionSupplierCategory(Guid Id, string Name, bool IsActive);
public sealed record ProductionSupplier(Guid Id, string Code, string Name, string FullName,
    IReadOnlyList<Guid> CategoryIds, bool IsActive, string Address, string Phone,
    string Contact, string ContactPhone, string Remarks, DateTimeOffset UpdatedAt);
public sealed record ProductionSupplierCatalog(Guid CompanyId,
    IReadOnlyList<ProductionSupplierCategory> Categories, IReadOnlyList<ProductionSupplier> Suppliers,
    long RowVersion = 0);
public sealed record SaveProductionSupplierCategory(string Name, bool IsActive, long ExpectedRowVersion);
public sealed record SaveProductionSupplier(string Code, string Name, string FullName,
    IReadOnlyList<Guid> CategoryIds, bool IsActive, long ExpectedRowVersion,
    string? Address = null, string? Phone = null, string? Contact = null,
    string? ContactPhone = null, string? Remarks = null);

public interface IProductionSupplierStore
{
    Task<ProductionSupplierCatalog> ReadAsync(Guid companyId, CancellationToken ct);
    Task<bool> SaveAsync(ProductionSupplierCatalog catalog, long expectedVersion, CancellationToken ct);
}

public sealed class ProductionSupplierService(IPdmRepository repository, IProductionSupplierStore store, TimeProvider clock)
{
    private async Task<Guid> RequireCompanyAsync(string actor, UserRole role, CancellationToken ct)
    {
        if (!await repository.HasUserPermissionAsync(actor, role, PermissionCodes.ProductionAssign, ct))
            throw new UnauthorizedAccessException("没有维护或选择生产供应商的权限。");
        return TenantContext.CompanyId ?? throw new PdmRuleException("请先选择所属公司。");
    }

    public async Task<ProductionSupplierCatalog> ReadAsync(string actor, UserRole role, CancellationToken ct) =>
        await store.ReadAsync(await RequireCompanyAsync(actor, role, ct), ct);

    public async Task<ProductionSupplier> ResolveAsync(Guid? id, string? name, string actor, UserRole role, CancellationToken ct)
    {
        var catalog = await ReadAsync(actor, role, ct);
        var supplier = catalog.Suppliers.FirstOrDefault(item => item.IsActive && (id.HasValue
            ? item.Id == id.Value : item.Name.Equals(name?.Trim(), StringComparison.OrdinalIgnoreCase)));
        if (supplier is null || !supplier.CategoryIds.Any(categoryId =>
                catalog.Categories.Any(category => category.Id == categoryId && category.IsActive)))
            throw new PdmRuleException("所选供应商不存在、已停用或不属于当前公司，请刷新供应商列表后重新选择。");
        return supplier;
    }

    public async Task<ProductionSupplierCatalog> SaveCategoryAsync(Guid id, SaveProductionSupplierCategory input,
        string actor, UserRole role, CancellationToken ct)
    {
        var catalog = await ReadAsync(actor, role, ct);
        CheckVersion(catalog, input.ExpectedRowVersion);
        var name = Text(input.Name, 80, "分类名称", true);
        if (id == Guid.Empty) throw new PdmRuleException("分类编号无效，请重新打开表单。");
        if (catalog.Categories.Any(item => item.Id != id && item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new PdmRuleException("该供应商分类已存在，请使用已有分类。");
        if (!input.IsActive && catalog.Suppliers.Any(item => item.IsActive && item.CategoryIds.Contains(id)))
            throw new PdmRuleException("该分类仍有启用的供应商，请先调整供应商分类或停用对应供应商。");
        var category = new ProductionSupplierCategory(id, name, input.IsActive);
        var updated = catalog with { Categories = catalog.Categories.Where(item => item.Id != id).Append(category).ToArray() };
        return await SaveAsync(updated, actor, "production.supplier-category.save", id, name, ct);
    }

    public async Task<ProductionSupplierCatalog> SaveSupplierAsync(Guid id, SaveProductionSupplier input,
        string actor, UserRole role, CancellationToken ct)
    {
        var catalog = await ReadAsync(actor, role, ct);
        CheckVersion(catalog, input.ExpectedRowVersion);
        if (id == Guid.Empty) throw new PdmRuleException("供应商编号无效，请重新打开表单。");
        var code = Text(input.Code, 50, "供应商编码", true);
        var name = Text(input.Name, 100, "供应商简称", true);
        var fullName = Text(input.FullName, 200, "供应商全称", true);
        if (catalog.Suppliers.Any(item => item.Id != id && item.Code.Equals(code, StringComparison.OrdinalIgnoreCase)))
            throw new PdmRuleException("供应商编码已存在，请使用其他编码。");
        if (catalog.Suppliers.Any(item => item.Id != id && item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new PdmRuleException("供应商简称已存在，请使用已有供应商或修改简称。");
        var categories = input.CategoryIds?.Distinct().ToArray() ?? [];
        if (categories.Length == 0 || categories.Any(categoryId => !catalog.Categories.Any(category =>
                category.Id == categoryId && (!input.IsActive || category.IsActive))))
            throw new PdmRuleException("请为供应商选择有效分类；启用供应商只能使用启用的分类。");
        var supplier = new ProductionSupplier(id, code, name, fullName, categories, input.IsActive,
            Text(input.Address, 400, "公司地址"), Text(input.Phone, 60, "公司电话"),
            Text(input.Contact, 100, "联系人"), Text(input.ContactPhone, 60, "联系人电话"),
            Text(input.Remarks, 1000, "备注"), clock.GetUtcNow());
        var updated = catalog with { Suppliers = catalog.Suppliers.Where(item => item.Id != id).Append(supplier).ToArray() };
        return await SaveAsync(updated, actor, "production.supplier.save", id, $"{code} · {name}", ct);
    }

    private async Task<ProductionSupplierCatalog> SaveAsync(ProductionSupplierCatalog catalog,
        string actor, string action, Guid id, string detail, CancellationToken ct)
    {
        if (!await store.SaveAsync(catalog, catalog.RowVersion, ct))
            throw new PdmConflictException("供应商台账已被其他人修改，请刷新后重新编辑。");
        await repository.AppendAuditAsync(new(Guid.NewGuid(), clock.GetUtcNow(), actor, action,
            "ProductionSupplier", id.ToString(), detail), ct);
        return catalog with { RowVersion = catalog.RowVersion + 1 };
    }

    private static void CheckVersion(ProductionSupplierCatalog catalog, long expected)
    {
        if (catalog.RowVersion != expected)
            throw new PdmConflictException("供应商台账已被其他人修改，请刷新后重新编辑。");
    }

    private static string Text(string? value, int maximum, string label, bool required = false)
    {
        var text = value?.Trim() ?? "";
        if (required && text.Length == 0) throw new PdmRuleException($"请填写{label}。");
        if (text.Length > maximum) throw new PdmRuleException($"{label}不能超过{maximum}个字符。");
        return text;
    }
}
