using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;
namespace Upton.Pdm.Domain.Tests;

public sealed class UserTodoServiceTests
{
    private static readonly Guid Company = Guid.Parse("70000000-0000-0000-0000-000000000001");
    private static void Actor(string actor, Guid? company = null) => TenantContext.Set(new(Guid.NewGuid(), company ?? Company, Company, actor, "Administrator", false, new HashSet<string>()));
    [Fact]
    public async Task SharedInbox_OnlyCreatorAndRecipientsCanRead_EvenForAdministrators_AndOtherCompaniesCannotRead()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        foreach (var user in new[] { "creator", "recipient", "administrator" })
            await repository.CreateUserAsync(new UserAccount(Guid.NewGuid(), user, user, "unused", UserRole.Administrator, true, CompanyId: Company), default);
        var service = new UserTodoService(repository, TimeProvider.System);
        var date = new DateOnly(2026, 10, 16);
        try
        {
            Actor("creator");
            var created = await service.CreateAsync(new("私人跟进内容", date, ["recipient", "recipient"]), "creator", default);
            Assert.Equal("ProjectTodo", created.Category);
            Assert.Null(created.ProjectId);
            Assert.Single(await service.ListAsync("creator", date, date, default));
            Assert.Single(await repository.ListUserNotificationsAsync("creator", 200, default));
            Actor("recipient");
            Assert.Single(await service.ListAsync("recipient", date, date, default));
            Assert.Single(await repository.ListUserNotificationsAsync("recipient", 200, default));
            Actor("administrator");
            Assert.Empty(await service.ListAsync("administrator", date, date, default));
            Assert.Empty(await repository.ListUserNotificationsAsync("administrator", 200, default));
            Actor("creator", Guid.NewGuid());
            Assert.Empty(await service.ListAsync("creator", date, date, default));
            Assert.Empty(await repository.ListUserNotificationsAsync("creator", 200, default));
        }
        finally { TenantContext.Clear(); }
    }
    [Fact]
    public async Task RejectsInvalidRecipientsSpoofedActorAndInvalidContentBeforeCreatingAnything()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        await repository.CreateUserAsync(new UserAccount(Guid.NewGuid(), "foreign", "外公司", "unused", UserRole.Administrator, true, CompanyId: Guid.NewGuid()), default);
        await repository.CreateUserAsync(new UserAccount(Guid.NewGuid(), "disabled", "停用", "unused", UserRole.Administrator, false, CompanyId: Company), default);
        var service = new UserTodoService(repository, TimeProvider.System);
        try
        {
            Actor("creator");
            foreach (var recipient in new[] { "foreign", "disabled", "unknown" })
                await Assert.ThrowsAsync<PdmRuleException>(() => service.CreateAsync(new("私人事项", null, [recipient]), "creator", default));
            await Assert.ThrowsAsync<PdmRuleException>(() => service.CreateAsync(new(" ", null, ["creator"]), "creator", default));
            await Assert.ThrowsAsync<PdmRuleException>(() => service.CreateAsync(new("私人事项", null, []), "creator", default));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.CreateAsync(new("私人事项", null, ["creator"]), "another", default));
            Assert.Empty(await repository.ListUserNotificationsAsync("creator", 200, default));
        }
        finally { TenantContext.Clear(); }
    }
}
