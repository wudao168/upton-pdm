using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class ProductionSupplierTests
{
    private static void Company(Guid company, bool allowed = true) => TenantContext.Set(new(
        Guid.NewGuid(), company, company, allowed ? "admin" : "engineer", allowed ? "Administrator" : "Engineer",
        false, allowed ? new HashSet<string> { PermissionCodes.ProductionAssign } : new HashSet<string>()));

    [Fact]
    public async Task CatalogKeepsCompaniesSeparateAndEnforcesCategoriesVersionsAndActivation()
    {
        var company = Guid.NewGuid();
        Company(company);
        try
        {
            var repository = new InMemoryPdmRepository(TimeProvider.System);
            var service = new ProductionSupplierService(repository, new InMemoryProductionSupplierStore(), TimeProvider.System);
            var categoryId = Guid.NewGuid();
            var catalog = await service.SaveCategoryAsync(categoryId, new("机加工", true, 0), "admin", UserRole.Administrator, default);
            var secondCategory = Guid.NewGuid();
            catalog = await service.SaveCategoryAsync(secondCategory, new("钣金", true, catalog.RowVersion), "admin", UserRole.Administrator, default);
            var supplierId = Guid.NewGuid();
            var input = new SaveProductionSupplier("S001", "外协厂", "测试外协有限公司", [categoryId, secondCategory], true, catalog.RowVersion);
            catalog = await service.SaveSupplierAsync(supplierId, input, "admin", UserRole.Administrator, default);
            Assert.Equal(2, Assert.Single(catalog.Suppliers).CategoryIds.Count);
            Assert.Equal(supplierId, (await service.ResolveAsync(supplierId, "伪造的名称", "admin", UserRole.Administrator, default)).Id);
            await Assert.ThrowsAsync<PdmRuleException>(() => service.SaveCategoryAsync(categoryId,
                new("机加工", false, catalog.RowVersion), "admin", UserRole.Administrator, default));
            await Assert.ThrowsAsync<PdmConflictException>(() => service.SaveSupplierAsync(supplierId,
                input, "admin", UserRole.Administrator, default));
            await Assert.ThrowsAsync<PdmRuleException>(() => service.SaveSupplierAsync(Guid.NewGuid(),
                input with { ExpectedRowVersion = catalog.RowVersion }, "admin", UserRole.Administrator, default));
            var storedVersion = catalog.RowVersion;
            Company(Guid.NewGuid());
            Assert.Empty((await service.ReadAsync("admin", UserRole.Administrator, default)).Suppliers);
            await Assert.ThrowsAsync<PdmRuleException>(() => service.ResolveAsync(supplierId, null, "admin", UserRole.Administrator, default));
            Company(company);
            catalog = await service.SaveSupplierAsync(supplierId,
                input with { IsActive = false, ExpectedRowVersion = storedVersion }, "admin", UserRole.Administrator, default);
            await Assert.ThrowsAsync<PdmRuleException>(() => service.ResolveAsync(supplierId, null, "admin", UserRole.Administrator, default));
            Assert.Single(catalog.Suppliers);
            await service.SaveCategoryAsync(categoryId, new("机加工", false, catalog.RowVersion), "admin", UserRole.Administrator, default);
            Company(company, false);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ReadAsync("engineer", UserRole.Engineer, default));
        }
        finally { TenantContext.Clear(); }
    }

    [Fact]
    public async Task AllocationUsesManagedNameAndRetrySurvivesSupplierDeactivation()
    {
        var company = Guid.NewGuid();
        Company(company);
        try
        {
            var repository = new InMemoryPdmRepository(TimeProvider.System);
            var supplierService = new ProductionSupplierService(repository, new InMemoryProductionSupplierStore(), TimeProvider.System);
            var category = Guid.NewGuid();
            var catalog = await supplierService.SaveCategoryAsync(category, new("表面处理", true, 0), "admin", UserRole.Administrator, default);
            var supplierId = Guid.NewGuid();
            var supplierInput = new SaveProductionSupplier("S002", "表处厂", "测试表面处理有限公司", [category], true, catalog.RowVersion);
            catalog = await supplierService.SaveSupplierAsync(supplierId, supplierInput, "admin", UserRole.Administrator, default);
            var project = await repository.CreateProjectAsync(new("SUP-" + Guid.NewGuid().ToString("N"), "供应商测试", "admin", "vault", "release"), "admin", default);
            var item = new BomItem(Guid.NewGuid(), project.Id, BomKind.NonStandard, 1, "NS-1", "支架", 3, "件", "钢", null, "W1", true);
            var package = new ReleasePackage(Guid.NewGuid(), project.Id, "RP-SUP", ReleasePackageState.Published, null, "W1", "W1", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "release")
            { Scope = ReleaseScope.NonStandardWithDrawing, NonStandardBomSnapshot = [item], SelectedBomItemIds = [item.Id] };
            await repository.CreateReleasePackageAsync(package, default);
            var service = new ProductionTrackingService(repository, new InMemoryProductionTrackingStore(DateTimeOffset.UtcNow.AddMinutes(-1)), TimeProvider.System, new ProductionDrawingService(repository), supplierService);
            var record = await service.ReceiveAsync(package.Id, Guid.NewGuid(), "admin", UserRole.Administrator, default);
            var line = Assert.Single(record.State.Lines);
            var input = new AssignProductionLot(line.Id, "External", 1, "客户端伪造名称", "供应链", DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2)), Guid.NewGuid(), supplierId);
            record = await service.AssignAsync(package.Id, input, "admin", UserRole.Administrator, default);
            var lot = Assert.Single(record.State.Lots);
            Assert.Equal("表处厂", lot.Supplier);
            Assert.Equal(supplierId, lot.SupplierId);
            await supplierService.SaveSupplierAsync(supplierId, supplierInput with { IsActive = false, ExpectedRowVersion = catalog.RowVersion }, "admin", UserRole.Administrator, default);
            record = await service.AssignAsync(package.Id, input, "admin", UserRole.Administrator, default);
            Assert.Single(record.State.Lots);
            await Assert.ThrowsAsync<PdmRuleException>(() => service.AssignAsync(package.Id,
                input with { RequestId = Guid.NewGuid() }, "admin", UserRole.Administrator, default));
            Assert.Equal("表处厂", record.State.Lots[0].Supplier);
        }
        finally { TenantContext.Clear(); }
    }
}
