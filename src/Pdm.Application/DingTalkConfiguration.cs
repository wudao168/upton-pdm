namespace Upton.Pdm.Application;

public sealed record DingTalkConfiguration(Guid CompanyId, string ProtectedPayload, long RowVersion);
public interface IDingTalkConfigurationRepository
{
    Task<DingTalkConfiguration?> FindAsync(Guid companyId, CancellationToken ct);
    Task<IReadOnlyList<DingTalkConfiguration>> ListAsync(CancellationToken ct);
    Task<DingTalkConfiguration> SaveAsync(DingTalkConfiguration value, long expectedVersion, CancellationToken ct);
}
