using Upton.Pdm.Application;

namespace Upton.Pdm.Api;

// 旧配置与回执保留在存储中；运行时停用该模块，业务通知由审批流程配置负责。
public sealed class ReleaseNotificationSettingsService : IReleaseNotificationConfigurationProvider
{
    public Task<(ReleaseNotificationSettings Settings, long Version)> LoadAsync(Guid companyId, CancellationToken ct) =>
        Task.FromResult<(ReleaseNotificationSettings Settings, long Version)>((new(), 0));
}
