using System.Text.Json;
using Upton.Pdm.Api;
using Upton.Pdm.Domain;

namespace Pdm.Api.Tests;

public sealed class ValidationAttachmentContractTests
{
    [Theory]
    [InlineData("\"PlanDocument\"", ValidationPlanAttachmentKind.PlanDocument)]
    [InlineData("\"Evidence\"", ValidationPlanAttachmentKind.Evidence)]
    [InlineData("0", ValidationPlanAttachmentKind.PlanDocument)]
    [InlineData("1", ValidationPlanAttachmentKind.Evidence)]
    public void UploadRequests_AcceptBrowserKindsAndLegacyNumbers(string kind, ValidationPlanAttachmentKind expected)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var start = JsonSerializer.Deserialize<StartValidationPlanAttachmentUploadRequest>("{\"kind\":" + kind + ",\"fileName\":\"report.pdf\",\"totalLength\":1,\"sha256\":\"hash\"}", options)!;
        var complete = JsonSerializer.Deserialize<CompleteValidationPlanAttachmentUploadRequest>("{\"kind\":" + kind + "}", options)!;
        Assert.Equal(expected, start.Kind);
        Assert.Equal(expected, complete.Kind);
    }
}
