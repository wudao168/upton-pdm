using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public static class ApprovalNodeCopies
{
    public static ApprovalCopyRule? Normalize(ApprovalCopyRule? rule)
    {
        if (rule is null) return null;
        var target = rule.Target?.Trim() ?? "";
        var message = rule.Message?.Trim() ?? "";
        if (message.Length is < 1 or > 500) throw new PdmRuleException("抄送消息须填写1至500个字符。");
        if (rule.Recipients is { } recipients) {
            if (recipients.Count is < 1 or > 100) throw new PdmRuleException("请选择1至100组抄送对象。");
            return rule with { Message = message, Recipients = recipients.Select(ApprovalCopyRecipients.Normalize).Distinct().ToArray() };
        }
        if (rule.Source is not ("Role" or "Department" or "User" or "Manager" or "Submitter"))
            throw new PdmRuleException("抄送对象类型无效。");
        if (rule.Source is "Role" or "Department" or "User" && target.Length == 0)
            throw new PdmRuleException("请选择抄送对象。");
        if (rule.Source == "Department" && !Guid.TryParse(target, out _)) throw new PdmRuleException("请选择有效的抄送部门。");
        if (message.Length is < 1 or > 500) throw new PdmRuleException("抄送消息须填写1至500个字符。");
        return rule with { Target = target, Message = message };
    }

    public static async Task<ApprovalCopyDelivery?> ResolveAsync(IPdmRepository repository, ApprovalWorkflowStepTemplate step,
        Project project, string actor, IReadOnlyList<UserAccount> users, CancellationToken ct)
    {
        if (step.CopyOnApproval is not { } rule) return null;
        if (rule.Recipients is { } copyRecipients) {
            var directory = await repository.GetOrganizationDirectoryAsync(ct);
            var department = directory.Memberships.FirstOrDefault(member => member.IsPrimary && member.Username.Equals(actor,StringComparison.OrdinalIgnoreCase))?.UnitId;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var recipient in copyRecipients) names.UnionWith(await ApprovalCopyRecipients.ResolveAsync(repository,recipient,project,actor,directory,department,ct));
            var eligible = new List<string>();
            foreach (var name in names) {
                var user = users.FirstOrDefault(user => user.IsActive && user.Username.Equals(name,StringComparison.OrdinalIgnoreCase));
                if (user is not null && await repository.HasProjectContentReadAccessAsync(project.Id,user.Username,user.Role,ct)) eligible.Add(user.Username);
            }
            if (eligible.Count == 0) throw new PdmRuleException($"“{step.Name}”没有可接收抄送消息的有效人员，请检查接收对象及项目权限。");
            return new(eligible,rule.Message);
        }
        IEnumerable<UserAccount> matches = users.Where(user => user.IsActive && (project.OrganizationId is null || user.CompanyId is null || user.CompanyId == project.OrganizationId));
        if (rule.Source == "Role") matches = matches.Where(user => user.HasRole(rule.Target));
        else if (rule.Source is "User" or "Submitter") matches = matches.Where(user => user.Username.Equals(rule.Source == "User" ? rule.Target : actor, StringComparison.OrdinalIgnoreCase));
        else
        {
            var directory = await repository.GetOrganizationDirectoryAsync(ct);
            var id = rule.Source == "Department" ? Guid.Parse(rule.Target) :
                directory.Memberships.FirstOrDefault(member => member.IsPrimary && member.Username.Equals(actor, StringComparison.OrdinalIgnoreCase))?.UnitId;
            var unit = directory.Units.FirstOrDefault(item => item.Id == id && item.IsActive && (project.OrganizationId is null || item.OrganizationId == project.OrganizationId));
            if (unit is null) throw new PdmRuleException($"“{step.Name}”的抄送部门无效，请检查组织设置。");
            var names = rule.Source == "Department" ? directory.Memberships.Where(member => member.UnitId == unit.Id).Select(member => member.Username)
                : directory.Managers.Where(manager => manager.UnitId == unit.Id).Select(manager => manager.PrimaryManager);
            var recipients = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
            matches = matches.Where(user => recipients.Contains(user.Username));
        }
        var targets = new List<string>();
        foreach (var user in matches)
            if (await repository.HasProjectContentReadAccessAsync(project.Id, user.Username, user.Role, ct)) targets.Add(user.Username);
        if (targets.Count == 0) throw new PdmRuleException($"“{step.Name}”没有可接收抄送消息的有效人员，请检查接收对象及项目权限。");
        return new(targets.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), rule.Message);
    }

    public static Task NotifyAsync(IPdmRepository repository, ApprovalCopyDelivery? delivery, Guid projectId, Guid? releasePackageId,
        Guid taskId, string name, DateTimeOffset? decisionTime, CancellationToken ct)
    {
        if (delivery is null || decisionTime is null) return Task.CompletedTask;
        return repository.CreateUserNotificationsAsync(delivery.Recipients.Select(recipient => new UserNotification(Guid.NewGuid(), recipient,
            "ApprovalNodeCopy", $"{name} · 审批抄送", delivery.Message, projectId, releasePackageId,
            $"approval-copy:{taskId:N}:{decisionTime.Value.UtcTicks}", decisionTime.Value, null)).ToArray(), ct);
    }
}
