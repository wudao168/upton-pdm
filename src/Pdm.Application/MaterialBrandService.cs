using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed record MaterialBrand(Guid Id, string Name, bool IsActive, long RowVersion, IReadOnlyList<string> PreviousNames);
public interface IMaterialBrandRepository
{
    Task<IReadOnlyList<MaterialBrand>> ListAsync(CancellationToken ct);
    Task<MaterialBrand> SaveAsync(MaterialBrand brand, long? expectedVersion, CancellationToken ct);
}

public sealed class MaterialBrandService(IMaterialBrandRepository brands, IPdmRepository pdm, TimeProvider clock)
{
    public async Task<IReadOnlyList<MaterialBrand>> ListAsync(string actor, UserRole role, CancellationToken ct)
    {
        if (!await pdm.HasUserPermissionAsync(actor, role, PermissionCodes.MaterialView, ct)
            && !await pdm.HasUserPermissionAsync(actor, role, PermissionCodes.MaterialManage, ct)
            && !await pdm.HasUserPermissionAsync(actor, role, PermissionCodes.MaterialApply, ct)
            && !await pdm.HasUserPermissionAsync(actor, role, PermissionCodes.BomEdit, ct)
            && !await pdm.HasUserPermissionAsync(actor, role, PermissionCodes.StandardLibraryView, ct)
            && !await pdm.HasUserPermissionAsync(actor, role, PermissionCodes.StandardLibraryManage, ct))
            throw new UnauthorizedAccessException("当前账号没有品牌库查看权限。");
        return await brands.ListAsync(ct);
    }

    public async Task<MaterialBrand> SaveAsync(Guid? id, string name, bool active, long? version, string actor, UserRole role, CancellationToken ct)
    {
        if (!await pdm.HasUserPermissionAsync(actor, role, PermissionCodes.MaterialManage, ct)) throw new UnauthorizedAccessException("当前账号没有品牌库维护权限。");
        name = name?.Trim() ?? "";
        if (name.Length is 0 or > 160) throw new PdmRuleException("品牌名称须为1至160个字符。");
        var catalog = await brands.ListAsync(ct);
        var current = id.HasValue ? catalog.FirstOrDefault(brand => brand.Id == id) ?? throw new PdmNotFoundException("品牌不存在，请刷新品牌库。") : null;
        if (current is not null && version is null) throw new PdmRuleException("更新品牌必须提供数据版本，请刷新后重试。");
        if (catalog.Any(brand => brand.Id != id && new[] { brand.Name }.Concat(brand.PreviousNames).Contains(name, StringComparer.OrdinalIgnoreCase)))
            throw new PdmConflictException("品牌名称或曾用名称已存在，请选择已有品牌。");
        var previous = (current?.PreviousNames ?? []).Concat(current is null || current.Name == name ? [] : new[] { current.Name })
            .Where(value => !string.Equals(value, name, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var saved = await brands.SaveAsync(new(id ?? Guid.NewGuid(), name, active, current?.RowVersion ?? 0, previous), version, ct);
        await pdm.AppendAuditAsync(new(Guid.NewGuid(), clock.GetUtcNow(), actor, "material-brand.save", nameof(MaterialBrand), saved.Id.ToString(), $"{saved.Name} · {(active ? "启用" : "停用")}"), ct);
        return saved;
    }

    public async Task<string?> NormalizeAsync(string? value, string? existing, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var brand = (await brands.ListAsync(ct)).FirstOrDefault(brand => new[] { brand.Name }.Concat(brand.PreviousNames).Contains(value.Trim(), StringComparer.OrdinalIgnoreCase));
        if (brand is null || !brand.IsActive && !string.Equals(existing?.Trim(), value.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new PdmRuleException("请选择品牌库中已启用的品牌；缺少品牌时请由料品维护人员先在品牌库新增。");
        return brand.Name;
    }
}
