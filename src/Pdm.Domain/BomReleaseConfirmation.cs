using System.Security.Cryptography;
using System.Text.Json;

namespace Upton.Pdm.Domain;

public sealed record BomReleaseConfirmation(string Token, string Actor, DateTimeOffset ConfirmedAt, string Reason);

public static class BomReleaseReview
{
    public static string Token(BomItem item, IReadOnlyDictionary<Guid, Guid> versions) => Convert.ToHexString(
        SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            item.Id, item.Kind, item.DrawingNumber, item.Name, item.Quantity, item.Unit,
            item.Material, item.Specification, item.Revision, item.Remark, item.Brand,
            item.SurfaceTreatment, item.HeatTreatment, item.Weight, item.IsWearPart, item.ImpactStage,
            item.SourceDocumentId, item.SourceConfiguration, item.SourceInstancePath,
            item.ParentDrawingNumber, Versions = versions.OrderBy(pair => pair.Key).ToArray()
        })));
}
