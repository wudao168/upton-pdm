using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Upton.Pdm.SolidWorks;

internal sealed partial class PdmApiClient
{
    public Task<StandardStructureViewDto> GetStandardStructuresAsync(CancellationToken ct) =>
        GetJsonAsync<StandardStructureViewDto>("api/standard-structures", ct);
    public Task<StandardStructureViewDto> GetStandardStructureCapabilitiesAsync(CancellationToken ct) =>
        GetJsonAsync<StandardStructureViewDto>("api/standard-structures/capabilities", ct);

    public async Task<StandardStructureDto> ArchiveStandardStructureAsync(object manifest, IDictionary<string, string> filePaths, CancellationToken ct)
    {
        using (var form = new MultipartFormDataContent())
        {
            form.Add(new StringContent(serializer.Serialize(manifest), Encoding.UTF8, "application/json"), "manifest");
            foreach (var file in filePaths)
                form.Add(new StreamContent(new FileStream(file.Value, FileMode.Open, FileAccess.Read, FileShare.Read)), file.Key, Path.GetFileName(file.Value));
            using (var response = await httpClient.PostAsync("api/standard-structures/archive", form, ct).ConfigureAwait(false))
            {
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    ThrowAuthenticationExpiredIfNeeded(response);
                    ThrowServiceUnavailableIfNeeded(response);
                    throw new InvalidOperationException(GetErrorMessage(response.StatusCode.ToString(), body));
                }
                return serializer.Deserialize<StandardStructureDto>(body);
            }
        }
    }
}
internal sealed class StandardStructureViewDto
{
    public StandardStructureCatalogDto Catalog { get; set; }
    public bool CanArchive { get; set; }
}
internal sealed class StandardStructureCatalogDto
{
    public List<StandardStructureCategoryDto> Categories { get; set; } = new List<StandardStructureCategoryDto>();
    public List<StandardStructureDto> Structures { get; set; } = new List<StandardStructureDto>();
    public long RowVersion { get; set; }
}
internal sealed class StandardStructureCategoryDto
{
    public Guid Id { get; set; }
    public Guid? ParentId { get; set; }
    public string Name { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; }
}
internal sealed class StandardStructureDto
{
    public Guid Id { get; set; }
    public Guid CategoryId { get; set; }
    public string Code { get; set; }
    public string Name { get; set; }
    public int DrawingType { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime? DeletedAt { get; set; }
    public List<StandardStructureRevisionDto> Revisions { get; set; } = new List<StandardStructureRevisionDto>();
}
internal sealed class StandardStructureRevisionDto
{
    public Guid Id { get; set; }
    public int Version { get; set; }
    public StandardStructureNodeDto Root { get; set; }
    public List<StandardStructureFileDto> Files { get; set; } = new List<StandardStructureFileDto>();
}
internal sealed class StandardStructureFileDto
{
    public string Key { get; set; }
    public string FileName { get; set; }
    public long Length { get; set; }
    public string Sha256 { get; set; }
}
internal sealed class StandardStructureNodeDto
{
    public string Id { get; set; }
    public string DrawingNumber { get; set; }
    public string Name { get; set; }
    public string FileName { get; set; }
    public string Kind { get; set; }
    public string FileKey { get; set; }
    public string PreviewFileKey { get; set; }
    public bool Embedded { get; set; }
    public decimal Quantity { get; set; }
    public List<StandardStructureNodeDto> Children { get; set; } = new List<StandardStructureNodeDto>();
}
internal sealed class StandardStructureBomDto
{
    public string Id { get; set; }
    public string Kind { get; set; }
    public string DrawingNumber { get; set; }
    public string Name { get; set; }
    public decimal Quantity { get; set; }
    public string Unit { get; set; }
    public string Revision { get; set; }
    public string Material { get; set; }
    public string Specification { get; set; }
    public string Brand { get; set; }
    public string SurfaceTreatment { get; set; }
    public string HeatTreatment { get; set; }
    public string Remark { get; set; }
}
