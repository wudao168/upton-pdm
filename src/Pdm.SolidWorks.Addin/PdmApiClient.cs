using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
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

    public Task<DocumentDto> CheckoutAsync(Guid documentId, CancellationToken cancellationToken) =>
        PostJsonAsync<DocumentDto>(string.Concat("api/documents/", documentId, "/checkout"), new { }, cancellationToken);

    public Task<DocumentDto> CheckInAsync(Guid documentId, Guid projectId, CadTreeNode root, string comment, CancellationToken cancellationToken) =>
        PostJsonAsync<DocumentDto>(
            string.Concat("api/documents/", documentId, "/checkin"),
            new { projectId, root = ToRequestNode(root), comment },
            cancellationToken);

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
    public string FileName { get; set; }
    public string CheckedOutBy { get; set; }
    public RevisionDto Revision { get; set; }
}
