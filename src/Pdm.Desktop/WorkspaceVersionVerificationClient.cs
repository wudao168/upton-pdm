#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Upton.Pdm.SolidWorks;

namespace Upton.Pdm.Desktop;

internal sealed class WorkspaceVersionVerification
{
    public Guid Id { get; set; }
    public Guid DocumentId { get; set; }
    public string Revision { get; set; }
    public long FileLength { get; set; }
    public string Sha256 { get; set; }
    public string SourceFileSha256 { get; set; }
}

internal sealed class WorkspaceVersionVerificationResult
{
    public Guid DocumentId { get; set; }
    public bool Accessible { get; set; }
    public WorkspaceVersionVerification Latest { get; set; }
    public WorkspaceVersionVerification Bound { get; set; }
}

internal static class WorkspaceVersionVerificationClient
{
    public static async Task<IReadOnlyList<WorkspaceVersionVerificationResult>> ReadAsync(HttpClient client,
        Func<HttpRequestMessage> createRequest, IReadOnlyList<WorkspaceLocalFileState> files, CancellationToken cancellationToken)
    {
        var serializer = new JavaScriptSerializer { MaxJsonLength = 1024 * 1024 };
        var results = new List<WorkspaceVersionVerificationResult>();
        for (var offset = 0; offset < files.Count; offset += 100)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = files.Skip(offset).Take(100).ToArray();
            using (var request = createRequest())
            {
                request.Content = new StringContent(serializer.Serialize(new { documents = batch.Select(file =>
                    new { documentId = file.DocumentId, boundVersionId = file.BoundVersionId }).ToArray() }), Encoding.UTF8, "application/json");
                using (var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        var error = new HttpRequestException("服务器暂未完成图档批量校验，状态码：" + (int)response.StatusCode);
                        error.Data["HttpStatusCode"] = response.StatusCode;
                        throw error;
                    }
                    var rows = serializer.Deserialize<WorkspaceVersionVerificationResult[]>(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                    var requested = batch.ToDictionary(file => file.DocumentId);
                    if (rows == null || rows.Length != batch.Length || rows.Any(row => row == null)
                        || rows.Select(row => row.DocumentId).Distinct().Count() != batch.Length)
                        throw new InvalidOperationException("服务器批量校验结果不完整或重复，已保留本地文件及校验记录。");
                    foreach (var row in rows)
                    {
                        if (!requested.TryGetValue(row.DocumentId, out var file)
                            || !ValidVersion(row.Latest, row.DocumentId) || !ValidVersion(row.Bound, row.DocumentId)
                            || row.Bound != null && row.Bound.Id != file.BoundVersionId
                            || row.Bound != null && row.Latest != null && row.Bound.Id == row.Latest.Id
                            || !row.Accessible && (row.Latest != null || row.Bound != null)
                            || row.Latest == null && row.Bound != null)
                            throw new InvalidOperationException("服务器批量校验结果与请求的图档或版本不一致，已保留本地文件及校验记录。");
                    }
                    results.AddRange(rows);
                }
            }
        }
        return results;
    }

    private static bool ValidVersion(WorkspaceVersionVerification version, Guid documentId) => version == null
        || version.DocumentId == documentId && version.Id != Guid.Empty && !string.IsNullOrWhiteSpace(version.Revision)
            && version.FileLength >= 0 && ValidHash(version.Sha256)
            && (string.IsNullOrWhiteSpace(version.SourceFileSha256) || ValidHash(version.SourceFileSha256));

    private static bool ValidHash(string value) => value != null && value.Length == 64
        && value.All(character => character >= '0' && character <= '9'
            || character >= 'a' && character <= 'f' || character >= 'A' && character <= 'F');
}
