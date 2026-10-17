using Upton.Pdm.Domain;
namespace Upton.Pdm.Application;

public sealed record CreateUserTodo(string Content, DateOnly? DueDate, IReadOnlyList<string> RecipientUsernames);

public sealed class UserTodoService(IPdmRepository repository, TimeProvider timeProvider)
{
    private static Guid Company(string actor) => TenantContext.Current is { } tenant && tenant.Username.Equals(actor, StringComparison.OrdinalIgnoreCase)
        ? tenant.CompanyId : throw new UnauthorizedAccessException("登录或公司信息已失效，请重新登录。");
    public Task<IReadOnlyList<UserNotification>> ListAsync(string actor, DateOnly from, DateOnly to, CancellationToken ct)
    {
        if (from > to || to.DayNumber - from.DayNumber > 366) throw new PdmRuleException("请选择一年以内的有效日期范围。");
        return repository.ListDatedUserTodosAsync(actor, Company(actor), from, to, ct);
    }
    public async Task<UserNotification> CreateAsync(CreateUserTodo command, string actor, CancellationToken ct)
    {
        var company = Company(actor);
        var content = command.Content?.Trim() ?? "";
        if (content.Length is < 1 or > 1000) throw new PdmRuleException("请填写待办内容，最多1000个字符。");
        var recipients = (command.RecipientUsernames ?? []).Select(name => name?.Trim() ?? "").Where(name => name.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (recipients.Length is < 1 or > 20) throw new PdmRuleException("请选择1到20名待办接收人。");
        foreach (var recipient in recipients)
        {
            var user = await repository.FindUserAsync(recipient, ct);
            var scope = user is null ? null : await repository.GetUserCompanyScopeAsync(user.Username, ct);
            if (user is null || !user.IsActive || scope is null || scope.PrimaryCompanyId != company && !scope.AccessibleCompanyIds.Contains(company))
                throw new PdmRuleException("请选择当前公司内的在职接收人。");
        }
        var now = timeProvider.GetUtcNow();
        var source = $"calendar-todo:{company:N}:{Guid.NewGuid():N}";
        var notifications = recipients.Append(actor).Distinct(StringComparer.OrdinalIgnoreCase).Select(recipient => new UserNotification(
            Guid.NewGuid(), recipient, "ProjectTodo", $"日历待办 · {actor}", content, null, null, source, now, null, command.DueDate)).ToArray();
        await repository.CreateUserNotificationsAsync(notifications, ct);
        return notifications.Single(todo => todo.Recipient.Equals(actor, StringComparison.OrdinalIgnoreCase));
    }
}
