using System.Net;
using System.Text;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class CrmCustomerIntegrationTests
{
    [Fact]
    public async Task CrmClient_UsesLoginTokenForTheOpenCustomerEndpoint()
    {
        var handler = new RecordingHttpHandler();
        var client = new CrmCustomerClient(new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) });

        var batch = await client.ListCustomersAsync(
            "http://crm.example.test:8080",
            "integration-user",
            "integration-secret",
            CancellationToken.None);

        Assert.Equal(2, handler.RequestCount);
        Assert.Equal("integration-user", handler.LoginUsername);
        Assert.Equal("integration-secret", handler.LoginPassword);
        Assert.Equal("Bearer", handler.CustomerAuthorizationScheme);
        Assert.Equal("crm-token", handler.CustomerAuthorizationToken);
        Assert.Equal(2, batch.SkippedCount);
        Assert.Collection(batch.Customers,
            customer => Assert.Equal(new CrmCustomerRecord("C00001", "客户甲"), customer),
            customer => Assert.Equal(new CrmCustomerRecord("C00002", "客户乙"), customer));
    }

    [Fact]
    public async Task Sync_UsesProtectedPasswordAndKeepsSecretsOutOfSettingsAndAudit()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var crmClient = new RecordingCrmClient([
            new("C10001", "CRM客户甲"),
            new("C10002", "CRM客户乙")
        ]);
        var protector = new RecordingProtector();
        var service = new CrmCustomerIntegrationService(repository, crmClient, protector, TimeProvider.System);

        var settings = await service.UpdateSettingsAsync(
            " http://127.0.0.1:8080/ ",
            " integration-user ",
            "integration-secret",
            true,
            30,
            "admin",
            UserRole.Administrator,
            CancellationToken.None);

        Assert.Equal("http://127.0.0.1:8080", settings.BaseUrl);
        Assert.Equal("integration-user", settings.Username);
        Assert.True(settings.PasswordConfigured);
        Assert.True(settings.AutoSyncEnabled);
        Assert.Equal(30, settings.AutoSyncIntervalMinutes);
        Assert.Equal("integration-secret", protector.LastProtectedValue);

        var testResult = await service.TestConnectionAsync("admin", UserRole.Administrator, CancellationToken.None);
        Assert.Equal(2, testResult.CustomerCount);
        Assert.Equal(0, testResult.SkippedCount);
        Assert.Equal("integration-secret", crmClient.LastPassword);

        var syncResult = await service.SyncCustomersAsync("admin", UserRole.Administrator, CancellationToken.None);
        Assert.Equal(2, syncResult.CustomerCount);
        Assert.Equal(0, syncResult.SkippedCount);
        Assert.Contains(syncResult.Customers, customer => customer.Code == "C10001" && customer.SourceSystem == "crm");
        Assert.Contains(syncResult.Customers, customer => customer.Code == "C10002" && customer.SourceSystem == "crm");
        Assert.Contains(syncResult.Customers, customer => customer.Code == "C00465" && !customer.IsActive);

        var audits = await repository.ListAuditAsync("admin", UserRole.Administrator, 100, CancellationToken.None);
        Assert.DoesNotContain(audits, entry => entry.Detail.Contains("integration-secret", StringComparison.Ordinal));
        Assert.DoesNotContain(audits, entry => entry.Detail.Contains("cipher:", StringComparison.Ordinal));
        Assert.Contains(audits, entry => entry.Action == "crm.customer.sync");
    }

    [Fact]
    public async Task UpdateSettings_RequiresValidUrlAndPasswordOnlyOnFirstSave()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var service = new CrmCustomerIntegrationService(repository, new RecordingCrmClient([]), new RecordingProtector(), TimeProvider.System);

        await Assert.ThrowsAsync<PdmRuleException>(() => service.UpdateSettingsAsync(
            "file:///crm",
            "integration-user",
            "secret",
            false,
            60,
            "admin",
            UserRole.Administrator,
            CancellationToken.None));

        await service.UpdateSettingsAsync(
            "https://crm.example.test",
            "integration-user",
            "secret",
            false,
            60,
            "admin",
            UserRole.Administrator,
            CancellationToken.None);
        var retained = await service.UpdateSettingsAsync(
            "https://crm.example.test/base",
            "integration-user-2",
            null,
            true,
            120,
            "admin",
            UserRole.Administrator,
            CancellationToken.None);

        Assert.True(retained.PasswordConfigured);
        Assert.Equal("integration-user-2", retained.Username);
        Assert.True(retained.AutoSyncEnabled);
        Assert.Equal(120, retained.AutoSyncIntervalMinutes);
    }

    [Fact]
    public async Task AutomaticSync_RunsWhenDueAndWaitsForTheConfiguredInterval()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 8, 16, 1, 0, 0, TimeSpan.Zero));
        var repository = new InMemoryPdmRepository(clock);
        var crmClient = new RecordingCrmClient([new("C10001", "自动同步客户")]);
        var service = new CrmCustomerIntegrationService(repository, crmClient, new RecordingProtector(), clock);
        await service.UpdateSettingsAsync(
            "https://crm.example.test",
            "integration-user",
            "secret",
            true,
            15,
            "admin",
            UserRole.Administrator,
            CancellationToken.None);

        var first = await service.TrySyncAutomaticallyAsync(CancellationToken.None);
        Assert.NotNull(first);
        Assert.Equal(1, crmClient.CallCount);
        Assert.Null(await service.TrySyncAutomaticallyAsync(CancellationToken.None));
        Assert.Equal(1, crmClient.CallCount);

        clock.Advance(TimeSpan.FromMinutes(15));
        var second = await service.TrySyncAutomaticallyAsync(CancellationToken.None);
        Assert.NotNull(second);
        Assert.Equal(2, crmClient.CallCount);
        var settings = await service.GetSettingsAsync(UserRole.Administrator, CancellationToken.None);
        Assert.Equal(clock.GetUtcNow(), settings.LastAutoSyncAttemptAt);
        Assert.Null(settings.LastAutoSyncError);
    }

    [Fact]
    public async Task AutomaticSync_RecordsFailureAndDoesNotRetryBeforeTheInterval()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 8, 16, 2, 0, 0, TimeSpan.Zero));
        var repository = new InMemoryPdmRepository(clock);
        var crmClient = new FailingCrmClient();
        var service = new CrmCustomerIntegrationService(repository, crmClient, new RecordingProtector(), clock);
        await service.UpdateSettingsAsync(
            "https://crm.example.test",
            "integration-user",
            "secret",
            true,
            5,
            "admin",
            UserRole.Administrator,
            CancellationToken.None);

        await Assert.ThrowsAsync<HttpRequestException>(() => service.TrySyncAutomaticallyAsync(CancellationToken.None));
        var settings = await service.GetSettingsAsync(UserRole.Administrator, CancellationToken.None);
        Assert.Equal(clock.GetUtcNow(), settings.LastAutoSyncAttemptAt);
        Assert.Equal("CRM unavailable", settings.LastAutoSyncError);
        Assert.Null(await service.TrySyncAutomaticallyAsync(CancellationToken.None));
        Assert.Equal(1, crmClient.CallCount);
    }

    private sealed class RecordingCrmClient(IReadOnlyList<CrmCustomerRecord> customers) : ICrmCustomerClient
    {
        public string LastPassword { get; private set; } = string.Empty;
        public int CallCount { get; private set; }

        public Task<CrmCustomerBatch> ListCustomersAsync(string baseUrl, string username, string password, CancellationToken cancellationToken)
        {
            CallCount++;
            LastPassword = password;
            return Task.FromResult(new CrmCustomerBatch(customers, 0));
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan value) => now = now.Add(value);
    }

    private sealed class FailingCrmClient : ICrmCustomerClient
    {
        public int CallCount { get; private set; }

        public Task<CrmCustomerBatch> ListCustomersAsync(string baseUrl, string username, string password, CancellationToken cancellationToken)
        {
            CallCount++;
            throw new HttpRequestException("CRM unavailable");
        }
    }

    private sealed class RecordingProtector : ICrmCredentialProtector
    {
        public string LastProtectedValue { get; private set; } = string.Empty;

        public string Protect(string password)
        {
            LastProtectedValue = password;
            return "cipher:" + password;
        }

        public string Unprotect(string ciphertext) => ciphertext["cipher:".Length..];
    }

    private sealed class RecordingHttpHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public string LoginUsername { get; private set; } = string.Empty;
        public string LoginPassword { get; private set; } = string.Empty;
        public string CustomerAuthorizationScheme { get; private set; } = string.Empty;
        public string CustomerAuthorizationToken { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            if (request.RequestUri?.AbsolutePath == "/api/auth/login")
            {
                using var body = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                LoginUsername = body.RootElement.GetProperty("username").GetString() ?? string.Empty;
                LoginPassword = body.RootElement.GetProperty("password").GetString() ?? string.Empty;
                return Json("""{"success":true,"data":{"token":"crm-token"}}""");
            }
            if (request.RequestUri?.AbsolutePath == "/api/open/v1/customers")
            {
                CustomerAuthorizationScheme = request.Headers.Authorization?.Scheme ?? string.Empty;
                CustomerAuthorizationToken = request.Headers.Authorization?.Parameter ?? string.Empty;
                return Json("""{"success":true,"data":[{"customerCode":"C00002","customerName":"客户乙"},{"customerCode":"c00001","customerName":"客户甲"},{"customerCode":"","customerName":"无编码客户"},{"customerCode":"C00001","customerName":"冲突名称"}]}""");
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }
}
