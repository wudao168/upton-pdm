using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Upton.Pdm.Application;

namespace Upton.Pdm.Api;

public sealed class LoginSecurityPolicy
{
    public bool SingleSessionEnabled { get; set; }
    public bool DeviceBindingEnabled { get; set; }
    public string Version { get; set; } = "initial";
}
public sealed record SaveLoginSecurityPolicy(bool SingleSessionEnabled, bool DeviceBindingEnabled);
public sealed class LoginUserState
{
    public string Username { get; set; } = "";
    public Guid CompanyId { get; set; }
    public List<LoginDevice> Devices { get; set; } = [];
    public List<LoginSession> Sessions { get; set; } = [];
}
public sealed class LoginDevice
{
    public string Id { get; set; } = "";
    public string Machine { get; set; } = "";
    public string Mac { get; set; } = "";
    public bool Approved { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
}
public sealed class LoginSession
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "web";
    public string Instance { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string Machine { get; set; } = "";
    public string Version { get; set; } = "initial";
    public bool Revoked { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
}
public sealed record LoginDeviceProof(string Type, string Instance, string PublicKey, string Machine, string Mac, long Time, string Nonce);
public sealed record LoginPeer(string Type, string Instance, string DeviceId, string Machine, string Mac);
public sealed record LoginSecurityResult(string? SessionId, string? Error);

public sealed class LoginSecurityService(LoginSecurityStore store, TimeProvider clock)
{
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public Task<LoginSecurityPolicy> PolicyAsync(CancellationToken ct) => store.ReadAsync<LoginSecurityPolicy>("policy", ct);

    public Task<LoginSecurityPolicy> SavePolicyAsync(SaveLoginSecurityPolicy input, CancellationToken ct) =>
        store.UpdateAsync<LoginSecurityPolicy, LoginSecurityPolicy>("policy", state =>
        {
            if ((!state.SingleSessionEnabled && input.SingleSessionEnabled) || (!state.DeviceBindingEnabled && input.DeviceBindingEnabled))
                state.Version = Guid.NewGuid().ToString("N");
            state.SingleSessionEnabled = input.SingleSessionEnabled;
            state.DeviceBindingEnabled = input.DeviceBindingEnabled;
            return state;
        }, ct);

    public LoginPeer Peer(HttpRequest request, string? browserInstance = null)
    {
        var encoded = request.Headers["X-UPLM-Device"].ToString();
        if (encoded.Length == 0)
            return new("web", Guid.TryParse(browserInstance, out var id) ? id.ToString("N") : Guid.NewGuid().ToString("N"), "", "网页浏览器", "");
        try
        {
            if (encoded.Length > 8192) throw new FormatException();
            var proof = JsonSerializer.Deserialize<LoginDeviceProof>(Convert.FromBase64String(encoded), Json)!;
            if (proof.Type is not ("desktop" or "solidworks") || !Guid.TryParse(proof.Instance, out _)
                || !Guid.TryParse(proof.Nonce, out _) || proof.PublicKey.Length > 4096
                || proof.Machine.Length is < 1 or > 160 || proof.Mac.Length > 512
                || proof.Time < clock.GetUtcNow().ToUnixTimeSeconds() - 180 || proof.Time > clock.GetUtcNow().ToUnixTimeSeconds() + 180) throw new FormatException();
            using var rsa = RSA.Create();
            rsa.FromXmlString(proof.PublicKey);
            if (rsa.KeySize < 2048 || proof.PublicKey.Contains("<D>", StringComparison.Ordinal)) throw new FormatException();
            var tokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.Headers.Authorization.ToString()))).ToLowerInvariant();
            var content = request.Method + "\n" + request.Path.ToUriComponent() + request.QueryString.ToUriComponent() + "\n" + encoded + "\n" + tokenHash;
            if (!rsa.VerifyData(Encoding.UTF8.GetBytes(content), Convert.FromBase64String(request.Headers["X-UPLM-Proof"].ToString()),
                    HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) throw new CryptographicException();
            var deviceId = Convert.ToHexString(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo())).ToLowerInvariant();
            return new(proof.Type, proof.Instance, deviceId, proof.Machine, proof.Mac);
        }
        catch (Exception error) when (error is FormatException or CryptographicException or JsonException or ArgumentException or NullReferenceException or System.Xml.XmlException)
        {
            throw new PdmRuleException("电脑身份验证失败，请校准电脑时间并升级客户端后重新登录。");
        }
    }

    public async Task<LoginPeer> ResolvePeerAsync(HttpRequest request, string? browserInstance, string? clientType, CancellationToken ct)
    {
        var policy = await PolicyAsync(ct);
        if (!policy.SingleSessionEnabled && !policy.DeviceBindingEnabled)
        {
            // 调试关闭限制时，身份采集只用于登记；旧程序、时间偏差或无效签名不能阻止密码登录。
            try { return Peer(request, browserInstance); }
            catch (PdmRuleException)
            {
                var type = clientType is "desktop" or "solidworks" ? clientType : "web";
                var instance = Guid.TryParse(browserInstance, out var id) ? id.ToString("N") : Guid.NewGuid().ToString("N");
                return new(type, instance, "", TypeName(type) + "（电脑身份未校验）", "");
            }
        }
        var peer = Peer(request, browserInstance);
        if (policy.SingleSessionEnabled || policy.DeviceBindingEnabled)
        {
            if ((string.IsNullOrWhiteSpace(clientType) && !request.Headers.ContainsKey("X-UPLM-Device"))
                || (!string.IsNullOrWhiteSpace(clientType) && clientType != peer.Type))
                throw new PdmRuleException("当前登录程序版本过旧或电脑身份信息不完整，请刷新网页，或升级客户端、插件后重新登录。");
        }
        return peer;
    }

    public async Task<LoginSecurityResult> OpenAsync(UserAccount account, LoginPeer peer, string? resumeId, bool resuming, CancellationToken ct, bool handoff = false, string? handoffFrom = null)
    {
        var policy = await PolicyAsync(ct);
        var now = clock.GetUtcNow();
        return await store.UpdateAsync<LoginUserState, LoginSecurityResult>(Key(account.Username), state =>
        {
            state.Username = account.Username;
            state.CompanyId = account.CompanyId ?? Guid.Empty;
            state.Sessions.RemoveAll(s => s.LastSeenAt < now.AddDays(-31));
            var enabled = policy.SingleSessionEnabled || policy.DeviceBindingEnabled;
            if (handoff)
            {
                var source = state.Sessions.Find(s => s.Id == handoffFrom);
                if ((handoffFrom is not null && (source is null || source.Revoked))
                    || (enabled && (peer.Type != "desktop" || source is null || source.Type != "solidworks"
                    || source.DeviceId != peer.DeviceId || source.Version != policy.Version || source.LastSeenAt <= now - Lease)))
                    return new(null, "插件登录交接已失效，请在桌面客户端重新登录。");
            }
            var existing = resumeId is null ? null : state.Sessions.Find(s => s.Id == resumeId);
            if (resuming && (existing is null || existing.Revoked || ((policy.SingleSessionEnabled || policy.DeviceBindingEnabled) && existing.Version != policy.Version)))
                return new(null, "登录已失效，请重新输入账号密码登录。");
            if (existing is not null && (existing.Type != peer.Type || existing.DeviceId != peer.DeviceId))
            {
                if (enabled) return new(null, "登录会话与当前电脑或终端不一致，请重新登录。");
                existing = null; // 关闭限制时允许旧版插件凭证交接，另建所属端的会话。
            }
            if (peer.Type != "web" && peer.DeviceId.Length > 0)
            {
                var device = state.Devices.Find(d => d.Id == peer.DeviceId);
                if (device is null)
                {
                    // 限制待批准记录数量，避免反复请求造成无界增长；已批准记录始终保留。
                    if (state.Devices.Count >= 20) state.Devices.RemoveAll(d => !d.Approved && d.RequestedAt < now.AddDays(-7));
                    if (state.Devices.Count >= 20) return new(null, "待确认电脑过多，请联系管理员清理后重试。");
                    device = new() { Id = peer.DeviceId, Machine = peer.Machine, Mac = peer.Mac, RequestedAt = now };
                    state.Devices.Add(device);
                }
                device.Machine = peer.Machine; device.Mac = peer.Mac; device.LastSeenAt = now;
                if (policy.DeviceBindingEnabled && !device.Approved)
                    return new(null, "当前电脑尚未绑定，已登记绑定申请，请联系管理员确认电脑后重试。");
            }
            bool Online(LoginSession s) => !s.Revoked && s.Version == policy.Version && s.LastSeenAt > now - Lease;
            if (policy.SingleSessionEnabled)
            {
                var occupied = state.Sessions.Find(s => s.Type == peer.Type && Online(s) && s.Id != existing?.Id && s.Instance != peer.Instance);
                if (occupied is not null)
                    return new(null, $"该账号已在{TypeName(peer.Type)}登录（{occupied.Machine}，{occupied.CreatedAt.ToOffset(TimeSpan.FromHours(8)):yyyy-MM-dd HH:mm}），请先退出原登录或联系管理员下线。");
                if (existing is not null && Online(existing) && existing.Instance != peer.Instance)
                    return new(null, "该终端仍在其他进程运行，请先退出原程序，或等待异常退出后约5分钟再登录。");
            }
            existing ??= state.Sessions.Find(s => s.Type == peer.Type && s.Instance == peer.Instance && s.DeviceId == peer.DeviceId && Online(s));
            existing ??= new() { Id = Guid.NewGuid().ToString("N"), Type = peer.Type, DeviceId = peer.DeviceId, CreatedAt = now };
            if (policy.SingleSessionEnabled)
                foreach (var other in state.Sessions.Where(s => s.Type == peer.Type && s.Id != existing.Id)) other.Revoked = true;
            existing.Instance = peer.Instance; existing.Machine = peer.Machine; existing.Version = policy.Version; existing.LastSeenAt = now;
            if (!state.Sessions.Contains(existing)) state.Sessions.Add(existing);
            return new(existing.Id, null);
        }, ct);
    }

    public async Task<string?> ValidateAsync(string username, string? sessionId, HttpRequest request, CancellationToken ct)
    {
        var policy = await PolicyAsync(ct);
        if (string.IsNullOrWhiteSpace(sessionId))
            return policy.SingleSessionEnabled || policy.DeviceBindingEnabled ? "登录安全规则已启用，请重新登录。" : null;
        LoginPeer? peer = null;
        if (policy.SingleSessionEnabled || policy.DeviceBindingEnabled)
        {
            try { peer = Peer(request); }
            catch (PdmRuleException error) { return error.Message; }
        }
        var now = clock.GetUtcNow();
        return await store.UpdateAsync<LoginUserState, string?>(Key(username), state =>
        {
            var session = state.Sessions.Find(s => s.Id == sessionId);
            if (session is null || session.Revoked) return "该登录已退出或被管理员下线，请重新登录。";
            if ((policy.SingleSessionEnabled || policy.DeviceBindingEnabled) && session.Version != policy.Version)
                return "登录安全规则已更新，请重新登录。";
            if (peer is not null && (session.Type != peer.Type || session.DeviceId != peer.DeviceId || (peer.Type != "web" && session.Instance != peer.Instance)))
                return "登录会话与当前电脑或终端不一致，请重新登录。";
            if (policy.DeviceBindingEnabled && session.Type != "web" && !state.Devices.Any(d => d.Id == session.DeviceId && d.Approved))
                return "当前电脑绑定已解除，请联系管理员重新绑定。";
            if (policy.SingleSessionEnabled && state.Sessions.Any(s => s.Type == session.Type && s.Id != session.Id && !s.Revoked
                    && s.Version == policy.Version && s.LastSeenAt > now - Lease))
                return "该账号已在其他位置登录，请重新登录。";
            // 合并高频业务请求的心跳写入；独立心跳每分钟调用一次。
            if (session.LastSeenAt < now.AddSeconds(-30))
            {
                session.LastSeenAt = now;
                var device = state.Devices.Find(d => d.Id == session.DeviceId);
                if (device is not null) device.LastSeenAt = now;
            }
            return null;
        }, ct);
    }

    public Task<bool> RevokeAsync(string username, string id, CancellationToken ct) =>
        store.UpdateAsync<LoginUserState, bool>(Key(username), state =>
        {
            var session = state.Sessions.Find(s => s.Id == id);
            if (session is null) return false;
            session.Revoked = true; return true;
        }, ct);

    public Task<bool> SetDeviceAsync(string username, string id, bool approve, CancellationToken ct) =>
        store.UpdateAsync<LoginUserState, bool>(Key(username), state =>
        {
            var device = state.Devices.Find(d => d.Id == id) ?? throw new PdmNotFoundException("未找到该电脑的绑定申请，请先在目标电脑登录一次。");
            if (approve && state.Devices.Any(d => d.Approved && d.Id != id)) throw new PdmRuleException("该账号已绑定其他电脑，请先解除原电脑绑定。");
            device.Approved = approve; device.ApprovedAt = approve ? clock.GetUtcNow() : null;
            if (!approve) foreach (var session in state.Sessions.Where(s => s.DeviceId == id)) session.Revoked = true;
            return true;
        }, ct);

    public Task<LoginUserState[]> ListAsync(CancellationToken ct) => store.ListUsersAsync(ct);
    private static string Key(string username) => "user:" + username.Trim().ToLowerInvariant();
    public static string TypeName(string type) => type switch { "desktop" => "桌面客户端", "solidworks" => "SolidWorks插件", _ => "网页端" };
}
