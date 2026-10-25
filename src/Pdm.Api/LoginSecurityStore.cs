using System.Collections.Concurrent;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Api;

// 每个账号单独加行锁；同一账号的并发登录在多个 API 进程之间也只能依次取得名额。
public sealed class LoginSecurityStore(IOptions<PdmDatabaseOptions> options)
{
    private readonly ConcurrentDictionary<string, string> memory = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private bool MySql => options.Value.Provider.Equals("MySql", StringComparison.OrdinalIgnoreCase);

    public async Task<T> ReadAsync<T>(string key, CancellationToken ct) where T : new()
    {
        string? value;
        if (!MySql) memory.TryGetValue(key, out value);
        else
        {
            await using var connection = new MySqlConnection(options.Value.ConnectionString);
            value = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
                "SELECT state_json FROM login_security_state WHERE state_key=@key", new { key }, cancellationToken: ct));
        }
        return value is null ? new T() : JsonSerializer.Deserialize<T>(value, Json)!;
    }

    public async Task<TResult> UpdateAsync<T, TResult>(string key, Func<T, TResult> update, CancellationToken ct) where T : new()
    {
        if (!MySql)
        {
            await gate.WaitAsync(ct);
            try
            {
                var value = memory.TryGetValue(key, out var json) ? JsonSerializer.Deserialize<T>(json, Json)! : new T();
                var result = update(value);
                memory[key] = JsonSerializer.Serialize(value, Json);
                return result;
            }
            finally { gate.Release(); }
        }
        await using var connection = new MySqlConnection(options.Value.ConnectionString);
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO login_security_state(state_key,state_json) VALUES(@key,@json) ON DUPLICATE KEY UPDATE state_key=state_key",
            new { key, json = JsonSerializer.Serialize(new T(), Json) }, transaction, cancellationToken: ct));
        var original = await connection.QuerySingleAsync<string>(new CommandDefinition(
            "SELECT state_json FROM login_security_state WHERE state_key=@key FOR UPDATE", new { key }, transaction, cancellationToken: ct));
        var state = JsonSerializer.Deserialize<T>(original, Json)!;
        var output = update(state);
        var changed = JsonSerializer.Serialize(state, Json);
        if (changed != original)
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE login_security_state SET state_json=@changed WHERE state_key=@key", new { key, changed }, transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
        return output;
    }

    public async Task<LoginUserState[]> ListUsersAsync(CancellationToken ct)
    {
        IEnumerable<string> values;
        if (!MySql) values = memory.Where(x => x.Key.StartsWith("user:", StringComparison.Ordinal)).Select(x => x.Value).ToArray();
        else
        {
            await using var connection = new MySqlConnection(options.Value.ConnectionString);
            values = await connection.QueryAsync<string>(new CommandDefinition(
                "SELECT state_json FROM login_security_state WHERE state_key LIKE 'user:%'", cancellationToken: ct));
        }
        return values.Select(x => JsonSerializer.Deserialize<LoginUserState>(x, Json)!).ToArray();
    }
}
