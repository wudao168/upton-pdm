namespace Upton.Pdm.Application;

public sealed record AiModel(string Id, string Name, bool Enabled = true, bool Tools = false, int ContextTokens = 32768, int OutputTokens = 2048);
public sealed record AiProvider(Guid Id, string Name, string BaseUrl, string Network, string Adapter, string? ProtectedKey,
    bool Enabled, AiModel[] Models);
public sealed record AiWebSettings(bool Enabled = false, Guid? ProviderId = null, int DailyLimit = 100, int MonthlyLimit = 1000, int PerTurnLimit = 3);
public sealed record AiWebUsage(int Today, int Month);
public sealed record AiSettings(long Revision, AiProvider[] Providers, string? DefaultModel = null, AiWebSettings? Web = null);
public sealed record AiReference(string Name, Guid? ProjectId, string[] Permissions, string Kind, string? Url = null, string? Date = null);
public sealed record AiMessage(Guid Id, string Role, string Text, DateTimeOffset CreatedAt, string Status = "complete", AiReference[]? Sources = null,
    long? InputTokens = null, long? OutputTokens = null);
public sealed record AiConversation(Guid Id, Guid CompanyId, Guid UserId, long Revision, string Title, string Model,
    bool Business, AiMessage[] Messages, DateTimeOffset UpdatedAt, DateTimeOffset? BusyUntil = null, Guid? LastRequestId = null, bool Web = false);

public interface IAiStore
{
    Task<AiSettings> SettingsAsync(Guid company, CancellationToken ct);
    Task SaveSettingsAsync(Guid company, AiSettings settings, CancellationToken ct);
    Task<AiWebUsage> WebUsageAsync(Guid company, DateTimeOffset at, CancellationToken ct);
    Task<bool> ReserveWebCallAsync(Guid company, DateTimeOffset at, AiWebSettings limits, CancellationToken ct);
    Task<IReadOnlyList<AiConversation>> ListAsync(Guid company, Guid user, CancellationToken ct);
    Task<AiConversation?> FindAsync(Guid company, Guid user, Guid id, CancellationToken ct);
    Task SaveAsync(AiConversation conversation, CancellationToken ct);
    Task DeleteAsync(Guid company, Guid user, Guid id, long revision, CancellationToken ct);
}
