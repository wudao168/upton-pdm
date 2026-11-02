using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Upton.Pdm.SolidWorks;

internal sealed partial class PdmApiClient
{
    internal sealed class ArchiveRequest
    {
        internal string Key, FileName, Path, Json;
        internal bool SafeRead, RetrySafe;
    }

    internal ArchiveRequest CreateArchiveRegistration(Guid projectId, Guid batchId, Guid sessionId,
        string machineName, CadTreeNode node, string hash, bool allowDuplicate, string reason) => new ArchiveRequest {
        Key = node.FullPath, FileName = node.FileName, RetrySafe = true,
        Path = "api/archive-batches/" + batchId + "/projects/" + projectId + "/registrations",
        Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(
            RegistrationPayload(node, hash, allowDuplicate, reason, batchId, sessionId, machineName)) };

    // Payloads and paths are captured on the owning thread. Workers only perform HTTP,
    // deserialize private results, and record durable reservation receipts; no CAD/UI calls.
    internal async Task<T[]> RunArchiveRequestsAsync<T>(IReadOnlyList<ArchiveRequest> requests,
        Action<ArchiveRequest, T> accepted, Action<int, int, string> progress, CancellationToken token,
        Action<ArchiveRequest, Exception> rejected = null)
    {
        var scope = EditJournalScope;
        var results = new T[requests.Count];
        var cursor = -1; var completed = 0;
        using (var stopped = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            Exception failure = null; var sync = new object();
            // Preserve write ordering and the existing one-in-flight recovery semantics.
            // Read-only queries/validation may run concurrently; reservations stay serial
            // on the worker, without one CAD-thread continuation for every file.
            var concurrency = requests.Any(request => request.Json != null && !request.SafeRead) ? 1 : 4;
            var workers = Enumerable.Range(0, Math.Min(concurrency, requests.Count)).Select(async worker =>
            {
                while (!stopped.IsCancellationRequested)
                {
                    var index = Interlocked.Increment(ref cursor);
                    if (index >= requests.Count) break;
                    try
                    {
                        var request = requests[index];
                        var value = await ReadArchiveResponseAsync<T>(request, stopped.Token).ConfigureAwait(false);
                        if (scope != EditJournalScope) throw new InvalidOperationException("登录范围已变化，已停止本批存档准备。");
                        // Serialize journal callbacks. A successfully reserved identity must
                        // remain recoverable even when another worker fails or cancellation arrives.
                        lock (sync) { accepted?.Invoke(request, value); results[index] = value; }
                        progress?.Invoke(Interlocked.Increment(ref completed), requests.Count, request.FileName);
                    }
                    catch (Exception error)
                    {
                        if (rejected != null && IsAuthenticated && scope == EditJournalScope && !(error is OperationCanceledException)
                            && !(error is HttpRequestException) && !(error is ServiceUnavailableException) && !(error is ArchiveConnectionTimeoutException))
                        {
                            lock (sync) rejected(requests[index], error);
                            progress?.Invoke(Interlocked.Increment(ref completed), requests.Count, requests[index].FileName);
                            continue;
                        }
                        lock (sync) if (failure == null) failure = error;
                        stopped.Cancel(); break;
                    }
                }
            }).ToArray();
            await Task.WhenAll(workers).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            return results;
        }
    }

    internal ArchiveRequest CreateArchivePreflight(Guid documentId, Guid projectId, string path,
        ArchiveReferenceSnapshot snapshot, IReadOnlyDictionary<string, string> properties,
        Guid sessionId, bool isProjectRoot, string drawingNumber, string name, Guid? writebackId, bool keepEditing, string handoffTo)
        => new ArchiveRequest { Key = path, FileName = snapshot.FileName, SafeRead = true, Path = "api/documents/" + documentId + "/checkin/preflight",
            Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(new {
                projectId, root = snapshot.CreateRoot(), properties = properties ?? new Dictionary<string, string>(),
                checkoutSessionId = sessionId, isProjectRoot, drawingNumber, name, fileName = snapshot.FileName,
                drawingReviewWritebackId = writebackId, expectedVersionId = snapshot.ExpectedVersionId,
                plannedDocumentIds = snapshot.PlannedDocumentIds, keepEditing, handoffTo }) };

    private async Task<T> ReadArchiveResponseAsync<T>(ArchiveRequest request, CancellationToken token)
    {
        if (request.Json == null || request.SafeRead || request.RetrySafe)
            return await RecoverArchiveConnectionAsync("存档信息：" + request.FileName,
                (retryToken, retry) => ReadArchiveResponseOnceAsync<T>(request, retryToken), token).ConfigureAwait(false);
        return await ReadArchiveResponseOnceAsync<T>(request, token).ConfigureAwait(false);
    }

    private async Task<T> ReadArchiveResponseOnceAsync<T>(ArchiveRequest request, CancellationToken token)
    {
                using (var content = request.Json == null ? null : new StringContent(request.Json, Encoding.UTF8, "application/json"))
                using (var response = content == null
                    ? await httpClient.GetAsync(request.Path, token).ConfigureAwait(false)
                    : await httpClient.PostAsync(request.Path, content, token).ConfigureAwait(false))
                {
                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        ThrowAuthenticationExpiredIfNeeded(response);
                        ThrowServiceUnavailableIfNeeded(response);
                        throw new InvalidOperationException(GetErrorMessage(response.StatusCode.ToString(), body));
                    }
                    return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<T>(body);
                }
    }

    internal async Task<Dictionary<Guid, IReadOnlyList<DocumentVersionDto>>> ReadArchiveHistoriesAsync(
        IReadOnlyList<Guid> ids, Action<int, int, string> progress, CancellationToken token)
    {
        var unique = ids.Distinct().ToArray();
        var requests = unique.Select(id => new ArchiveRequest { Key = id.ToString(),
            Path = "api/documents/" + id + "/versions" }).ToArray();
        var values = await RunArchiveRequestsAsync<List<DocumentVersionDto>>(requests, null, progress, token).ConfigureAwait(false);
        if (values.Any(value => value == null)) throw new InvalidOperationException("批量版本查询未返回完整结果，已停止存档准备。");
        return unique.Select((id, index) => new { id, value = (IReadOnlyList<DocumentVersionDto>)values[index] })
            .ToDictionary(item => item.id, item => item.value);
    }

    internal async Task RefreshArchiveHistoriesAsync(Dictionary<Guid, IReadOnlyList<DocumentVersionDto>> histories, CancellationToken token)
    {
        var ids = histories.Keys.ToArray();
        var changed = new List<Guid>();
        var session = ArchiveSession.Current;
        session?.SetStep(session.DisplayStep, "正在核对引用版本…", ids.Length);
        for (var offset = 0; offset < ids.Length; offset += 100)
        {
            var chunk = ids.Skip(offset).Take(100).ToArray();
            session?.ReportFile(offset, ids.Length, null, "正在核对引用版本，第" + (offset / 100 + 1) + "/" + ((ids.Length + 99) / 100) + "批…");
            var result = await VerifyReferenceVersionsAsync(chunk.Select(id => new DocumentVersionVerificationRequestDto { DocumentId = id }).ToArray(), token).ConfigureAwait(false);
            if (result == null) { changed.AddRange(chunk); session?.ReportFile(offset + chunk.Length, ids.Length, null, "版本摘要接口不可用，将读取完整版本核对…"); continue; }
            if (result.Count != chunk.Length || result.Select(value => value.DocumentId).Distinct().Count() != chunk.Length
                || result.Any(value => !chunk.Contains(value.DocumentId) || !value.Accessible))
                throw new InvalidOperationException("版本基准批量核对未返回完整的可访问结果，尚未上传。");
            changed.AddRange(result.Where(value => value.Latest?.Id != histories[value.DocumentId].FirstOrDefault()?.Id).Select(value => value.DocumentId));
            session?.ReportFile(offset + chunk.Length, ids.Length, null, "引用版本已核对 " + (offset + chunk.Length) + "/" + ids.Length + "份");
        }
        if (changed.Count == 0) return;
        var refreshed = await ReadArchiveHistoriesAsync(changed, null, token).ConfigureAwait(false);
        foreach (var pair in refreshed) histories[pair.Key] = pair.Value;
    }

    internal async Task<ArchiveReferenceSnapshot> PrepareArchiveSnapshotWithHistoryAsync(CadTreeNode root,
        Guid? expectedOverride, ArchiveReferenceSnapshot snapshot, IReadOnlyList<DocumentVersionDto> history, CancellationToken token)
    {
        var baseline = expectedOverride ?? await BaseVersionFromHistoryAsync(root, root.DocumentId.Value, token, history).ConfigureAwait(false);
        return snapshot.WithBaseVersion(baseline);
    }
}
