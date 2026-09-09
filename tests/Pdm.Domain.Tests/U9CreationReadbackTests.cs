using System.Net;
using System.Text;
using System.Text.Json;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class U9CreationReadbackTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Item_ReadsActualExpandFlagThroughScopedSupplement(bool value)
    {
        var handler = new Responses(
            """{"ResCode":0,"Data":[{"m_iD":101,"m_code":"TEST","m_mfgInfo":{"m_designationRule":1}}]}""",
            JsonSerializer.Serialize(new { ResCode = 0, Data = new[] { new { ItemId = 101, IsExpandByOrder = value } } }));
        var result = await new U9OpenApiClient(new HttpClient(handler)).QueryItemsAsync(
            "http://u9.test", U9MaterialContract.QueryPath, "test-token", "[]", default);
        Assert.Equal(value.ToString().ToLowerInvariant(), Assert.Single(result.Items).CreationAttributes["MfgInfo.IsExpandByOrder"]);
        Assert.Contains("i.ID IN (101)", handler.LastBody);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Item_FailedSupplementDoesNotConfirmSuccess()
    {
        var handler = new Responses(
            """{"ResCode":0,"Data":[{"m_iD":101,"m_mfgInfo":{"m_designationRule":1}}]}""",
            """{"ResCode":1,"Data":[]}""");
        await Assert.ThrowsAsync<PdmRuleException>(() => new U9OpenApiClient(new HttpClient(handler)).QueryItemsAsync(
            "http://u9.test", U9MaterialContract.QueryPath, "test-token", "[]", default));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Bom_ReadsActualFixedOrganizationFlag(bool value)
    {
        var handler = new Responses(
            """{"ResCode":0,"Data":[{"m_itemMaster":{"m_iD":101,"m_code":"TEST"},"m_bOMVersionCode":"A1","m_lot":1,"m_bOMComponents":[{"m_sequence":10,"m_issueOrg":{"m_code":"7"}}]}]}""",
            JsonSerializer.Serialize(new { ResCode = 0, Data = new[] { new { ItemId = 101, BOMVersionCode = "A1", Lot = 1, Sequence = 10, IsIssueOrgFixed = value } } }));
        var result = await new U9OpenApiClient(new HttpClient(handler)).QueryBomsAsync(
            "http://u9.test", U9BomContract.QueryPath, "test-token", "[]", default);
        var component = Assert.Single(Assert.Single(result.Boms).Components);
        Assert.Equal(value, component.IsIssueOrgFixed);
        Assert.Equal("7", component.IssueOrgCode);
        Assert.Contains("b.ItemMaster IN (101)", handler.LastBody);
    }

    private sealed class Responses(params string[] responses) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string LastBody { get; private set; } = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Contains("Query", request.RequestUri!.AbsolutePath);
            Assert.Equal("test-token", request.Headers.GetValues("token").Single());
            LastBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new(HttpStatusCode.OK) { Content = new StringContent(responses[Calls++], Encoding.UTF8, "application/json") };
        }
    }
}
