using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public static class ProjectChildCreation
{
    private const int MaxChildSequence = 99;

    public static void RequirePreviewCount(int count)
    {
        if (count < 0) throw new PdmRuleException("子项目行数不能小于0。");
        if (count > MaxChildSequence) throw new PdmRuleException($"每个上级项目最多可创建{MaxChildSequence}个子项目，请减少行数。");
    }

    public static ProjectCodePreview Preview(string parentCode, IEnumerable<int> usedSequences, int count) =>
        new(parentCode, count == 0 ? [] : AllocateSequences(usedSequences, count).Select(sequence => ChildCode(parentCode, sequence)).ToArray());

    public static void RequireParentNumberingData(Project parent)
    {
        if (parent.OrganizationId is null || parent.EquipmentTypeCode is null || parent.CustomerProjectSequence is null
            || string.IsNullOrWhiteSpace(parent.ProjectTypeCode) || string.IsNullOrWhiteSpace(parent.CustomerCode)
            || string.IsNullOrWhiteSpace(parent.CustomerName))
            throw new PdmRuleException("旧项目缺少自动编号资料，不能直接创建子项目。");
    }

    private static string ChildCode(string parentCode, int sequence) => $"{parentCode}-{sequence}";

    public static int[] AllocateSequences(IEnumerable<int> usedSequences, int count)
    {
        if (count == 0) throw new PdmRuleException("请至少填写一行子项目。");
        var used = usedSequences.ToHashSet();
        var available = Enumerable.Range(1, MaxChildSequence).Where(sequence => !used.Contains(sequence)).Take(count).ToArray();
        if (available.Length == count) return available;
        if (available.Length == 0) throw new PdmRuleException("该主项目的两位子项目号已用尽。");
        throw new PdmRuleException($"该上级项目还可创建{available.Length}个子项目，请减少本次创建行数。");
    }

    public static AuditEntry CreateAudit(Project project, string actor, DateTimeOffset occurredAt) =>
        new(Guid.NewGuid(), occurredAt, actor, project.ParentProjectId.HasValue ? "project.child.create" : "project.create", nameof(Project), project.Id.ToString(),
            $"{project.Code} · {project.Name} · 数量{project.Quantity}");

    public static Project Build(Project parent, string rootCode, string vaultRoot, string releaseRoot, string modelCompanyCode,
        CreateSubprojectItem item, int childSequence, IReadOnlyList<string> serialNumbers)
    {
        var code = ChildCode(parent.Code, childSequence);
        var modelSuffix = string.Join('-', parent.Code.Split('-', StringSplitOptions.RemoveEmptyEntries).Skip(1)
            .Select(segment => int.TryParse(segment, out var number) ? number.ToString("D2") : segment)
            .Append(childSequence.ToString("D2")));
        var equipmentTypeCode = item.EquipmentTypeCode ?? parent.EquipmentTypeCode;
        return new Project(Guid.NewGuid(), code, item.Name, parent.Owner,
            StorageLocationPolicy.ProjectStorageLocation(vaultRoot, rootCode, code),
            StorageLocationPolicy.ProjectStorageLocation(releaseRoot, rootCode, code), true)
        {
            ProjectAlias = item.ProjectAlias,
            OrganizationId = parent.OrganizationId,
            OrganizationName = parent.OrganizationName,
            ProjectTypeCode = parent.ProjectTypeCode,
            EquipmentTypeCode = equipmentTypeCode,
            CustomerCode = parent.CustomerCode,
            CustomerName = parent.CustomerName,
            CustomerProjectSequence = parent.CustomerProjectSequence,
            DeviceModel = $"{modelCompanyCode}-{equipmentTypeCode}-{parent.CustomerCode}-{parent.CustomerProjectSequence:D3}-{modelSuffix}",
            SignedDate = parent.SignedDate,
            Quantity = item.Quantity,
            ParentProjectId = parent.Id,
            RootProjectId = parent.RootProjectId ?? parent.Id,
            ChildSequence = childSequence,
            SerialNumbers = serialNumbers,
            BomItemCategoryCode = "0302",
            IsSpecialInitiation = parent.IsSpecialInitiation,
            DeliveryDate = parent.DeliveryDate,
            ResponsibleUsers = parent.ResponsibleUsers,
            ExecutionUnitId = parent.ExecutionUnitId,
            ExecutionUnitName = parent.ExecutionUnitName,
            PrimaryProjectManager = parent.PrimaryProjectManager,
            CollaborativeProjectManagers = parent.CollaborativeProjectManagers,
            DesignLead = parent.DesignLead,
            DesignLeads = parent.DesignLeads
        };
    }
}
