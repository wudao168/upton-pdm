using System.Net;
using System.Text;
using System.Text.Json;
using Upton.Pdm.Application;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class U9OpenApiClientTests
{
    [Fact]
    public async Task Authenticate_UsesOneStepOAuthAndReturnsDataToken()
    {
        var handler = new RecordingHandler("""{"ResCode":0,"Data":"token-123"}""");
        var client = new U9OpenApiClient(new HttpClient(handler));

        var result = await client.AuthenticateAsync(new(
            "http://u9.example.test/U9", "01", "7", "00004", "PDM", "secret-value"), default);

        Assert.Equal("token-123", result.Token);
        Assert.Equal(HttpMethod.Get, handler.RequestMethod);
        Assert.Contains("/U9/webapi/OAuth2/AuthLogin", handler.RequestUri);
        Assert.Contains("userCode=00004", handler.RequestUri);
        Assert.Contains("entcode=01", handler.RequestUri);
        Assert.Contains("orgcode=7", handler.RequestUri);
        Assert.Contains("clientid=PDM", handler.RequestUri);
    }

    [Fact]
    public async Task PostBatch_SendsTokenHeaderAndParsesSerializedPerRowResults()
    {
        var rows = JsonSerializer.Serialize<object[]>(
        [
            new { m_isSucess = true, ItemID = "1001", ItemCode = "EL-001" },
            new { m_isSucess = false, m_errorMsg = "单位不存在" }
        ]);
        var response = JsonSerializer.Serialize(new { ResCode = 0, Data = rows });
        var handler = new RecordingHandler(response);
        var client = new U9OpenApiClient(new HttpClient(handler));

        var result = await client.PostBatchAsync(
            "http://u9.example.test/U9", "/webapi/ItemMaster/Create", "token-123", "[{\"Code\":\"EL-001\"}]", default);

        Assert.Equal(0, result.ResponseCode);
        Assert.Equal("token-123", handler.Token);
        Assert.Equal(HttpMethod.Post, handler.RequestMethod);
        Assert.Collection(result.Rows,
            row => Assert.Equal((true, "1001", "EL-001"), (row.IsSuccess, row.U9ItemId, row.U9ItemCode)),
            row => Assert.Equal((false, "单位不存在"), (row.IsSuccess, row.ErrorMessage)));
    }

    [Fact]
    public async Task Authenticate_DoesNotExposeSecretWhenU9RejectsTheRequest()
    {
        var handler = new RecordingHandler("""{"ResCode":503,"ResMsg":"登录失败"}""");
        var client = new U9OpenApiClient(new HttpClient(handler));

        var exception = await Assert.ThrowsAsync<PdmRuleException>(() => client.AuthenticateAsync(new(
            "http://u9.example.test/U9", "01", "7", "00004", "PDM", "never-log-this"), default));

        Assert.Contains("ResCode=503", exception.Message);
        Assert.DoesNotContain("never-log-this", exception.Message);
    }

    [Fact]
    public async Task QueryItems_ParsesOfficialItemMasterDtoFields()
    {
        var rows = JsonSerializer.Serialize<object[]>(
        [
            new
            {
                m_iD = 1001L, m_code = "010100001", m_name = "气缸", m_sPECS = "CDQ2B32-100",
                MainItemCategory = new { Code = "0101", Name = "电气外购件" },
                InventoryUOM = new { Code = "001", Name = "个" },
                TradeMark = new { Code = "OMRON", Name = "欧姆龙" },
                Description = "测试备注",
                Weight = 0.15m,
                WeightUom = new { Code = "KG", Name = "千克" },
                DescFlexField = new { PubDescSeg1 = "https://shop.example.test/item/010100001" },
                ItemFormAttribute = 9
            }
        ]);
        var handler = new RecordingHandler(JsonSerializer.Serialize(new { ResCode = 0, Data = rows }));
        var client = new U9OpenApiClient(new HttpClient(handler));

        var result = await client.QueryItemsAsync(
            "http://u9.example.test/U9",
            U9MaterialContract.QueryPath,
            "token-123",
            "[{\"ItemMaster\":{\"Code\":\"010100001\"}}]",
            default);

        Assert.Equal(0, result.ResponseCode);
        var item = Assert.Single(result.Items);
        Assert.Equal(("1001", "010100001"), (item.U9ItemId, item.U9ItemCode));
        Assert.Equal(("气缸", "CDQ2B32-100"), (item.U9ItemName, item.U9Specification));
        Assert.Equal(("0101", "电气外购件", "001", 9),
            (item.U9CategoryCode, item.U9CategoryName, item.U9UnitCode, item.U9ItemFormAttribute));
        Assert.Equal("欧姆龙", item.U9Brand);
        Assert.Equal("测试备注", item.U9Description);
        Assert.Equal((0.15m, "KG", "https://shop.example.test/item/010100001"),
            (item.U9Weight, item.U9WeightUnitCode, item.U9PurchaseLink));
        Assert.Equal("token-123", handler.Token);
        Assert.Contains(U9MaterialContract.QueryPath, handler.RequestUri);
    }

    [Fact]
    public async Task QueryItems_ParsesU9InternalNestedEntityNames()
    {
        var rows = JsonSerializer.Serialize<object[]>(
        [
            new
            {
                m_iD = 1002L, m_code = "01010000002", m_name = "按钮",
                m_mainItemCategory = new { m_code = "0101", m_name = "电气外购件" },
                m_inventoryUOM = new { m_code = "001", m_name = "个" },
                m_weight = 1.25m,
                m_weightUom = new { m_code = "KG" },
                m_descFlexField = new
                {
                    m_pubDescSeg1 = "https://shop.example.test/item/01010000002",
                    m_pubDescSeg3 = "施耐德",
                    m_privateDescSeg2 = "铝合金",
                    m_privateDescSeg3 = "阳极氧化"
                }
            }
        ]);
        var handler = new RecordingHandler(JsonSerializer.Serialize(new { ResCode = 0, Data = rows }));
        var client = new U9OpenApiClient(new HttpClient(handler));

        var result = await client.QueryItemsAsync(
            "http://u9.example.test/U9",
            U9MaterialContract.QueryPath,
            "token-123",
            "[{}]",
            default);

        var item = Assert.Single(result.Items);
        Assert.Equal(("0101", "电气外购件", "001"),
            (item.U9CategoryCode, item.U9CategoryName, item.U9UnitCode));
        Assert.Equal("施耐德", item.U9Brand);
        Assert.Equal(("铝合金", "阳极氧化", 1.25m, "KG"),
            (item.U9Material, item.U9SurfaceTreatment, item.U9Weight, item.U9WeightUnitCode));
        Assert.Equal("https://shop.example.test/item/01010000002", item.U9PurchaseLink);
    }

    [Fact]
    public async Task QueryCustomerReferences_ParsesCodeAndNameFromNestedReferenceData()
    {
        var response = JsonSerializer.Serialize(new
        {
            ResCode = 0,
            Data = new
            {
                Data = new object[]
                {
                    new { Code = "C00001", Name = "客户甲" },
                    new { Code = "C00002", Name = "客户乙" },
                    new { Code = "C00002", Name = "重复客户" },
                    new { Code = "C00003" }
                }
            }
        });
        var handler = new RecordingHandler(response);
        var client = new U9OpenApiClient(new HttpClient(handler));

        var result = await client.QueryCustomerReferencesAsync(
            "http://u9.example.test/U9",
            U9MaterialContract.CustomerReferencePath,
            "token-123",
            "{\"ReferenceCode\":\"Customer\",\"PageIndex\":0,\"PageSize\":1000}",
            default);

        Assert.Equal(0, result.ResponseCode);
        Assert.Equal(4, result.RawCount);
        Assert.Collection(result.Customers,
            customer => Assert.Equal(("C00001", "客户甲"), (customer.Code, customer.Name)),
            customer => Assert.Equal(("C00002", "客户乙"), (customer.Code, customer.Name)));
        Assert.Equal("token-123", handler.Token);
        Assert.Contains(U9MaterialContract.CustomerReferencePath, handler.RequestUri);
    }

    [Fact]
    public async Task QueryBoms_ParsesOfficialPrefixedMasterAndComponentFields()
    {
        var response = JsonSerializer.Serialize(new
        {
            ResCode = 0,
            Data = new object[]
            {
                new
                {
                    m_itemMaster = new { m_iD = 1001L, m_code = "03010000001", m_name = "测试设备" },
                    m_bOMVersionCode = "V1",
                    m_org = new { m_code = "7", m_name = "昆山工厂" },
                    m_productUOM = new { m_code = "001", m_name = "个" },
                    m_lot = 1,
                    m_status = 2,
                    m_bOMType = 1,
                    m_bOMSort = 0,
                    m_projectMapNum = "ASM-001",
                    m_explain = "测试BOM",
                    m_bOMComponents = new object[]
                    {
                        new
                        {
                            m_sequence = 10,
                            m_itemMaster = new { m_iD = 2001L, m_code = "01020000057", m_name = "阀岛" },
                            m_itemVersionCode = "10",
                            m_usageQty = 2.5m,
                            m_issueUOM = new { m_code = "001", m_name = "个" },
                            m_parentQty = 1m,
                            m_isEffective = true,
                            m_remark = "装配备注",
                            m_isPhantomPart = false
                        }
                    }
                }
            }
        });
        var handler = new RecordingHandler(response);
        var client = new U9OpenApiClient(new HttpClient(handler));

        var result = await client.QueryBomsAsync(
            "http://u9.example.test/U9", U9BomContract.QueryPath, "token-123",
            "[{\"Org\":{\"Code\":\"7\"},\"ItemMaster\":{\"Code\":\"03010000001\"}}]", default);

        Assert.Equal(0, result.ResponseCode);
        var bom = Assert.Single(result.Boms);
        Assert.Equal(("03010000001", "测试设备", "V1", "7", "001", 1),
            (bom.ItemCode, bom.ItemName, bom.BomVersionCode, bom.OrganizationCode, bom.ProductUomCode, bom.Lot));
        Assert.Equal((2, 1, 0, "ASM-001", "测试BOM"),
            (bom.Status, bom.BomType, bom.BomSort, bom.ProjectMapNum, bom.Explain));
        var component = Assert.Single(bom.Components);
        Assert.Equal((10, "01020000057", "阀岛", "10", 2.5m, "001", 1m, true, "装配备注", false),
            (component.Sequence, component.ItemCode, component.ItemName, component.ItemVersionCode,
                component.UsageQty, component.IssueUomCode, component.ParentQty, component.IsEffective,
                component.Remark, component.IsPhantomPart));
        Assert.Equal("token-123", handler.Token);
        Assert.Contains(U9BomContract.QueryPath, handler.RequestUri);
    }

    [Fact]
    public async Task QueryBomOperation_ParsesNestedSerializedBipPageRecord()
    {
        const string otherId = "pdm-bom-1234567890abcdef12345678";
        var records = JsonSerializer.Serialize(new
        {
            RecordList = new object[]
            {
                new { m_iD = 9876543210L, m_sysVersion = 12L, m_otherID = otherId }
            }
        });
        var handler = new RecordingHandler(JsonSerializer.Serialize(new { ResCode = 0, Data = records }));
        var client = new U9OpenApiClient(new HttpClient(handler));

        var result = await client.QueryBomOperationAsync(
            "http://u9.example.test/U9", U9BomContract.BipQueryPagePath, "token-123", "7",
            "03010000001", "A9", otherId, default);

        Assert.NotNull(result);
        Assert.Equal((9876543210L, 12L, otherId), (result.Id, result.CurrentSysVersion, result.OtherId));
        Assert.Equal("token-123", handler.Token);
        Assert.Contains(U9BomContract.BipQueryPagePath, handler.RequestUri);
    }

    [Fact]
    public async Task QueryBomOperation_AcceptsSingleFilteredRecordWhenU9OmitsOtherId()
    {
        const string otherId = "pdm-bom-1234567890abcdef12345678";
        var response = JsonSerializer.Serialize(new
        {
            ResCode = 0,
            Data = new
            {
                RecordCount = 1,
                RecordList = new object[]
                {
                    new
                    {
                        ID = 321L,
                        CurrentSysVersion = 8L,
                        BOMVersionCode = "A9",
                        ItemMaster = new { Code = "03010000001" }
                    }
                }
            }
        });
        var handler = new RecordingHandler(response);
        var client = new U9OpenApiClient(new HttpClient(handler));

        var result = await client.QueryBomOperationAsync(
            "http://u9.example.test/U9", U9BomContract.BipQueryPagePath, "token-123", "7",
            "03010000001", "A9", otherId, default);

        Assert.NotNull(result);
        Assert.Equal((321L, 8L, otherId), (result.Id, result.CurrentSysVersion, result.OtherId));
    }

    private sealed class RecordingHandler(string responseJson) : HttpMessageHandler
    {
        public HttpMethod? RequestMethod { get; private set; }
        public string RequestUri { get; private set; } = string.Empty;
        public string? Token { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestMethod = request.Method;
            RequestUri = request.RequestUri?.ToString() ?? string.Empty;
            Token = request.Headers.TryGetValues("token", out var values) ? values.SingleOrDefault() : null;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            });
        }
    }
}
