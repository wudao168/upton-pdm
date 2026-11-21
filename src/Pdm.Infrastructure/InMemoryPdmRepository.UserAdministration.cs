using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed partial class InMemoryPdmRepository
{
    public Task<IReadOnlyDictionary<string, string>> GetUserDeletionReferencesAsync(IReadOnlyList<UserAccount> candidates, CancellationToken ct)
    {
        lock (gate)
        {
            IReadOnlyDictionary<string, string> references = candidates
                .Select(user => (user.Username, Reason: FindUserDeletionReference(user)))
                .Where(item => item.Reason is not null)
                .ToDictionary(item => item.Username, item => item.Reason!, StringComparer.OrdinalIgnoreCase);
            return Task.FromResult(references);
        }
    }

    public async Task<UserAccount> DeactivateManagedUserAsync(string username, string actor, Guid? companyId, CancellationToken ct)
    {
        await GetAuthorizationSettingsAsync(ct);
        lock (gate)
        {
            var target = UserAccountAdministration.RequireOperation(authorizationSettings!, users.Values.ToArray(), actor, username, companyId, false);
            if (!target.IsActive) return target;
            var saved = target with { IsActive = false, TokenVersion = target.TokenVersion + 1 };
            users[target.Id] = saved;
            RecordUserAdministrationAudit(target, actor, "user.deactivate", "停用账号");
            return saved;
        }
    }

    public async Task DeleteUnreferencedUserAsync(string username, string actor, Guid? companyId, CancellationToken ct)
    {
        await GetAuthorizationSettingsAsync(ct);
        lock (gate)
        {
            var target = UserAccountAdministration.RequireOperation(authorizationSettings!, users.Values.ToArray(), actor, username, companyId, true);
            if (FindUserDeletionReference(target) is { } reason) throw new PdmRuleException(reason);
            users.TryRemove(target.Id, out _);
            userProfiles.TryRemove(target.Username, out _);
            userCompanyAccess.TryRemove(target.Id, out _);
            RecordUserAdministrationAudit(target, actor, "user.delete", "删除未被引用的账号");
        }
    }

    private void RecordUserAdministrationAudit(UserAccount target, string actor, string action, string description) =>
        audits.Enqueue(new AuditEntry(Guid.NewGuid(), timeProvider.GetUtcNow(), actor, action,
            nameof(UserAccount), target.Username, $"{description} · {target.DisplayName}"));

    private string? FindUserDeletionReference(UserAccount user)
    {
        bool Matches(string? username) => string.Equals(username, user.Username, StringComparison.OrdinalIgnoreCase);

        if (organizationMemberships.TryGetValue(user.Username, out var membership) && membership.UnitIds.Count > 0
            || organizationManagers.Values.Any(item => Matches(item.PrimaryManager) || item.CollaborativeManagers.Any(Matches)))
            return "该账号仍被组织关系引用，无法删除。";
        if (userCompanyAccess.TryGetValue(user.Id, out var companies) && companies.Count > 0)
            return "该账号仍与跨公司授权有关，无法删除。";
        if (projectResponsibles.Values.Any(items => items.Any(Matches)) || projects.Values.Any(project =>
                Matches(project.Owner) || Matches(project.PrimaryProjectManager) || Matches(project.DesignLead)
                || project.ResponsibleUsers.Any(Matches) || project.CollaborativeProjectManagers.Any(Matches)
                || project.DesignLeads.Any(Matches) || project.Designers.Any(Matches) || project.PhaseOwners.Values.Any(Matches)
                || project.CustomPositionMembers.Values.SelectMany(items => items).Any(Matches)))
            return "该账号仍被项目职责或人员分工引用，无法删除。";
        if (projectFolders.Values.SelectMany(folder => folder.Permissions).Concat(folderTemplate.Values.SelectMany(folder => folder.Permissions))
            .Any(permission => permission.PrincipalType == FolderPrincipalType.User && Matches(permission.PrincipalKey)))
            return "该账号仍有项目、图档或目录授权，无法删除。";
        if (documents.Values.Any(item => Matches(item.CheckedOutBy) || Matches(item.CheckoutReleaseRequestedBy) || Matches(item.DeletedBy))
            || versions.Values.Any(item => Matches(item.CreatedBy)))
            return "该账号仍被图档或版本记录引用，无法删除。";
        if (packages.Values.SelectMany(package => package.ApprovalTasks)
            .Any(task => Matches(task.Assignee) || Matches(task.DecisionBy) || task.Participants?.Members.Any(Matches) == true
                || task.Participants?.Signatures.Any(signature => Matches(signature.Actor)) == true))
            return "该账号仍被审批分工或历史记录引用，无法删除。";
        if (passwordResetTasks.Values.Any(item => Matches(item.Username)))
            return "该账号仍有密码重置申请，无法删除。";
        if (audits.Any(item => Matches(item.Actor)))
            return "该账号仍被操作审计记录引用，无法删除。";
        if (userNotifications.Values.Any(item => Matches(item.Recipient)))
            return "该账号仍被系统业务记录或配置引用，无法删除。";
        return null;
    }
}
