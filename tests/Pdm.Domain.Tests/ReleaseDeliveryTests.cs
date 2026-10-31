using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class ReleaseDeliveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SystemAdministratorDepartmentRecipientsDependOnBusinessRoles(bool mixed)
    {
        var f = await Fixture.CreateAsync();
        await f.Repository.UpdateUserAsync("admin", "系统管理员", UserRole.Administrator, "Administrator", mixed ? ["Administrator", "Engineer"] : ["Administrator"], true, default);
        var record = await f.Service.EnsureAsync(f.Package.Id, default);
        Assert.All(record.State.Departments, receipt => Assert.Equal(mixed ? 1 : 0, receipt.Recipients.Count));
        Assert.Equal(mixed ? 3 : 0, record.State.Deliveries.Count);
        await f.Service.PublishedAsync(f.Package.Id, default);
        await f.RunAsync();
        Assert.Equal(mixed ? 2 : 0, f.Sender.Deliveries.Count);
        Assert.Equal(mixed ? 1 : 0, (await f.Repository.ListUserNotificationsAsync("admin", 100, default)).Count);
    }

    [Fact]
    public async Task AdministratorChosenDepartmentsAreFrozenAndOverlappingRecipientsAreDeduplicated()
    {
        var f = await Fixture.CreateAsync();
        var record = await f.Service.EnsureAsync(f.Package.Id, default);
        Assert.Equal(2, record.State.Departments.Count);
        Assert.Equal(new[] { f.First.Id, f.Second.Id }.Order(), record.State.Departments.Select(item => item.DepartmentId).Order());
        Assert.Equal(3, record.State.Deliveries.Count); // Same user and same group across two departments.
        f.Configuration.Settings = f.Configuration.Settings with { Rules = [] };
        await f.Service.PublishedAsync(f.Package.Id, default);
        await f.RunAsync();
        Assert.Equal(2, f.Sender.Deliveries.Count);
        Assert.Single(await f.Repository.ListUserNotificationsAsync("admin", 100, default), item => item.Category == "ReleaseDepartment");
        Assert.Equal(2, (await f.Store.FindAsync(f.Package.Id, default))!.State.Departments.Count);
        f.Clock.Advance(); await f.RunAsync();
        Assert.Equal(2, f.Sender.Deliveries.Count);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task DisabledOrPreEnablePublicationsNeverSendHistoricalNotifications(bool enabled, bool historical)
    {
        var f = await Fixture.CreateAsync();
        f.Configuration.Settings = f.Configuration.Settings with { Enabled = enabled, EnabledAt = historical ? f.Clock.Now.AddHours(1) : f.Clock.Now.AddDays(-1) };
        var record = await f.Service.EnsureAsync(f.Package.Id, default);
        Assert.Empty(record.State.Deliveries);
        await f.RunAsync(); Assert.Empty(f.Sender.Deliveries);
        Assert.DoesNotContain(await f.Repository.ListUserNotificationsAsync("admin", 100, default), item => item.Category == "ReleaseDepartment");
    }

    [Fact]
    public async Task RetiredNotificationsCancelPendingDeliveriesAndCannotBeRetried()
    {
        var f = await Fixture.CreateAsync();
        var record = await f.Service.EnsureAsync(f.Package.Id, default);
        Assert.NotEmpty(record.State.Deliveries);
        f.Configuration.Settings = new();
        await f.RunAsync();
        var retired = (await f.Store.FindAsync(f.Package.Id, default))!;
        Assert.All(retired.State.Deliveries, item => Assert.Equal("Cancelled", item.State));
        Assert.Empty(f.Sender.Deliveries);
        await Assert.ThrowsAsync<PdmRuleException>(() => f.Service.RetryAsync(f.Package.Id, record.State.Deliveries[0].Id, true, "admin", UserRole.Administrator, default));
    }

    [Fact]
    public async Task UncertainExternalDeliveryDoesNotAutoRepeatAndRetryRequiresExplicitConfirmation()
    {
        var f = await Fixture.CreateAsync();
        f.Sender.Outcome = "Unknown";
        await f.Service.PublishedAsync(f.Package.Id, default); await f.RunAsync();
        var record = (await f.Store.FindAsync(f.Package.Id, default))!;
        var email = Assert.Single(record.State.Deliveries, item => item.Channel == "Email");
        Assert.Equal("Unknown", email.State);
        f.Clock.Advance(); await f.RunAsync(); Assert.Equal(2, f.Sender.Deliveries.Count);
        await Assert.ThrowsAsync<PdmRuleException>(() => f.Service.RetryAsync(f.Package.Id, email.Id, false, "admin", UserRole.Administrator, default));
        f.Sender.Outcome = "Succeeded";
        await f.Service.RetryAsync(f.Package.Id, email.Id, true, "admin", UserRole.Administrator, default);
        await f.RunAsync();
        Assert.Equal(3, f.Sender.Deliveries.Count);
        Assert.Single(f.Sender.Deliveries, item => item.Channel == "DingTalk");
        Assert.Single(await f.Repository.ListUserNotificationsAsync("admin", 100, default), item => item.Category == "ReleaseDepartment");
    }

    [Fact]
    public async Task ReadyNotificationWaitsForTheBoundBomReceiptAndIsOnlyCreatedOnce()
    {
        var f = await Fixture.CreateAsync();
        f.Configuration.Settings = f.Configuration.Settings with { Rules = f.Configuration.Settings.Rules.Select(rule => rule with { Event = "Ready" }).ToArray() };
        await f.Service.PublishedAsync(f.Package.Id, default); await f.RunAsync(); Assert.Empty(f.Sender.Deliveries);
        await f.Service.U9ResultAsync(f.Package.ProjectId, ProjectBomHeaderKind.Standard, new(f.Package.StandardBomVersionId, [f.Package.Id]), "UpToDate", "U9回查一致", default);
        await f.RunAsync();
        var record = (await f.Store.FindAsync(f.Package.Id, default))!;
        Assert.True(ReleaseDeliveryService.Ready(record.State)); Assert.True(record.State.ReadyEventCreated);
        Assert.Equal(2, record.State.Departments.Count); Assert.Equal(2, f.Sender.Deliveries.Count);
        await f.Service.U9ResultAsync(f.Package.ProjectId, ProjectBomHeaderKind.Standard, new(f.Package.StandardBomVersionId, [f.Package.Id]), "UpToDate", "重复回查一致", default);
        await f.RunAsync(); Assert.Equal(2, f.Sender.Deliveries.Count);
    }

    [Fact]
    public async Task U9ReceiptCannotBeAttributedToANewerPublicationAndIncludesLongLeadSnapshots()
    {
        var f = await Fixture.CreateAsync();
        await f.Service.PublishedAsync(f.Package.Id, default);
        var newer = f.Package with { Id = Guid.NewGuid(), Number = "NEWER", StandardBomVersionId = Guid.NewGuid(), PublishedAt = f.Clock.Now.AddMinutes(1) };
        await f.Repository.CreateReleasePackageAsync(newer, default);
        await f.Service.PublishedAsync(newer.Id, default);
        await f.Service.U9ResultAsync(f.Package.ProjectId, ProjectBomHeaderKind.Standard,
            new(f.Package.StandardBomVersionId, [f.Package.Id]), "UpToDate", "原请求的回查结果", default);
        Assert.Equal("Succeeded", (await f.Store.FindAsync(f.Package.Id, default))!.State.U9["Standard"].State);
        Assert.Equal("Pending", (await f.Store.FindAsync(newer.Id, default))!.State.U9["Standard"].State);
        var longLead = f.Package with { Id = Guid.NewGuid(), Number = "LONG-LEAD", Scope = ReleaseScope.StandardLongLead, StandardBomVersionId = null };
        await f.Repository.CreateReleasePackageAsync(longLead, default);
        // A recovered receipt creates the missing tracking row, without attributing it to the latest ordinary BOM.
        await f.Service.U9ResultAsync(f.Package.ProjectId, ProjectBomHeaderKind.Standard,
            new(null, [longLead.Id]), "UpToDate", "长周期已批准范围回查一致", default);
        Assert.Equal("Succeeded", (await f.Store.FindAsync(longLead.Id, default))!.State.U9["Standard"].State);
        Assert.Equal("Pending", (await f.Store.FindAsync(newer.Id, default))!.State.U9["Standard"].State);
    }

    [Fact]
    public async Task DepartmentAcknowledgementsRequireMembershipAndPreservePriorExecution()
    {
        var f = await Fixture.CreateAsync();
        var record = await f.Service.EnsureAsync(f.Package.Id, default);
        var receipt = record.State.Departments[0];
        await f.Repository.CreateUserAsync(new(Guid.NewGuid(), "outsider", "非该部门人员", "unused", UserRole.Administrator, true), default);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.AcknowledgeAsync(f.Package.Id, receipt.Id, false, null, "outsider", UserRole.Administrator, default));
        await Assert.ThrowsAsync<PdmRuleException>(() => f.Service.AcknowledgeAsync(f.Package.Id, receipt.Id, true, "", "admin", UserRole.Administrator, default));
        await f.Service.AcknowledgeAsync(f.Package.Id, receipt.Id, true, "旧图已回收，按新图执行", "admin", UserRole.Administrator, default);
        await f.Service.AcknowledgeAsync(f.Package.Id, receipt.Id, true, "不应覆盖第一次执行记录", "admin", UserRole.Administrator, default);
        var saved = (await f.Store.FindAsync(f.Package.Id, default))!.State.Departments.Single(item => item.Id == receipt.Id);
        Assert.NotNull(saved.ReceivedAt); Assert.NotNull(saved.ExecutedAt);
        Assert.Equal("旧图已回收，按新图执行", saved.ExecutionNote);
    }

    [Fact]
    public async Task LeasePreventsConcurrentSendingAndInterruptedExternalSendsAreNotBlindlyRetried()
    {
        var f = await Fixture.CreateAsync();
        var record = await f.Service.EnsureAsync(f.Package.Id, default);
        var email = record.State.Deliveries.Single(item => item.Channel == "Email");
        Assert.True(await f.Store.SaveAsync(record with { State = record.State with
            { Deliveries = record.State.Deliveries.Select(item => item.Id == email.Id ? item with { State = "Sending", Attempts = 1 } : item).ToArray() } }, record.RowVersion, default));
        var first = Assert.Single(await f.Store.ClaimDueAsync(f.Clock.Now, 5, default));
        Assert.Empty(await f.Store.ClaimDueAsync(f.Clock.Now, 5, default));
        f.Clock.Now = f.Clock.Now.AddMinutes(4);
        var replacement = Assert.Single(await f.Store.ClaimDueAsync(f.Clock.Now, 5, default));
        await f.Service.ProcessAsync(first, default); Assert.Empty(f.Sender.Deliveries);
        await f.Service.ProcessAsync(replacement, default);
        Assert.Equal("Unknown", (await f.Store.FindAsync(f.Package.Id, default))!.State.Deliveries.Single(item => item.Id == email.Id).State);
        Assert.DoesNotContain(f.Sender.Deliveries, item => item.Channel == "Email");
    }

    [Fact]
    public async Task DefinitiveFailureHasBoundedRetriesAndCannotRepeatSuccessfulChannels()
    {
        var f = await Fixture.CreateAsync(); f.Sender.Outcome = "Failed";
        await f.Service.PublishedAsync(f.Package.Id, default);
        for (var attempt = 0; attempt < 8; attempt++) { await f.RunAsync(); f.Clock.Now = f.Clock.Now.AddHours(2); }
        Assert.Equal(10, f.Sender.Deliveries.Count);
        Assert.Single(await f.Repository.ListUserNotificationsAsync("admin", 100, default), item => item.Category == "ReleaseDepartment");
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 18, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance() => Now = Now.AddMinutes(2);
    }
    private sealed class Configuration : IReleaseNotificationConfigurationProvider
    {
        public ReleaseNotificationSettings Settings = new();
        public Task<(ReleaseNotificationSettings Settings, long Version)> LoadAsync(Guid id, CancellationToken ct) => Task.FromResult((Settings, 1L));
    }
    private sealed class Sender : IReleaseNotificationSender
    {
        public string Outcome = "Succeeded";
        public List<ReleaseChannelDelivery> Deliveries = [];
        public Task<ReleaseSendResult> SendAsync(ReleaseDeliveryState state, ReleaseChannelDelivery delivery, CancellationToken ct)
        { Deliveries.Add(delivery); return Task.FromResult(new ReleaseSendResult(Outcome, "隔离模拟结果")); }
    }
    private sealed record Fixture(InMemoryPdmRepository Repository, InMemoryReleaseDeliveryStore Store, ReleaseDeliveryService Service,
        Configuration Configuration, Sender Sender, TestClock Clock, ReleasePackage Package, OrganizationUnit First, OrganizationUnit Second)
    {
        public static async Task<Fixture> CreateAsync()
        {
            TenantContext.Clear();
            var clock = new TestClock(); var repository = new InMemoryPdmRepository(clock);
            var company = (await repository.GetOrganizationDirectoryAsync(default)).Organizations.First(item => item.IsActive).Id;
            await repository.CreateUserAsync(new(Guid.NewGuid(), "admin", "部门测试成员", "unused", UserRole.Administrator, true, RoleCode: "developer", CompanyId: company), default);
            var customer = (await repository.ListCustomersAsync(false, default)).First();
            var equipment = (await repository.ListEquipmentTypesAsync(false, default)).First();
            var project = await repository.CreateNumberedProjectAsync(new(company, "P", equipment.Code, customer.Id, "发布通知隔离项目", null,
                DateOnly.FromDateTime(clock.Now.UtcDateTime), 1, "admin", Path.Combine(Path.GetTempPath(), "notice-vault"), Path.Combine(Path.GetTempPath(), "notice-release")), default);
            var first = await repository.SaveOrganizationUnitAsync(new(null, company, null, "NOTICE-A", "管理员选择的部门甲", OrganizationUnitKind.Department, true, 0), default);
            var second = await repository.SaveOrganizationUnitAsync(new(null, company, null, "NOTICE-B", "管理员选择的部门乙", OrganizationUnitKind.Department, true, 1), default);
            await repository.SetOrganizationMembershipsAsync("admin", [first.Id, second.Id], first.Id, default);
            BomItem[] items = [new(Guid.NewGuid(), project.Id, BomKind.Standard, 1, "NOTICE-PART", "通知测试物料", 1, "001", null, "M1", "W1", true)];
            var bom = await repository.SaveBomDraftAsync(project.Id, BomKind.Standard, items, "admin", default);
            await repository.SetBomVersionStateAsync([bom.Id], BomVersionState.Released, "admin", clock.Now, default);
            var package = new ReleasePackage(Guid.NewGuid(), project.Id, "RP-NOTICE", ReleasePackageState.Published, null, "M", "E", [], clock.Now, clock.Now, "unused")
            { Scope = ReleaseScope.StandardFormal, StandardBomVersionId = bom.Id, StandardBomSnapshot = items, SelectedBomItemIds = items.Select(item => item.Id).ToArray(), LocksDocuments = false };
            await repository.CreateReleasePackageAsync(package, default);
            var group = Guid.NewGuid();
            var configuration = new Configuration { Settings = new() { Enabled = true, EnabledAt = clock.Now.AddDays(-1), PublicUrl = "http://192.168.2.8:5173",
                Rules = [new(Guid.NewGuid(), null, "Standard", "Published", [first.Id, second.Id], false, ["InApp", "Email", "DingTalk"], group, true)] } };
            var store = new InMemoryReleaseDeliveryStore(repository); var sender = new Sender();
            return new(repository, store, new(repository, store, configuration, sender, clock), configuration, sender, clock, package, first, second);
        }
        public async Task RunAsync() { foreach (var claim in await Store.ClaimDueAsync(Clock.Now, 5, default)) await Service.ProcessAsync(claim, default); }
    }
}
