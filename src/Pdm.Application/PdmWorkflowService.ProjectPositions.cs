using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed partial class PdmWorkflowService
{
    public async Task<Project> SetCustomProjectPositionsAsync(Guid projectId, IReadOnlyDictionary<string, IReadOnlyList<string>> input, string actor, UserRole role, CancellationToken cancellationToken)
    {
        var project = (await repository.ListProjectsForUserAsync(actor, role, cancellationToken)).FirstOrDefault(item => item.Id == projectId)
            ?? throw new UnauthorizedAccessException("无权配置该项目的岗位人员。");
        if (role != UserRole.Administrator && !(project.CanManageMainStaffing || project.CanAssignDesigners)) throw new UnauthorizedAccessException("仅有项目分工管理权限的人员可以分配自定义岗位。");
        var settings = await repository.GetProjectPermissionSettingsAsync(cancellationToken);
        var definitions = (settings.CustomPositions ?? []).ToDictionary(item => item.Key);
        if (input is null || input.Keys.Any(key => !definitions.ContainsKey(key))) throw new PdmRuleException("项目岗位已调整，请刷新后重新分配。");
        var directory = await repository.GetOrganizationDirectoryAsync(cancellationToken);
        var companyUnits = directory.Units.Where(item => item.OrganizationId == project.OrganizationId && item.IsActive).Select(item => item.Id).ToHashSet();
        var allowedUsers = directory.Memberships.Where(item => companyUnits.Contains(item.UnitId)).Select(item => item.Username).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var members = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var position in input)
        {
            if (position.Value is null) throw new PdmRuleException("岗位人员不能为空，请使用空列表清除分配。");
            var users = position.Value.Select(item => item.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (!definitions[position.Key].IsActive && users.Except(project.CustomPositionMembers.GetValueOrDefault(position.Key, []), StringComparer.OrdinalIgnoreCase).Any()) throw new PdmRuleException("停用岗位不能新增人员分配。");
            foreach (var username in users)
            {
                var user = await repository.FindUserAsync(username, cancellationToken);
                if (user?.IsActive != true || !allowedUsers.Contains(username)) throw new PdmRuleException("只能分配该项目所属公司的在职人员。");
            }
            members[position.Key] = users;
        }
        var saved = await repository.SetCustomProjectPositionsAsync(projectId, members, actor, cancellationToken);
        await AuditAsync(actor, "project.positions.assign", nameof(Project), projectId.ToString(), "更新自定义岗位人员分配", cancellationToken);
        return (await repository.ListProjectsForUserAsync(actor, role, cancellationToken)).First(item => item.Id == saved.Id);
    }
}
