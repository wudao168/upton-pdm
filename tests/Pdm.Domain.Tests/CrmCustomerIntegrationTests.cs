using System.Text.Json;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class CrmCustomerIntegrationTests
{
    [Fact]
    public async Task Sync_ReusesProtectedU9ConfigurationAndKeepsSecretsOutOfSettingsAndAudit()
    {
        var clock = TimeProvider.System;
        var repository = new InMemoryPdmRepository(clock);
        var materials = await ConfiguredMaterialsAsync(clock);
        var u9Client = new RecordingU9Client([
            new("C10001", "U9C客户甲"),
            new("C10002", "U9C客户乙")
        ]);
        var service = new CrmCustomerIntegrationService(repository, materials, u9Client, new TestProtector(), clock);

        var settings = await service.UpdateSettingsAsync(
            "ignored", "ignored", null, true, 30,
            "admin", UserRole.Administrator, CancellationToken.None);

        Assert.Equal("http://u9.example.test/U9", settings.BaseUrl);
        Assert.Equal("pdm", settings.Username);
        Assert.True(settings.PasswordConfigured);
        Assert.True(settings.AutoSyncEnabled);
        Assert.Equal(30, settings.AutoSyncIntervalMinutes);

        var testResult = await service.TestConnectionAsync("admin", UserRole.Administrator, CancellationToken.None);
        Assert.Equal(2, testResult.CustomerCount);
        Assert.Equal("test-secret", u9Client.LastAuthentication?.ClientSecret);
        Assert.Contains("\"ReferenceCode\":\"Customer\"", u9Client.LastCustomerPayload);
        Assert.Contains("\"TargetOrgCode\":\"7\"", u9Client.LastCustomerPayload);

        var syncResult = await service.SyncCustomersAsync("admin", UserRole.Administrator, CancellationToken.None);
        Assert.Equal(2, syncResult.CustomerCount);
        Assert.Contains(syncResult.Customers, customer => customer.Code == "C10001" && customer.SourceSystem == "u9c");
        Assert.Contains(syncResult.Customers, customer => customer.Code == "C10002" && customer.SourceSystem == "u9c");
        Assert.Contains(syncResult.Customers, customer => customer.Code == "C00465" && !customer.IsActive);

        var audits = await repository.ListAuditAsync("admin", UserRole.Administrator, 100, CancellationToken.None);
        Assert.DoesNotContain(audits, entry => entry.Detail.Contains("test-secret", StringComparison.Ordinal));
        Assert.DoesNotContain(audits, entry => entry.Detail.Contains("protected:", StringComparison.Ordinal));
        Assert.Contains(audits, entry => entry.Action == "u9.customer.sync");
    }

    [Fact]
    public async Task UpdateSettings_OnlyChangesTheScheduleAndRequiresU9OAuthConfiguration()
    {
        var clock = TimeProvider.System;
        var repository = new InMemoryPdmRepository(clock);
        var unconfiguredMaterials = new InMemoryMaterialRepository(clock);
        var service = new CrmCustomerIntegrationService(repository, unconfiguredMaterials, new RecordingU9Client([]), new TestProtector(), clock);

        await Assert.ThrowsAsync<PdmRuleException>(() => service.UpdateSettingsAsync(
            "", "", null, false, 60,
            "admin", UserRole.Administrator, CancellationToken.None));

        var materials = await ConfiguredMaterialsAsync(clock);
        service = new CrmCustomerIntegrationService(repository, materials, new RecordingU9Client([new("C1", "客户")]), new TestProtector(), clock);
        await Assert.ThrowsAsync<PdmRuleException>(() => service.UpdateSettingsAsync(
            "", "", null, true, 4,
            "admin", UserRole.Administrator, CancellationToken.None));

        var retained = await service.UpdateSettingsAsync(
            "http://crm-should-be-ignored", "crm-user-ignored", "crm-password-ignored", true, 120,
            "admin", UserRole.Administrator, CancellationToken.None);

        Assert.Equal("http://u9.example.test/U9", retained.BaseUrl);
        Assert.Equal("pdm", retained.Username);
        Assert.True(retained.AutoSyncEnabled);
        Assert.Equal(120, retained.AutoSyncIntervalMinutes);
        var storedSchedule = await repository.GetCrmIntegrationConfigurationAsync(CancellationToken.None);
        Assert.Empty(storedSchedule.PasswordCiphertext);
    }

    [Fact]
    public async Task AutomaticSync_RunsWhenDueAndWaitsForTheConfiguredInterval()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 8, 16, 1, 0, 0, TimeSpan.Zero));
        var repository = new InMemoryPdmRepository(clock);
        var materials = await ConfiguredMaterialsAsync(clock);
        var u9Client = new RecordingU9Client([new("C10001", "自动同步客户")]);
        var service = new CrmCustomerIntegrationService(repository, materials, u9Client, new TestProtector(), clock);
        await service.UpdateSettingsAsync("", "", null, true, 15, "admin", UserRole.Administrator, CancellationToken.None);

        var first = await service.TrySyncAutomaticallyAsync(CancellationToken.None);
        Assert.NotNull(first);
        Assert.Equal(1, u9Client.CustomerQueryCallCount);
        Assert.Null(await service.TrySyncAutomaticallyAsync(CancellationToken.None));
        Assert.Equal(1, u9Client.CustomerQueryCallCount);

        clock.Advance(TimeSpan.FromMinutes(15));
        var second = await service.TrySyncAutomaticallyAsync(CancellationToken.None);
        Assert.NotNull(second);
        Assert.Equal(2, u9Client.CustomerQueryCallCount);
        var settings = await service.GetSettingsAsync("admin", UserRole.Administrator, CancellationToken.None);
        Assert.Equal(clock.GetUtcNow(), settings.LastAutoSyncAttemptAt);
        Assert.Null(settings.LastAutoSyncError);
    }

    [Fact]
    public async Task AutomaticSync_RecordsFailureAndDoesNotRetryBeforeTheInterval()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 8, 16, 2, 0, 0, TimeSpan.Zero));
        var repository = new InMemoryPdmRepository(clock);
        var materials = await ConfiguredMaterialsAsync(clock);
        var u9Client = new FailingU9Client();
        var service = new CrmCustomerIntegrationService(repository, materials, u9Client, new TestProtector(), clock);
        await service.UpdateSettingsAsync("", "", null, true, 5, "admin", UserRole.Administrator, CancellationToken.None);

        await Assert.ThrowsAsync<HttpRequestException>(() => service.TrySyncAutomaticallyAsync(CancellationToken.None));
        var settings = await service.GetSettingsAsync("admin", UserRole.Administrator, CancellationToken.None);
        Assert.Equal(clock.GetUtcNow(), settings.LastAutoSyncAttemptAt);
        Assert.Equal("U9C unavailable", settings.LastAutoSyncError);
        Assert.Null(await service.TrySyncAutomaticallyAsync(CancellationToken.None));
        Assert.Equal(1, u9Client.CustomerQueryCallCount);
    }

    private static async Task<InMemoryMaterialRepository> ConfiguredMaterialsAsync(TimeProvider clock)
    {
        var materials = new InMemoryMaterialRepository(clock);
        await materials.SaveIntegrationConfigurationAsync(new(
            "http://u9.example.test/U9", "01", "7", "pdm", "PDM", "protected:test-secret",
            U9MaterialContract.CreatePath, U9MaterialContract.QueryPath, false, "admin", clock.GetUtcNow()),
            CancellationToken.None);
        return materials;
    }

    private sealed class RecordingU9Client(IReadOnlyList<U9CustomerReference> customers) : IU9OpenApiClient
    {
        public U9AuthenticationRequest? LastAuthentication { get; private set; }
        public string LastCustomerPayload { get; private set; } = string.Empty;
        public int CustomerQueryCallCount { get; private set; }

        public Task<U9AuthenticationResult> AuthenticateAsync(U9AuthenticationRequest request, CancellationToken cancellationToken)
        {
            LastAuthentication = request;
            return Task.FromResult(new U9AuthenticationResult("token"));
        }

        public Task<U9CustomerQueryResult> QueryCustomerReferencesAsync(string baseUrl, string token, string payloadJson, CancellationToken cancellationToken)
        {
            CustomerQueryCallCount++;
            LastCustomerPayload = payloadJson;
            using var payload = JsonDocument.Parse(payloadJson);
            Assert.Equal(0, payload.RootElement.GetProperty("PageIndex").GetInt32());
            return Task.FromResult(new U9CustomerQueryResult(0, null, customers, customers.Count));
        }

        public Task<U9BusinessBatchResult> PostBatchAsync(string baseUrl, string path, string token, string payloadJson, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<U9ItemQueryResult> QueryItemsAsync(string baseUrl, string path, string token, string payloadJson, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<U9UomQueryResult> QueryUomsAsync(string baseUrl, string token, string payloadJson, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FailingU9Client : IU9OpenApiClient
    {
        public int CustomerQueryCallCount { get; private set; }
        public Task<U9AuthenticationResult> AuthenticateAsync(U9AuthenticationRequest request, CancellationToken cancellationToken) => Task.FromResult(new U9AuthenticationResult("token"));
        public Task<U9CustomerQueryResult> QueryCustomerReferencesAsync(string baseUrl, string token, string payloadJson, CancellationToken cancellationToken)
        {
            CustomerQueryCallCount++;
            throw new HttpRequestException("U9C unavailable");
        }
        public Task<U9BusinessBatchResult> PostBatchAsync(string baseUrl, string path, string token, string payloadJson, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<U9ItemQueryResult> QueryItemsAsync(string baseUrl, string path, string token, string payloadJson, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<U9UomQueryResult> QueryUomsAsync(string baseUrl, string token, string payloadJson, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan value) => now = now.Add(value);
    }

    private sealed class TestProtector : IU9SecretProtector
    {
        public string Protect(string secret) => "protected:" + secret;
        public string Unprotect(string ciphertext) => ciphertext["protected:".Length..];
    }
}
