using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Upton.Pdm.Application;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Api;

public sealed record SaveUserTablePreference(string Key, string Value);

public sealed class UserTablePreferenceStore(IOptions<PdmDatabaseOptions> options)
{
    private readonly ConcurrentDictionary<(Guid Company, string User, string Key), string> memory = new();
    private bool MySql => options.Value.Provider.Equals("MySql", StringComparison.OrdinalIgnoreCase);

    public async Task<Dictionary<string, string>> ReadAsync(Guid company, string username, CancellationToken ct)
    {
        if (!MySql) return memory.Where(item => item.Key.Company == company && item.Key.User == username.ToLowerInvariant())
            .ToDictionary(item => item.Key.Key, item => item.Value);
        await using var connection = new MySqlConnection(options.Value.ConnectionString);
        var rows = await connection.QueryAsync<(string Key, string Value)>(new CommandDefinition(
            "SELECT preference_key, value_json FROM user_table_preference WHERE company_id=@company AND username=@username",
            new { company = company.ToString(), username }, cancellationToken: ct));
        return rows.ToDictionary(item => item.Key, item => item.Value);
    }

    public async Task SaveAsync(Guid company, string username, SaveUserTablePreference input, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input.Key) || input.Key.Length > 16384
            || !new[] { "upton-pdm:procurement-columns:", "upton-pdm:bom-columns:", "upton-pdm:production-drawing-columns:", "upton-pdm-column-widths:v1:" }.Any(input.Key.StartsWith)
            || string.IsNullOrWhiteSpace(input.Value) || Encoding.UTF8.GetByteCount(input.Value) > 65536)
            throw new PdmRuleException("列设置内容无效或过大，请恢复默认后重新选择。");
        using var json = JsonDocument.Parse(input.Value);
        if (json.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
            throw new PdmRuleException("列设置格式不正确，请重新选择显示列。");
        if (!MySql) { memory[(company, username.ToLowerInvariant(), input.Key)] = input.Value; return; }
        await using var connection = new MySqlConnection(options.Value.ConnectionString);
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO user_table_preference(company_id,username,preference_hash,preference_key,value_json,updated_at)
            VALUES(@company,@username,@hash,@key,@value,UTC_TIMESTAMP(6))
            ON DUPLICATE KEY UPDATE preference_key=@key,value_json=@value,updated_at=UTC_TIMESTAMP(6)
            """, new { company = company.ToString(), username, hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input.Key))), key = input.Key, value = input.Value }, cancellationToken: ct));
    }
}

public static class UserTablePreferenceEndpoints
{
    public static void MapUserTablePreferences(this WebApplication app)
    {
        var group = app.MapGroup("/api/me/table-preferences").RequireAuthorization();
        group.AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (PdmRuleException error) { return Results.BadRequest(new { message = error.Message }); }
            catch (JsonException) { return Results.BadRequest(new { message = "列设置格式无效，请重新选择。" }); }
            catch (Exception error) when (!context.HttpContext.RequestAborted.IsCancellationRequested)
            {
                context.HttpContext.RequestServices.GetRequiredService<ILogger<UserTablePreferenceStore>>().LogError(error, "个人列设置同步失败");
                return Results.Json(new { message = "列设置同步失败，请稍后重试；当前选择已保留在本机。" }, statusCode: 503);
            }
        });
        group.MapGet("", async (UserTablePreferenceStore store, CancellationToken ct) =>
        {
            var tenant = TenantContext.Current;
            return tenant is null ? Results.Unauthorized()
                : Results.Ok(new { preferences = await store.ReadAsync(tenant.CompanyId, tenant.Username, ct) });
        });
        group.MapPut("", async (SaveUserTablePreference input, UserTablePreferenceStore store, CancellationToken ct) =>
        {
            var tenant = TenantContext.Current;
            if (tenant is null) return Results.Unauthorized();
            await store.SaveAsync(tenant.CompanyId, tenant.Username, input, ct);
            return Results.NoContent();
        }).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(256 * 1024));
    }
}
