using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed record ReleaseNotificationRule(Guid Id, Guid? ProjectId, string BomKind, string Event,
    IReadOnlyList<Guid> DepartmentIds, bool IncludeChildren, IReadOnlyList<string> Channels,
    Guid? DingTalkGroupId, bool RequireExecution);

public sealed record ReleaseMailSettings(string Host = "", int Port = 587, string From = "", string Username = "", string Password = "");

public sealed record ReleaseNotificationSettings
{
    public bool Enabled { get; init; }
    public DateTimeOffset? EnabledAt { get; init; }
    public string PublicUrl { get; init; } = "";
    public IReadOnlyList<ReleaseNotificationRule> Rules { get; init; } = [];
    public ReleaseMailSettings Mail { get; init; } = new();
}

public sealed record ReleaseNotificationConfiguration(Guid CompanyId, string ProtectedPayload, long RowVersion, bool Enabled = false, DateTimeOffset? EnabledAt = null);
public sealed record ReleaseNoticeRecipient(string Username, string? Email);
public sealed record ReleaseDepartmentReceipt
{
    public required Guid Id { get; init; }
    public required Guid DepartmentId { get; init; }
    public required string DepartmentName { get; init; }
    public required string Event { get; init; }
    public bool IncludeChildren { get; init; }
    public bool RequireExecution { get; init; }
    public IReadOnlyList<ReleaseNoticeRecipient> Recipients { get; init; } = [];
    public string? ReceivedBy { get; init; }
    public DateTimeOffset? ReceivedAt { get; init; }
    public string? ExecutedBy { get; init; }
    public DateTimeOffset? ExecutedAt { get; init; }
    public string? ExecutionNote { get; init; }
}

public sealed record ReleaseChannelDelivery
{
    public required Guid Id { get; init; }
    public required string Event { get; init; }
    public required string Channel { get; init; }
    public required string Destination { get; init; }
    public IReadOnlyList<Guid> DepartmentIds { get; init; } = [];
    public string State { get; init; } = "Pending";
    public int Attempts { get; init; }
    public DateTimeOffset? NextAttemptAt { get; init; }
    public string? Message { get; init; }
    public string? ProviderReceipt { get; init; }
    public DateTimeOffset? SentAt { get; init; }
}

public sealed record ReleaseIntegrationResult(string State, string Message, DateTimeOffset? ConfirmedAt = null);

public sealed record ReleaseDeliveryState
{
    public required Guid ReleasePackageId { get; init; }
    public required Guid ProjectId { get; init; }
    public required Guid CompanyId { get; init; }
    public required string PackageNumber { get; init; }
    public required string ProjectCode { get; init; }
    public required DateTimeOffset PublishedAt { get; init; }
    public string? ChangeNumber { get; init; }
    public string? ChangeReason { get; init; }
    public string? EffectiveSerialFrom { get; init; }
    public string? EffectiveSerialTo { get; init; }
    public IReadOnlyList<ReleaseVersionBinding> VersionBindings { get; init; } = [];
    public IReadOnlyList<Guid> SelectedBomItemIds { get; init; } = [];
    public IReadOnlyList<Guid> ConfirmedRemovedBomItemIds { get; init; } = [];
    public Dictionary<string, Guid> BomVersionIds { get; init; } = [];
    public Dictionary<string, ReleaseIntegrationResult> U9 { get; init; } = [];
    public string PreviewState { get; init; } = ReleasePreviewState.None;
    public bool PreviewRequired { get; init; }
    public string? PreviewMessage { get; init; }
    public long NotificationConfigurationVersion { get; init; }
    public IReadOnlyList<ReleaseNotificationRule> NotificationRules { get; init; } = [];
    public IReadOnlyList<ReleaseDepartmentReceipt> Departments { get; init; } = [];
    public IReadOnlyList<ReleaseChannelDelivery> Deliveries { get; init; } = [];
    public bool ReadyEventCreated { get; init; }
    public string PublicUrl { get; init; } = "";
}

public sealed record ReleaseDeliveryRecord(ReleaseDeliveryState State, long RowVersion, DateTimeOffset? NextCheckAt);
public sealed record ReleaseDeliveryClaim(Guid ReleasePackageId, Guid CompanyId, Guid LeaseId, DateTimeOffset LeaseUntil);
public sealed record ReleaseSendResult(string State, string Message, string? Receipt = null);

public interface IReleaseDeliveryStore
{
    Task<ReleaseNotificationConfiguration?> FindConfigurationAsync(Guid companyId, CancellationToken ct);
    Task<ReleaseNotificationConfiguration> SaveConfigurationAsync(ReleaseNotificationConfiguration value, long expectedVersion, CancellationToken ct);
    Task<ReleaseDeliveryRecord?> FindAsync(Guid releasePackageId, CancellationToken ct);
    Task<ReleaseDeliveryRecord> CreateAsync(ReleaseDeliveryState value, DateTimeOffset nextCheckAt, CancellationToken ct);
    Task<bool> SaveAsync(ReleaseDeliveryRecord value, long expectedVersion, CancellationToken ct);
    Task<IReadOnlyList<ReleaseDeliveryRecord>> ListProjectAsync(Guid projectId, CancellationToken ct);
    Task<IReadOnlyList<ReleaseDeliveryClaim>> ClaimDueAsync(DateTimeOffset now, int count, CancellationToken ct);
    Task<bool> RenewClaimAsync(ReleaseDeliveryClaim claim, DateTimeOffset now, CancellationToken ct);
    Task<IReadOnlyList<Guid>> ListUntrackedPublishedAsync(int count, CancellationToken ct);
    Task ReleaseClaimAsync(ReleaseDeliveryClaim claim, CancellationToken ct);
}

public interface IReleaseNotificationConfigurationProvider
{
    Task<(ReleaseNotificationSettings Settings, long Version)> LoadAsync(Guid companyId, CancellationToken ct);
}

public interface IReleaseNotificationSender
{
    Task<ReleaseSendResult> SendAsync(ReleaseDeliveryState state, ReleaseChannelDelivery delivery, CancellationToken ct);
}

public interface IReleaseDeliveryObserver
{
    Task PublishedAsync(Guid releasePackageId, CancellationToken ct);
    Task U9ResultAsync(Guid projectId, ProjectBomHeaderKind kind, U9ReleaseScope sourceScope, string state, string message, CancellationToken ct);
}
