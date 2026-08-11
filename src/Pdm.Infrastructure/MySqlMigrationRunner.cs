using System.Reflection;
using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace Upton.Pdm.Infrastructure;

public sealed class MySqlMigrationRunner(IOptions<PdmDatabaseOptions> options, ILogger<MySqlMigrationRunner> logger, TimeProvider timeProvider)
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!settings.RunMigrations || !string.Equals(settings.Provider, "MySql", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await using var connection = new MySqlConnection(settings.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            CREATE TABLE IF NOT EXISTS pdm_schema_migration (
                version VARCHAR(64) NOT NULL PRIMARY KEY,
                applied_at DATETIME(6) NOT NULL
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
            """,
            cancellationToken: cancellationToken));

        var applied = (await connection.QueryAsync<string>(new CommandDefinition("SELECT version FROM pdm_schema_migration", cancellationToken: cancellationToken)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var assembly = typeof(MySqlMigrationRunner).Assembly;
        var resources = assembly.GetManifestResourceNames()
            .Where(name => name.Contains(".Migrations.", StringComparison.Ordinal) && name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        foreach (var resourceName in resources)
        {
            var fileName = resourceName[(resourceName.LastIndexOf(".Migrations.", StringComparison.Ordinal) + ".Migrations.".Length)..];
            var version = fileName[..^4];
            if (applied.Contains(version))
            {
                continue;
            }

            await using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"无法读取迁移资源：{resourceName}");
            using var reader = new StreamReader(stream);
            var sql = await reader.ReadToEndAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                await connection.ExecuteAsync(new CommandDefinition(sql, transaction: transaction, commandTimeout: 180, cancellationToken: cancellationToken));
                await connection.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO pdm_schema_migration(version, applied_at) VALUES (@Version, @AppliedAt)",
                    new { Version = version, AppliedAt = timeProvider.GetUtcNow().UtcDateTime },
                    transaction,
                    cancellationToken: cancellationToken));
                await transaction.CommitAsync(cancellationToken);
                logger.LogInformation("Applied PDM database migration {MigrationVersion}", version);
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                throw;
            }
        }
    }
}
