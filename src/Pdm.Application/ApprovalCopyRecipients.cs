using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

/// <summary>Shared recipient scopes for node copies and business-completion copies. Never grants project access.</summary>
public static class ApprovalCopyRecipients
{
    public static readonly IReadOnlySet<string> ProjectRoles = new HashSet<string>(StringComparer.Ordinal) {
        "ProjectManager", "CollaborativeProjectManager", "DesignLead", "Designer", "SalesPeople", "PresalesPeople",
        "ElectricalEngineer", "ElectricalHardware", "StandardProcurement", "NonStandardProcurement", "NonStandardProduction",
        "MechanicalAssembly", "ElectricalAssembly", "ElectricalCommissioning", "Acceptance"
    };
    public static ApprovalCopyRecipient Normalize(ApprovalCopyRecipient input)
    {
        if (input is null) throw new PdmRuleException("抄送对象不能为空，请重新选择。");
        var target = input.Target?.Trim() ?? "";
        var role = string.IsNullOrWhiteSpace(input.RoleCode) ? null : input.RoleCode.Trim();
        if (input.Source is not ("Role" or "Department" or "User" or "Submitter" or "Manager" or "FixedManager"
            or "SubmitterDepartment" or "ParentDepartment" or "SubmitterDivision" or "ProjectRole" or "ProjectMembers"))
            throw new PdmRuleException("请选择有效的抄送对象类型。");
        if (input.Source is "Role" or "Department" or "User" or "FixedManager" or "ProjectRole" && string.IsNullOrEmpty(target))
            throw new PdmRuleException("请为每组抄送对象选择人员、部门或角色。");
        if (input.Source is "Department" or "FixedManager" && (!Guid.TryParse(target, out var id) || id == Guid.Empty))
            throw new PdmRuleException("请选择有效的抄送部门。");
        if (input.Source == "ProjectRole" && !ProjectRoles.Contains(target)) throw new PdmRuleException("请选择有效的项目关联角色。");
        var department = input.Source is "Department" or "SubmitterDepartment" or "ParentDepartment" or "SubmitterDivision";
        return input with { Target = input.Source is "Role" or "Department" or "User" or "FixedManager" or "ProjectRole" ? target : "",
            RoleCode = department ? role : null, IncludeSubdepartments = department && input.IncludeSubdepartments };
    }

    public static async Task ValidateAsync(IPdmRepository repository, IReadOnlyList<ApprovalCopyRecipient> recipients, Guid? company, CancellationToken ct)
    {
        var directory = await repository.GetOrganizationDirectoryAsync(ct);
        var roles = (await repository.GetRolePermissionDirectoryAsync(ct)).Roles.Select(role => role.Role).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var units = ActiveUnits(directory, company);
        foreach (var input in recipients) {
            var item = Normalize(input);
            if (item.Source == "Role" && !roles.Contains(item.Target) || item.RoleCode is not null && !roles.Contains(item.RoleCode))
                throw new PdmRuleException("抄送角色不存在，请刷新角色列表后重新选择。");
            if (item.Source is "Department" or "FixedManager" && !units.Contains(Guid.Parse(item.Target)))
                throw new PdmRuleException("抄送部门已停用或不属于当前公司，请重新选择。");
            if (item.Source == "User" && !directory.Users.Any(user => user.Username.Equals(item.Target,StringComparison.OrdinalIgnoreCase) && CompanyUser(directory,user.Username,company)))
                throw new PdmRuleException("指定的抄送人员已停用或不属于当前公司，请重新选择。");
        }
    }

    public static async Task<IReadOnlyList<string>> ResolveAsync(IPdmRepository repository, ApprovalCopyRecipient input, Project project,
        string submitter, OrganizationDirectory directory, Guid? submitterDepartment, CancellationToken ct)
    {
        var item = Normalize(input);
        var units = ActiveUnits(directory,project.OrganizationId);
        var roleCode = item.Source == "Role" ? item.Target : item.RoleCode;
        if (roleCode is not null && !(await repository.GetRolePermissionDirectoryAsync(ct)).Roles.Any(role => role.Role.Equals(roleCode,StringComparison.OrdinalIgnoreCase))) return [];
        var candidates = directory.Users.Where(user => CompanyUser(directory,user.Username,project.OrganizationId));
        HashSet<string>? names = null;
        if (item.Source == "Role") candidates = candidates.Where(user => user.EffectiveRoleCodes.Contains(item.Target,StringComparer.OrdinalIgnoreCase));
        else if (item.Source is "User" or "Submitter") names = new(StringComparer.OrdinalIgnoreCase) { item.Source == "User" ? item.Target : submitter };
        else if (item.Source is "ProjectRole" or "ProjectMembers") {
            var rootId = project.RootProjectId ?? project.ParentProjectId;
            var root = rootId is Guid id && id != project.Id ? await repository.FindProjectAsync(id,ct) : null;
            if (root?.OrganizationId != project.OrganizationId) root = null;
            IEnumerable<string> Members(Project value) => item.Source == "ProjectMembers" ? ProjectActivityFeed.Members(value) : ProjectRoleMembers(value,item.Target);
            names = Members(project).Concat(root is null ? [] : Members(root)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (item.Source == "ProjectMembers" || item.Target is "SalesPeople" or "PresalesPeople") {
                var contacts = await repository.FindProjectContactInformationAsync((root ?? project).Id, ct);
                var text = item.Source == "ProjectMembers" ? contacts?.SalesPeople + "、" + contacts?.PresalesPeople
                    : item.Target == "SalesPeople" ? contacts?.SalesPeople : contacts?.PresalesPeople;
                foreach (var name in (text ?? "").Split([',','，',';','；','、','\n'],StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries)) {
                    var exact = candidates.FirstOrDefault(user => user.Username.Equals(name,StringComparison.OrdinalIgnoreCase));
                    var matched = exact is null ? candidates.Where(user => user.DisplayName == name).ToArray() : [exact];
                    if (matched.Length == 1) names.Add(matched[0].Username);
                }
            }
        } else {
            var id = item.Source is "Department" or "FixedManager" ? Guid.Parse(item.Target) : submitterDepartment;
            var unit = directory.Units.FirstOrDefault(unit => unit.Id == id && units.Contains(unit.Id));
            if (item.Source == "ParentDepartment") unit = directory.Units.FirstOrDefault(parent => parent.Id == unit?.ParentUnitId && units.Contains(parent.Id));
            if (item.Source == "SubmitterDivision") {
                while (unit is not null && unit.Kind != OrganizationUnitKind.BusinessDivision && unit.ParentUnitId is Guid parent)
                    unit = directory.Units.FirstOrDefault(candidate => candidate.Id == parent && units.Contains(candidate.Id));
                if (unit?.Kind != OrganizationUnitKind.BusinessDivision) unit = null;
            }
            if (unit is null) return [];
            var selected = new HashSet<Guid> { unit.Id };
            if (item.IncludeSubdepartments) for (var previous = -1; previous != selected.Count;) {
                previous = selected.Count;
                foreach (var child in directory.Units.Where(child => units.Contains(child.Id) && child.ParentUnitId is Guid parent && selected.Contains(parent))) selected.Add(child.Id);
            }
            names = (item.Source is "Manager" or "FixedManager"
                ? directory.Managers.Where(manager => manager.UnitId == unit.Id).SelectMany(manager => manager.CollaborativeManagers.Append(manager.PrimaryManager))
                : directory.Memberships.Where(member => selected.Contains(member.UnitId)).Select(member => member.Username)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        if (names is not null) candidates = candidates.Where(user => names.Contains(user.Username));
        if (item.RoleCode is not null) candidates = candidates.Where(user => user.EffectiveRoleCodes.Contains(item.RoleCode,StringComparer.OrdinalIgnoreCase));
        return candidates.Select(user => user.Username).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IEnumerable<string> ProjectRoleMembers(Project project,string role) => role switch {
        "ProjectManager" => string.IsNullOrWhiteSpace(project.PrimaryProjectManager) ? [] : [project.PrimaryProjectManager],
        "CollaborativeProjectManager" => project.CollaborativeProjectManagers,
        "DesignLead" => project.DesignLeads.Concat(string.IsNullOrWhiteSpace(project.DesignLead) ? [] : [project.DesignLead]),
        "Designer" => project.Designers,
        _ => project.CustomPositionMembers.TryGetValue(role,out var users) ? users : project.PhaseOwners.TryGetValue(role,out var names)
            ? names.Split([',','，',';','；','、','\n'],StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries) : []
    };

    public static HashSet<Guid> ActiveUnits(OrganizationDirectory directory,Guid? company)
    {
        var units = new HashSet<Guid>();
        for (var previous = -1; previous != units.Count;) {
            previous = units.Count;
            foreach (var unit in directory.Units.Where(unit => (company is null || unit.OrganizationId == company) && unit.IsActive && (unit.ParentUnitId is null || units.Contains(unit.ParentUnitId.Value)))) units.Add(unit.Id);
        }
        return units;
    }
    public static bool CompanyUser(OrganizationDirectory directory,string name,Guid? company) => directory.Users.Any(user => user.IsActive && user.Username.Equals(name,StringComparison.OrdinalIgnoreCase)
        && (company is null || user.CompanyId == company || user.CompanyId is null && directory.Memberships.Any(member => member.Username.Equals(name,StringComparison.OrdinalIgnoreCase)
            && directory.Units.Any(unit => unit.Id == member.UnitId && unit.OrganizationId == company && unit.IsActive))));
}
