using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Upton.Pdm.Application;

namespace Upton.Pdm.Infrastructure;

public sealed record ServerStorageSettings
{
    public long Version { get; init; }
    public string ProjectFileRoot { get; init; } = "";
    public string OplAttachmentRoot { get; init; } = "";
    public string LegacyOplAttachmentRoot { get; init; } = "";
    public string ProgramTemplateRoot { get; init; } = "";
    public string UploadTempRoot { get; init; } = "";
    public string BrowserCadArchiveRoot { get; init; } = "";
    public string ConversionWorkRoot { get; init; } = "";
    public string ClientLogRoot { get; init; } = "";
    public string BackupRoot { get; init; } = "";
    // These are migration targets, never live database/key/remote-agent switches.
    public string DatabaseDataTarget { get; init; } = "";
    public string SecretsTarget { get; init; } = "";
    public string PreviewAgentWorkTarget { get; init; } = "";
    public IReadOnlyDictionary<string, string[]> HistoricalRoots { get; init; } = new Dictionary<string, string[]>();
}

public sealed class ServerStorageSettingsStore(IOptions<PdmDatabaseOptions> options, TimeProvider clock)
{
    private const string Key = "server_storage_locations";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim gate = new(1, 1);
    private ServerStorageSettings? memory;

    public async Task<ServerStorageSettings?> ReadAsync(CancellationToken ct)
    {
        if (!IsMySql) return memory;
        await using var db = new MySqlConnection(options.Value.ConnectionString);
        var json = await db.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT setting_value FROM pdm_system_setting WHERE setting_key=@Key", new { Key }, cancellationToken: ct));
        return json is null ? null : JsonSerializer.Deserialize<ServerStorageSettings>(json, Json);
    }

    public async Task<ServerStorageSettings> SaveAsync(ServerStorageSettings value, long expectedVersion, CancellationToken ct)
    {
        value = value with { Version = expectedVersion + 1 };
        if (!IsMySql)
        {
            await gate.WaitAsync(ct);
            try
            {
                if ((memory?.Version ?? 0) != expectedVersion) throw new PdmConflictException("存储设置已被其他管理员修改，请重新加载后保存。");
                return memory = value;
            }
            finally { gate.Release(); }
        }
        await using var db = new MySqlConnection(options.Value.ConnectionString);
        var parameters = new { Key, Value = JsonSerializer.Serialize(value, Json), ExpectedVersion = expectedVersion, Now = clock.GetUtcNow().UtcDateTime };
        var changed = expectedVersion == 0
            ? await db.ExecuteAsync(new CommandDefinition("INSERT IGNORE INTO pdm_system_setting(setting_key,setting_value,updated_at) VALUES(@Key,@Value,@Now)", parameters, cancellationToken: ct))
            : await db.ExecuteAsync(new CommandDefinition("UPDATE pdm_system_setting SET setting_value=@Value,updated_at=@Now WHERE setting_key=@Key AND CAST(JSON_UNQUOTE(JSON_EXTRACT(setting_value,'$.version')) AS UNSIGNED)=@ExpectedVersion", parameters, cancellationToken: ct));
        if (changed != 1) throw new PdmConflictException("存储设置已被其他管理员修改，请重新加载后保存。");
        return value;
    }

    private bool IsMySql => options.Value.Provider.Equals("MySql", StringComparison.OrdinalIgnoreCase);
}
