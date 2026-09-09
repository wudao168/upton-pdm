using System.Collections.Concurrent;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed class InMemoryEngineeringKitRepository : IEngineeringKitRepository
{
    private readonly object gate = new();
    private readonly ConcurrentDictionary<Guid, EngineeringKit> values = new();
    private long currentSequence;

    public Task<IReadOnlyList<EngineeringKit>> ListAsync(bool releasedOnly, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<EngineeringKit>>(values.Values
            .Where(item => !releasedOnly || item.CurrentReleasedRevisionId.HasValue)
            .OrderBy(item => item.Code is null).ThenBy(item => item.Code).ThenBy(item => item.Name)
            .ToArray());

    public Task<EngineeringKit?> FindAsync(Guid kitId, CancellationToken cancellationToken) =>
        Task.FromResult(values.GetValueOrDefault(kitId));

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

    public Task<EngineeringKit> PublishAsync(Guid kitId, long expectedRowVersion, string actor, DateTimeOffset publishedAt, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!values.TryGetValue(kitId, out var current) || current.RowVersion != expectedRowVersion)
                throw new PdmConflictException("套件已被其他用户修改，请刷新后重试。");
            var draft = current.DraftRevision ?? throw new PdmRuleException("套件没有待发布草稿。");
            var released = draft with { State = EngineeringKitRevisionState.Released, PublishedBy = actor, PublishedAt = publishedAt };
            var revisions = current.Revisions.Select(item => item.Id == draft.Id ? released : item).ToArray();
            var code = current.Code ?? $"UKIT-{++currentSequence:D6}";
            var saved = current with
            {
                Code = code,
                CurrentReleasedRevisionId = released.Id,
                Revisions = revisions,
                UpdatedBy = actor,
                UpdatedAt = publishedAt,
                RowVersion = current.RowVersion + 1
            };
            values[kitId] = saved;
            return Task.FromResult(saved);
        }
    }
}
