using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed class InMemoryMaterialRelationRepository : IMaterialRelationRepository
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, MaterialRelationTemplate> templates = [];
    private readonly Dictionary<Guid, (Guid TemplateId, MaterialRelationRevision Revision)> superseded = [];
    private readonly List<MaterialRelationSelection> selections = [];
    private readonly HashSet<(Guid ProjectId, Guid AccessoryId, Guid MainId)> itemSources = [];
    private readonly List<MaterialRelationReview> reviews = [];

    public Task<IReadOnlyList<MaterialRelationTemplate>> ListTemplatesAsync(bool includeDraft, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<MaterialRelationTemplate>>(templates.Values
                .Where(item => includeDraft || item.PublishedRevision is not null)
                .Select(item => includeDraft ? item : item with { DraftRevision = null })
                .OrderBy(item => item.MainMaterialCode, StringComparer.OrdinalIgnoreCase).ToArray());
        }
    }

    public Task<MaterialRelationTemplate> SaveDraftAsync(Guid? templateId, SaveMaterialRelationTemplateCommand command, string mainMaterialCode, string mainMaterialName, IReadOnlyList<MaterialRelationGroup> groups, string actor, DateTimeOffset now, CancellationToken cancellationToken, IReadOnlyList<MaterialRelationMainMaterial>? mainMaterials = null)
    {
        lock (gate)
        {
            var existing = templateId.HasValue ? templates.GetValueOrDefault(templateId.Value) : mainMaterials is null
                ? templates.Values.FirstOrDefault(item => !item.IsArchived && item.MainMaterials.Concat(item.PublishedMainMaterials).Any(main => main.MaterialId == command.MainMaterialId)) : null;
            if (templateId.HasValue && existing is null) throw new PdmNotFoundException("配套模板不存在。");
            if (existing?.IsArchived == true) throw new PdmRuleException("请先恢复启用该关联配置，再维护草稿。");
            mainMaterials ??= [new(command.MainMaterialId, mainMaterialCode, mainMaterialName)];
            foreach (var main in mainMaterials)
                if (templates.Values.FirstOrDefault(item => item.Id != existing?.Id && !item.IsArchived && item.MainMaterials.Concat(item.PublishedMainMaterials).Any(value => value.MaterialId == main.MaterialId)) is { } conflict)
                    throw new PdmConflictException($"主料{main.MaterialCode}已归属配置“{conflict.Name}”，请先调整原配置的主料范围并发布。");
            if (existing?.DraftRevision is { } draft && command.ExpectedRevisionRowVersion.HasValue && draft.RowVersion != command.ExpectedRevisionRowVersion.Value)
                throw new PdmConflictException("配套模板草稿已被其他用户修改，请刷新后重试。");
            if (existing?.DraftRevision is null && command.ExpectedRevisionRowVersion.HasValue)
                throw new PdmConflictException("草稿已发布或已发生变化，请刷新后重新维护。");
            var revision = new MaterialRelationRevision(existing?.DraftRevision?.Id ?? Guid.NewGuid(), existing?.DraftRevision?.Version ?? ((existing?.PublishedRevision?.Version ?? 0) + 1),
                MaterialRelationRevisionState.Draft, command.ChangeNote?.Trim(), actor, existing?.DraftRevision?.CreatedAt ?? now, null, null,
                (existing?.DraftRevision?.RowVersion ?? 0) + 1, groups) { MainMaterials = mainMaterials.ToArray() };
            var saved = new MaterialRelationTemplate(existing?.Id ?? Guid.NewGuid(), existing?.MainMaterialId ?? command.MainMaterialId,
                existing?.MainMaterialCode ?? mainMaterialCode, existing?.MainMaterialName ?? mainMaterialName, command.Name.Trim(), false,
                existing?.PublishedRevision, revision, actor, now, (existing?.RowVersion ?? 0) + 1);
            templates[saved.Id] = saved;
            return Task.FromResult(saved);
        }
    }

    public Task<MaterialRelationTemplate> PublishAsync(Guid templateId, Guid revisionId, long expectedRowVersion, string actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!templates.TryGetValue(templateId, out var template) || template.DraftRevision is not { } draft || draft.Id != revisionId)
                throw new PdmNotFoundException("待发布配套模板草稿不存在。");
            if (template.IsArchived) throw new PdmRuleException("请先恢复启用该关联配置，再发布草稿。");
            if (draft.RowVersion != expectedRowVersion) throw new PdmConflictException("配套模板草稿已被其他用户修改，请刷新后重试。");
            if (template.PublishedRevision is { } previous)
                superseded[previous.Id] = (templateId, previous with { State = MaterialRelationRevisionState.Superseded });
            var published = draft with { State = MaterialRelationRevisionState.Published, PublishedBy = actor, PublishedAt = now, RowVersion = draft.RowVersion + 1 };
            var saved = template with { PublishedRevision = published, DraftRevision = null, UpdatedBy = actor, UpdatedAt = now, RowVersion = template.RowVersion + 1 };
            templates[templateId] = saved;
            return Task.FromResult(saved);
        }
    }

    public Task<IReadOnlyList<MaterialRelationTemplate>> ListRetiredRevisionsAsync(IReadOnlyCollection<Guid> revisionIds, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var archived = templates.Values.Where(template => template.IsArchived).ToDictionary(template => template.Id);
            var current = archived.Values.Where(template => template.PublishedRevision is { } revision && revisionIds.Contains(revision.Id))
                .Select(template => template with { DraftRevision = null });
            var history = revisionIds.Where(superseded.ContainsKey).Select(id => superseded[id])
                .Where(item => archived.ContainsKey(item.TemplateId))
                .Select(item => archived[item.TemplateId] with { PublishedRevision = item.Revision, DraftRevision = null });
            return Task.FromResult<IReadOnlyList<MaterialRelationTemplate>>(current.Concat(history).ToArray());
        }
    }

    public Task<MaterialRelationTemplate> SetActiveAsync(Guid templateId, bool isActive, long expectedRowVersion, string actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var template = RequireVersion(templateId, expectedRowVersion);
            if (template.PublishedRevision is null) throw new PdmRuleException("关联配置发布后才可停用或恢复启用。");
            if (template.IsArchived != isActive) return Task.FromResult(template);
            if (isActive)
            {
                var mainIds = template.MainMaterials.Concat(template.PublishedMainMaterials).Select(main => main.MaterialId).ToHashSet();
                var conflict = templates.Values.FirstOrDefault(item => item.Id != templateId && !item.IsArchived
                    && item.MainMaterials.Concat(item.PublishedMainMaterials).Any(main => mainIds.Contains(main.MaterialId)));
                if (conflict is not null) throw new PdmConflictException($"主料已归属配置“{conflict.Name}”，请调整该配置后再恢复启用。");
            }
            var saved = template with { IsArchived = !isActive, UpdatedBy = actor, UpdatedAt = now, RowVersion = template.RowVersion + 1 };
            templates[templateId] = saved;
            return Task.FromResult(saved);
        }
    }

    public Task DeleteDraftAsync(Guid templateId, long expectedRowVersion, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var template = RequireVersion(templateId, expectedRowVersion);
            if (template.PublishedRevision is not null || superseded.Values.Any(item => item.TemplateId == templateId))
                throw new PdmRuleException("已发布过的关联配置请使用停用操作。");
            if (template.DraftRevision is { } draft &&
                (selections.Any(item => item.RevisionId == draft.Id) || reviews.Any(item => item.RevisionId == draft.Id)))
                throw new PdmConflictException("此关联配置已有引用，请解除引用后再删除。");
            templates.Remove(templateId);
            return Task.CompletedTask;
        }
    }

    private MaterialRelationTemplate RequireVersion(Guid templateId, long expectedRowVersion)
    {
        if (!templates.TryGetValue(templateId, out var template)) throw new PdmNotFoundException("关联配置不存在。");
        if (template.RowVersion != expectedRowVersion) throw new PdmConflictException("关联配置已被修改，请刷新后重试。");
        return template;
    }

    public Task<IReadOnlyList<MaterialRelationSelection>> ListSelectionsAsync(Guid projectId, CancellationToken cancellationToken)
    {
        lock (gate) return Task.FromResult<IReadOnlyList<MaterialRelationSelection>>(selections.Where(item => item.ProjectId == projectId).ToArray());
    }

    public Task ReplaceSelectionsAsync(Guid projectId, Guid mainBomItemId, IReadOnlyList<MaterialRelationSelection> items, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            foreach (var item in selections.Where(item => item.ProjectId == projectId && item.MainBomItemId == mainBomItemId).Concat(items))
                itemSources.Add((item.ProjectId, item.AccessoryBomItemId, item.MainBomItemId));
            selections.RemoveAll(item => item.ProjectId == projectId && item.MainBomItemId == mainBomItemId);
            selections.AddRange(items);
            return Task.CompletedTask;
        }
    }

    public Task<IReadOnlyList<Guid>> ListRelatedMainBomItemIdsAsync(Guid projectId, IReadOnlyCollection<Guid> accessoryIds, CancellationToken ct)
    {
        lock (gate) return Task.FromResult<IReadOnlyList<Guid>>(itemSources.Where(item => item.ProjectId == projectId && accessoryIds.Contains(item.AccessoryId))
            .Select(item => item.MainId).Distinct().ToArray());
    }

    public Task<IReadOnlyList<MaterialRelationReview>> ListReviewsAsync(Guid projectId, CancellationToken cancellationToken)
    {
        lock (gate) return Task.FromResult<IReadOnlyList<MaterialRelationReview>>(reviews.Where(item => item.ProjectId == projectId).ToArray());
    }

    public Task ReplaceReviewsAsync(Guid projectId, Guid mainBomItemId, IReadOnlyList<MaterialRelationReview> items, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            reviews.RemoveAll(item => item.ProjectId == projectId && item.MainBomItemId == mainBomItemId);
            reviews.AddRange(items);
            return Task.CompletedTask;
        }
    }
}
