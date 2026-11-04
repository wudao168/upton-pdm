using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Pdm.Domain.Tests;
public sealed class MaterialBrandServiceTests
{
    [Fact]
    public async Task CatalogEnforcesPermissionUniquenessVersionsAndActiveSelections()
    {
        var pdm = new InMemoryPdmRepository(TimeProvider.System);
        var service = new MaterialBrandService(new InMemoryMaterialBrandRepository(), pdm, TimeProvider.System);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SaveAsync(null, "品牌", true, null, "engineer", UserRole.Engineer, default));
        var brand = await service.SaveAsync(null, " UPTON ", true, null, "admin", UserRole.Administrator, default);
        Assert.Equal("UPTON", await service.NormalizeAsync("upton", null, default));
        await Assert.ThrowsAsync<PdmConflictException>(() => service.SaveAsync(null, "upton", true, null, "admin", UserRole.Administrator, default));
        var renamed = await service.SaveAsync(brand.Id, "阿普顿", true, brand.RowVersion, "admin", UserRole.Administrator, default);
        Assert.Contains("UPTON", renamed.PreviousNames);
        Assert.Equal("阿普顿", await service.NormalizeAsync("UPTON", null, default));
        await Assert.ThrowsAsync<PdmConflictException>(() => service.SaveAsync(brand.Id, "旧版本", true, brand.RowVersion, "admin", UserRole.Administrator, default));
        await service.SaveAsync(brand.Id, renamed.Name, false, renamed.RowVersion, "admin", UserRole.Administrator, default);
        await Assert.ThrowsAsync<PdmRuleException>(() => service.NormalizeAsync(renamed.Name, null, default));
        Assert.Equal(renamed.Name, await service.NormalizeAsync(renamed.Name, renamed.Name, default));
        await Assert.ThrowsAsync<PdmRuleException>(() => service.NormalizeAsync("库外品牌", null, default));
        await pdm.SetRolePermissionsAsync("Engineer", [PermissionCodes.MaterialApply], default);
        Assert.Single(await service.ListAsync("engineer", UserRole.Engineer, default));
    }
}
