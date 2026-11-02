using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Upton.Pdm.SolidWorks;

internal sealed partial class PdmApiClient
{
    private TimeSpan archiveRecoveryWindow = TimeSpan.FromMinutes(5);
    private Func<TimeSpan, CancellationToken, Task> archiveRecoveryDelay = Task.Delay;

    internal sealed class ArchiveConnectionTimeoutException : InvalidOperationException
    {
        internal ArchiveConnectionTimeoutException(Exception inner = null) : base(
            "PLM请求重试已超时，已保留本地文件和存档记录；请确认服务状态后核对本批结果。", inner) { }
    }

    // Call only for reads or operations whose retry semantics have been checked.
    // The bool tells writes to reconcile a potentially lost response before retrying.
    private async Task<T> RecoverArchiveConnectionAsync<T>(string operation,
        Func<CancellationToken, bool, Task<T>> action, CancellationToken token)
    {
        var scope = EditJournalScope;
        var authorization = httpClient.DefaultRequestHeaders.Authorization?.ToString();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        Stopwatch elapsed = null;
        var attempt = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (elapsed != null && (elapsed.Elapsed >= archiveRecoveryWindow || budget.IsCancellationRequested))
                throw new ArchiveConnectionTimeoutException();
            if (scope != EditJournalScope || authorization != httpClient.DefaultRequestHeaders.Authorization?.ToString())
                throw new InvalidOperationException("登录范围已变化，已停止本批网络重试；请重新登录后核对存档结果。");
            try
            {
                var result = await action(budget.Token, attempt != 0).ConfigureAwait(false);
                if (scope != EditJournalScope || authorization != httpClient.DefaultRequestHeaders.Authorization?.ToString())
                    throw new InvalidOperationException("登录范围已变化，暂不能确认本批操作结果，请重新登录后核对。");
                if (attempt != 0) ArchiveSession.Current?.SetStage("服务器请求已成功，继续" + operation + "…");
                return result;
            }
            catch (Exception error) when (!token.IsCancellationRequested
                && (error is HttpRequestException || error is ServiceUnavailableException || error is OperationCanceledException))
            {
                if (elapsed == null) { elapsed = Stopwatch.StartNew(); budget.CancelAfter(archiveRecoveryWindow); }
                if (budget.IsCancellationRequested || elapsed.Elapsed >= archiveRecoveryWindow)
                    throw new ArchiveConnectionTimeoutException(error);
                var remaining = archiveRecoveryWindow - elapsed.Elapsed;
                var delay = TimeSpan.FromSeconds(attempt == 0 ? 1 : attempt == 1 ? 3 : 10);
                if (delay > remaining) delay = remaining;
                attempt++;
                var reason = error is OperationCanceledException ? "服务器响应超时，正在重试核对"
                    : error is ServiceUnavailableException ? "PLM服务暂时不可用，正在等待恢复并核对"
                    : "PLM连接失败，正在等待恢复并核对";
                var message = reason + operation + "（第" + attempt + "次，剩余"
                    + Math.Max(0, (int)remaining.TotalSeconds) + "秒）…";
                ArchiveSession.Current?.SetStage(message);
                PluginDiagnosticLog.Record("存档连接恢复", message + "；异常类型=" + error.GetType().Name);
                try { await archiveRecoveryDelay(delay, budget.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                { throw new ArchiveConnectionTimeoutException(error); }
            }
        }
    }

    private bool ArchiveConnectionRecoveryActive => ArchiveSession.Current is ArchiveSession session
        && session.WorkflowStarted && !session.IsFinished;

    private async Task<UploadSessionDto> ReadUploadSessionOnceAsync(Guid id, CancellationToken token)
    {
        using var response = await httpClient.GetAsync("api/uploads/sessions/" + id, token).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            ThrowAuthenticationExpiredIfNeeded(response); ThrowServiceUnavailableIfNeeded(response);
            throw new InvalidOperationException(GetErrorMessage(response.StatusCode.ToString(), body));
        }
        return serializer.Deserialize<UploadSessionDto>(body)
            ?? throw new InvalidOperationException("服务器未返回有效的上传会话，已停止上传。");
    }

    private Task<bool> UploadArchiveChunkAsync(UploadSessionDto session, long offset, int index,
        byte[] buffer, int count, CancellationToken token)
        => RecoverArchiveConnectionAsync("第" + (index + 1) + "块上传", async (retryToken, retry) =>
        {
            if (retry)
            {
                var current = await ReadUploadSessionOnceAsync(session.Id, retryToken).ConfigureAwait(false);
                if (current == null || current.Id != session.Id || current.ProjectId != session.ProjectId
                    || current.TotalLength != session.TotalLength || current.ChunkSize != session.ChunkSize
                    || !string.Equals(current.ExpectedSha256, session.ExpectedSha256, StringComparison.OrdinalIgnoreCase)
                    || current.ExpiresAt <= DateTime.UtcNow
                    || current.ReceivedLength != offset && current.ReceivedLength != offset + count)
                    throw new InvalidOperationException("连接恢复后上传会话或文件位置不一致，已保留本地文件并停止上传。");
                if (current.ReceivedLength == offset + count) return true;
            }
            using var content = new ByteArrayContent(buffer, 0, count);
            using var response = await httpClient.PutAsync("api/uploads/sessions/" + session.Id + "/chunks/" + index,
                content, retryToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                ThrowAuthenticationExpiredIfNeeded(response); ThrowServiceUnavailableIfNeeded(response);
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                throw new InvalidOperationException("版本文件第" + (index + 1) + "块上传失败：" + GetErrorMessage(response.StatusCode.ToString(), body));
            }
            return true;
        }, token);
}
