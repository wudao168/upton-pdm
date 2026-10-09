using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Tests;

public sealed class ProjectContactInformationServiceTests
{
    [Fact]
    public async Task MultipleShippingAddressesAndContactsWithoutAddressesArePersisted()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var root = Assert.Single(await repository.ListProjectsAsync(default));
        var store = new InMemoryProjectContactInformationRepository();
        var service = new ProjectContactInformationService(repository, store, TimeProvider.System);
        var saved = await service.SaveAsync(root.Id, new("", "", "", "", "", 0,
            [new(" 联系人 ", " 电话 ", "", Position: " 采购经理 ", Remarks: " 工作日联系 "), new("收货甲", "", " 地址甲 ", true), new("收货乙", "", "地址乙", true)], " 销售甲、销售乙 ", " 售前甲 "), "admin", UserRole.Administrator, default);
        var entries = saved.Information.Entries!;
        Assert.Equal(3, entries.Count);
        Assert.Equal("联系人", entries[0].Contact);
        Assert.Equal("采购经理", entries[0].Position);
        Assert.Equal("工作日联系", entries[0].Remarks);
        Assert.Equal("", entries[0].Address);
        Assert.Equal(2, entries.Count(entry => entry.IsShippingAddress));
        Assert.Equal("地址甲", entries[1].Address);
        Assert.Equal("销售甲、销售乙", saved.Information.SalesPeople);
        Assert.Equal("售前甲", (await service.GetAsync(root.Id, "admin", UserRole.Administrator, default)).Information.PresalesPeople);
        await Assert.ThrowsAsync<PdmRuleException>(() => service.SaveAsync(root.Id, new("", "", "", "", "", 1, [new("联系人", "", "", true)]), "admin", UserRole.Administrator, default));
        await Assert.ThrowsAsync<PdmRuleException>(() => service.SaveAsync(root.Id, new("", "", "", "", "", 1, [new("", "", "")]), "admin", UserRole.Administrator, default));
        await Assert.ThrowsAsync<PdmRuleException>(() => service.SaveAsync(root.Id, new("", "", "", "", "", 1, [], new string('x', 501)), "admin", UserRole.Administrator, default));
        await Assert.ThrowsAsync<PdmRuleException>(() => service.SaveAsync(root.Id, new("", "", "", "", "", 1, [new("联系人", "", "", Position: new string('x', 101))]), "admin", UserRole.Administrator, default));
        await Assert.ThrowsAsync<PdmRuleException>(() => service.SaveAsync(root.Id, new("", "", "", "", "", 1, [new("联系人", "", "", Remarks: new string('x', 501))]), "admin", UserRole.Administrator, default));
    }

    [Fact]
    public async Task LegacyCustomerAndShippingFieldsBecomeSeparateRows()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var root = Assert.Single(await repository.ListProjectsAsync(default));
        var store = new InMemoryProjectContactInformationRepository();
        await store.SaveAsync(new(root.Id, "客户联系人", "电话", "地址", "收货人", "收货电话"), 0, default);
        var view = await new ProjectContactInformationService(repository, store, TimeProvider.System).GetAsync(root.Id, "admin", UserRole.Administrator, default);
        Assert.Equal(2, view.Information.Entries!.Count);
        Assert.False(view.Information.Entries[0].IsShippingAddress);
        Assert.True(view.Information.Entries[1].IsShippingAddress);
    }

    [Fact]
    public void ExistingStoredEntriesDefaultPositionAndRemarksToEmpty()
    {
        var entry = System.Text.Json.JsonSerializer.Deserialize<ProjectContactEntry>("""{"contact":"联系人","phone":"电话","address":"","isShippingAddress":false}""", new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.Equal("", entry!.Position);
        Assert.Equal("", entry.Remarks);
    }

    [Fact]
    public async Task ContactInformationIsSharedByTheFamilyAndRejectsStaleWrites()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var root = await repository.CreateNumberedProjectAsync(new(Guid.Parse("70000000-0000-0000-0000-000000000001"), "C", 2, Guid.Parse("c0046500-0000-0000-0000-000000000001"), "联系信息验证", null, new(2026, 10, 2), 1, "admin", "D:/test", "D:/release"), default);
        var child = await repository.CreateSubprojectAsync(new(root.Id, "子项目", null, 1), default);
        var store = new InMemoryProjectContactInformationRepository();
        var service = new ProjectContactInformationService(repository, store, TimeProvider.System);
        var saved = await service.SaveAsync(child.Id, new(" 张先生 ", "13800000000", " 苏州市工业园区 ", "李女士", "13900000000", 0), "admin", UserRole.Administrator, default);
        Assert.Equal(root.Id, saved.Information.ProjectId);
        Assert.Equal(root.CustomerName, saved.CustomerName);
        Assert.Equal("张先生", saved.Information.CustomerContact);
        Assert.Equal("苏州市工业园区", saved.Information.ShippingAddress);
        Assert.Equal(saved.Information, (await service.GetAsync(root.Id, "admin", UserRole.Administrator, default)).Information);
        Assert.Null(await store.FindAsync(child.Id, default));
        await Assert.ThrowsAsync<PdmConflictException>(() => service.SaveAsync(root.Id, new("", "", "", "", "", 0), "admin", UserRole.Administrator, default));
    }

    [Fact]
    public async Task ContactInformationEditingUsesIndependentRolePermission()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var root = Assert.Single(await repository.ListProjectsAsync(default));
        await repository.SetMainProjectStaffingAsync(root.Id, new("pm", ["co-pm"], ["designer"]), "admin", default);
        await repository.SetRolePermissionsAsync("Engineer", [PermissionCodes.ProjectView, PermissionCodes.ProjectContentView, PermissionCodes.ProjectContactEdit], default);
        var service = new ProjectContactInformationService(repository, new InMemoryProjectContactInformationRepository(), TimeProvider.System);
        foreach (var actor in new[] { "pm", "co-pm" })
        {
            var view = await service.GetAsync(root.Id, actor, UserRole.Engineer, default);
            Assert.True(view.CanEdit);
            await service.SaveAsync(root.Id, new(actor, "", "", "", "", view.Information.RowVersion), actor, UserRole.Engineer, default);
        }
        Assert.True((await service.GetAsync(root.Id, "designer", UserRole.Engineer, default)).CanEdit);
        await repository.SetRolePermissionsAsync("Engineer", [PermissionCodes.ProjectView, PermissionCodes.ProjectContentView], default);
        Assert.False((await service.GetAsync(root.Id, "pm", UserRole.Engineer, default)).CanEdit);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SaveAsync(root.Id, new("", "", "", "", "", 2), "pm", UserRole.Engineer, default));
    }

    [Fact]
    public async Task InvalidLengthsAndMissingProjectsAreRejected()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var root = Assert.Single(await repository.ListProjectsAsync(default));
        var service = new ProjectContactInformationService(repository, new InMemoryProjectContactInformationRepository(), TimeProvider.System);
        await Assert.ThrowsAsync<PdmRuleException>(() => service.SaveAsync(root.Id, new(new string('x', 101), "", "", "", "", 0), "admin", UserRole.Administrator, default));
        await Assert.ThrowsAsync<PdmRuleException>(() => service.SaveAsync(root.Id, new("", "", new string('x', 501), "", "", 0), "admin", UserRole.Administrator, default));
        await Assert.ThrowsAsync<PdmNotFoundException>(() => service.GetAsync(Guid.NewGuid(), "admin", UserRole.Administrator, default));
    }
}
