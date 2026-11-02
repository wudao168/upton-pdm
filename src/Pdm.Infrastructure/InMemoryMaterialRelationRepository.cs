using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed class InMemoryMaterialRelationRepository : IMaterialRelationRepository
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, MaterialRelationTemplate> templates = [];
    private readonly List<MaterialRelationSelection> selections = [];
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
                ? templates.Values.FirstOrDefault(item => item.MainMaterials.Concat(item.PublishedMainMaterials).Any(main => main.MaterialId == command.MainMaterialId)) : null;
            if (templateId.HasValue && existing is null) throw new PdmNotFoundException("配套模板不存在。");
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
            if (draft.RowVersion != expectedRowVersion) throw new PdmConflictException("配套模板草稿已被其他用户修改，请刷新后重试。");
            var published = draft with { State = MaterialRelationRevisionState.Published, PublishedBy = actor, PublishedAt = now, RowVersion = draft.RowVersion + 1 };
            var saved = template with { PublishedRevision = published, DraftRevision = null, UpdatedBy = actor, UpdatedAt = now, RowVersion = template.RowVersion + 1 };
            templates[templateId] = saved;
            return Task.FromResult(saved);
        }
    }

    public Task<IReadOnlyList<MaterialRelationSelection>> ListSelectionsAsync(Guid projectId, CancellationToken cancellationToken)
    {
        lock (gate) return Task.FromResult<IReadOnlyList<MaterialRelationSelection>>(selections.Where(item => item.ProjectId == projectId).ToArray());
    }

    public Task ReplaceSelectionsAsync(Guid projectId, Guid mainBomItemId, IReadOnlyList<MaterialRelationSelection> items, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            selections.RemoveAll(item => item.ProjectId == projectId && item.MainBomItemId == mainBomItemId);
            selections.AddRange(items);
            return Task.CompletedTask;
        }
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
