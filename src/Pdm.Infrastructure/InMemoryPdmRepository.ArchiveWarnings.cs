using Upton.Pdm.Domain;
using Upton.Pdm.Application;

namespace Upton.Pdm.Infrastructure;

public sealed partial class InMemoryPdmRepository
{
    private readonly object archivePolicySync = new();
    private ArchiveWarningPolicy archivePolicy = new();
    private static ArchiveWarningPolicy CopyArchivePolicy(ArchiveWarningPolicy policy) => new() {
        Revision = policy.Revision, BlockAll = policy.BlockAll, BlockingCodes = policy.BlockingCodes.ToArray() };
    public Task<ArchiveWarningPolicy> GetArchiveWarningPolicyAsync(CancellationToken ct)
    {
        lock (archivePolicySync) return Task.FromResult(CopyArchivePolicy(archivePolicy));
    }
    public Task<ArchiveWarningPolicy> SaveArchiveWarningPolicyAsync(ArchiveWarningPolicy input, CancellationToken ct)
    {
        ArchiveWarningPolicy.Validate(input);
        lock (archivePolicySync)
        {
            if (archivePolicy.Revision != input.Revision) throw new PdmConflictException("存档警告策略已被修改，请刷新后重试。");
            archivePolicy = CopyArchivePolicy(input);
            archivePolicy.Revision++;
            return Task.FromResult(CopyArchivePolicy(archivePolicy));
        }
    }
}
