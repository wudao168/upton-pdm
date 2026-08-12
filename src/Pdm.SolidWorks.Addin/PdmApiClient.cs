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

    public Task<DocumentDto> RegisterDocumentAsync(Guid projectId, CadTreeNode node, CancellationToken cancellationToken) =>
        PostJsonAsync<DocumentDto>(
            string.Concat("api/projects/", projectId, "/documents/register"),
            new
            {
                drawingNumber = Path.GetFileNameWithoutExtension(node.FileName),
                name = string.IsNullOrWhiteSpace(node.DisplayName) ? Path.GetFileNameWithoutExtension(node.FileName) : node.DisplayName,
                fileName = node.FileName,
                kind = (int)node.Kind
            },
            cancellationToken);

    public Task<List<DocumentVersionDto>> GetVersionsAsync(Guid documentId, CancellationToken cancellationToken) =>
        GetJsonAsync<List<DocumentVersionDto>>(string.Concat("api/documents/", documentId, "/versions"), cancellationToken);

    public Task<DocumentDto> CheckoutAsync(Guid documentId, CancellationToken cancellationToken) =>
        PostJsonAsync<DocumentDto>(string.Concat("api/documents/", documentId, "/checkout"), new { }, cancellationToken);

    public Task<DocumentDto> CompleteEditWithoutChangesAsync(Guid documentId, string sha256, CancellationToken cancellationToken) =>
        PostJsonAsync<DocumentDto>(string.Concat("api/documents/", documentId, "/complete-edit"), new { sha256 }, cancellationToken);

    public Task<DocumentDto> DiscardCheckoutAsync(Guid documentId, CancellationToken cancellationToken) =>
        PostJsonAsync<DocumentDto>(string.Concat("api/documents/", documentId, "/discard-checkout"), new { }, cancellationToken);

    public Task<CheckInResultDto> CheckInAsync(Guid documentId, Guid projectId, CadTreeNode root, string comment, StoredVersionFile storedFile, IReadOnlyDictionary<string, string> modelProperties, CancellationToken cancellationToken) =>
        PostJsonAsync<CheckInResultDto>(
            string.Concat("api/documents/", documentId, "/checkin"),
            new { projectId, root = ToRequestNode(root), comment, storageRelativePath = storedFile.RelativePath, fileLength = storedFile.Length, sha256 = storedFile.Sha256, properties = MergeProperties(storedFile.Properties, modelProperties) },
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
        string sha256;
        using (var input = File.OpenRead(filePath))
        using (var hash = SHA256.Create())
        {
            sha256 = BitConverter.ToString(hash.ComputeHash(input)).Replace("-", string.Empty);
        }
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
        return new StoredVersionFile(stored.RelativePath, stored.Length, stored.Sha256, new Dictionary<string, string> { ["FileName"] = originalFile.Name, ["Extension"] = originalFile.Extension, ["LastWriteTimeUtc"] = originalFile.LastWriteTimeUtc.ToString("O") });
    }

    public async Task<string> DownloadVersionToTempAsync(Guid documentId, Guid versionId, string fileName, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Path.GetTempPath(), "UPTON-PDM", "history", documentId.ToString("N"), versionId.ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Path.GetFileName(fileName));
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

    private static object ToRequestNode(CadTreeNode node)
    {
        var children = new List<object>();
        foreach (var child in node.Children)
        {
            children.Add(ToRequestNode(child));
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
            revision = (object)null,
            checkedOutBy = node.CheckedOutBy,
            children
        };
    }

    private static string GetErrorMessage(string status, string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Concat("PDM服务返回", status, "。 ");
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

    public override string ToString() => string.Concat(Code, " · ", Name);
}

internal sealed class RevisionDto
{
    public string Display { get; set; }
}

internal sealed class DocumentDto
{
    public Guid Id { get; set; }
    public string DrawingNumber { get; set; }
    public string Name { get; set; }
    public string FileName { get; set; }
    public string CheckedOutBy { get; set; }
    public RevisionDto Revision { get; set; }
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
}

internal sealed class CheckInResultDto { public DocumentDto Document { get; set; } public DocumentVersionDto Version { get; set; } public bool VersionCreated { get; set; } }
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
