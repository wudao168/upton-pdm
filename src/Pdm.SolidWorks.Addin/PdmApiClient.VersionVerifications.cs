using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Upton.Pdm.SolidWorks;

internal sealed partial class PdmApiClient
{
    private TimeSpan versionVerificationTimeout = TimeSpan.FromSeconds(120);
    internal async Task<List<DocumentVersionVerificationResultDto>> VerifyReferenceVersionsAsync(
        IReadOnlyList<DocumentVersionVerificationRequestDto> requests, CancellationToken token)
    {
        return await RecoverArchiveConnectionAsync("引用版本", (retryToken, retry) => VerifyReferenceVersionsOnceAsync(requests, retryToken), token).ConfigureAwait(false);
    }

    private async Task<List<DocumentVersionVerificationResultDto>> VerifyReferenceVersionsOnceAsync(
        IReadOnlyList<DocumentVersionVerificationRequestDto> requests, CancellationToken token)
    {
        using var requestDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        requestDeadline.CancelAfter(versionVerificationTimeout);
        using (var content = new StringContent(serializer.Serialize(new { Documents = requests }), Encoding.UTF8, "application/json"))
        using (var response = await controlledOpenHttpClient.PostAsync("api/documents/version-verifications", content, requestDeadline.Token).ConfigureAwait(false))
        {
            if (response.StatusCode == HttpStatusCode.NotFound || response.StatusCode == HttpStatusCode.MethodNotAllowed) return null;
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                ThrowAuthenticationExpiredIfNeeded(response);
                ThrowServiceUnavailableIfNeeded(response);
                throw new InvalidOperationException(GetErrorMessage(response.StatusCode.ToString(), body));
            }
            return serializer.Deserialize<List<DocumentVersionVerificationResultDto>>(body)
                ?? throw new InvalidOperationException("批量引用版本核对未返回有效结果，尚未上传，请稍后重试。");
        }
    }

}

internal sealed class DocumentVersionVerificationRequestDto
{
    public Guid DocumentId { get; set; }
    public Guid? BoundVersionId { get; set; }
}

internal sealed class DocumentVersionVerificationDto
{
    public Guid Id { get; set; }
    public Guid DocumentId { get; set; }
    public string Revision { get; set; }
    public long FileLength { get; set; }
    public string Sha256 { get; set; }
    public string SourceFileSha256 { get; set; }
}

internal sealed class DocumentVersionVerificationResultDto
{
    public Guid DocumentId { get; set; }
    public DocumentVersionVerificationDto Latest { get; set; }
    public DocumentVersionVerificationDto Bound { get; set; }
    public bool Accessible { get; set; } = true;
}
