using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed partial class InMemoryPdmRepository
{
    private readonly List<ReleaseItemComment> releaseItemComments = [];

    public Task<IReadOnlyList<ReleaseItemComment>> ListReleaseItemCommentsAsync(Guid releasePackageId, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<ReleaseItemComment>>(releaseItemComments
                .Where(item => item.ReleasePackageId == releasePackageId)
                .OrderBy(item => item.CreatedAt)
                .ThenBy(item => item.Id)
                .ToArray());
        }
    }

    public Task<ReleaseItemComment> AddReleaseItemCommentAsync(ReleaseItemComment comment, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!packages.ContainsKey(comment.ReleasePackageId)) throw new PdmNotFoundException("发布包不存在。");
            releaseItemComments.Add(comment);
            return Task.FromResult(comment);
        }
    }
}
