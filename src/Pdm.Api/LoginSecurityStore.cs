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
    private readonly ConcurrentDictionary<string, byte> heartbeats = new();
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

    // 心跳只更新已存在的会话；忙时延后，不与登录/管理员下线争抢账号行锁。
    // 返回未完成时，调用方必须重新读取并验证最新已提交状态，不能直接放行。
    public async Task<(bool Completed, string? Error)> TryHeartbeatAsync(string key, Func<LoginUserState, string?> update, CancellationToken ct)
    {
        if (!heartbeats.TryAdd(key, 0)) return (false, null);
        try
        {
            if (!MySql) return (true, await UpdateAsync(key, update, ct));
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                await using var connection = new MySqlConnection(options.Value.ConnectionString);
                await connection.OpenAsync(budget.Token);
                await using var transaction = await connection.BeginTransactionAsync(budget.Token);
                var original = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
                    "SELECT state_json FROM login_security_state WHERE state_key=@key FOR UPDATE NOWAIT", new { key }, transaction, commandTimeout: 2, cancellationToken: budget.Token));
                var state = original is null ? new LoginUserState() : JsonSerializer.Deserialize<LoginUserState>(original, Json)!;
                var error = update(state);
                var changed = JsonSerializer.Serialize(state, Json);
                if (original is not null && changed != original)
                    await connection.ExecuteAsync(new CommandDefinition(
                        "UPDATE login_security_state SET state_json=@changed WHERE state_key=@key", new { key, changed }, transaction, commandTimeout: 2, cancellationToken: budget.Token));
                await transaction.CommitAsync(budget.Token);
                return (true, error);
            }
            catch (Exception error) when (!ct.IsCancellationRequested && (
                error is MySqlException { Number: 1205 or 1213 or 3572 } ||
                error is MySqlException { ErrorCode: MySqlErrorCode.CommandTimeoutExpired } ||
                (budget.IsCancellationRequested && error is OperationCanceledException or MySqlException)))
            {
                return (false, null);
            }
        }
        finally { heartbeats.TryRemove(key, out _); }
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
