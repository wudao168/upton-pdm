using System.IdentityModel.Tokens.Jwt;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Upton.Pdm.Api;
using Upton.Pdm.Application;
using Upton.Pdm.Infrastructure;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api.Tests;

public sealed class ClientLogTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pdm-client-log-tests-" + Guid.NewGuid().ToString("N"));
    private readonly TestClock clock = new();
    private readonly Guid company = Guid.NewGuid();
    private static ClientLogQuery All => new(null, null, null, null, null, null, null);
    private ClientLogStore Store(long capacity = 10 * 1024 * 1024) => new(Options.Create(new PdmDatabaseOptions()), Options.Create(new ClientLogOptions { Root = root, MaxBytes = capacity }), clock, NullLogger<ClientLogStore>.Instance);
    private ClientLogBatch Batch(string kind = "runtime") => new(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), "设计电脑", "solidworks",
        [new(clock.GetUtcNow(), kind, "存档", "图档=组件.SLDASM；password=private-pass Bearer private-token; resumeToken=fake-resume-camel; resume_token='fake-resume-underscore'; resume-token=\"fake-resume-hyphen\"", "operation-1", "request-1", "session-1", "2026-test", "main-test.js")]);

    [Fact]
    public async Task DuplicateUploadIsIdempotentAndTenantScopeIsEnforced()
    {
        var store = Store(); var batch = Batch();
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => store.SaveAsync(batch, company, "engineer", default)));
        var result = await store.ListAsync(company, All, 50, default);
        Assert.Single(result.Items); Assert.Equal(1, result.Total);
        Assert.Empty((await store.ListAsync(Guid.NewGuid(), All, 50, default)).Items);
        await Assert.ThrowsAsync<PdmNotFoundException>(() => store.DownloadAsync(Guid.NewGuid(), All, default));
        var download = await store.DownloadAsync(company, All, default);
        using var zip = new ZipArchive(new MemoryStream(download.Content));
        var log = zip.Entries.Single(e => e.FullName.EndsWith(".jsonl"));
        using var reader = new StreamReader(log.Open()); var text = await reader.ReadToEndAsync();
        Assert.DoesNotContain("private-pass", text); Assert.DoesNotContain("private-token", text);
        Assert.DoesNotContain("fake-resume", text);
        Assert.Contains("request-1", text); Assert.Contains("operation-1", text);
        Assert.Contains(zip.Entries, e => e.FullName == "诊断说明.txt");
    }

    [Fact]
    public async Task FiltersCoverDeviceUserComponentTypeVersionAndTime()
    {
        var store = Store(); var runtime = Batch(); var error = Batch("error") with { DeviceId = runtime.DeviceId };
        await store.SaveAsync(runtime, company, "engineer", default); await store.SaveAsync(error, company, "engineer", default);
        var query = new ClientLogQuery(runtime.DeviceId, "engineer", "solidworks", "error", "main-test", clock.GetUtcNow().AddMinutes(-1), clock.GetUtcNow().AddMinutes(1));
        Assert.Single((await store.ListAsync(company, query, 50, default)).Items);
        Assert.Empty((await store.ListAsync(company, query with { User = "other" }, 50, default)).Items);
        Assert.Empty((await store.ListAsync(company, query with { From = clock.GetUtcNow().AddDays(1), To = null }, 50, default)).Items);
    }

    [Fact]
    public async Task RuntimeExpiresAt30DaysErrorsAt90DaysAndFilesAreRemoved()
    {
        var store = Store();
        await store.SaveAsync(Batch(), company, "engineer", default); await store.SaveAsync(Batch("error"), company, "engineer", default);
        clock.Advance(31); await store.CleanupAsync(default);
        Assert.Equal("error", Assert.Single((await store.ListAsync(company, All, 50, default)).Items).Kind);
        Assert.Single(Directory.GetFiles(root, "*.gz"));
        clock.Advance(60); await store.CleanupAsync(default);
        Assert.Empty((await store.ListAsync(company, All, 50, default)).Items); Assert.Empty(Directory.GetFiles(root, "*.gz"));
    }

    [Fact]
    public async Task CapacityRejectsWithoutAcknowledgingOrWritingAndInvalidBatchesAreRejected()
    {
        var store = Store(1);
        await Assert.ThrowsAsync<PdmRuleException>(() => store.SaveAsync(Batch(), company, "engineer", default));
        Assert.Empty((await store.ListAsync(company, All, 50, default)).Items);
        var batch = Batch();
        await Assert.ThrowsAsync<PdmRuleException>(() => Store().SaveAsync(batch with { BatchId = "../../escape" }, company, "engineer", default));
        await Assert.ThrowsAsync<PdmRuleException>(() => Store().SaveAsync(batch with { Entries = [batch.Entries[0], batch.Entries[0] with { Kind = "error" }] }, company, "engineer", default));
    }

    [Fact]
    public async Task ApiRequiresAuthenticationAdministratorAndReturnsReceiptAndAudit()
    {
        using var factory = new PdmApiFactory().WithWebHostBuilder(builder => builder.ConfigureServices(services => services.Configure<ClientLogOptions>(o => o.Root = root)));
        using var client = factory.CreateClient();
        await SeedAdmin(factory.Services);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/client-logs")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/client-logs/batches", Content(Batch()))).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token("engineer", "Engineer"));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/client-logs")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/client-logs/download")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token("admin", "Administrator"));
        var batch = Batch();
        var upload = await client.PostAsync("/api/client-logs/batches", Content(batch));
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        Assert.Contains(batch.BatchId, await upload.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/client-logs/batches", Content(batch))).StatusCode);
        var rows = JsonDocument.Parse(await client.GetStringAsync("/api/client-logs"));
        Assert.Equal(1, rows.RootElement.GetProperty("total").GetInt32());
        var download = await client.GetAsync("/api/client-logs/download");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("application/zip", download.Content.Headers.ContentType!.MediaType);
        Assert.Contains("client-logs.download", await client.GetStringAsync("/api/audit"));
    }

    [Fact]
    public async Task MalformedAndDecompressionBombRequestsReturnChineseErrors()
    {
        using var factory = new PdmApiFactory(); using var client = factory.CreateClient();
        await SeedAdmin(factory.Services);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token("admin", "Administrator"));
        var malformed = await client.PostAsync("/api/client-logs/batches", new ByteArrayContent([1, 2, 3]));
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        using var bytes = new MemoryStream();
        using (var gzip = new GZipStream(bytes, CompressionMode.Compress, true)) gzip.Write(new byte[11 * 1024 * 1024]);
        var bomb = await client.PostAsync("/api/client-logs/batches", new ByteArrayContent(bytes.ToArray()));
        Assert.Equal(HttpStatusCode.BadRequest, bomb.StatusCode);
        var body = JsonDocument.Parse(await bomb.Content.ReadAsStringAsync());
        Assert.Contains("大小限制", body.RootElement.GetProperty("message").GetString());
    }

    private static ByteArrayContent Content(ClientLogBatch batch)
    {
        using var bytes = new MemoryStream();
        using (var gzip = new GZipStream(bytes, CompressionMode.Compress, true)) JsonSerializer.Serialize(gzip, batch, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return new ByteArrayContent(bytes.ToArray());
    }
    private static async Task SeedAdmin(IServiceProvider services)
    {
        var repository = services.GetRequiredService<IPdmRepository>();
        var company = (await repository.GetProjectNumberingOptionsAsync(default)).Organizations.First(x => x.IsActive).Id;
        await repository.CreateUserAsync(new(Guid.NewGuid(), "admin", "管理员", "unused", UserRole.Administrator, true), default);
        await repository.SetUserCompanyScopeAsync("admin", company, false, [], "admin", default);
    }
    private static string Token(string username, string role) => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("upton-pdm", "upton-pdm-clients",
        [new Claim(ClaimTypes.Name, username), new Claim(ClaimTypes.Role, role)], expires: DateTime.UtcNow.AddMinutes(10),
        signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256)));
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(int days) => now = now.AddDays(days);
    }
}
