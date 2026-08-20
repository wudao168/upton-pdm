using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Upton.Pdm.SolidWorks;

internal sealed class PdmApiClient : IDisposable
{
    private readonly HttpClient httpClient;
    private readonly JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

    public PdmApiClient(string baseAddress)
    {
        httpClient = new HttpClient
        {
            BaseAddress = new Uri(baseAddress.TrimEnd('/') + "/", UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    public bool IsAuthenticated => httpClient.DefaultRequestHeaders.Authorization != null;

    public async Task<LoginResponseDto> LoginAsync(string username, string password, CancellationToken cancellationToken)
    {
        var response = await PostJsonAsync<LoginResponseDto>("api/auth/login", new { username, password }, cancellationToken).ConfigureAwait(false);
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", response.AccessToken);
        return response;
    }

    public Task<List<ProjectDto>> GetProjectsAsync(CancellationToken cancellationToken) =>
        GetJsonAsync<List<ProjectDto>>("api/projects", cancellationToken);

    public Task<List<DocumentDto>> GetDocumentsAsync(Guid projectId, CancellationToken cancellationToken) =>
        GetJsonAsync<List<DocumentDto>>(string.Concat("api/projects/", projectId, "/documents"), cancellationToken);

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
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(GetErrorMessage(response.StatusCode.ToString(), body));
            }

            return serializer.Deserialize<DocumentReferenceNodeDto>(body);
        }
    }

    public Task<ControlledOpenManifestDto> CreateControlledOpenManifestAsync(
        Guid documentId,
        Guid? versionId,
        bool releasedOnly,
        bool forEdit,
        CancellationToken cancellationToken) =>
        PostJsonAsync<ControlledOpenManifestDto>(
            string.Concat("api/documents/", documentId, "/open-manifest"),
            new { versionId, releasedOnly, forEdit },
            cancellationToken);

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
        CancellationToken cancellationToken) =>
        PostJsonAsync<DocumentDto>(
            string.Concat("api/projects/", projectId, "/documents/register"),
            new
            {
                drawingNumber = Path.GetFileNameWithoutExtension(node.FileName),
                name = string.IsNullOrWhiteSpace(node.DisplayName) ? Path.GetFileNameWithoutExtension(node.FileName) : node.DisplayName,
                fileName = node.FileName,
                kind = (int)node.Kind,
                relatedModelDocumentId = node.RelatedModelDocumentId,
                sourceSha256,
                allowDuplicateContent,
                duplicateReason
            },
            cancellationToken);

    public Task<List<DocumentVersionDto>> GetVersionsAsync(Guid documentId, CancellationToken cancellationToken) =>
        GetJsonAsync<List<DocumentVersionDto>>(string.Concat("api/documents/", documentId, "/versions"), cancellationToken);

    public Task<DocumentDto> CheckoutAsync(Guid documentId, Guid sessionId, string machineName, CancellationToken cancellationToken) =>
        PostJsonAsync<DocumentDto>(string.Concat("api/documents/", documentId, "/checkout"), new { sessionId, machineName }, cancellationToken);

    public Task<DocumentDto> CompleteEditWithoutChangesAsync(Guid documentId, Guid checkoutSessionId, string sha256, CancellationToken cancellationToken) =>
        PostJsonAsync<DocumentDto>(string.Concat("api/documents/", documentId, "/complete-edit"), new { sha256, checkoutSessionId }, cancellationToken);

    public Task<DocumentDto> DiscardCheckoutAsync(Guid documentId, Guid checkoutSessionId, CancellationToken cancellationToken) =>
        PostJsonAsync<DocumentDto>(string.Concat("api/documents/", documentId, "/discard-checkout"), new { checkoutSessionId }, cancellationToken);

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

    public Task<CheckInResultDto> CheckInAsync(
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
        CancellationToken cancellationToken) =>
        PostJsonAsync<CheckInResultDto>(
            string.Concat("api/documents/", documentId, "/checkin"),
            new
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
                fileName = root.FileName
            },
            cancellationToken);

    private static Dictionary<string, string> MergeProperties(IReadOnlyDictionary<string, string> fileProperties, IReadOnlyDictionary<string, string> modelProperties)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (fileProperties != null)
            foreach (var property in fileProperties) result[property.Key] = property.Value;
        if (modelProperties != null)
            foreach (var property in modelProperties) result[property.Key] = property.Value;
        return result;
    }

    public async Task<StoredVersionFile> UploadVersionFileAsync(Guid projectId, string filePath, Guid documentId, string originalFilePath, CancellationToken cancellationToken)
    {
        var file = new FileInfo(filePath);
        var originalFile = new FileInfo(originalFilePath);
        var sha256 = ComputeFileSha256(filePath);
        var sourceFileSha256 = ComputeFileSha256(originalFilePath);
        var session = await PostJsonAsync<UploadSessionDto>("api/uploads/sessions", new { projectId, fileName = file.Name, totalLength = file.Length, sha256 }, cancellationToken).ConfigureAwait(false);
        using (var input = File.OpenRead(filePath))
        {
            var buffer = new byte[session.ChunkSize];
            var chunkIndex = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
            {
                using (var content = new ByteArrayContent(buffer, 0, read))
                using (var response = await httpClient.PutAsync(string.Concat("api/uploads/sessions/", session.Id, "/chunks/", chunkIndex), content, cancellationToken).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode) throw new InvalidOperationException(string.Concat("版本文件第", chunkIndex + 1, "块上传失败。"));
                }
                chunkIndex++;
            }
        }
        var relative = Path.Combine(".versions", documentId.ToString("N"), Guid.NewGuid().ToString("N"), file.Name);
        var stored = await PostJsonAsync<StoredFileDto>(string.Concat("api/uploads/sessions/", session.Id, "/complete"), new { relativeTargetPath = relative }, cancellationToken).ConfigureAwait(false);
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
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException("历史版本文件读取失败。");
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
            using (var response = await httpClient.GetAsync(
                string.Concat("api/documents/", documentId, "/versions/", versionId, "/file?download=false"),
                cancellationToken).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException("PDM最新版本文件读取失败。");
                }

                using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var output = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await input.CopyToAsync(output).ConfigureAwait(false);
                }
            }

            var actualSha256 = ComputeFileSha256(partialPath);
            if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("PDM最新版本文件校验失败，未更新本地工作文件。");
            }

            File.Move(partialPath, path);
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
            throw new InvalidDataException("PDM打开清单包含无效相对路径。");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path));
        var partialPath = path + ".download";
        if (File.Exists(partialPath)) File.Delete(partialPath);
        try
        {
            using (var response = await httpClient.GetAsync(
                string.Concat("api/documents/", file.DocumentId, "/versions/", file.VersionId, "/file?download=false"),
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException(string.Concat(file.FileName, "的受控版本读取失败。"));
                }

                using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var output = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 256 * 1024, true))
                {
                    await input.CopyToAsync(output).ConfigureAwait(false);
                }
            }

            var info = new FileInfo(partialPath);
            if (info.Length != file.FileLength
                || !string.Equals(ComputeFileSha256(partialPath), file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(string.Concat(file.FileName, "的长度或SHA-256校验失败。"));
            }

            File.Move(partialPath, path);
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

    public void Dispose() => httpClient.Dispose();

    private async Task<T> GetJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        using (var response = await httpClient.GetAsync(path, cancellationToken).ConfigureAwait(false))
        {
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(GetErrorMessage(response.StatusCode.ToString(), body));
            }

            return serializer.Deserialize<T>(body);
        }
    }

    private async Task<T> PostJsonAsync<T>(string path, object payload, CancellationToken cancellationToken)
    {
        var json = serializer.Serialize(payload);
        using (var content = new StringContent(json, Encoding.UTF8, "application/json"))
        using (var response = await httpClient.PostAsync(path, content, cancellationToken).ConfigureAwait(false))
        {
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(GetErrorMessage(response.StatusCode.ToString(), body));
            }

            return serializer.Deserialize<T>(body);
        }
    }

    private static object ToRequestNode(CadTreeNode node, bool isRoot)
    {
        var children = new List<object>();
        foreach (var child in node.Children)
        {
            if (child.Kind == CadDocumentKind.Drawing)
            {
                continue;
            }

            children.Add(ToRequestNode(child, false));
        }

        var displayedRevisionText = isRoot ? node.Revision : node.CurrentRevision;
        var revisionText = !isRoot && displayedRevisionText?.EndsWith("*", StringComparison.Ordinal) == true
            ? displayedRevisionText.TrimEnd('*').Trim()
            : displayedRevisionText;
        var revision = ToRevisionRequest(revisionText);
        if (!isRoot && node.DocumentId.HasValue && revision == null)
        {
            throw new InvalidOperationException(string.Concat(
                "引用文件",
                string.IsNullOrWhiteSpace(node.FileName) ? node.DisplayName : node.FileName,
                "的本机版本无法对应PDM受控版本（当前显示：",
                string.IsNullOrWhiteSpace(displayedRevisionText) ? "待识别" : displayedRevisionText,
                "）。请先保存并提交或更新该子件，再提交根装配。"));
        }

        return new
        {
            nodeId = node.NodeId,
            documentId = node.DocumentId,
            instancePath = node.InstancePath,
            fileName = node.FileName,
            displayName = node.DisplayName,
            kind = (int)node.Kind,
            configuration = node.Configuration,
            quantity = node.Quantity,
            status = (int)node.Status,
            revision,
            checkedOutBy = node.CheckedOutBy,
            children
        };
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
            return string.Concat("PDM服务返回", status, "。 ");
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

internal sealed class LoginResponseDto
{
    public string AccessToken { get; set; }
    public string Username { get; set; }
    public string DisplayName { get; set; }
    public string Role { get; set; }
}

internal sealed class ProjectDto
{
    public Guid Id { get; set; }
    public string Code { get; set; }
    public string Name { get; set; }
    public Guid? ParentProjectId { get; set; }
    public int? ChildSequence { get; set; }
    public bool CanReadContent { get; set; }
    public int? DocumentCount { get; set; }
    public string BusinessStatus { get; set; }

    public override string ToString() => string.Concat(Code, " · ", Name);
}

internal sealed class RevisionDto
{
    public string Display { get; set; }
}

internal sealed class DocumentReferenceNodeDto
{
    public Guid? DocumentId { get; set; }
    public string InstancePath { get; set; }
    public string FileName { get; set; }
    public string DisplayName { get; set; }
    public int Kind { get; set; }
    public string Configuration { get; set; }
    public int Quantity { get; set; }
    public int Status { get; set; }
    public RevisionDto Revision { get; set; }
    public string CheckedOutBy { get; set; }
    public List<DocumentReferenceNodeDto> Children { get; set; }
}

internal sealed class DocumentDto
{
    public Guid Id { get; set; }
    public string DrawingNumber { get; set; }
    public string Name { get; set; }
    public string FileName { get; set; }
    public int Kind { get; set; }
    public int State { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public string CheckedOutBy { get; set; }
    public DateTime? CheckedOutAt { get; set; }
    public Guid? CheckoutSessionId { get; set; }
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
    public CheckoutPolicyDto Settings { get; set; }
}

internal sealed class CheckoutPolicyDto
{
    public int CheckoutHeartbeatSeconds { get; set; } = 180;
    public int CheckoutReminderHours { get; set; } = 4;
    public int CheckoutStrongReminderHours { get; set; } = 8;
}

internal sealed class DocumentVersionDto
{
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

internal sealed class CheckInResultDto
{
    public DocumentDto Document { get; set; }
    public DocumentVersionDto Version { get; set; }
    public bool VersionCreated { get; set; }
    public BomGenerationResultDto BomUpdate { get; set; }
    public string BomUpdateError { get; set; }
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
}
internal sealed class ControlledOpenFileDto
{
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
internal sealed class UploadSessionDto { public Guid Id { get; set; } public int ChunkSize { get; set; } }
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
