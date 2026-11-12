using System.Text.Json;
using Dapper;
using Upton.Pdm.Application;

namespace Upton.Pdm.Infrastructure;

public sealed partial class MySqlPdmRepository : IAiStore
{
    async Task<AiSettings> IAiStore.SettingsAsync(Guid company, CancellationToken ct)
    {
        await using var db = await OpenAsync(ct);
        var json = await db.QuerySingleOrDefaultAsync<string>(new CommandDefinition("SELECT payload_json FROM ai_settings WHERE company_id=@company", new { company }, cancellationToken: ct));
        return json is null ? new(0, []) : JsonSerializer.Deserialize<AiSettings>(json)!;
    }
    async Task IAiStore.SaveSettingsAsync(Guid company, AiSettings settings, CancellationToken ct)
    {
        await using var db = await OpenAsync(ct);
        var sql = settings.Revision == 0 ? "INSERT IGNORE INTO ai_settings VALUES(@company,1,@json)"
            : "UPDATE ai_settings SET revision=revision+1,payload_json=@json WHERE company_id=@company AND revision=@revision";
        if (await db.ExecuteAsync(new CommandDefinition(sql, new { company, revision = settings.Revision,
            json = JsonSerializer.Serialize(settings with { Revision = settings.Revision + 1 }) }, cancellationToken: ct)) != 1)
            throw new PdmConflictException("AI配置已被修改，请刷新后重试。");
    }
    async Task<IReadOnlyList<AiConversation>> IAiStore.ListAsync(Guid company, Guid user, CancellationToken ct)
    {
        await using var db = await OpenAsync(ct);
        var rows = await db.QueryAsync<string>(new CommandDefinition("SELECT payload_json FROM ai_conversations WHERE company_id=@company AND user_id=@user ORDER BY updated_at DESC LIMIT 100", new { company, user }, cancellationToken: ct));
        return rows.Select(row => JsonSerializer.Deserialize<AiConversation>(row)!).ToArray();
    }
    async Task<AiConversation?> IAiStore.FindAsync(Guid company, Guid user, Guid id, CancellationToken ct)
    {
        await using var db = await OpenAsync(ct);
        var json = await db.QuerySingleOrDefaultAsync<string>(new CommandDefinition("SELECT payload_json FROM ai_conversations WHERE id=@id AND company_id=@company AND user_id=@user", new { company, user, id }, cancellationToken: ct));
        return json is null ? null : JsonSerializer.Deserialize<AiConversation>(json);
    }
    async Task IAiStore.SaveAsync(AiConversation conversation, CancellationToken ct)
    {
        await using var db = await OpenAsync(ct);
        var sql = conversation.Revision == 0 ? "INSERT IGNORE INTO ai_conversations VALUES(@id,@company,@user,1,@at,@json)"
            : "UPDATE ai_conversations SET revision=revision+1,updated_at=@at,payload_json=@json WHERE id=@id AND company_id=@company AND user_id=@user AND revision=@revision";
        if (await db.ExecuteAsync(new CommandDefinition(sql, new { id = conversation.Id, company = conversation.CompanyId, user = conversation.UserId,
            revision = conversation.Revision, at = conversation.UpdatedAt.UtcDateTime, json = JsonSerializer.Serialize(conversation with { Revision = conversation.Revision + 1 }) }, cancellationToken: ct)) != 1)
            throw new PdmConflictException("会话已变化或正在生成，请刷新后重试。");
    }
    async Task IAiStore.DeleteAsync(Guid company, Guid user, Guid id, long revision, CancellationToken ct)
    {
        await using var db = await OpenAsync(ct);
        if (await db.ExecuteAsync(new CommandDefinition("DELETE FROM ai_conversations WHERE id=@id AND company_id=@company AND user_id=@user AND revision=@revision", new { company, user, id, revision }, cancellationToken: ct)) != 1)
            throw new PdmConflictException("会话已变化，请刷新后重试。");
    }
    async Task IAiStore.DeleteAllAsync(Guid company, Guid user, DateTimeOffset now, CancellationToken ct)
    {
        await using var db = await OpenAsync(ct);
        await using var transaction = await db.BeginTransactionAsync(ct);
        const string scope = " WHERE company_id=@company AND user_id=@user";
        var args = new { company, user };
        var rows = await db.QueryAsync<string>(new CommandDefinition("SELECT payload_json FROM ai_conversations" + scope + " FOR UPDATE", args, transaction, cancellationToken: ct));
        if (rows.Any(row => JsonSerializer.Deserialize<AiConversation>(row)!.BusyUntil > now))
            throw new PdmRuleException("有对话正在生成，请等待结束后再清空。");
        await db.ExecuteAsync(new CommandDefinition("DELETE FROM ai_conversations" + scope, args, transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
    }
}

public sealed partial class InMemoryPdmRepository : IAiStore
{
    private readonly object aiGate = new();
    private readonly Dictionary<Guid, AiSettings> aiSettings = [];
    private readonly Dictionary<Guid, AiConversation> aiConversations = [];
    Task<AiSettings> IAiStore.SettingsAsync(Guid company, CancellationToken ct) { lock (aiGate) return Task.FromResult(aiSettings.GetValueOrDefault(company) ?? new(0, [])); }
    Task IAiStore.SaveSettingsAsync(Guid company, AiSettings settings, CancellationToken ct) {
        lock (aiGate) { if ((aiSettings.GetValueOrDefault(company)?.Revision ?? 0) != settings.Revision) throw new PdmConflictException("AI配置已被修改，请刷新后重试。");
            aiSettings[company] = settings with { Revision = settings.Revision + 1 }; } return Task.CompletedTask;
    }
    Task<IReadOnlyList<AiConversation>> IAiStore.ListAsync(Guid company, Guid user, CancellationToken ct) {
        lock (aiGate) return Task.FromResult<IReadOnlyList<AiConversation>>(aiConversations.Values.Where(c => c.CompanyId == company && c.UserId == user).OrderByDescending(c => c.UpdatedAt).Take(100).ToArray());
    }
    Task<AiConversation?> IAiStore.FindAsync(Guid company, Guid user, Guid id, CancellationToken ct) {
        lock (aiGate) return Task.FromResult(aiConversations.GetValueOrDefault(id) is { } c && c.CompanyId == company && c.UserId == user ? c : null);
    }
    Task IAiStore.SaveAsync(AiConversation conversation, CancellationToken ct) {
        lock (aiGate) { if ((aiConversations.GetValueOrDefault(conversation.Id)?.Revision ?? 0) != conversation.Revision) throw new PdmConflictException("会话已变化或正在生成，请刷新后重试。");
            aiConversations[conversation.Id] = conversation with { Revision = conversation.Revision + 1 }; } return Task.CompletedTask;
    }
    Task IAiStore.DeleteAsync(Guid company, Guid user, Guid id, long revision, CancellationToken ct) {
        lock (aiGate) { if (!aiConversations.TryGetValue(id, out var c) || c.CompanyId != company || c.UserId != user || c.Revision != revision) throw new PdmConflictException("会话已变化，请刷新后重试。");
            aiConversations.Remove(id); } return Task.CompletedTask;
    }
    Task IAiStore.DeleteAllAsync(Guid company, Guid user, DateTimeOffset now, CancellationToken ct)
    {
        lock (aiGate)
        {
            ct.ThrowIfCancellationRequested();
            var conversations = aiConversations.Values.Where(c => c.CompanyId == company && c.UserId == user).ToArray();
            if (conversations.Any(c => c.BusyUntil > now))
                throw new PdmRuleException("有对话正在生成，请等待结束后再清空。");
            foreach (var conversation in conversations) aiConversations.Remove(conversation.Id);
        }
        return Task.CompletedTask;
    }
}
