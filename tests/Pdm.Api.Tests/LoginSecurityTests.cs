using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Upton.Pdm.Api;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Api.Tests;

public sealed class LoginSecurityTests
{
    private readonly TestClock clock = new();
    private readonly LoginSecurityService service;
    private readonly UserAccount account = new(Guid.NewGuid(), "engineer", "工程师", "unused", UserRole.Engineer, true);
    private readonly CancellationToken ct = CancellationToken.None;
    public LoginSecurityTests() => service = new(new LoginSecurityStore(Options.Create(new PdmDatabaseOptions())), clock);
    private static LoginPeer Peer(string type = "web", string device = "") => new(type, Guid.NewGuid().ToString("N"), device, "测试电脑", "001122334455");

    [Fact]
    public async Task DisabledByDefault_AllowsMultipleLocations()
    {
        var policy = await service.PolicyAsync(ct);
        Assert.False(policy.SingleSessionEnabled); Assert.False(policy.DeviceBindingEnabled);
        Assert.Null((await service.OpenAsync(account, Peer(), null, false, ct)).Error);
        Assert.Null((await service.OpenAsync(account, Peer(), null, false, ct)).Error);
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("expired")]
    [InlineData("signature")]
    public async Task DisabledRestrictions_DoNotRequireDeviceProofForLoginOrBusinessRequests(string failure)
    {
        using var rsa = RSA.Create(2048);
        var request = new DefaultHttpContext().Request;
        request.Method = "POST"; request.Path = "/api/auth/login";
        var instance = Guid.NewGuid().ToString("N");
        Sign(request, rsa, "desktop", instance, clock.GetUtcNow().AddMinutes(failure == "expired" ? -10 : 0));
        if (failure == "malformed") request.Headers["X-UPLM-Device"] = "invalid";
        if (failure == "signature") request.Headers["X-UPLM-Proof"] = Convert.ToBase64String(new byte[256]);
        var peer = await service.ResolvePeerAsync(request, instance, "desktop", ct);
        Assert.Equal("desktop", peer.Type);
        var login = await service.OpenAsync(account, peer, null, false, ct);
        Assert.Null(login.Error);
        Assert.Empty((await service.ListAsync(ct)).Single().Devices);
        Assert.Null(await service.ValidateAsync(account.Username, login.SessionId, request, ct));
        var resumedPeer = await service.ResolvePeerAsync(request, instance, "desktop", ct);
        Assert.Null((await service.OpenAsync(account, resumedPeer, login.SessionId, true, ct)).Error);
        await service.RevokeAsync(account.Username, login.SessionId!, ct);
        Assert.NotNull(await service.ValidateAsync(account.Username, login.SessionId, request, ct));
        Assert.NotNull((await service.OpenAsync(account, resumedPeer, login.SessionId, true, ct)).Error);
        await service.SavePolicyAsync(new(true, true), ct);
        await Assert.ThrowsAsync<PdmRuleException>(() => service.ResolvePeerAsync(request, instance, "desktop", ct));
    }

    [Fact]
    public async Task DisabledRestrictions_ApiAcceptsLegacyDeviceHeadersAndStillChecksPasswordAndLogout()
    {
        await using var factory = new PdmApiFactory();
        using var client = factory.CreateClient();
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        var passwords = factory.Services.GetRequiredService<IPasswordService>();
        var username = "legacy-device-" + Guid.NewGuid().ToString("N");
        await repository.CreateUserAsync(new(Guid.NewGuid(), username, "其他客户端兼容回归", passwords.Hash("Password1"), UserRole.Engineer, true), ct);
        client.DefaultRequestHeaders.Add("X-UPLM-Device", "legacy-invalid-device-header");
        client.DefaultRequestHeaders.Add("X-UPLM-Proof", "legacy-invalid-proof");
        var instance = Guid.NewGuid().ToString("N");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/login", new { username, password = "wrong", clientType = "desktop", clientInstanceId = instance })).StatusCode);
        var response = await client.PostAsJsonAsync("/api/auth/login", new { username, password = "Password1", clientType = "desktop", clientInstanceId = instance });
        response.EnsureSuccessStatusCode();
        var login = (await response.Content.ReadFromJsonAsync<LoginResponse>())!;
        var resumed = await client.PostAsJsonAsync("/api/auth/resume", new { login.ResumeToken, clientType = "desktop", clientInstanceId = instance });
        resumed.EnsureSuccessStatusCode();
        var session = (await resumed.Content.ReadFromJsonAsync<LoginResponse>())!;
        client.DefaultRequestHeaders.Authorization = new("Bearer", session.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/auth/heartbeat", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/auth/heartbeat", null)).StatusCode);
    }

    [Fact]
    public async Task EachTerminalGetsOneSlot_AndDuplicateDoesNotRevokeOriginal()
    {
        await service.SavePolicyAsync(new(true, false), ct);
        foreach (var type in new[] { "web", "desktop", "solidworks" })
        {
            var peer = Peer(type, type == "web" ? "" : "device");
            var first = await service.OpenAsync(account, peer, null, false, ct);
            Assert.Null(first.Error);
            Assert.Equal(first.SessionId, (await service.OpenAsync(account, peer, first.SessionId, true, ct)).SessionId);
            Assert.Contains("已在", (await service.OpenAsync(account, Peer(type, peer.DeviceId), null, false, ct)).Error!);
            Assert.False((await service.ListAsync(ct)).Single().Sessions.Single(s => s.Id == first.SessionId).Revoked);
        }
    }

    [Fact]
    public async Task ConcurrentLogins_OnlyOneWins()
    {
        await service.SavePolicyAsync(new(true, false), ct);
        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => service.OpenAsync(account, Peer(), null, false, ct)));
        Assert.Single(results, r => r.SessionId is not null);
    }

    [Fact]
    public async Task SameBrowserTabsShareOneSession()
    {
        await service.SavePolicyAsync(new(true, false), ct);
        var peer = Peer();
        var first = await service.OpenAsync(account, peer, null, false, ct);
        Assert.Equal(first.SessionId, (await service.OpenAsync(account, peer, null, false, ct)).SessionId);
    }

    [Fact]
    public async Task CrashExpiresAfterFiveMinutes_OldResumeCannotTakeBackNewSlot()
    {
        await service.SavePolicyAsync(new(true, false), ct);
        var peer = Peer();
        var first = await service.OpenAsync(account, peer, null, false, ct);
        clock.Now += TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1);
        Assert.Null((await service.OpenAsync(account, Peer(), null, false, ct)).Error);
        Assert.NotNull((await service.OpenAsync(account, peer, first.SessionId, true, ct)).Error);
    }

    [Fact]
    public async Task NativeResumeCannotCloneAnotherDeviceTypeOrLiveProcess()
    {
        await service.SavePolicyAsync(new(true, false), ct);
        var peer = Peer("desktop", "one");
        var session = await service.OpenAsync(account, peer, null, false, ct);
        foreach (var other in new[] { Peer("web"), Peer("solidworks", "one"), Peer("desktop", "two"), Peer("desktop", "one") })
            Assert.NotNull((await service.OpenAsync(account, other, session.SessionId, true, ct)).Error);
    }

    [Fact]
    public async Task BindingNeedsApproval_OneComputerSharedByDesktopAndPlugin_WebRemainsUnbound()
    {
        await service.SavePolicyAsync(new(false, true), ct);
        var peer = Peer("desktop", "one");
        Assert.Contains("尚未绑定", (await service.OpenAsync(account, peer, null, false, ct)).Error!);
        Assert.False((await service.ListAsync(ct)).Single().Devices.Single().Approved);
        await service.SetDeviceAsync(account.Username, "one", true, ct);
        Assert.Null((await service.OpenAsync(account, peer, null, false, ct)).Error);
        Assert.Null((await service.OpenAsync(account, Peer("solidworks", "one"), null, false, ct)).Error);
        Assert.Null((await service.OpenAsync(account, Peer(), null, false, ct)).Error);
        Assert.Null((await service.OpenAsync(account, peer with { Mac = "AABBCCDDEEFF" }, null, false, ct)).Error);
        Assert.NotNull((await service.OpenAsync(account, Peer("desktop", "two"), null, false, ct)).Error);
        await Assert.ThrowsAsync<PdmRuleException>(() => service.SetDeviceAsync(account.Username, "two", true, ct));
        await service.SetDeviceAsync(account.Username, "one", false, ct);
        await service.SetDeviceAsync(account.Username, "two", true, ct);
        Assert.All((await service.ListAsync(ct)).Single().Sessions.Where(s => s.DeviceId == "one"), s => Assert.True(s.Revoked));
    }

    [Fact]
    public async Task TurningOffRetainsBindings_ReenablingInvalidatesAllOldSessions()
    {
        var peer = Peer("desktop", "one");
        await service.OpenAsync(account, peer, null, false, ct);
        await service.SetDeviceAsync(account.Username, "one", true, ct);
        var firstPolicy = await service.SavePolicyAsync(new(true, true), ct);
        var session = await service.OpenAsync(account, peer, null, false, ct);
        await service.SavePolicyAsync(new(false, false), ct);
        Assert.True((await service.ListAsync(ct)).Single().Devices.Single().Approved);
        Assert.Null((await service.OpenAsync(account, Peer("desktop", "two"), null, false, ct)).Error);
        var secondPolicy = await service.SavePolicyAsync(new(true, true), ct);
        Assert.NotEqual(firstPolicy.Version, secondPolicy.Version);
        Assert.NotNull((await service.OpenAsync(account, peer, session.SessionId, true, ct)).Error);
        Assert.NotNull(await service.ValidateAsync(account.Username, null, new DefaultHttpContext().Request, ct));
    }

    [Fact]
    public async Task LogoutAndAdminRevocationAlsoInvalidateResume_WhenSwitchesAreOff()
    {
        var peer = Peer();
        var session = await service.OpenAsync(account, peer, null, false, ct);
        await service.RevokeAsync(account.Username, session.SessionId!, ct);
        Assert.NotNull((await service.OpenAsync(account, peer, session.SessionId, true, ct)).Error);
        Assert.NotNull(await service.ValidateAsync(account.Username, session.SessionId, new DefaultHttpContext().Request, ct));
    }

    [Fact]
    public void DeviceProofVerifiesSignaturePathAndAuthorization_AndRejectsTampering()
    {
        using var rsa = RSA.Create(2048);
        var request = new DefaultHttpContext().Request;
        request.Method = "POST"; request.Path = "/api/auth/login";
        Sign(request, rsa, "desktop", Guid.NewGuid().ToString("N"), clock.GetUtcNow());
        Assert.Equal("desktop", service.Peer(request).Type);
        request.Path = "/api/auth/resume";
        Assert.Throws<PdmRuleException>(() => service.Peer(request));
        request.Path = "/api/auth/login"; request.Headers.Authorization = "Bearer copied-token";
        Assert.Throws<PdmRuleException>(() => service.Peer(request));
        request.Headers.Authorization = "";
        clock.Now += TimeSpan.FromMinutes(4);
        Assert.Throws<PdmRuleException>(() => service.Peer(request));
    }

    [Fact]
    public async Task HandoffUsesSeparateDesktopSlot_AndCannotReviveRevokedPlugin()
    {
        await service.SavePolicyAsync(new(true, false), ct);
        var plugin = await service.OpenAsync(account, Peer("solidworks", "one"), null, false, ct);
        var desktopPeer = Peer("desktop", "one");
        var desktop = await service.OpenAsync(account, desktopPeer, null, false, ct, true, plugin.SessionId);
        Assert.Null(desktop.Error); Assert.NotEqual(plugin.SessionId, desktop.SessionId);
        Assert.NotNull((await service.OpenAsync(account, Peer("desktop", "one"), null, false, ct, true, plugin.SessionId)).Error);
        Assert.NotNull((await service.OpenAsync(account, Peer("desktop", "two"), null, false, ct, true, plugin.SessionId)).Error);
        await service.RevokeAsync(account.Username, plugin.SessionId!, ct);
        await service.SavePolicyAsync(new(false, false), ct);
        Assert.NotNull((await service.OpenAsync(account, desktopPeer, null, false, ct, true, plugin.SessionId)).Error);
    }

    [Fact]
    public async Task SignedNativeApi_RequiresApproval_RejectsCopiedTokens_AndKeepsThreeSlotsIndependent()
    {
        await using var factory = new PdmApiFactory();
        using var client = factory.CreateClient();
        using var rsa = RSA.Create(2048);
        using var otherRsa = RSA.Create(2048);
        var security = factory.Services.GetRequiredService<LoginSecurityService>();
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        var passwords = factory.Services.GetRequiredService<IPasswordService>();
        var username = "native-" + Guid.NewGuid().ToString("N");
        await repository.CreateUserAsync(new(Guid.NewGuid(), username, "原生端验证", passwords.Hash("Password1"), UserRole.Engineer, true), ct);
        await security.SavePolicyAsync(new(true, true), ct);
        var desktopInstance = Guid.NewGuid().ToString("N");
        async Task<HttpResponseMessage> Send(string path, string type, string instance, object? body = null, string? token = null, RSA? identity = null)
        {
            var context = new DefaultHttpContext();
            context.Request.Method = "POST"; context.Request.Path = path;
            if (token is not null) context.Request.Headers.Authorization = "Bearer " + token;
            Sign(context.Request, identity ?? rsa, type, instance, DateTimeOffset.UtcNow);
            var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = body is null ? null : JsonContent.Create(body) };
            foreach (var header in context.Request.Headers) request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            return await client.SendAsync(request);
        }
        var credentials = new { username, password = "Password1" };
        Assert.Equal(HttpStatusCode.Conflict, (await Send("/api/auth/login", "desktop", desktopInstance, credentials)).StatusCode);
        var device = (await security.ListAsync(ct)).Single(s => s.Username == username).Devices.Single();
        await security.SetDeviceAsync(username, device.Id, true, ct);
        var desktopResponse = await Send("/api/auth/login", "desktop", desktopInstance, credentials);
        desktopResponse.EnsureSuccessStatusCode();
        var desktop = (await desktopResponse.Content.ReadFromJsonAsync<LoginResponse>())!;
        Assert.Equal(HttpStatusCode.OK, (await Send("/api/auth/heartbeat", "desktop", desktopInstance, token: desktop.AccessToken)).StatusCode);
        using (var malformed = new HttpRequestMessage(HttpMethod.Post, "/api/auth/heartbeat"))
        {
            malformed.Headers.Authorization = new("Bearer", desktop.AccessToken);
            malformed.Headers.Add("X-UPLM-Device", "invalid");
            malformed.Headers.Add("X-UPLM-Proof", "invalid");
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(malformed)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send("/api/auth/heartbeat", "desktop", desktopInstance, token: desktop.AccessToken, identity: otherRsa)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send("/api/auth/heartbeat", "desktop", Guid.NewGuid().ToString("N"), token: desktop.AccessToken)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", desktop.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
        client.DefaultRequestHeaders.Authorization = null;
        Assert.Equal(HttpStatusCode.Conflict, (await Send("/api/auth/login", "desktop", Guid.NewGuid().ToString("N"), credentials)).StatusCode);
        var pluginInstance = Guid.NewGuid().ToString("N");
        var pluginResponse = await Send("/api/auth/login", "solidworks", pluginInstance, credentials);
        pluginResponse.EnsureSuccessStatusCode();
        var plugin = (await pluginResponse.Content.ReadFromJsonAsync<LoginResponse>())!;
        Assert.Equal(HttpStatusCode.OK, (await Send("/api/auth/resume", "desktop", desktopInstance, new { plugin.ResumeToken, desktopHandoff = true })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/login", new { username, password = "Password1", clientInstanceId = Guid.NewGuid(), clientType = "web" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/auth/login", new { username, password = "Password1", clientInstanceId = Guid.NewGuid(), clientType = "web" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send("/api/auth/logout", "desktop", desktopInstance, token: desktop.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send("/api/auth/resume", "desktop", desktopInstance, new { desktop.ResumeToken })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send("/api/auth/heartbeat", "solidworks", pluginInstance, token: plugin.AccessToken)).StatusCode);
    }

    [Fact]
    public async Task BuiltInAdminCanEditGlobalPolicy_CompanyAdministratorCannot()
    {
        await using var factory = new PdmApiFactory();
        using var client = factory.CreateClient();
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        var issuer = factory.Services.GetRequiredService<ITokenIssuer>();
        var admin = new UserAccount(Guid.NewGuid(), "admin", "系统管理员", "unused", UserRole.Administrator, true);
        await repository.CreateUserAsync(admin, ct);
        client.DefaultRequestHeaders.Authorization = new("Bearer", issuer.Issue(admin, TimeSpan.FromHours(1)));
        var response = await client.GetFromJsonAsync<JsonElement>("/api/login-security");
        Assert.True(response.GetProperty("canEditPolicy").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/login-security/policy", new SaveLoginSecurityPolicy(false, false))).StatusCode);
        var companyAdmin = new UserAccount(Guid.NewGuid(), "company-admin-" + Guid.NewGuid().ToString("N"), "公司管理员", "unused", UserRole.Administrator, true);
        await repository.CreateUserAsync(companyAdmin, ct);
        client.DefaultRequestHeaders.Authorization = new("Bearer", issuer.Issue(companyAdmin, TimeSpan.FromHours(1)));
        response = await client.GetFromJsonAsync<JsonElement>("/api/login-security");
        Assert.False(response.GetProperty("canEditPolicy").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync("/api/login-security/policy", new SaveLoginSecurityPolicy(true, false))).StatusCode);
    }

    [Fact]
    public async Task ApiEnforcesLegacyTokens_Logout_AndAdministratorPermissions()
    {
        await using var factory = new PdmApiFactory();
        using var client = factory.CreateClient();
        var security = factory.Services.GetRequiredService<LoginSecurityService>();
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        var passwords = factory.Services.GetRequiredService<IPasswordService>();
        var user = new UserAccount(Guid.NewGuid(), "security-" + Guid.NewGuid().ToString("N"), "测试工程师", passwords.Hash("Password1"), UserRole.Engineer, true);
        await repository.CreateUserAsync(user, ct);
        var browser = Guid.NewGuid().ToString("N");
        async Task<LoginResponse> Login()
        {
            client.DefaultRequestHeaders.Authorization = null;
            var response = await client.PostAsJsonAsync("/api/auth/login", new { username = user.Username, password = "Password1", clientInstanceId = browser, clientType = "web" });
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<LoginResponse>())!;
        }
        var login = await Login();
        client.DefaultRequestHeaders.Authorization = new("Bearer", login.AccessToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/login-security")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/auth/logout", null)).StatusCode);
        var heartbeat = await client.PostAsync("/api/auth/heartbeat", null);
        Assert.Equal(HttpStatusCode.Unauthorized, heartbeat.StatusCode);
        Assert.Equal("true", heartbeat.Headers.GetValues("X-UPLM-Session-Final").Single());
        client.DefaultRequestHeaders.Authorization = null;
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/resume", new { login.ResumeToken, clientInstanceId = browser, clientType = "web" })).StatusCode);
        login = await Login();
        await security.SavePolicyAsync(new(true, false), ct);
        var legacy = new JwtSecurityToken("upton-pdm", "upton-pdm-clients", [new Claim(ClaimTypes.Name, user.Username), new Claim(ClaimTypes.Role, "Engineer")],
            expires: DateTime.UtcNow.AddHours(1), signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256));
        client.DefaultRequestHeaders.Authorization = new("Bearer", new JwtSecurityTokenHandler().WriteToken(legacy));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
        client.DefaultRequestHeaders.Authorization = null;
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/resume", new { login.ResumeToken, clientInstanceId = browser, clientType = "web" })).StatusCode);
        await Login();
        client.DefaultRequestHeaders.Authorization = null;
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/auth/login", new { username = user.Username, password = "Password1", clientInstanceId = Guid.NewGuid(), clientType = "web" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/auth/login", new { username = user.Username, password = "Password1", clientType = "desktop" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/auth/login", new { username = user.Username, password = "Password1" })).StatusCode);
    }

    internal static void Sign(HttpRequest request, RSA rsa, string type, string instance, DateTimeOffset now)
    {
        var proof = new LoginDeviceProof(type, instance, rsa.ToXmlString(false), "测试电脑", "001122334455", now.ToUnixTimeSeconds(), Guid.NewGuid().ToString("N"));
        var encoded = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(proof, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.Headers.Authorization.ToString()))).ToLowerInvariant();
        var content = request.Method + "\n" + request.Path.ToUriComponent() + request.QueryString.ToUriComponent() + "\n" + encoded + "\n" + hash;
        request.Headers["X-UPLM-Device"] = encoded;
        request.Headers["X-UPLM-Proof"] = Convert.ToBase64String(rsa.SignData(Encoding.UTF8.GetBytes(content), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
