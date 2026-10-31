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
    internal async Task<List<DocumentVersionVerificationResultDto>> VerifyReferenceVersionsAsync(
        IReadOnlyList<DocumentVersionVerificationRequestDto> requests, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
        try
        {
        using (var content = new StringContent(serializer.Serialize(new { Documents = requests }), Encoding.UTF8, "application/json"))
        using (var response = await httpClient.PostAsync("api/documents/version-verifications", content, token).ConfigureAwait(false))
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
        catch (Exception error) when ((error is HttpRequestException || error is ServiceUnavailableException)
            && attempt < 2 && !token.IsCancellationRequested)
        {
            ArchiveSession.Current?.SetStage("连接暂未恢复，正在重试只读版本核对（" + (attempt + 1) + "/2）…");
            await Task.Delay(attempt == 0 ? 1000 : 3000, token).ConfigureAwait(false);
        }
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
