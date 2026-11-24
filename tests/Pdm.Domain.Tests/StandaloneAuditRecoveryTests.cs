using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

[AttributeUsage(AttributeTargets.Method)]
internal sealed class PrivateAuditDatabaseFactAttribute : FactAttribute
{
    public PrivateAuditDatabaseFactAttribute()
    {
        if (!Regex.IsMatch(Environment.GetEnvironmentVariable("UPLM_AUDIT_TEST_PIPE") ?? "", "^UPLM_AUDIT_QA_[a-f0-9]{32}$"))
            Skip = "需要独立 MySQL 测试实例的专用本机命名管道。";
    }
}

public sealed class StandaloneAuditRecoveryTests
{
    [PrivateAuditDatabaseFact]
    public async Task Autocommit_recovers_only_the_exact_event_after_an_uncertain_result()
    {
        var pipe = Environment.GetEnvironmentVariable("UPLM_AUDIT_TEST_PIPE")!;
        var conn = new MySqlConnectionStringBuilder
        {
            Server = ".", ConnectionProtocol = MySqlConnectionProtocol.NamedPipe, PipeName = pipe,
            UserID = "root", SslMode = MySqlSslMode.Disabled, Pooling = false, GuidFormat = MySqlGuidFormat.Binary16
        };
        await using var privateServer = new MySqlConnection(conn.ConnectionString);
        await privateServer.OpenAsync();
        var dataRoot = Path.TrimEndingDirectorySeparator(await privateServer.ExecuteScalarAsync<string>("SELECT @@datadir") ?? "");
        Assert.Equal("UPLM-audit-" + pipe["UPLM_AUDIT_QA_".Length..], Directory.GetParent(dataRoot)?.Name);
        Assert.Equal(1, await privateServer.ExecuteScalarAsync<int>("SELECT @@skip_networking"));
        var schema = "audit_qa_" + Guid.NewGuid().ToString("N");
        await privateServer.ExecuteAsync($"CREATE DATABASE {schema} CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci");
        try
        {
            conn.Database = schema;
            await using var db = new MySqlConnection(conn.ConnectionString);
            await db.OpenAsync();
            await db.ExecuteAsync("""
                CREATE TABLE audit_entry(id BINARY(16) PRIMARY KEY, occurred_at DATETIME(6) NOT NULL,
                    actor VARCHAR(100) NOT NULL, action_name VARCHAR(120) NOT NULL, entity_type VARCHAR(120) NOT NULL,
                    entity_id VARCHAR(160) NOT NULL, detail_json JSON NOT NULL) ENGINE=InnoDB;
                CREATE TABLE attempts(id BINARY(16) PRIMARY KEY, n INT NOT NULL) ENGINE=MEMORY;
                """);
            await db.ExecuteAsync("""
                CREATE TRIGGER first_command_timeout BEFORE INSERT ON audit_entry FOR EACH ROW
                BEGIN
                    DECLARE calls INT;
                    IF NEW.action_name IN ('test.slow','test.slow.always') THEN
                        INSERT INTO attempts VALUES(NEW.id,1) ON DUPLICATE KEY UPDATE n=n+1;
                        SELECT n INTO calls FROM attempts WHERE id=NEW.id;
                        IF calls=1 OR NEW.action_name='test.slow.always' THEN DO SLEEP(8); END IF;
                    END IF;
                END
                """);
            MySqlPdmRepository Repo(bool retry, uint timeout)
            {
                var cs = new MySqlConnectionStringBuilder(conn.ConnectionString) { DefaultCommandTimeout = timeout };
                return new MySqlPdmRepository(Options.Create(new PdmDatabaseOptions
                    { ConnectionString = cs.ConnectionString, RetryTransientAuditWrites = retry }), TimeProvider.System);
            }
            AuditEntry NewEvent(string action) => new(Guid.NewGuid(), DateTimeOffset.UtcNow.AddTicks(7), "设计A", action, "PdmDocument", "文档A", "中文和 😀 原始内容");
            Task<int> Rows(Guid id) => db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM audit_entry WHERE id=@id", new { id });

            var recovered = NewEvent("test.slow");
            await Repo(true, 1).AppendAuditAsync(recovered, default);
            Assert.Equal(2, await db.ExecuteScalarAsync<int>("SELECT n FROM attempts WHERE id=@id", new { recovered.Id }));
            Assert.Equal(1, await Rows(recovered.Id));

            var disabled = NewEvent("test.slow");
            var timeout = await Assert.ThrowsAsync<MySqlException>(() => Repo(false, 1).AppendAuditAsync(disabled, default));
            Assert.Equal(MySqlErrorCode.CommandTimeoutExpired, timeout.ErrorCode);
            Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT n FROM attempts WHERE id=@id", new { disabled.Id }));

            var exhausted = NewEvent("test.slow.always");
            Assert.Equal(MySqlErrorCode.CommandTimeoutExpired,
                (await Assert.ThrowsAsync<MySqlException>(() => Repo(true, 1).AppendAuditAsync(exhausted, default))).ErrorCode);
            Assert.Equal(2, await db.ExecuteScalarAsync<int>("SELECT n FROM attempts WHERE id=@id", new { exhausted.Id }));
            Assert.Equal(0, await Rows(exhausted.Id));

            var ambient = NewEvent("test.slow");
            using (var txScope = new System.Transactions.TransactionScope(System.Transactions.TransactionScopeAsyncFlowOption.Enabled))
                Assert.Equal(MySqlErrorCode.CommandTimeoutExpired,
                    (await Assert.ThrowsAsync<MySqlException>(() => Repo(true, 1).AppendAuditAsync(ambient, default))).ErrorCode);
            Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT n FROM attempts WHERE id=@id", new { ambient.Id }));
            Assert.Equal(0, await Rows(ambient.Id));

            var username = "read_" + Guid.NewGuid().ToString("N")[..12];
            var password = Guid.NewGuid().ToString("N");
            await privateServer.ExecuteAsync($"CREATE USER '{username}'@'localhost' IDENTIFIED BY '{password}'");
            try
            {
                await privateServer.ExecuteAsync($"GRANT SELECT ON {schema}.* TO '{username}'@'localhost'");
                var readOnly = new MySqlConnectionStringBuilder(conn.ConnectionString)
                    { UserID = username, Password = password, AllowPublicKeyRetrieval = true };
                var restricted = new MySqlPdmRepository(Options.Create(new PdmDatabaseOptions
                    { ConnectionString = readOnly.ConnectionString }), TimeProvider.System);
                var denied = NewEvent("test.denied");
                Assert.Equal(1142, (await Assert.ThrowsAsync<MySqlException>(() => restricted.AppendAuditAsync(denied, default))).Number);
                Assert.Equal(0, await Rows(denied.Id));
            }
            finally { await privateServer.ExecuteAsync($"DROP USER '{username}'@'localhost'"); }

            var canceled = NewEvent("test.slow");
            using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(400)))
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Repo(true, 6).AppendAuditAsync(canceled, cancel.Token));
            Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT n FROM attempts WHERE id=@id", new { canceled.Id }));
            Assert.Equal(0, await Rows(canceled.Id));

            // An outstanding insert is known to the server but its outcome isn't
            // visible to the recovery connection until the primary-key lock is released.
            await privateServer.ExecuteAsync("SET GLOBAL innodb_monitor_enable='lock_timeouts'");
            foreach (var difference in new[] { "none", "actor-case", "detail-case" })
            {
                var entry = NewEvent("test.recovery");
                await using var writer = new MySqlConnection(conn.ConnectionString);
                await writer.OpenAsync();
                await using var tx = await writer.BeginTransactionAsync();
                var actor = difference == "actor-case" ? "设计a" : entry.Actor;
                var detail = difference == "detail-case" ? entry.Detail + " " : entry.Detail;
                await writer.ExecuteAsync("""
                    INSERT INTO audit_entry VALUES(@id,@at,@actor,@action,@entityType,@entityId,@json)
                    """, new { entry.Id, at = entry.OccurredAt.UtcDateTime, actor, entry.Action, entry.EntityType,
                        entry.EntityId, json = JsonSerializer.Serialize(new { detail }) }, tx);
                Task<long> Timeouts() => db.ExecuteScalarAsync<long>("SELECT count FROM information_schema.innodb_metrics WHERE name='lock_timeouts'");
                var before = await Timeouts();
                var pending = Repo(true, 10).AppendAuditAsync(entry, default);
                try
                {
                    var timer = Stopwatch.StartNew();
                    while (await Timeouts() == before && timer.Elapsed < TimeSpan.FromSeconds(6)) await Task.Delay(25);
                    Assert.True(await Timeouts() > before, "第一次写入须真实经过 InnoDB 行锁超时");
                    await tx.CommitAsync();
                }
                catch { await tx.RollbackAsync(); throw; }
                if (difference == "none") await pending;
                else Assert.Equal(1062, (await Assert.ThrowsAsync<MySqlException>(() => pending)).Number);
                Assert.Equal(1, await Rows(entry.Id));
                Assert.Equal(actor, await db.ExecuteScalarAsync<string>("SELECT actor FROM audit_entry WHERE id=@id", new { entry.Id }));
                Assert.Equal(detail, await db.ExecuteScalarAsync<string>("SELECT JSON_UNQUOTE(detail_json->'$.detail') FROM audit_entry WHERE id=@id", new { entry.Id }));
                // Unrelated duplicate calls still fail on their very first insert.
                Assert.Equal(1062, (await Assert.ThrowsAsync<MySqlException>(() => Repo(true, 5).AppendAuditAsync(entry, default))).Number);
            }
        }
        finally { await privateServer.ExecuteAsync($"DROP DATABASE {schema}"); }
    }
}
