using System.Collections.Concurrent;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed class InMemoryEngineeringKitRepository(IPdmRepository? repository = null) : IEngineeringKitRepository
{
    private readonly object gate = new();
    private readonly ConcurrentDictionary<Guid, EngineeringKit> values = new();
    private readonly ConcurrentDictionary<Guid, MaterialAttachment> attachments = new();
    private long currentSequence;
    private readonly Dictionary<string, long> modelSequences = new(StringComparer.Ordinal);

    public async Task<IReadOnlyList<EngineeringKit>> ListAsync(bool releasedOnly, CancellationToken cancellationToken)
    {
        var result = new List<EngineeringKit>();
        foreach (var kit in values.Values.Where(item => !releasedOnly || item.CurrentReleasedRevisionId.HasValue)
            .OrderBy(item => item.Code is null).ThenBy(item => item.Code).ThenBy(item => item.Name))
            result.Add(await WithLibraryCountsAsync(kit, cancellationToken));
        return result;
    }

    public async Task<EngineeringKit?> FindAsync(Guid kitId, CancellationToken cancellationToken) =>
        values.TryGetValue(kitId, out var kit) ? await WithLibraryCountsAsync(kit, cancellationToken) : null;

    private async Task<EngineeringKit> WithLibraryCountsAsync(EngineeringKit kit, CancellationToken cancellationToken)
    {
        var references = new HashSet<(Guid, Guid)>();
        if (repository is not null)
            foreach (var project in await repository.ListProjectsAsync(cancellationToken))
                foreach (var kind in Enum.GetValues<BomKind>())
                    foreach (var item in await repository.GetBomAsync(project.Id, kind, cancellationToken))
                        if (item.EngineeringKitId == kit.Id) references.Add((project.Id, item.EngineeringKitReferenceId ?? item.Id));
        return kit with { ReferenceCount = references.Count,
            Model3DAttachmentCount = attachments.Values.Count(item => item.MaterialId == kit.Id && item.Kind == MaterialAttachmentKind.Model3D),
            DocumentAttachmentCount = attachments.Values.Count(item => item.MaterialId == kit.Id && item.Kind == MaterialAttachmentKind.Document) };
    }

    public Task<IReadOnlyList<MaterialAttachment>> ListLibraryAttachmentsAsync(Guid kitId, MaterialAttachmentKind? kind, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<MaterialAttachment>>(attachments.Values.Where(item => item.MaterialId == kitId && (kind is null || item.Kind == kind))
            .OrderByDescending(item => item.UploadedAt).ToArray());
    public Task<MaterialAttachment?> FindLibraryAttachmentAsync(Guid attachmentId, CancellationToken cancellationToken) =>
        Task.FromResult(attachments.GetValueOrDefault(attachmentId));
    public Task<MaterialAttachment> CreateLibraryAttachmentAsync(MaterialAttachment attachment, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!values.TryGetValue(attachment.MaterialId, out var kit)) throw new PdmNotFoundException("套件不存在。");
            if (!kit.IsActive || kit.CurrentReleasedRevisionId is null) throw new PdmConflictException("套件已停用或尚未发布，附件未入库。");
            if (attachment.Kind == MaterialAttachmentKind.CoverImage && attachments.Values.Count(item => item.MaterialId == kit.Id && item.Kind == MaterialAttachmentKind.CoverImage) >= 3)
                throw new PdmRuleException("每个套件最多上传3张图片。");
            if (!attachments.TryAdd(attachment.Id, attachment)) throw new PdmConflictException("附件已存在。");
        }
        return Task.FromResult(attachment);
    }

    public Task<bool> DeleteLibraryAttachmentAsync(Guid kitId, Guid attachmentId, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!values.TryGetValue(kitId, out var kit) || !kit.IsActive || kit.CurrentReleasedRevisionId is null
                || !attachments.TryGetValue(attachmentId, out var attachment) || attachment.MaterialId != kitId
                || attachment.Kind is not (MaterialAttachmentKind.Model3D or MaterialAttachmentKind.Document))
                return Task.FromResult(false);
            return Task.FromResult(attachments.TryRemove(attachmentId, out _));
        }
    }

    public async Task<EngineeringKit> UpdateLibraryMetadataAsync(EngineeringKit kit, long expectedRowVersion, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!values.TryGetValue(kit.Id, out var current) || current.RowVersion != expectedRowVersion || !current.IsActive || current.CurrentReleasedRevisionId is null)
                throw new PdmConflictException("套件已被其他用户修改或停用，请刷新后重试。");
            values[kit.Id] = current with { Description = kit.Description, SelectionAdvice = kit.SelectionAdvice, ReferencePrice = kit.ReferencePrice,
                IsRecommended = kit.IsRecommended, CoverImageAttachmentId = kit.CoverImageAttachmentId, UpdatedBy = kit.UpdatedBy, UpdatedAt = kit.UpdatedAt, RowVersion = current.RowVersion + 1 };
        }
        return (await FindAsync(kit.Id, cancellationToken))!;
    }

    public async Task<EngineeringKit> SetActiveAsync(Guid kitId, bool isActive, long expectedRowVersion, string actor, DateTimeOffset updatedAt, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!values.TryGetValue(kitId, out var kit) || kit.RowVersion != expectedRowVersion)
                throw new PdmConflictException("套件不存在或已被其他用户修改，请刷新后重试。");
            values[kitId] = kit with { IsActive = isActive, UpdatedBy = actor, UpdatedAt = updatedAt, RowVersion = kit.RowVersion + 1 };
        }
        return (await FindAsync(kitId, cancellationToken))!;
    }

    public Task<EngineeringKit> SaveDraftAsync(EngineeringKit kit, EngineeringKitRevision revision, long? expectedRowVersion, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!values.TryGetValue(kit.Id, out var current))
            {
                if (expectedRowVersion is not null) throw new PdmConflictException("套件不存在或已被其他用户修改。");
                if (!values.TryAdd(kit.Id, kit)) throw new PdmConflictException("套件已经存在。");
                return Task.FromResult(kit);
            }
            if (expectedRowVersion is null || current.RowVersion != expectedRowVersion.Value)
                throw new PdmConflictException("套件已被其他用户修改，请刷新后重试。");
            var revisions = current.Revisions.Where(item => item.State != EngineeringKitRevisionState.Draft).Append(revision)
                .OrderBy(item => item.VersionNumber).ToArray();
            var saved = kit with { Revisions = revisions, RowVersion = current.RowVersion + 1 };
            values[kit.Id] = saved;
            return Task.FromResult(saved);
        }
    }

    public Task<EngineeringKit> PublishAsync(Guid kitId, long expectedRowVersion, string actor, DateTimeOffset publishedAt, string? modelCategoryCode, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!values.TryGetValue(kitId, out var current) || current.RowVersion != expectedRowVersion)
                throw new PdmConflictException("套件已被其他用户修改，请刷新后重试。");
            var draft = current.DraftRevision ?? throw new PdmRuleException("套件没有待发布草稿。");
            if (!current.IsActive) throw new PdmRuleException("套件已停用，请恢复启用后再发布。");
            var released = draft with { State = EngineeringKitRevisionState.Released, PublishedBy = actor, PublishedAt = publishedAt };
            var revisions = current.Revisions.Select(item => item.Id == draft.Id ? released : item).ToArray();
            var model = current.Model?.Trim();
            if (string.IsNullOrWhiteSpace(model))
            {
                var sequence = modelSequences.GetValueOrDefault(modelCategoryCode ?? string.Empty) + 1;
                model = EngineeringKitNumbering.Model(modelCategoryCode, sequence);
                modelSequences[modelCategoryCode!] = sequence;
            }
            var code = current.Code ?? $"UKIT-{++currentSequence:D6}";
            var saved = current with
            {
                Code = code,
                Model = model,
                CategoryCode = modelCategoryCode,
                CurrentReleasedRevisionId = released.Id,
                StandardLibraryCategoryId = draft.StandardLibraryCategoryId,
                Revisions = revisions,
                UpdatedBy = actor,
                UpdatedAt = publishedAt,
                RowVersion = current.RowVersion + 1
            };
            values[kitId] = saved;
            return Task.FromResult(saved);
        }
    }

    public Task<EngineeringKit> MoveCategoryAsync(Guid kitId, Guid categoryId, long expectedRowVersion, string actor, DateTimeOffset updatedAt, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!values.TryGetValue(kitId, out var kit) || kit.RowVersion != expectedRowVersion || kit.CurrentReleasedRevisionId is null)
                throw new PdmConflictException("套件未发布或已被其他用户修改，请刷新后重试。");
            var saved = kit with { StandardLibraryCategoryId = categoryId, UpdatedBy = actor, UpdatedAt = updatedAt, RowVersion = kit.RowVersion + 1 };
            values[kitId] = saved;
            return Task.FromResult(saved);
        }
    }
}
