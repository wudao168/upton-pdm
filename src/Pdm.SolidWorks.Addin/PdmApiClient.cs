using System;
using System.Linq;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Upton.Pdm.ClientShared;

namespace Upton.Pdm.SolidWorks;

internal sealed partial class PdmApiClient : IDisposable
{
    private readonly HttpClient httpClient;
    private readonly HttpClient controlledOpenHttpClient;
    private readonly JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
    private int authenticationExpiredRaised;
    private System.Threading.Timer loginHeartbeat;
    private int heartbeatRunning;

    public event EventHandler AuthenticationExpired;
    public Func<Guid, CancellationToken, Task> BeforeCheckout { get; set; }

    public PdmApiClient(string baseAddress)
    {
        httpClient = new HttpClient(new ClientDiagnosticHandler())
        {
            BaseAddress = new Uri(baseAddress.TrimEnd('/') + "/", UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(30)
        };
        controlledOpenHttpClient = new HttpClient(new ClientDiagnosticHandler())
        {
            BaseAddress = httpClient.BaseAddress,
            Timeout = TimeSpan.FromMinutes(15)
        };
    }

    public bool IsAuthenticated => httpClient.DefaultRequestHeaders.Authorization != null;
    public string EditJournalScope { get; private set; }

    public async Task<LoginResponseDto> LoginAsync(string username, string password, CancellationToken cancellationToken)
    {
        var response = await PostJsonAsync<LoginResponseDto>("api/auth/login", new { username, password, clientType = "solidworks" }, cancellationToken).ConfigureAwait(false);
        Interlocked.Exchange(ref authenticationExpiredRaised, 0);
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", response.AccessToken);
        controlledOpenHttpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", response.AccessToken);
        SetActiveCompany(response.ActiveCompanyId);
        loginHeartbeat?.Dispose();
        loginHeartbeat = new System.Threading.Timer(_ => { _ = HeartbeatAsync(); }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        return response;
    }

    private async Task HeartbeatAsync()
    {
        if (!IsAuthenticated || Interlocked.Exchange(ref heartbeatRunning, 1) != 0) return;
        try { await PostJsonAsync<bool>("api/auth/heartbeat", new { }, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception error) { ClientDiagnostics.Record("runtime", "登录心跳", error.Message); }
        finally { Interlocked.Exchange(ref heartbeatRunning, 0); }
    }

    public void SetActiveCompany(Guid companyId)
    {
        documentRulesScope = null;
        nextDocumentRulesRefreshUtc = DateTime.MinValue;
        DocumentRuleGate.Configure(Array.Empty<string>());
        EditJournalScope = httpClient.BaseAddress.AbsoluteUri.ToLowerInvariant() + "|" + companyId.ToString("N");
        httpClient.DefaultRequestHeaders.Remove("X-Company-Id");
        controlledOpenHttpClient.DefaultRequestHeaders.Remove("X-Company-Id");
        if (companyId != Guid.Empty) httpClient.DefaultRequestHeaders.Add("X-Company-Id", companyId.ToString());
        if (companyId != Guid.Empty) controlledOpenHttpClient.DefaultRequestHeaders.Add("X-Company-Id", companyId.ToString());
        if (httpClient.DefaultRequestHeaders.Authorization != null)
            ClientDiagnostics.Bind(httpClient.BaseAddress.AbsoluteUri, httpClient.DefaultRequestHeaders.Authorization.Parameter, companyId.ToString());
    }

    public Task<List<ProjectDto>> GetProjectsAsync(CancellationToken cancellationToken) =>
        GetJsonAsync<List<ProjectDto>>("api/projects", cancellationToken);

    public Task<UserThemeDto> GetUserThemeAsync(CancellationToken cancellationToken) =>
        GetJsonAsync<UserThemeDto>("api/auth/me", cancellationToken);

    public Task<ProjectPlanDto> GetProjectPlanAsync(Guid projectId, CancellationToken cancellationToken) =>
        GetJsonAsync<ProjectPlanDto>(string.Concat("api/projects/", projectId, "/plan"), cancellationToken);

    public Task<OrganizationDirectoryDto> GetOrganizationDirectoryAsync(CancellationToken cancellationToken) =>
        GetJsonAsync<OrganizationDirectoryDto>("api/organization-directory", cancellationToken);

    public async Task<List<DocumentDto>> GetDocumentsAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await RefreshDocumentRulesAsync(cancellationToken);
        return await GetJsonAsync<List<DocumentDto>>(string.Concat("api/projects/", projectId, "/documents"), cancellationToken);
    }

    public Task<List<BomPropertyMappingDto>> GetBomPropertyMappingsAsync(CancellationToken cancellationToken) =>
        GetJsonAsync<List<BomPropertyMappingDto>>("api/bom-property-mappings", cancellationToken);

    public Task<List<BomItemDto>> GetBomAsync(Guid projectId, string kind, CancellationToken cancellationToken) =>
        GetJsonAsync<List<BomItemDto>>(
            string.Concat("api/projects/", projectId, "/boms/", Uri.EscapeDataString(kind ?? string.Empty)),
            cancellationToken);

    public Task<List<DocumentModelDrawingRelationDto>> GetDocumentRelationsAsync(Guid projectId, CancellationToken cancellationToken) =>
        GetJsonAsync<List<DocumentModelDrawingRelationDto>>(string.Concat("api/projects/", projectId, "/document-relations"), cancellationToken);

    public Task<DocumentReferenceNodeDto> GetReferenceTreeAsync(Guid projectId, CancellationToken cancellationToken) =>
        GetJsonAsync<DocumentReferenceNodeDto>(string.Concat("api/projects/", projectId, "/reference-tree"), cancellationToken);

    public async Task<DocumentReferenceNodeDto> GetReferenceTreeOrNullAsync(Guid projectId, CancellationToken cancellationToken)
    {
        using (var response = await httpClient.GetAsync(
            string.Concat("api/projects/", projectId, "/reference-tree"),
            cancellationToken).ConfigureAwait(false))
        {
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound
                || response.StatusCode == System.Net.HttpStatusCode.NoContent)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                ThrowAuthenticationExpiredIfNeeded(response);
                ThrowServiceUnavailableIfNeeded(response);
                throw new InvalidOperationException(GetErrorMessage(response.StatusCode.ToString(), body));
            }

            return serializer.Deserialize<DocumentReferenceNodeDto>(body);
        }
    }

    public async Task<ControlledOpenManifestDto> CreateControlledOpenManifestAsync(
        Guid documentId,
        Guid? versionId,
        bool releasedOnly,
        bool forEdit,
        CancellationToken cancellationToken,
        bool resolveLegacyReferences = false)
    {
        try
        {
            return await PostJsonAsync<ControlledOpenManifestDto>(
                controlledOpenHttpClient,
                string.Concat("api/documents/", documentId, "/open-manifest"),
                new { versionId, releasedOnly, forEdit, resolveLegacyReferences, treeScopeOnly = true },
                cancellationToken).ConfigureAwait(false);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("准备受控图档超时。大型装配需要逐项校验受控文件，请确认PLM服务和文件库可访问后重试。", exception);
        }
    }

    public Task<List<DocumentRegistrationMatchDto>> PreflightDocumentRegistrationAsync(
        Guid projectId,
        IReadOnlyList<DocumentRegistrationCandidateDto> candidates,
        CancellationToken cancellationToken) =>
        PostJsonAsync<List<DocumentRegistrationMatchDto>>(
            string.Concat("api/projects/", projectId, "/documents/registration-preflight"),
            new { candidates },
            cancellationToken);

    public Task<DocumentDto> RegisterDocumentAsync(
        Guid projectId,
        CadTreeNode node,
        string sourceSha256,
        bool allowDuplicateContent,
        string duplicateReason,
        CancellationToken cancellationToken,
        Guid? archiveBatchId = null, Guid? sessionId = null, string machineName = null) =>
        PostJsonAsync<DocumentDto>(
            archiveBatchId.HasValue ? "api/archive-batches/" + archiveBatchId + "/projects/" + projectId + "/registrations"
                : string.Concat("api/projects/", projectId, "/documents/register"),
            RegistrationPayload(node, sourceSha256, allowDuplicateContent, duplicateReason, archiveBatchId, sessionId, machineName), cancellationToken);

    private static object RegistrationPayload(CadTreeNode node, string sourceSha256, bool allowDuplicateContent, string duplicateReason,
        Guid? archiveBatchId, Guid? sessionId, string machineName)
    {
        var document = new
            {
                drawingNumber = Path.GetFileNameWithoutExtension(node.FileName),
                name = string.IsNullOrWhiteSpace(node.DisplayName) ? Path.GetFileNameWithoutExtension(node.FileName) : node.DisplayName,
                fileName = node.FileName,
                kind = (int)node.Kind,
                relatedModelDocumentId = node.RelatedModelDocumentId,
                sourceSha256,
                allowDuplicateContent,
                duplicateReason
            };
        return archiveBatchId.HasValue ? (object)new { document, sessionId, machineName } : document;
    }

    public Task<object> CancelArchiveRegistrationAsync(Guid batchId, Guid sessionId, CancellationToken token) =>
        PostJsonAsync<object>("api/archive-batches/" + batchId + "/cancel-registration", new { sessionId }, token);

    public Task<List<DocumentVersionDto>> GetVersionsAsync(Guid documentId, CancellationToken cancellationToken, int? take = null) =>
        GetJsonAsync<List<DocumentVersionDto>>(string.Concat("api/documents/", documentId, "/versions", take.HasValue ? "?take=" + take.Value : string.Empty), cancellationToken);

    public async Task<DocumentDto> CheckoutAsync(Guid documentId, Guid sessionId, string machineName, CancellationToken cancellationToken, Guid? drawingReviewWritebackId = null)
    {
        if (BeforeCheckout != null) await BeforeCheckout(documentId, cancellationToken);
        return await PostJsonAsync<DocumentDto>(string.Concat("api/documents/", documentId, "/checkout"), new { sessionId, machineName, drawingReviewWritebackId }, cancellationToken).ConfigureAwait(false);
    }

    public Task<DocumentDto> CompleteEditWithoutChangesAsync(Guid documentId, Guid checkoutSessionId, string sha256, CancellationToken cancellationToken) =>
        PostJsonAsync<DocumentDto>(string.Concat("api/documents/", documentId, "/complete-edit"), new { sha256, checkoutSessionId }, cancellationToken);

    public Task<DocumentDto> AutoReleaseEditAsync(LocalEditDraft entry, Guid baseVersionId, string sha256, CancellationToken cancellationToken) =>
        PostJsonAsync<DocumentDto>(string.Concat("api/documents/", entry.DocumentId, "/auto-release"), new { sessionId = entry.SessionId, checkedOutAt = entry.CheckedOutAt.ToUniversalTime().ToString("O"), baseVersionId, sha256 }, cancellationToken);

    public Task<DocumentDto> DiscardCheckoutAsync(Guid documentId, Guid checkoutSessionId, CancellationToken cancellationToken) =>
        PostJsonAsync<DocumentDto>(string.Concat("api/documents/", documentId, "/discard-checkout"), new { checkoutSessionId }, cancellationToken);

    public Task<DocumentDto> CloseEditAsync(LocalEditDraft entry, bool draftPreserved, CancellationToken cancellationToken)
    {
        var sha256 = entry.DiskSha256;
        if (!draftPreserved && PdmDocumentIdentityStore.TryReadLocalMetadata(entry.SourcePath, out var baseline)
            && baseline.DocumentId == entry.DocumentId && baseline.VersionId == entry.BaseVersionId
            && !string.IsNullOrWhiteSpace(baseline.ResolvedReferenceSha256)
            && string.Equals(sha256, baseline.ResolvedReferenceSha256, StringComparison.OrdinalIgnoreCase))
            sha256 = baseline.Sha256;
        return PostJsonAsync<DocumentDto>(string.Concat("api/documents/", entry.DocumentId, "/close-edit"),
            new { sessionId = entry.SessionId, checkedOutAt = entry.CheckedOutAt.ToUniversalTime().ToString("O"), sha256, draftPreserved }, cancellationToken);
    }

    public Task<List<DocumentWhereUsedDto>> GetWhereUsedAsync(Guid documentId, CancellationToken cancellationToken) =>
        GetJsonAsync<List<DocumentWhereUsedDto>>(string.Concat("api/documents/", documentId, "/where-used"), cancellationToken);

    public Task<DocumentDto> RequestCheckoutReleaseAsync(Guid documentId, string reason, CancellationToken cancellationToken) =>
        PostJsonAsync<DocumentDto>(string.Concat("api/documents/", documentId, "/request-release"), new { reason }, cancellationToken);

    public Task<DocumentDto> ObsoleteAsync(Guid documentId, string comment, CancellationToken cancellationToken) =>
        PostJsonAsync<DocumentDto>(string.Concat("api/documents/", documentId, "/obsolete"), new { comment }, cancellationToken);

    public Task<List<ReleasePackageDto>> GetReleasePackagesAsync(Guid projectId, CancellationToken cancellationToken) =>
        GetJsonAsync<List<ReleasePackageDto>>(string.Concat("api/projects/", projectId, "/release-packages"), cancellationToken);

    public Task<List<CadPropertyWritebackDto>> GetCadPropertyWritebacksAsync(Guid projectId, CancellationToken cancellationToken) =>
        GetJsonAsync<List<CadPropertyWritebackDto>>(string.Concat("api/projects/", projectId, "/cad-property-writebacks?activeOnly=true"), cancellationToken);

    public Task<CadPropertyWritebackDto> StartCadPropertyWritebackAsync(Guid id, CancellationToken cancellationToken) =>
        PostJsonAsync<CadPropertyWritebackDto>(string.Concat("api/cad-property-writebacks/", id, "/start"), new { }, cancellationToken);

    public Task<CadPropertyWritebackDto> CompleteCadPropertyWritebackAsync(Guid id, Guid resultVersionId, CancellationToken cancellationToken) =>
        PostJsonAsync<CadPropertyWritebackDto>(string.Concat("api/cad-property-writebacks/", id, "/complete"), new { resultVersionId }, cancellationToken);

    public Task<CadPropertyWritebackDto> FailCadPropertyWritebackAsync(Guid id, string error, bool conflict, CancellationToken cancellationToken) =>
        PostJsonAsync<CadPropertyWritebackDto>(string.Concat("api/cad-property-writebacks/", id, "/fail"), new { error, conflict }, cancellationToken);

    public Task<ReleasePackageDto> WithdrawReleasePackageAsync(Guid releasePackageId, string comment, CancellationToken cancellationToken) =>
        PostJsonAsync<ReleasePackageDto>(string.Concat("api/release-packages/", releasePackageId, "/withdraw"), new { comment }, cancellationToken);

    public Task<EditSessionHeartbeatDto> HeartbeatEditSessionAsync(Guid sessionId, string machineName, IReadOnlyList<Guid> documentIds, CancellationToken cancellationToken) =>
        PostJsonAsync<EditSessionHeartbeatDto>(string.Concat("api/edit-sessions/", sessionId, "/heartbeat"), new { machineName, documentIds }, cancellationToken);

    public async Task<CheckInResultDto> CheckInAsync(
        Guid documentId,
        Guid projectId,
        CadTreeNode root,
        string comment,
        StoredVersionFile storedFile,
        IReadOnlyDictionary<string, string> modelProperties,
        Guid checkoutSessionId,
        bool isProjectRoot,
        bool forceVersion,
        string drawingNumber,
        string name,
        CancellationToken cancellationToken,
        Guid? drawingReviewWritebackId = null,
        Guid? expectedVersionOverride = null,
        bool deferBomRefresh = false)
    {
        var expectedVersionId = expectedVersionOverride ?? await BaseVersionAsync(root, documentId, cancellationToken).ConfigureAwait(false);
        var payload = new
        {
            projectId,
            root = ToRequestNode(root, true),
            comment,
            storageRelativePath = storedFile.RelativePath,
            fileLength = storedFile.Length,
            sha256 = storedFile.Sha256,
            properties = MergeProperties(storedFile.Properties, modelProperties),
            checkoutSessionId,
            isProjectRoot,
            forceVersion,
            drawingNumber,
            name,
            fileName = root.FileName,
            drawingReviewWritebackId,
            expectedVersionId,
            deferBomRefresh
        };
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await PostJsonAsync<CheckInResultDto>(
                    string.Concat("api/documents/", documentId, "/checkin"), payload, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (attempt < 2
                && (exception is HttpRequestException
                    || exception is TaskCanceledException && !cancellationToken.IsCancellationRequested))
            {
                await Task.Delay(TimeSpan.FromSeconds(2 * (attempt + 1)), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task PreflightCheckInAsync(
        Guid documentId,
        Guid projectId,
        CadTreeNode root,
        IReadOnlyDictionary<string, string> modelProperties,
        Guid checkoutSessionId,
        bool isProjectRoot,
        string drawingNumber,
        string name,
        CancellationToken cancellationToken,
        Guid? drawingReviewWritebackId = null,
        ICollection<string> plannedPaths = null,
        Guid? expectedVersionOverride = null,
        ArchiveReferenceSnapshot referenceSnapshot = null, bool keepEditing = false, string handoffTo = null)
    {
        var snapshot = referenceSnapshot ?? CaptureArchiveSnapshot(root, plannedPaths);
        var expectedVersionId = referenceSnapshot != null ? snapshot.ExpectedVersionId
            : expectedVersionOverride ?? await BaseVersionAsync(root, documentId, cancellationToken).ConfigureAwait(false);
        _ = await PostJsonAsync<object>(
            string.Concat("api/documents/", documentId, "/checkin/preflight"),
            new
            {
                projectId,
                root = snapshot.CreateRoot(),
                properties = modelProperties ?? new Dictionary<string, string>(),
                checkoutSessionId,
                isProjectRoot,
                drawingNumber,
                name,
                fileName = snapshot.FileName,
                drawingReviewWritebackId,
                expectedVersionId,
                plannedDocumentIds = snapshot.PlannedDocumentIds, keepEditing, handoffTo
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<object> PrepareArchiveBatchFileAsync(Guid documentId, Guid projectId, CadTreeNode root, string comment,
        StoredVersionFile storedFile, IReadOnlyDictionary<string, string> properties, Guid sessionId, bool isRoot,
        string drawingNumber, string name, Guid? writebackId, Guid? expectedVersionOverride, ICollection<string> plannedPaths, CancellationToken token,
        ArchiveReferenceSnapshot referenceSnapshot = null, bool keepEditing = false, string handoffTo = null)
    {
        var snapshot = referenceSnapshot ?? CaptureArchiveSnapshot(root, plannedPaths);
        var expectedVersionId = referenceSnapshot != null ? snapshot.ExpectedVersionId
            : expectedVersionOverride ?? await BaseVersionAsync(root, documentId, token).ConfigureAwait(false);
        return new { documentId, request = new {
            projectId, root = snapshot.CreateRoot(), comment,
            storageRelativePath = storedFile.RelativePath, fileLength = storedFile.Length, sha256 = storedFile.Sha256,
            properties = MergeProperties(storedFile.Properties, properties), checkoutSessionId = sessionId,
            isProjectRoot = isRoot, drawingNumber, name, fileName = snapshot.FileName, drawingReviewWritebackId = writebackId, expectedVersionId, keepEditing, handoffTo
        } };
    }

    public async Task<ArchiveBatchReceiptDto> CommitArchiveBatchAsync(Guid batchId, Guid sessionId, IReadOnlyList<object> files, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(15));
        var commitToken = deadline.Token;
        var payload = new { batchId, checkoutSessionId = sessionId, files };
        var expectedIds = files.Select(file => Guid.Parse(Convert.ToString(file.GetType().GetProperty("documentId").GetValue(file)))).ToArray();
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try { return RequireCompleteArchiveReceipt(await PostJsonAsync<ArchiveBatchReceiptDto>(controlledOpenHttpClient, "api/archive-batches", payload, commitToken).ConfigureAwait(false), batchId, expectedIds); }
                catch (Exception exception) when (attempt < 2 && !commitToken.IsCancellationRequested
                    && (exception is HttpRequestException || exception is TaskCanceledException))
                { await Task.Delay(TimeSpan.FromSeconds(attempt + 1), commitToken).ConfigureAwait(false); }
            }
        }
        catch (Exception original) when (original is HttpRequestException || original is TaskCanceledException || original is InvalidOperationException)
        {
            using (var verify = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
            {
                try
                {
                    var receipt = await GetArchiveBatchReceiptOrNullAsync(batchId, verify.Token).ConfigureAwait(false);
                    if (receipt != null) return RequireCompleteArchiveReceipt(receipt, batchId, expectedIds);
                    if (original is InvalidOperationException && !original.Data.Contains("ArchiveReceiptIncomplete")) original.Data["ArchiveBatchNotCommitted"] = true;
                }
                catch (Exception verification) { PluginDiagnosticLog.Record("批次", "暂未取得整批存档回执：" + batchId + "，" + verification.GetType().Name); }
            }
            if (token.IsCancellationRequested) throw new OperationCanceledException(token);
            if (original is InvalidOperationException) throw;
            throw new InvalidOperationException("整批提交结果暂未确认。本地文件已保留；连接恢复后插件会核对原批次，在结果明确前请勿重新提交。服务器按整批提交，不会产生部分存档。", original);
        }
    }

    internal static ArchiveBatchReceiptDto RequireCompleteArchiveReceipt(ArchiveBatchReceiptDto receipt, Guid batchId, Guid[] expectedIds)
    {
        var ids = receipt?.Results?.Select(result => result.Document?.Id ?? Guid.Empty).ToArray();
        if (receipt?.BatchId != batchId || ids == null || ids.Length != expectedIds.Length
            || ids.Distinct().Count() != ids.Length || !new HashSet<Guid>(ids).SetEquals(expectedIds))
        {
            var error = new InvalidOperationException("服务器回执与完整提交清单不一致，暂不能确认整套存档成功。请保留本地文件并按批次核对结果：" + batchId);
            error.Data["ArchiveReceiptIncomplete"] = true;
            throw error;
        }
        return receipt;
    }

    internal async Task<ArchiveBatchReceiptDto> GetArchiveBatchReceiptOrNullAsync(Guid batchId, CancellationToken token)
    {
        using (var response = await httpClient.GetAsync("api/archive-batches/" + batchId, token).ConfigureAwait(false))
        {
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            { ThrowAuthenticationExpiredIfNeeded(response); throw new InvalidOperationException(GetErrorMessage(response.StatusCode.ToString(), body)); }
            return serializer.Deserialize<ArchiveBatchReceiptDto>(body);
        }
    }

    private async Task<Guid?> BaseVersionAsync(CadTreeNode node, Guid documentId, CancellationToken cancellationToken)
    {
        Guid? recordedBase = node.OpenedVersionId;
        if (PdmDocumentIdentityStore.TryReadLocalMetadata(node.FullPath, out var metadata)
            && metadata.DocumentId == documentId && metadata.VersionId.HasValue) recordedBase = metadata.VersionId;
        var versions = await GetVersionsAsync(documentId, cancellationToken, 1).ConfigureAwait(false);
        if (recordedBase.HasValue)
        {
            var current = versions.FirstOrDefault();
            if (current != null && current.Id != recordedBase.Value && current.Status == 1)
            {
                var history = await GetVersionsAsync(documentId, cancellationToken).ConfigureAwait(false);
                var source = history.FirstOrDefault(version => version.Id == recordedBase.Value);
                if (ReleasedArchiveBaseMatches(source, current)) return current.Id;
            }
            return recordedBase;
        }
        if (versions.Count == 0 || !File.Exists(node.FullPath)) return null;
        var latest = versions[0];
        var hash = ComputeFileSha256(node.FullPath);
        string sourceHash = null;
        latest.PropertySnapshot?.TryGetValue("SourceFileSha256", out sourceHash);
        if (!string.Equals(hash, latest.Sha256, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(hash, sourceHash, StringComparison.OrdinalIgnoreCase)) return null;
        if (!PdmAddin.ReferenceSnapshotMatchesTree(latest.ReferenceSnapshot, node)) return null;
        PdmDocumentIdentityStore.TryWriteControlledVersion(node.FullPath, documentId, null, latest.Id,
            latest.Revision?.Display, hash, new FileInfo(node.FullPath).Length);
        return latest.Id;
    }

    private static Guid[] PlannedDocumentIds(CadTreeNode root, ICollection<string> paths)
    {
        var ids = new HashSet<Guid>();
        var pending = new Stack<CadTreeNode>(); var visited = new HashSet<CadTreeNode>(); pending.Push(root);
        while (pending.Count > 0)
        {
            var node = pending.Pop(); if (node == null || !visited.Add(node)) continue;
            if (node.DocumentId.HasValue && paths.Contains(node.FullPath)) ids.Add(node.DocumentId.Value);
            foreach (var child in node.Children.Concat(node.ReferencedModels)) pending.Push(child);
        }
        return ids.ToArray();
    }

    internal static ArchiveReferenceSnapshot CaptureArchiveSnapshot(CadTreeNode root, ICollection<string> plannedPaths)
    {
        // Capture on the SolidWorks thread, before an await can refresh the held tree.
        var plannedIds = new HashSet<Guid>(plannedPaths == null ? Array.Empty<Guid>() : PlannedDocumentIds(root, plannedPaths));
        var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        var serialized = json.Serialize(ToRequestNode(root, true, plannedPaths));
        var capturedRoot = (Dictionary<string, object>)json.DeserializeObject(serialized);
        if (root.NativeFileScopeVerified && root.NativeFileScopePaths?.Contains(root.FullPath) == true)
        {
            // File inventory and structure indexing have different completion states.
            // Keep exact, registered inventory files even when an occurrence cannot be indexed.
            var inventory = new List<object>();
            var pendingNodes = new Stack<CadTreeNode>(); pendingNodes.Push(root);
            var visitedNodes = new HashSet<CadTreeNode>();
            var inventoryIds = new HashSet<Guid>();
            while (pendingNodes.Count > 0)
            {
                var node = pendingNodes.Pop();
                if (!visitedNodes.Add(node)) continue;
                foreach (var child in node.Children.Concat(node.ReferencedModels)) pendingNodes.Push(child);
                if (node == root || node.Kind == CadDocumentKind.Drawing || node.Status == CadReferenceStatus.Virtual
                    || !node.DocumentId.HasValue || node.DocumentId == root.DocumentId
                    || !root.NativeFileScopePaths.Contains(node.FullPath) || !inventoryIds.Add(node.DocumentId.Value)) continue;
                // Drawing model associations already identify these files. A second
                // inventory child would duplicate the same model relationship.
                if (root.Kind == CadDocumentKind.Drawing && root.ReferencedModels.Any(model => model.DocumentId == node.DocumentId)) continue;
                var entry = (Dictionary<string, object>)json.DeserializeObject(json.Serialize(ToRequestNode(node, false, plannedPaths)));
                entry["children"] = Array.Empty<object>();
                entry["role"] = (int)CadReferenceRole.ExternalFileDependency;
                entry["instancePath"] = root.InstancePath + "/native-file/" + node.DocumentId.Value.ToString("N");
                inventory.Add(entry);
            }
            bool incomplete = false;
            var inventoryNames = new HashSet<string>(inventory.Cast<Dictionary<string, object>>()
                .Select(entry => Convert.ToString(entry["fileName"])), StringComparer.OrdinalIgnoreCase);
            inventoryNames.Add(root.FileName);
            var availableNativeNames = new HashSet<string>(root.NativeFileScopePaths.Where(File.Exists)
                .Select(Path.GetFileName), StringComparer.OrdinalIgnoreCase);
            void PruneIndex(Dictionary<string, object> node)
            {
                var children = new List<object>();
                foreach (var child in ((object[])node["children"]).Cast<Dictionary<string, object>>())
                {
                    if (Convert.ToInt32(child["status"]) != (int)CadReferenceStatus.Virtual && child["documentId"] == null)
                    {
                        var name = Convert.ToString(child["fileName"]);
                        if (availableNativeNames.Contains(name) && !inventoryNames.Contains(name))
                        {
                            // This is an actual unregistered file, not merely a failed
                            // occurrence index. Keep it for the normal preflight failure.
                            children.Add(child);
                            continue;
                        }
                        incomplete = true;
                        PluginDiagnosticLog.Record("结构待补全", "索引身份尚未确定，按已核对的原生文件清单保存：" + child["fileName"]);
                        continue;
                    }
                    PruneIndex(child); children.Add(child);
                }
                node["children"] = children.ToArray();
            }
            PruneIndex(capturedRoot);
            if (incomplete)
            {
                root.StructureIndexPending = true;
                capturedRoot["structureIndexPending"] = true;
            }
            // Persist the complete, canonical native-file inventory on every capture.
            // It must survive both legacy empty indexes and subsequent unchanged saves.
            capturedRoot["children"] = ((object[])capturedRoot["children"]).Concat(inventory).ToArray();
            serialized = json.Serialize(capturedRoot);
        }
        var requiredIds = new HashSet<Guid>();
        Action<Dictionary<string, object>, bool> validate = null;
        validate = (node, isRoot) =>
        {
            var hasId = node["documentId"] != null && Guid.TryParse(node["documentId"].ToString(), out var id) && id != Guid.Empty;
            if ((isRoot || Convert.ToInt32(node["status"]) != (int)CadReferenceStatus.Virtual)
                && (!hasId || isRoot && node["revision"] == null))
            {
                var detail = "文件=" + node["fileName"] + "；实例=" + node["instancePath"]
                    + "；配置=" + node["configuration"] + "；图档编号=" + node["documentId"]
                    + "；版本=" + json.Serialize(node["revision"]);
                PluginDiagnosticLog.Record("引用", "提交快照不完整：" + detail);
                throw new InvalidOperationException("引用文件“" + node["fileName"] + "”的"
                    + (!hasId ? "存档身份" : "引用版本") + "未准备完成，本次尚未上传文件。请刷新设计树后重新整体存档。实例："
                    + node["instancePath"] + "；配置：" + node["configuration"]);
            }
            if (hasId) requiredIds.Add(Guid.Parse(node["documentId"].ToString()));
            foreach (var child in (object[])node["children"]) validate((Dictionary<string, object>)child, false);
        };
        validate((Dictionary<string, object>)json.DeserializeObject(serialized), true);
        return new ArchiveReferenceSnapshot(serialized, root.FileName, plannedIds.Where(requiredIds.Contains).ToArray());
    }

    internal async Task<ArchiveReferenceSnapshot> PrepareArchiveSnapshotAsync(CadTreeNode root, ICollection<string> plannedPaths,
        Guid? expectedVersionOverride, CancellationToken token, ArchiveReferenceSnapshot captured = null)
    {
        var snapshot = captured ?? CaptureArchiveSnapshot(root, plannedPaths);
        var expectedVersionId = expectedVersionOverride ?? await BaseVersionAsync(root, root.DocumentId.Value, token).ConfigureAwait(false);
        return snapshot.WithBaseVersion(expectedVersionId);
    }

    internal sealed class ArchiveReferenceSnapshot
    {
        private readonly string serializedRoot;
        public ArchiveReferenceSnapshot(string serializedRoot, string fileName, Guid[] plannedDocumentIds, Guid? expectedVersionId = null)
        { this.serializedRoot = serializedRoot; FileName = fileName; PlannedDocumentIds = Array.AsReadOnly(plannedDocumentIds); ExpectedVersionId = expectedVersionId; }
        public string FileName { get; }
        public IReadOnlyCollection<Guid> PlannedDocumentIds { get; }
        public Guid? ExpectedVersionId { get; }
        public bool HasPendingStructureIndex => HasPendingIndex((Dictionary<string, object>)CreateRoot());
        private static bool HasPendingIndex(Dictionary<string, object> node) =>
            node.TryGetValue("structureIndexPending", out var pending) && Convert.ToBoolean(pending)
            || node.TryGetValue("children", out var children) && ((object[])children)
                .Cast<Dictionary<string, object>>().Any(HasPendingIndex);
        public ArchiveReferenceSnapshot WithBaseVersion(Guid? id) => new ArchiveReferenceSnapshot(serializedRoot, FileName, PlannedDocumentIds.ToArray(), id);
        public ArchiveReferenceSnapshot WithNativeIsolationVerified()
        {
            var root = (Dictionary<string, object>)CreateRoot();
            root["nativeIsolationVerified"] = true;
            root["usesNativeFileInventory"] = true;
            return new ArchiveReferenceSnapshot(new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(root), FileName, PlannedDocumentIds.ToArray(), ExpectedVersionId);
        }
        public ArchiveReferenceSnapshot WithNativeIsolation(NativeFileIsolationDto copy)
        {
            var root = (Dictionary<string, object>)CreateRoot();
            root["nativeIsolation"] = new { sourceVersionId = copy.SourceVersionId, sourceSha256 = copy.SourceSha256,
                relativePath = copy.RelativePath, length = copy.Length, sha256 = copy.Sha256,
                geometryEvidenceSha256 = copy.GeometryEvidenceSha256, policyVersion = 1 };
            return new ArchiveReferenceSnapshot(new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(root), FileName, PlannedDocumentIds.ToArray(), ExpectedVersionId);
        }
        public object CreateRoot() => new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(serializedRoot);
    }

    internal static bool ReleasedArchiveBaseMatches(DocumentVersionDto source, DocumentVersionDto current)
    {
        if (source == null || current == null || source.Revision == null
            || current.Revision == null
            || source.Status != 0 || current.Status != 1 || current.CreatedAt < source.CreatedAt
            || string.IsNullOrWhiteSpace(source.Sha256)
            || !string.Equals(source.Sha256, current.Sha256, StringComparison.OrdinalIgnoreCase)
            || source.PropertySnapshot == null || current.PropertySnapshot == null
            || source.PropertySnapshot.Count != current.PropertySnapshot.Count
            || !source.PropertySnapshot.All(pair => current.PropertySnapshot.TryGetValue(pair.Key, out var value) && value == pair.Value)
            || source.ReferenceSnapshot == null || current.ReferenceSnapshot == null) return false;
        // Release-only promotion may change the version id while retaining the exact
        // source content and reference snapshot. Never compare against the modified local file.
        return SameReleasedReferences(source.ReferenceSnapshot, current.ReferenceSnapshot);
    }

    private static bool SameReleasedReferences(DocumentReferenceNodeDto left, DocumentReferenceNodeDto right)
    {
        if (left == null || right == null) return left == right;
        if (left.DocumentId != right.DocumentId || left.InstancePath != right.InstancePath
            || left.FileName != right.FileName || left.DisplayName != right.DisplayName
            || left.Kind != right.Kind || left.Configuration != right.Configuration
            || left.Quantity != right.Quantity || left.Status != right.Status
            || left.Revision?.Display != right.Revision?.Display) return false;
        var before = left.Children ?? new List<DocumentReferenceNodeDto>();
        var after = right.Children ?? new List<DocumentReferenceNodeDto>();
        return before.Count == after.Count && before.Zip(after, SameReleasedReferences).All(equal => equal);
    }

    private static Dictionary<string, string> MergeProperties(IReadOnlyDictionary<string, string> fileProperties, IReadOnlyDictionary<string, string> modelProperties)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (fileProperties != null)
            foreach (var property in fileProperties) result[property.Key] = property.Value;
        if (modelProperties != null)
            foreach (var property in modelProperties) result[property.Key] = property.Value;
        return result;
    }

    public async Task<StoredVersionFile> UploadVersionFileAsync(
        Guid projectId,
        string filePath,
        Guid documentId,
        string originalFilePath,
        CancellationToken cancellationToken,
        Action<long, long> reportProgress = null,
        string exactCopySourceSha256 = null,
        string expectedSourceSha256 = null,
        Guid? archiveBatchId = null,
        Guid? checkoutSessionId = null,
        string frozenUploadSha256 = null)
    {
        var file = new FileInfo(filePath);
        var originalFile = new FileInfo(originalFilePath);
        var hashes = await Task.Run(
            () => frozenUploadSha256 == null
                ? CheckInUploadHashes.Read(filePath, originalFilePath, exactCopySourceSha256, cancellationToken, expectedSourceSha256)
                : CheckInUploadHashes.ReadFrozen(filePath, frozenUploadSha256, expectedSourceSha256, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        var sha256 = hashes.Upload;
        var sourceFileSha256 = hashes.Source;
        var journalPath = ArchiveUploadJournal.PathFor(projectId, documentId, originalFilePath, sha256, file.Length);
        if (archiveBatchId.HasValue)
            journalPath = Path.ChangeExtension(journalPath, archiveBatchId.Value.ToString("N") + ".json");
        var transfer = ArchiveUploadJournal.Read(journalPath);
        UploadSessionDto session = null;
        if (transfer != null)
        {
            using (var response = await httpClient.GetAsync("api/uploads/sessions/" + transfer.SessionId, cancellationToken).ConfigureAwait(false))
            {
                if (response.IsSuccessStatusCode)
                {
                    session = serializer.Deserialize<UploadSessionDto>(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                    if (session.ProjectId != projectId || session.TotalLength != file.Length
                        || !string.Equals(session.ExpectedSha256, sha256, StringComparison.OrdinalIgnoreCase)
                        || session.ReceivedLength < transfer.AcknowledgedLength || session.ExpiresAt <= DateTime.UtcNow)
                        session = null;
                }
                else
                {
                    ThrowAuthenticationExpiredIfNeeded(response);
                    if ((int)response.StatusCode >= 500)
                        throw new InvalidOperationException("暂时无法核对上次上传结果，已保留续传记录，请稍后重试。");
                }
            }
        }
        if (session == null)
        {
            session = await PostJsonAsync<UploadSessionDto>("api/uploads/sessions", new { projectId, fileName = file.Name, totalLength = file.Length, sha256,
                batchId = archiveBatchId, documentId = archiveBatchId.HasValue ? (Guid?)documentId : null,
                checkoutSessionId = archiveBatchId.HasValue ? checkoutSessionId : null }, cancellationToken).ConfigureAwait(false);
            transfer = new ArchiveUploadState { SessionId = session.Id,
                RelativePath = string.IsNullOrWhiteSpace(session.TargetRelativePath)
                    ? Path.Combine(".versions", documentId.ToString("N"), Guid.NewGuid().ToString("N"), file.Name)
                    : session.TargetRelativePath };
            ArchiveUploadJournal.Save(journalPath, transfer);
        }
        if (session.ChunkSize <= 0 || session.ChunkSize > 16 * 1024 * 1024 || transfer.AcknowledgedLength > file.Length
            || transfer.AcknowledgedLength < 0
            || (transfer.AcknowledgedLength != file.Length && transfer.AcknowledgedLength % session.ChunkSize != 0))
            throw new InvalidOperationException("上传分块配置或续传记录无效，已停止上传。");
        using (var input = File.OpenRead(filePath))
        {
            var buffer = new byte[session.ChunkSize];
            input.Position = transfer.AcknowledgedLength;
            var chunkIndex = (int)(transfer.AcknowledgedLength / session.ChunkSize);
            long uploaded = transfer.AcknowledgedLength;
            reportProgress?.Invoke(uploaded, file.Length);
            int read;
            while ((read = await ReadUploadChunkAsync(input, buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                using (var content = new ByteArrayContent(buffer, 0, read))
                using (var response = await httpClient.PutAsync(string.Concat("api/uploads/sessions/", session.Id, "/chunks/", chunkIndex), content, cancellationToken).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        ThrowAuthenticationExpiredIfNeeded(response);
                        throw new InvalidOperationException(string.Concat("版本文件第", chunkIndex + 1, "块上传失败。"));
                    }
                }
                uploaded += read;
                transfer.AcknowledgedLength = uploaded;
                ArchiveUploadJournal.Save(journalPath, transfer);
                reportProgress?.Invoke(uploaded, file.Length);
                chunkIndex++;
            }
        }
        var relative = transfer.RelativePath;
        var stored = await PostJsonAsync<StoredFileDto>(string.Concat("api/uploads/sessions/", session.Id, "/complete"), new { relativeTargetPath = relative }, cancellationToken).ConfigureAwait(false);
        if (frozenUploadSha256 == null) CheckInUploadHashes.VerifySource(originalFilePath, sourceFileSha256, cancellationToken);
        else CheckInUploadHashes.ReadFrozen(filePath, frozenUploadSha256, sourceFileSha256, cancellationToken);
        return new StoredVersionFile(stored.RelativePath, stored.Length, stored.Sha256, new Dictionary<string, string> { ["FileName"] = originalFile.Name, ["Extension"] = originalFile.Extension, ["LastWriteTimeUtc"] = originalFile.LastWriteTimeUtc.ToString("O"), ["SourceFileSha256"] = sourceFileSha256 });
    }

    private static string ComputeFileSha256(string path)
    {
        using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var hash = SHA256.Create())
        {
            return BitConverter.ToString(hash.ComputeHash(input)).Replace("-", string.Empty);
        }
    }

    private static async Task<int> ReadUploadChunkAsync(Stream input, byte[] buffer, CancellationToken cancellationToken)
    {
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await input.ReadAsync(buffer, length, buffer.Length - length, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            length += read;
        }
        return length;
    }

    public async Task<string> DownloadVersionToTempAsync(Guid documentId, Guid versionId, string fileName, string revision, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Path.GetTempPath(), "UPTON-PDM", "history", documentId.ToString("N"), versionId.ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, BuildVersionCopyFileName(fileName, revision, versionId));
        if (File.Exists(path))
        {
            return path;
        }

        var partialPath = path + ".download";
        if (File.Exists(partialPath)) File.Delete(partialPath);
        using (var response = await httpClient.GetAsync(string.Concat("api/documents/", documentId, "/versions/", versionId, "/file?download=false"), cancellationToken).ConfigureAwait(false))
        {
            if (!response.IsSuccessStatusCode)
            {
                ThrowAuthenticationExpiredIfNeeded(response);
                throw new InvalidOperationException("历史版本文件读取失败。");
            }
            using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
            using (var output = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) await input.CopyToAsync(output).ConfigureAwait(false);
        }
        if (File.Exists(path))
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
        File.Move(partialPath, path);
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
        return path;
    }

    public async Task<string> DownloadVersionToWorkspaceStageAsync(
        Guid documentId,
        Guid versionId,
        string fileName,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Path.GetTempPath(), "UPTON-PDM", "workspace-stage", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Path.GetFileName(fileName));
        var partialPath = path + ".download";
        try
        {
            string contentSha256 = expectedSha256;
            long? sourceLength = null, contentLength = null;
            Guid? nativeSnapshotId = null;
            using (var response = await httpClient.GetAsync(
                string.Concat("api/documents/", documentId, "/versions/", versionId, "/file?download=false&preferNativeIsolation=true"),
                cancellationToken).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                {
                    ThrowAuthenticationExpiredIfNeeded(response);
                    throw new InvalidOperationException("PLM最新版本文件读取失败。");
                }

                string Header(string name) => response.Headers.TryGetValues(name, out var values) ? values.SingleOrDefault() : null;
                if (Header("X-UPLM-Native-Snapshot-Id") is string snapshot)
                {
                    if (!Guid.TryParse(snapshot, out var id) || id == Guid.Empty
                        || !string.Equals(Header("X-UPLM-Native-Source-Sha256"), expectedSha256, StringComparison.OrdinalIgnoreCase)
                        || !long.TryParse(Header("X-UPLM-Native-Source-Length"), out var originalLength) || originalLength <= 0
                        || !long.TryParse(Header("X-UPLM-Native-Content-Length"), out var independentLength) || independentLength <= 0
                        || Header("X-UPLM-Native-Content-Sha256") is not string independentSha
                        || independentSha.Length != 64 || !independentSha.All(Uri.IsHexDigit))
                        throw new InvalidDataException("PLM独立副本的来源校验记录不完整，未更新本地文件。");
                    nativeSnapshotId = id; sourceLength = originalLength; contentLength = independentLength; contentSha256 = independentSha;
                }

                using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var output = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await input.CopyToAsync(output).ConfigureAwait(false);
                }
            }

            var actualSha256 = ComputeFileSha256(partialPath);
            if (!string.Equals(actualSha256, contentSha256, StringComparison.OrdinalIgnoreCase)
                || contentLength.HasValue && new FileInfo(partialPath).Length != contentLength.Value)
            {
                throw new InvalidDataException("PLM最新版本文件校验失败，未更新本地工作文件。");
            }

            File.Move(partialPath, path);
            if (nativeSnapshotId.HasValue && (!PdmDocumentIdentityStore.TryWriteControlledVersion(path, documentId, null,
                    versionId, string.Empty, expectedSha256, sourceLength.Value)
                || !PdmDocumentIdentityStore.TryReadLocalMetadata(path, out var metadata)
                || !PdmDocumentIdentityStore.TryRecordNativeIsolation(path, metadata, nativeSnapshotId.Value, actualSha256)))
                throw new IOException("独立副本已读取，但来源版本记录未能保存，未更新本地工作文件。");
            return path;
        }
        catch
        {
            if (File.Exists(partialPath)) File.Delete(partialPath);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
            throw;
        }
    }

    public async Task<string> DownloadControlledOpenFileAsync(
        ControlledOpenFileDto file,
        string stagingDirectory,
        CancellationToken cancellationToken)
    {
        var relativePath = file.RelativePath ?? file.FileName;
        var root = Path.GetFullPath(stagingDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, relativePath ?? string.Empty));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("PLM打开清单包含无效相对路径。");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path));
        var partialPath = path + ".download";
        if (File.Exists(partialPath)) File.Delete(partialPath);
        try
        {
            using (var response = await httpClient.GetAsync(
                string.Concat("api/documents/", file.DocumentId, "/versions/", file.VersionId, "/file?download=false",
                    file.NativeIsolation == null ? "" : "&nativeIsolationSnapshotId=" + file.NativeIsolation.ReferenceSnapshotId),
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                {
                    ThrowAuthenticationExpiredIfNeeded(response);
                    throw new InvalidOperationException(string.Concat(file.FileName, "的受控版本读取失败。"));
                }

                using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var output = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 256 * 1024, true))
                {
                    await input.CopyToAsync(output).ConfigureAwait(false);
                }
            }

            var info = new FileInfo(partialPath);
            if (info.Length != (file.NativeIsolation?.Length ?? file.FileLength)
                || !string.Equals(ComputeFileSha256(partialPath), file.NativeIsolation?.Sha256 ?? file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(string.Concat(file.FileName, "的长度或SHA-256校验失败。"));
            }

            File.Move(partialPath, path);
            if ((file.NativeIsolation != null || file.IsNativeIsolated) && (!PdmDocumentIdentityStore.TryWriteControlledVersion(path, file.DocumentId,
                    null, file.VersionId, file.Revision, file.Sha256, file.FileLength)
                || !PdmDocumentIdentityStore.TryReadLocalMetadata(path, out var binding)
                || !PdmDocumentIdentityStore.TryRecordNativeIsolation(path, binding,
                    file.NativeIsolation?.ReferenceSnapshotId ?? file.VersionId, file.NativeIsolation?.Sha256 ?? file.Sha256)))
                throw new IOException("独立原生副本已下载，但固化来源校验记录未能保存。");
            return path;
        }
        catch
        {
            if (File.Exists(partialPath)) File.Delete(partialPath);
            throw;
        }
    }

    private static string BuildVersionCopyFileName(string fileName, string revision, Guid versionId)
    {
        var safeFileName = Path.GetFileName(fileName);
        var extension = Path.GetExtension(safeFileName);
        var name = Path.GetFileNameWithoutExtension(safeFileName);
        var invalidCharacters = new HashSet<char>(Path.GetInvalidFileNameChars());
        var revisionToken = new StringBuilder();
        foreach (var character in revision ?? string.Empty)
        {
            if (!invalidCharacters.Contains(character) && !char.IsWhiteSpace(character))
            {
                revisionToken.Append(character);
            }
        }

        if (revisionToken.Length == 0)
        {
            revisionToken.Append("VERSION");
        }

        return string.Concat(name, "__PDM_", revisionToken, "_", versionId.ToString("N").Substring(0, 8), extension);
    }

    public void Dispose()
    {
        loginHeartbeat?.Dispose();
        if (IsAuthenticated)
        {
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
                try { PostJsonAsync<bool>("api/auth/logout", new { }, timeout.Token).GetAwaiter().GetResult(); }
                catch (Exception error) { ClientDiagnostics.Record("runtime", "退出登录", error.Message); }
        }
        if (httpClient.DefaultRequestHeaders.Authorization != null) ClientDiagnostics.Pause(httpClient.DefaultRequestHeaders.Authorization.Parameter);
        controlledOpenHttpClient.Dispose();
        httpClient.Dispose();
    }

    private async Task<T> GetJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        using (var response = await httpClient.GetAsync(path, cancellationToken).ConfigureAwait(false))
        {
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                ThrowAuthenticationExpiredIfNeeded(response);
                ThrowServiceUnavailableIfNeeded(response);
                throw new InvalidOperationException(GetErrorMessage(response.StatusCode.ToString(), body));
            }

            return serializer.Deserialize<T>(body);
        }
    }

    private async Task<T> PostJsonAsync<T>(string path, object payload, CancellationToken cancellationToken)
        => await PostJsonAsync<T>(httpClient, path, payload, cancellationToken).ConfigureAwait(false);

    private async Task<T> PostJsonAsync<T>(HttpClient client, string path, object payload, CancellationToken cancellationToken)
    {
        var json = serializer.Serialize(payload);
        using (var content = new StringContent(json, Encoding.UTF8, "application/json"))
        using (var response = await client.PostAsync(path, content, cancellationToken).ConfigureAwait(false))
        {
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                if (!string.Equals(path, "api/auth/login", StringComparison.OrdinalIgnoreCase))
                {
                    ThrowAuthenticationExpiredIfNeeded(response);
                }
                ThrowServiceUnavailableIfNeeded(response);
                throw new InvalidOperationException(GetErrorMessage(response.StatusCode.ToString(), body));
            }

            return serializer.Deserialize<T>(body);
        }
    }

    internal static bool HasResolvedReferenceVersion(CadTreeNode node) =>
        HasResolvedRevision(node.CurrentRevision?.TrimEnd('*').Trim());

    private static void ThrowServiceUnavailableIfNeeded(HttpResponseMessage response)
    {
        if (response.StatusCode == System.Net.HttpStatusCode.BadGateway
            || response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable
            || response.StatusCode == System.Net.HttpStatusCode.GatewayTimeout)
            throw new ServiceUnavailableException();
    }

    internal sealed class ServiceUnavailableException : InvalidOperationException
    {
        public ServiceUnavailableException() : base("PLM服务器暂时不可用，请确认服务恢复后重试。") { }
    }

    internal static bool HasResolvedRevision(string revision) => ToRevisionRequest(revision) != null;

    /// <summary>
    /// 提交前校验引用版本。plannedPaths 传入本次一同提交的文件，
    /// 这些引用会在执行阶段先被提交并回填版本，因此校验阶段不做要求。
    /// </summary>
    internal static void ValidateCheckInReferences(CadTreeNode node, ICollection<string> plannedPaths = null)
    {
        ValidateArchiveReferenceCompleteness(node);
        ToRequestNode(node, true, plannedPaths);
    }

    internal async Task RequireReferenceWarningsSupportAsync(CancellationToken token)
    {
        var capabilities = await GetJsonAsync<Dictionary<string, bool>>("api/archive-capabilities", token);
        if (capabilities == null || !capabilities.TryGetValue("referenceWarnings", out var supported) || !supported)
            throw new InvalidOperationException("服务器尚不支持随版本保存引用提示，已保留本地图档。请更新服务器后重新存档。");
    }

    internal static void ValidateArchiveReferenceCompleteness(CadTreeNode node)
    {
        if (node == null) return;
        void Fail(string ruleId, InvalidOperationException error)
        {
            DocumentRuleGate.Fail(ruleId, error);
            node.StructureIndexPending = true;
        }
        var pending = new Stack<CadTreeNode>();
        var visited = new HashSet<CadTreeNode>();
        var missingFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        pending.Push(node);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            // A model contains its drawings, whose ReferencedModels point back to that model.
            if (current == null || !visited.Add(current)) continue;
            if (current.IsOutsideNativeFileScope) continue;
            if (NativeArchiveManifest.IsInterconnectCachePath(current.FullPath)
                && (!current.IsStoredImport || !ImportedComponentStorage.IsStableOwner(current.ImportedStorageOwnerPath)))
                Fail("B11", new InvalidOperationException("树内导入组件仅有临时缓存，尚未形成可确认的原生存档内容。请保存为稳定原生文件或真正嵌入父装配后重试：" + current.FileName));
            if (!current.NativeFileScopeVerified && current.IsStoredImport && current.IsModifiedInSolidWorks)
                Fail("B13", new InvalidOperationException("导入组件仍有未保存内容，请先在SolidWorks中保存所属装配并重新检查。组件：" + current.FileName
                    + "；所属装配：" + current.ImportedStorageOwnerPath + "。本次尚未上传文件，未保存内容仍保留。"));
            if (!current.NativeFileScopeVerified && !string.IsNullOrWhiteSpace(current.ArchiveReadError))
                Fail("B08", new InvalidOperationException(current.ArchiveReadError));
            if (current.Status == CadReferenceStatus.Missing)
                missingFiles.Add(current.FileName + "；实例：" + current.InstancePath + "；配置：" + current.Configuration + "；路径：" + current.FullPath);
            if (current.Status == CadReferenceStatus.Suppressed || current.Status == CadReferenceStatus.Lightweight)
            {
                if (string.IsNullOrWhiteSpace(current.FullPath) || !File.Exists(current.FullPath))
                    missingFiles.Add(current.FileName + "；实例：" + current.InstancePath + "；配置：" + current.Configuration + "；路径：" + current.FullPath);
                else if (!current.NativeFileScopeVerified && SuppressedAssemblyArchive.IsUnexpanded(current) && !SuppressedAssemblyArchive.CanReuse(current)
                    && !SuppressedAssemblyLocalStructure.CanUse(current))
                    Fail("B07", new InvalidOperationException("压缩或轻化子装配体“" + current.FileName + "”的内部结构尚未完整读取，已停止存档以防遗漏子件。请核对引用配置并重新执行整体存档。路径：" + current.FullPath));
            }
            foreach (var child in current.Children) pending.Push(child);
            foreach (var model in current.ReferencedModels) pending.Push(model);
        }
        if (missingFiles.Count > 0)
            Fail("B09", new InvalidOperationException("以下存档所需文件尚未定位，无法保证其他客户端独立打开；本地文件和修改均已保留：\r\n"
                + string.Join("\r\n", missingFiles.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
                + "\r\n请补齐以上实际文件后重试；压缩状态本身不限制存档。"));
    }
    private static object ToRequestNode(CadTreeNode node, bool isRoot, ICollection<string> plannedPaths = null)
    {
        if (isRoot) ValidateArchiveReferenceCompleteness(node);
        var children = new List<object>();
        if (node.Kind == CadDocumentKind.Drawing)
        {
            foreach (var model in node.ReferencedModels.Where(model => !model.IsOutsideNativeFileScope))
            {
                var reference = new CadTreeNode { DocumentId = model.DocumentId, FullPath = model.FullPath,
                    FileName = model.FileName, DisplayName = model.DisplayName, Kind = model.Kind,
                    ImportedSourcePath = model.ImportedSourcePath, ImportedStorageOwnerPath = model.ImportedStorageOwnerPath,
                    InstancePath = "model/" + model.FileName + "/" + model.Configuration,
                    Configuration = model.Configuration, Revision = model.Revision, CurrentRevision = model.CurrentRevision, Status = model.Status };
                children.Add(ToRequestNode(reference, false, plannedPaths));
            }
        }
        foreach (var child in node.Children)
        {
            if (child.IsOutsideNativeFileScope) continue;
            if (child.Kind == CadDocumentKind.Drawing)
            {
                continue;
            }

            if (plannedPaths != null
                && !string.IsNullOrWhiteSpace(child.FullPath)
                && plannedPaths.Contains(child.FullPath))
            {
                // 本次一起提交：执行阶段该子件会先入库并回填版本，这里不要求已解析。
                children.Add(ToRequestNode(child, false, plannedPaths));
                continue;
            }

            children.Add(ToRequestNode(child, false, plannedPaths));
        }

        var reusedArchive = SuppressedAssemblyArchive.CanReuse(node)
            ? node.SuppressedArchiveVersion.ReferenceSnapshot : null;
        if (reusedArchive != null)
        {
            var archived = reusedArchive;
            children = (archived.Children ?? new List<DocumentReferenceNodeDto>())
                .Where(child => child.Kind != (int)CadDocumentKind.Drawing)
                .Select(child => ToArchivedReferenceRequest(child, archived.InstancePath, node.InstancePath)).ToList();
        }

        var displayedRevisionText = isRoot ? node.Revision : node.CurrentRevision;
        var revisionText = displayedRevisionText?.EndsWith("*", StringComparison.Ordinal) == true
            ? displayedRevisionText.TrimEnd('*').Trim()
            : displayedRevisionText;
        var revision = ToRevisionRequest(revisionText);
        if (isRoot && revision == null && node.DocumentId.HasValue)
        {
            // Modified/display labels are not version identities. Recover only a
            // recorded base; the server still verifies ExpectedVersionId before upload.
            if (PdmDocumentIdentityStore.TryReadLocalMetadata(node.FullPath, out var binding)
                && binding.DocumentId == node.DocumentId && binding.VersionId.HasValue)
                revision = ToRevisionRequest(binding.Revision);
            else if (node.OpenedVersionId.HasValue)
                revision = ToRevisionRequest(node.OpenedRevision);
        }
        if (!isRoot && revision == null && plannedPaths?.Contains(node.FullPath) == true)
            revision = ToRevisionRequest(node.Revision);
        if (!isRoot && node.Status != CadReferenceStatus.Virtual && !node.DocumentId.HasValue)
        {
            if (plannedPaths?.Contains(node.FullPath) != true && !node.NativeFileScopeVerified)
            {
                PluginDiagnosticLog.Record("引用", "引用身份缺失：文件=" + node.FileName + "；路径=" + node.FullPath
                    + "；实例=" + node.InstancePath + "；配置=" + node.Configuration);
                throw new InvalidOperationException("引用文件“" + node.FileName + "”未匹配到当前项目的存档身份，请刷新设计树并重新整体存档。实例："
                    + node.InstancePath + "；路径：" + node.FullPath);
            }
        }
        if (!isRoot && node.Status != CadReferenceStatus.Virtual && node.DocumentId.HasValue
            && revision == null && plannedPaths?.Contains(node.FullPath) != true)
        {
            // The frozen parent baseline on the server can identify an existing
            // reference even when local display metadata was lost. Preflight must
            // resolve it before any upload; never guess a latest child revision.
            PluginDiagnosticLog.Record("引用版本", "本地引用版本待由PLM存档快照核对：" + node.FileName
                + "；图档=" + node.DocumentId + "；实例=" + node.InstancePath);
        }

        return new
        {
            treeScopeOnly = false, includesDesignDependencies = isRoot, nodeId = node.NodeId,
            referenceWarnings = node.ReferenceWarnings.Distinct().ToArray(),
            usesNativeFileInventory = isRoot && node.NativeFileScopeVerified,
            structureIndexPending = node.StructureIndexPending || node.HasUnverifiedDrawingIndex
                || reusedArchive?.StructureIndexPending == true,
            documentId = node.Status == CadReferenceStatus.Virtual ? (Guid?)null : node.DocumentId,
            instancePath = node.InstancePath,
            fileName = node.FileName,
            displayName = node.DisplayName,
            kind = (int)node.Kind,
            role = (int)node.Role,
            configuration = node.Configuration,
            quantity = node.Quantity,
            status = (int)node.Status,
            isSuppressed = node.IsSuppressed,
            revision = node.Status == CadReferenceStatus.Virtual ? null : revision,
            checkedOutBy = node.Status == CadReferenceStatus.Virtual ? null : node.CheckedOutBy,
            children
        };
    }

    private static object ToArchivedReferenceRequest(DocumentReferenceNodeDto node, string oldRootPath, string newRootPath)
    {
        // Missing occurrence identities are separated from the verified native file
        // inventory by CaptureArchiveSnapshot; they remain pending instead of guessed.
        var prefix = (oldRootPath ?? string.Empty).TrimEnd('/') + "/";
        var path = node.InstancePath ?? node.FileName ?? string.Empty;
        var relative = path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? path.Substring(prefix.Length) : path;
        return new
        {
            nodeId = Guid.NewGuid(), documentId = node.DocumentId,
            structureIndexPending = node.StructureIndexPending,
            referenceWarnings = node.ReferenceWarnings,
            instancePath = (newRootPath ?? string.Empty).TrimEnd('/') + "/" + relative,
            fileName = node.FileName, displayName = node.DisplayName, kind = node.Kind,
            role = node.Role, isSuppressed = node.IsSuppressed,
            configuration = node.Configuration, quantity = node.Quantity, status = node.Status,
            revision = ToRevisionRequest(node.Revision?.Display), checkedOutBy = (string)null,
            children = (node.Children ?? new List<DocumentReferenceNodeDto>())
                .Where(child => child.Kind != (int)CadDocumentKind.Drawing)
                .Select(child => ToArchivedReferenceRequest(child, oldRootPath, newRootPath)).ToArray()
        };
    }

    private void ThrowAuthenticationExpiredIfNeeded(HttpResponseMessage response)
    {
        if (response == null || response.StatusCode != System.Net.HttpStatusCode.Unauthorized)
        {
            return;
        }

        httpClient.DefaultRequestHeaders.Authorization = null;
        ClientDiagnostics.Pause();
        controlledOpenHttpClient.DefaultRequestHeaders.Authorization = null;
        if (Interlocked.Exchange(ref authenticationExpiredRaised, 1) == 0)
        {
            AuthenticationExpired?.Invoke(this, EventArgs.Empty);
        }

        throw new InvalidOperationException("PLM登录已过期，请重新登录。");
    }

    private static object ToRevisionRequest(string revision)
    {
        var normalized = revision?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        if (normalized.Length == 1 && normalized[0] >= 'A' && normalized[0] <= 'Z')
        {
            return new { baseRevision = normalized, workIteration = 0, isReleased = true };
        }

        if (normalized.StartsWith("W", StringComparison.Ordinal)
            && int.TryParse(normalized.Substring(1), out var workIteration)
            && workIteration > 0)
        {
            return new { baseRevision = (string)null, workIteration, isReleased = false };
        }

        if (normalized.Length >= 4
            && normalized[0] >= 'A'
            && normalized[0] <= 'Z'
            && string.Equals(normalized.Substring(1, 2), "-W", StringComparison.Ordinal)
            && int.TryParse(normalized.Substring(3), out workIteration)
            && workIteration > 0)
        {
            return new { baseRevision = normalized.Substring(0, 1), workIteration, isReleased = false };
        }

        return null;
    }

    private static string GetErrorMessage(string status, string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Concat("PLM服务返回", status, "。 ");
        }

        try
        {
            var problem = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(body);
            foreach (var key in new[] { "detail", "message", "title" })
            {
                if (problem.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value?.ToString()))
                {
                    return value.ToString();
                }
            }
        }
        catch
        {
            // Preserve the original response when it is not JSON problem details.
        }

        return body.Length <= 500 ? body : body.Substring(0, 500);
    }
}

internal sealed class UserThemeDto
{
    public string Theme { get; set; }
}

internal sealed class LoginResponseDto
{
    public string AccessToken { get; set; }
    public string ExpiresAt { get; set; }
    public string ResumeToken { get; set; }
    public string Username { get; set; }
    public string DisplayName { get; set; }
    public string Role { get; set; }
    public List<string> Roles { get; set; }
    public List<string> Permissions { get; set; }
    public Guid PrimaryCompanyId { get; set; }
    public Guid ActiveCompanyId { get; set; }
    public string ActiveCompanyName { get; set; }
    public bool CrossCompanyView { get; set; }
    public List<CompanyOptionDto> AccessibleCompanies { get; set; }
}

internal sealed class CompanyOptionDto
{
    public Guid Id { get; set; }
    public string Name { get; set; }
    public string Code { get; set; }
}

internal sealed class ProjectPlanDto
{
    public List<ProjectPlanTaskDto> Tasks { get; set; }
}

internal sealed class ProjectPlanTaskDto
{
    public string Name { get; set; }
    public string Assignee { get; set; }
    public string PlannedFinish { get; set; }
}

internal sealed class ProjectDto
{
    public Guid Id { get; set; }
    public string Code { get; set; }
    public string Name { get; set; }
    public Guid? ParentProjectId { get; set; }
    public int? ChildSequence { get; set; }
    public bool CanReadContent { get; set; }
    public bool CanSubmitArchive { get; set; }
    public int? DocumentCount { get; set; }
    public string BusinessStatus { get; set; }
    public string ExecutionUnitName { get; set; }
    public string DesignLead { get; set; }
    public List<string> DesignLeads { get; set; }
    public List<string> Designers { get; set; }
    public List<string> SerialNumbers { get; set; }

    public override string ToString() => string.Concat(Code, " · ", Name);
}

internal sealed class OrganizationDirectoryDto
{
    public List<OrganizationDirectoryUserDto> Users { get; set; }
}

internal sealed class OrganizationDirectoryUserDto
{
    public string Username { get; set; }
    public string DisplayName { get; set; }
}

internal sealed class RevisionDto
{
    public string Display { get; set; }
}

internal sealed class DocumentReferenceNodeDto
{
    public List<string> ReferenceWarnings { get; set; }
    public bool StructureIndexPending { get; set; }
    public Guid? DocumentId { get; set; }
    public string InstancePath { get; set; }
    public string FileName { get; set; }
    public string DisplayName { get; set; }
    public int Kind { get; set; }
    public int Role { get; set; }
    public string Configuration { get; set; }
    public int Quantity { get; set; }
    public int Status { get; set; }
    public bool IsSuppressed { get; set; }
    public RevisionDto Revision { get; set; }
    public string CheckedOutBy { get; set; }
    public List<DocumentReferenceNodeDto> Children { get; set; }
}

internal sealed class DocumentDto
{
    public string DrawingVersion { get; set; }
    public string PublishedVersion { get; set; }
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public string DrawingNumber { get; set; }
    public string Name { get; set; }
    public string FileName { get; set; }
    public int Kind { get; set; }
    public int State { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public string CheckedOutBy { get; set; }
    public DateTime? CheckedOutAt { get; set; }
    public Guid? CheckoutSessionId { get; set; }
    public bool DrawingReviewLocked { get; set; }
    public int? StoredVersionCount { get; set; }
    public string CheckoutMachine { get; set; }
    public DateTime? CheckoutLastHeartbeatAt { get; set; }
    public DateTime? CheckoutLeaseExpiresAt { get; set; }
    public string CheckoutReleaseRequestedBy { get; set; }
    public DateTime? CheckoutReleaseRequestedAt { get; set; }
    public RevisionDto Revision { get; set; }
}

internal sealed class DocumentRegistrationCandidateDto
{
    public string CandidateKey { get; set; }
    public string FileName { get; set; }
    public int Kind { get; set; }
    public string SourceSha256 { get; set; }
}

internal sealed class DocumentRegistrationMatchDto
{
    public string CandidateKey { get; set; }
    public int MatchKind { get; set; }
    public Guid? ExistingDocumentId { get; set; }
    public Guid? ExistingProjectId { get; set; }
    public string ExistingProjectCode { get; set; }
    public string ExistingProjectName { get; set; }
    public string ExistingDrawingNumber { get; set; }
    public string ExistingFileName { get; set; }
    public string ExistingRevision { get; set; }
}

internal sealed class DocumentModelDrawingRelationDto
{
    public Guid ModelDocumentId { get; set; }
    public Guid DrawingDocumentId { get; set; }
}

internal sealed class DocumentWhereUsedDto
{
    public Guid ParentDocumentId { get; set; }
    public Guid ProjectId { get; set; }
    public string ProjectCode { get; set; }
    public string ProjectName { get; set; }
    public string ParentDrawingNumber { get; set; }
    public string ParentName { get; set; }
    public string ParentFileName { get; set; }
    public int ParentState { get; set; }
    public RevisionDto ParentRevision { get; set; }
    public string InstancePath { get; set; }
    public string Configuration { get; set; }
    public int Quantity { get; set; }
}

internal sealed class ReleasePackageDto
{
    public Guid Id { get; set; }
    public int State { get; set; }
    public string Number { get; set; }
}

internal sealed class EditSessionHeartbeatDto
{
    public Guid SessionId { get; set; }
    public DateTime ServerTime { get; set; }
    public DateTime LeaseExpiresAt { get; set; }
    public List<Guid> ActiveDocumentIds { get; set; }
    public List<Guid> LostDocumentIds { get; set; }
    public List<Guid> ReleaseRequestedDocumentIds { get; set; }
    public CheckoutPolicyDto Settings { get; set; }
}

internal sealed class CheckoutPolicyDto
{
    public int CheckoutHeartbeatSeconds { get; set; } = 180;
    public bool CheckoutAutoReleaseEnabled { get; set; }
    public int CheckoutIdleReleaseMinutes { get; set; } = 30;
    public int CheckoutReleaseCountdownSeconds { get; set; } = 60;
    public int CheckoutReminderHours { get; set; } = 4;
    public int CheckoutStrongReminderHours { get; set; } = 8;
}

internal sealed class DocumentVersionDto
{
    public string DrawingVersion { get; set; }
    public string PublishedVersion { get; set; }
    public string LatestPublishedVersion { get; set; }
    public string StorageRelativePath { get; set; }
    public long FileLength { get; set; }
    public Guid Id { get; set; }
    public RevisionDto Revision { get; set; }
    public int Status { get; set; }
    public string CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public string ChangeNote { get; set; }
    public string Sha256 { get; set; }
    public Dictionary<string, string> PropertySnapshot { get; set; }
    public DocumentReferenceNodeDto ReferenceSnapshot { get; set; }
}

internal sealed class BomPropertyMappingDto
{
    public string PdmPropertyKey { get; set; }
    public string PdmPropertyName { get; set; }
    public string SolidWorksProperty { get; set; }
    public string Source { get; set; }
    public bool MappingEditable { get; set; }
}

internal sealed class BomItemDto
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public int Kind { get; set; }
    public int Sequence { get; set; }
    public string DrawingNumber { get; set; }
    public string Name { get; set; }
    public decimal Quantity { get; set; }
    public string Unit { get; set; }
    public string Material { get; set; }
    public string Specification { get; set; }
    public string Revision { get; set; }
    public string Remark { get; set; }
    public string Brand { get; set; }
    public string SurfaceTreatment { get; set; }
    public string Weight { get; set; }
    public Guid? SourceDocumentId { get; set; }
    public string SourceConfiguration { get; set; }
    public bool IsManuallyExcluded { get; set; }
}

internal sealed class CheckInResultDto
{
    public DocumentDto Document { get; set; }
    public DocumentVersionDto Version { get; set; }
    public bool VersionCreated { get; set; }
    public BomGenerationResultDto BomUpdate { get; set; }
    public string BomUpdateError { get; set; }
    public bool BomRefreshDeferred { get; set; }
    public List<string> AffectedAssemblies { get; set; }
}

internal sealed class BomGenerationResultDto
{
    public int UnclassifiedCount { get; set; }
    public int PendingRemovalCount { get; set; }
    public int ManualUnmatchedCount { get; set; }
    public bool Applied { get; set; }
}
internal sealed class CadPropertyWritebackDto
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid BomItemId { get; set; }
    public Guid SourceDocumentId { get; set; }
    public string SourceConfiguration { get; set; }
    public Guid ExpectedVersionId { get; set; }
    public string ExpectedRevision { get; set; }
    public Dictionary<string, string> Properties { get; set; }
    public int Status { get; set; }
    public string RequestedBy { get; set; }
    public DateTime RequestedAt { get; set; }
    public string LastError { get; set; }
}
internal sealed class ArchiveBatchReceiptDto
{
    public Guid BatchId { get; set; }
    public bool FollowupPending { get; set; }
    public List<CheckInResultDto> Results { get; set; }
}

internal sealed class ControlledOpenManifestDto
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public string ProjectCode { get; set; }
    public Guid RootDocumentId { get; set; }
    public Guid RootVersionId { get; set; }
    public string RootRevision { get; set; }
    public string RootRelativePath { get; set; }
    public bool ForEdit { get; set; }
    public List<ControlledOpenFileDto> Files { get; set; }
    public List<string> Warnings { get; set; } = new List<string>();
    [ScriptIgnore]
    public List<string> LocalCompatibilityNotes { get; } = new List<string>();
}
internal sealed class ControlledOpenFileDto
{
    public bool IsNativeIsolated { get; set; }
    public NativeFileIsolationDto NativeIsolation { get; set; }
    public Guid DocumentId { get; set; }
    public Guid VersionId { get; set; }
    public string Revision { get; set; }
    public string FileName { get; set; }
    public string RelativePath { get; set; }
    public long FileLength { get; set; }
    public string Sha256 { get; set; }
    public string Configuration { get; set; }
    public bool IsRoot { get; set; }
}
internal sealed class NativeFileIsolationDto
{
    public Guid SourceVersionId { get; set; }
    public string SourceSha256 { get; set; }
    public string RelativePath { get; set; }
    public long Length { get; set; }
    public string Sha256 { get; set; }
    public string GeometryEvidenceSha256 { get; set; }
    public int PolicyVersion { get; set; }
    public Guid ReferenceSnapshotId { get; set; }
    public string ReferenceScopeSha256 { get; set; }
}
internal sealed class UploadSessionDto
{
    public string TargetRelativePath { get; set; }
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public int ChunkSize { get; set; }
    public long TotalLength { get; set; }
    public long ReceivedLength { get; set; }
    public string ExpectedSha256 { get; set; }
    public DateTime ExpiresAt { get; set; }
}
internal sealed class StoredFileDto { public string RelativePath { get; set; } public long Length { get; set; } public string Sha256 { get; set; } }
internal sealed class StoredVersionFile
{
    public StoredVersionFile(string relativePath, long length, string sha256, Dictionary<string, string> properties)
    {
        RelativePath = relativePath;
        Length = length;
        Sha256 = sha256;
        Properties = properties;
    }
    public string RelativePath { get; }
    public long Length { get; }
    public string Sha256 { get; }
    public Dictionary<string, string> Properties { get; }
}
