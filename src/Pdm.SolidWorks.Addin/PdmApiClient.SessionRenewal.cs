using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Upton.Pdm.ClientShared;

namespace Upton.Pdm.SolidWorks;

internal sealed partial class PdmApiClient
{
    private sealed class RenewableSession
    {
        internal readonly string AccessToken, ResumeToken, Username;
        internal readonly DateTimeOffset ExpiresAt;
        internal RenewableSession(LoginResponseDto response)
        {
            AccessToken = response.AccessToken; ResumeToken = response.ResumeToken; Username = response.Username;
            ExpiresAt = DateTimeOffset.TryParse(response.ExpiresAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
                ? at : DateTimeOffset.MinValue;
        }
    }

    private RenewableSession renewableSession;
    private readonly SemaphoreSlim sessionRenewalGate = new SemaphoreSlim(1, 1);
    private Func<DateTimeOffset> sessionNow = () => DateTimeOffset.UtcNow;
    private long renewableSessionEpoch;
    private int sessionClosing;
    private string CurrentSessionAccessToken => Volatile.Read(ref renewableSession)?.AccessToken
        ?? httpClient.DefaultRequestHeaders.Authorization?.Parameter;

    private void StartRenewableSession(LoginResponseDto response)
    {
        Interlocked.Increment(ref renewableSessionEpoch);
        Volatile.Write(ref renewableSession, new RenewableSession(response));
    }

    private void ClearRenewableSession()
    {
        Interlocked.Increment(ref renewableSessionEpoch);
        Volatile.Write(ref renewableSession, null);
    }

    private async Task RenewSessionAsync(bool force, string observedToken, CancellationToken token)
    {
        var before = Volatile.Read(ref renewableSession);
        if (before == null || string.IsNullOrWhiteSpace(before.ResumeToken)) return;
        if (!force && before.ExpiresAt > sessionNow().AddMinutes(5)) return;
        var epoch = Interlocked.Read(ref renewableSessionEpoch);
        await sessionRenewalGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref sessionClosing) != 0) throw new ObjectDisposedException(nameof(PdmApiClient));
            var current = Volatile.Read(ref renewableSession);
            if (epoch != Interlocked.Read(ref renewableSessionEpoch) || current == null)
                throw new InvalidOperationException("登录状态已改变，本次请求未继续执行。");
            if (force ? !string.Equals(current.AccessToken, observedToken, StringComparison.Ordinal)
                : current.ExpiresAt > sessionNow().AddMinutes(5)) return;
            var scope = EditJournalScope;
            using var content = new StringContent(serializer.Serialize(new { resumeToken = current.ResumeToken, clientType = "solidworks" }), Encoding.UTF8, "application/json");
            using var response = await httpClient.PostAsync("api/auth/resume", content, token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            // A response from an earlier login must never restore a disposed or
            // switched account/company, even if its renewal itself succeeded.
            if (epoch != Interlocked.Read(ref renewableSessionEpoch) || scope != EditJournalScope || Volatile.Read(ref sessionClosing) != 0)
                throw new InvalidOperationException("登录或项目公司已改变，未使用先前的续期结果。");
            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
            {
                using var expired = new HttpResponseMessage(HttpStatusCode.Unauthorized);
                ThrowAuthenticationExpiredIfNeeded(expired);
            }
            ThrowServiceUnavailableIfNeeded(response);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(GetErrorMessage(response.StatusCode.ToString(), body));
            var renewed = serializer.Deserialize<LoginResponseDto>(body);
            if (renewed == null || string.IsNullOrWhiteSpace(renewed.AccessToken) || string.IsNullOrWhiteSpace(renewed.ResumeToken)
                || !string.Equals(renewed.Username, current.Username, StringComparison.OrdinalIgnoreCase)
                || renewed.ActiveCompanyId != ActiveSessionCompany())
                throw new InvalidOperationException("登录续期返回的账号或公司不一致，已停止本次操作，请重新核对登录。");
            var next = new RenewableSession(renewed);
            if (next.ExpiresAt <= sessionNow()) throw new InvalidOperationException("登录续期未返回有效的到期时间，请稍后重试。");
            Volatile.Write(ref renewableSession, next);
            ClientDiagnostics.Bind(httpClient.BaseAddress.AbsoluteUri, renewed.AccessToken, renewed.ActiveCompanyId.ToString());
            ClientDiagnostics.Record("runtime", "登录续期", "已自动延续当前账号会话。");
        }
        finally { sessionRenewalGate.Release(); }
    }

    private Guid ActiveSessionCompany()
    {
        if (httpClient.DefaultRequestHeaders.TryGetValues("X-Company-Id", out var values))
            foreach (var value in values) if (Guid.TryParse(value, out var id)) return id;
        return Guid.Empty;
    }

    private sealed class SessionRenewalHandler : DelegatingHandler
    {
        private readonly PdmApiClient owner;
        internal SessionRenewalHandler(PdmApiClient owner, HttpMessageHandler transport) : base(transport) => this.owner = owner;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri.AbsolutePath.TrimEnd('/');
            if (path.EndsWith("/auth/login", StringComparison.OrdinalIgnoreCase) || path.EndsWith("/auth/resume", StringComparison.OrdinalIgnoreCase))
                return await base.SendAsync(request, token).ConfigureAwait(false);
            if (path.EndsWith("/auth/logout", StringComparison.OrdinalIgnoreCase))
            {
                var access = owner.CurrentSessionAccessToken;
                if (access != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
                return await base.SendAsync(request, token).ConfigureAwait(false);
            }
            var epoch = Interlocked.Read(ref owner.renewableSessionEpoch); var scope = owner.EditJournalScope;
            await owner.RenewSessionAsync(false, null, token).ConfigureAwait(false);
            void ValidateScope()
            {
                if (epoch != Interlocked.Read(ref owner.renewableSessionEpoch) || scope != owner.EditJournalScope || Volatile.Read(ref owner.sessionClosing) != 0)
                    throw new InvalidOperationException("登录状态已改变，本次请求未继续执行。");
            }
            ValidateScope();
            var accessToken = owner.CurrentSessionAccessToken;
            if (accessToken != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            var result = await base.SendAsync(request, token).ConfigureAwait(false);
            if (result.StatusCode != HttpStatusCode.Unauthorized || Volatile.Read(ref owner.renewableSession)?.ResumeToken == null) return result;
            result.Dispose();
            await owner.RenewSessionAsync(true, accessToken, token).ConfigureAwait(false);
            ValidateScope();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", owner.CurrentSessionAccessToken);
            // Retry only a definitive 401, rejected before the endpoint ran. Do not
            // retry successful or ambiguous business writes at this layer.
            return await base.SendAsync(request, token).ConfigureAwait(false);
        }
    }
}
