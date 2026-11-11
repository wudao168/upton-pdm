using System.Text.Json;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

internal static class ApprovalGroupJson
{
    public static string? Write(ApprovalGroup? group) => group is null ? null : JsonSerializer.Serialize(group);
    public static ApprovalGroup? Read(string? json) => json is null ? null : JsonSerializer.Deserialize<ApprovalGroup>(json);
}
