using System.Text.Json;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed partial class StandardStructureService
{
    private static bool CategoryActive(StandardStructureCatalog catalog, Guid id)
    {
        var byId = catalog.Categories.ToDictionary(c => c.Id);
        var visited = new HashSet<Guid>();
        while (byId.TryGetValue(id, out var current))
        {
            if (!current.IsActive || !visited.Add(current.Id)) return false;
            if (current.ParentId is not { } parent) return true;
            id = parent;
        }
        return false;
    }

    public async Task<StandardStructureCatalog> ChangeStateAsync(Guid id, ChangeStandardStructureState input, string actor, UserRole role, CancellationToken ct)
    {
        await RequireView(actor, role, ct);
        if (!await CanManage(actor, role, ct)) throw new UnauthorizedAccessException("当前账号没有管理标准结构状态的权限。");
        await ReferenceGate.WaitAsync(ct);
        try
        {
            var catalog = await store.ReadAsync(GroupScope(), ct);
            CheckVersion(catalog, input.ExpectedRowVersion);
            var item = catalog.Structures.FirstOrDefault(s => s.Id == id) ?? throw new PdmNotFoundException("标准结构不存在。");
            if (input.Action != "Restore" && item.DeletedAt is not null) throw new PdmRuleException("该标准结构已在回收站，请先恢复。");
            StandardStructure next;
            switch (input.Action)
            {
                case "Disable": next = item with { IsActive = false }; break;
                case "Enable": next = item with { IsActive = true }; break;
                case "Delete":
                    if ((await References(item, ct)).Count > 0)
                        throw new PdmRuleException("该标准结构已有项目引用或待核对引用，不能删除；可停用并通过“查看引用”核对使用情况。");
                    next = item with { DeletedAt = clock.GetUtcNow() };
                    break;
                case "Restore":
                    if (item.DeletedAt is null) throw new PdmRuleException("该标准结构不在回收站，无需恢复。");
                    next = item with { DeletedAt = null };
                    break;
                default: throw new PdmRuleException("标准结构状态操作无效，请刷新后重试。");
            }
            var actionName = input.Action switch { "Disable" => "停用", "Enable" => "启用", "Delete" => "移入回收站", _ => "恢复" };
            return await Save(catalog with { Structures = catalog.Structures.Select(s => s.Id == id ? next : s).ToArray() }, actor,
                input.Action.ToLowerInvariant(), id, $"{actionName}整套标准结构 {item.Code}，包含 {item.Revisions.Count} 个历史版本及其完整图档、BOM；保留存档文件。", ct);
        }
        finally { ReferenceGate.Release(); }
    }

    public async Task<IReadOnlyList<StandardStructureReference>> ListReferencesAsync(Guid id, string actor, UserRole role, CancellationToken ct)
    {
        await RequireView(actor, role, ct);
        if (!await CanManage(actor, role, ct)) throw new UnauthorizedAccessException("当前账号不能查看标准结构的项目引用。");
        var catalog = await store.ReadAsync(GroupScope(), ct);
        var item = catalog.Structures.FirstOrDefault(s => s.Id == id) ?? throw new PdmNotFoundException("标准结构不存在。");
        var result = new List<StandardStructureReference>();
        foreach (var reference in await References(item, ct))
        {
            var canRead = await repository.HasProjectReadAccessAsync(reference.ProjectId, actor, role, ct);
            var project = canRead ? await repository.FindProjectAsync(reference.ProjectId, ct) : null;
            result.Add(reference with { ProjectCode = project?.Code ?? "受限或已归档项目" });
        }
        return result;
    }

    private sealed record LegacyReference(ReferenceStandardStructure? Input, string? Code, int Version);
    private async Task<IReadOnlyList<StandardStructureReference>> References(StandardStructure item, CancellationToken ct)
    {
        var references = item.References.ToList();
        // 旧引用保存在审计中；使用专门查询，不受普通审计的500条上限及操作人筛选影响。
        foreach (var audit in await repository.ListStandardStructureReferenceAuditsAsync(ct))
        {
            LegacyReference? legacy;
            try { legacy = JsonSerializer.Deserialize<LegacyReference>(audit.Detail, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
            catch (JsonException) { throw new PdmRuleException("历史引用记录无法核对，请联系管理员检查，暂不能删除标准结构。"); }
            if (legacy?.Code is null) throw new PdmRuleException("历史引用记录不完整，请联系管理员检查，暂不能删除标准结构。");
            if (legacy.Code != item.Code) continue;
            if (legacy.Input is not { } input || !item.Revisions.Any(r => r.Id == input.RevisionId))
                throw new PdmRuleException("历史引用版本无法核对，请联系管理员检查，暂不能删除标准结构。");
            references.Add(new(input.ProjectId, input.RevisionId, legacy.Version, input.Kind, input.Quantity, input.RequestId, audit.OccurredAt, audit.Actor));
        }
        return references.GroupBy(r => (r.RequestId, r.RevisionId, r.Kind)).Select(group => group.OrderBy(r => r.Pending).First())
            .OrderByDescending(r => r.ReferencedAt).ToArray();
    }

    private async Task SaveReference(Guid id, ReferenceStandardStructure input, int version, string actor, bool pending, CancellationToken ct)
    {
        var catalog = await store.ReadAsync(GroupScope(), ct);
        var item = catalog.Structures.Single(s => s.Id == id);
        if (pending && (item.DeletedAt is not null || !item.IsActive || !CategoryActive(catalog, item.CategoryId)))
            throw new PdmRuleException("该标准结构或所属分类已停用或删除，不能新增项目引用。");
        var previous = item.References.FirstOrDefault(r => r.RequestId == input.RequestId && r.Kind == input.Kind);
        if (previous is not null && (previous.RevisionId != input.RevisionId || previous.ProjectId != input.ProjectId || previous.Quantity != input.Quantity))
            throw new PdmConflictException("引用请求内容已变化，请重新打开引用窗口后操作。");
        var reference = previous is null
            ? new StandardStructureReference(input.ProjectId, input.RevisionId, version, input.Kind, input.Quantity, input.RequestId, clock.GetUtcNow(), actor, pending)
            : previous with { Pending = pending };
        var next = item with { References = item.References.Where(r => r.RequestId != input.RequestId || r.Kind != input.Kind).Append(reference).ToArray() };
        // 先保留引用占用，再写项目BOM。中途失败仍阻止删除，避免丢失已经写入的项目引用。
        await Save(catalog with { Structures = catalog.Structures.Select(s => s.Id == id ? next : s).ToArray() }, actor,
            pending ? "reference.reserve" : "reference.confirm", id, $"{item.Code} / {StandardStructureRules.Version(version)}；项目 {input.ProjectId}；{(pending ? "引用处理中，失败后需核对" : "引用已完成")}", ct);
    }
}
