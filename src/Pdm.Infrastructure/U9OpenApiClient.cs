using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed class U9OpenApiClient(HttpClient httpClient) : IU9OpenApiClient, IU9InventoryClient, IU9ProcurementClient, IU9BomQueryClient
{
    public async Task<U9AuthenticationResult> AuthenticateAsync(
        U9AuthenticationRequest request,
        CancellationToken cancellationToken)
    {
        var endpoint = BuildEndpoint(request.BaseUrl, "/webapi/OAuth2/AuthLogin");
        var url = QueryHelpers.AddQueryString(endpoint.ToString(), new Dictionary<string, string?>
        {
            ["userCode"] = Required(request.UserCode, "用户编码"),
            ["entcode"] = Required(request.EnterpriseCode, "企业编码"),
            ["orgcode"] = Required(request.OrganizationCode, "组织编码"),
            ["clientid"] = Required(request.ClientId, "应用ID"),
            ["clientsecret"] = Required(request.ClientSecret, "应用密钥")
        });

        using var authenticationRequest = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await SendAsync(authenticationRequest, "U9C认证", cancellationToken);
        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new PdmRuleException($"U9C认证请求失败：HTTP {(int)response.StatusCode}。");

        using var document = ParseJson(responseJson, "U9C认证响应不是有效JSON。");
        var root = document.RootElement;
        var responseCode = ReadInt(root, "ResCode") ?? throw new PdmRuleException("U9C认证响应缺少ResCode。");
        if (responseCode != 0)
            throw new PdmRuleException($"U9C认证失败（ResCode={responseCode}）：{ReadMessage(root) ?? "未返回错误说明"}。");

        var token = ReadToken(root);
        if (string.IsNullOrWhiteSpace(token)) throw new PdmRuleException("U9C认证成功但未返回Token。");
        return new U9AuthenticationResult(token);
    }

    public async Task<U9InventoryQueryResult> QueryInventoryAsync(
        string baseUrl,
        string path,
        string token,
        string organizationCode,
        string? materialCode,
        CancellationToken cancellationToken)
    {
        var endpoint = BuildEndpoint(baseUrl, path);
        var payload = new Dictionary<string, object?>
        {
            ["Org"] = Required(organizationCode, "组织编码"),
            ["ItemCode"] = string.IsNullOrWhiteSpace(materialCode) ? null : materialCode.Trim()
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload.Where(pair => pair.Value is not null).ToDictionary(pair => pair.Key, pair => pair.Value)),
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.TryAddWithoutValidation("token", Required(token, "U9C Token"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await SendAsync(request, "U9C库存查询", cancellationToken);
        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new PdmRuleException($"U9C库存查询请求失败：HTTP {(int)response.StatusCode}。");

        using var document = ParseJson(responseJson, "U9C库存查询响应不是有效JSON。");
        var root = document.RootElement;
        var responseCode = ReadInt(root, "ResCode") ?? throw new PdmRuleException("U9C库存查询响应缺少ResCode。");
        var success = ReadBool(root, "Success") ?? responseCode == 0;
        var rows = ReadInventoryRows(root, organizationCode, includeZeroStock: !string.IsNullOrWhiteSpace(materialCode));
        return new U9InventoryQueryResult(responseCode, success, ReadMessage(root), rows);
    }

    public async Task<U9ProcurementQueryResult> QueryProcurementAsync(
        string baseUrl,
        string path,
        string token,
        string organizationCode,
        IReadOnlyCollection<string> projectCodes,
        CancellationToken cancellationToken)
    {
        var normalizedCodes = projectCodes
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedCodes.Length == 0) return new(0, true, null, []);

        var endpoint = BuildEndpoint(baseUrl, path);
        var organization = EscapeSqlLiteral(Required(organizationCode, "组织编码"));
        var codeList = string.Join(",", normalizedCodes.Select(code => $"'{EscapeSqlLiteral(code)}'"));
        var sql = $$"""
            SELECT
                'PR' AS RecordKind,
                CAST(line.ID AS VARCHAR(80)) AS LineId,
                CAST(line.ID AS VARCHAR(80)) AS SourcePrLineId,
                org.Code AS OrganizationCode,
                pr.DocNo AS DocumentNumber,
                line.DocLineNo AS LineNumber,
                line.Status AS LineStatus,
                line.Cancel_Canceled AS IsCanceled,
                pr.BusinessDate AS BusinessDate,
                line.ItemInfo_ItemCode AS MaterialCode,
                line.ItemInfo_ItemName AS ItemName,
                item.SPECS AS Specification,
                item.DescFlexField_PubDescSeg3 AS Brand,
                project.Code AS ProjectCode,
                project.ShortName AS ProjectName,
                line.SeiBanCode AS Subproject,
                line.ReqQtyReqUOM AS RequestedQuantity,
                line.ApprovedQtyReqUOM AS ApprovedQuantity,
                CAST(0 AS DECIMAL(24,9)) AS PurchaseQuantity,
                CAST(0 AS DECIMAL(24,9)) AS ArrivedQuantity,
                CAST(NULL AS NVARCHAR(1000)) AS PurchaseRemark,
                line.DeliveryDate AS DeliveryDate,
                CAST(NULL AS DATETIME) AS LatestDeliveryDate
            FROM PR_PR pr
            INNER JOIN PR_PRLine line ON line.PR=pr.ID
            INNER JOIN Base_Organization org ON org.ID=pr.Org
            LEFT JOIN CBO_ItemMaster item ON item.ID=line.ItemInfo_ItemID
            LEFT JOIN CBO_Project project ON project.ID=line.Project
            WHERE org.Code='{{organization}}'
              AND (project.Code IN ({{codeList}}) OR line.SeiBanCode IN ({{codeList}}))

            UNION ALL

            SELECT
                'PO' AS RecordKind,
                CAST(pol.ID AS VARCHAR(80)) AS LineId,
                CAST(pol.SrcDocInfo_SrcDocLine_EntityID AS VARCHAR(80)) AS SourcePrLineId,
                org.Code AS OrganizationCode,
                po.DocNo AS DocumentNumber,
                pol.DocLineNo AS LineNumber,
                pol.Status AS LineStatus,
                pol.Cancel_Canceled AS IsCanceled,
                po.BusinessDate AS BusinessDate,
                pol.ItemInfo_ItemCode AS MaterialCode,
                pol.ItemInfo_ItemName AS ItemName,
                item.SPECS AS Specification,
                item.DescFlexField_PubDescSeg3 AS Brand,
                project.Code AS ProjectCode,
                project.ShortName AS ProjectName,
                pol.SeiBanCode AS Subproject,
                CAST(0 AS DECIMAL(24,9)) AS RequestedQuantity,
                CAST(0 AS DECIMAL(24,9)) AS ApprovedQuantity,
                pol.PurQtyTU AS PurchaseQuantity,
                pol.TotalRecievedQtyTU AS ArrivedQuantity,
                po.PurAdviceMemo AS PurchaseRemark,
                ship.DeliveryDate AS DeliveryDate,
                ship.LatestDeliveryDate AS LatestDeliveryDate
            FROM PM_PurchaseOrder po
            INNER JOIN PM_POLine pol ON pol.PurchaseOrder=po.ID
            INNER JOIN PM_PODocType documentType ON documentType.ID=po.DocumentType
            INNER JOIN Base_Organization org ON org.ID=po.Org
            LEFT JOIN CBO_ItemMaster item ON item.ID=pol.ItemInfo_ItemID
            LEFT JOIN CBO_Project project ON project.ID=pol.Project
            LEFT JOIN (
                SELECT POLine,MAX(DeliveryDate) AS DeliveryDate,MAX(COALESCE(PlanArriveDate,DeliveryDate)) AS LatestDeliveryDate
                FROM PM_POShipLine
                GROUP BY POLine
            ) ship ON ship.POLine=pol.ID
            WHERE org.Code='{{organization}}'
              AND documentType.ShortName='PO01'
              AND (
                    project.Code IN ({{codeList}})
                    OR pol.SeiBanCode IN ({{codeList}})
                    OR pol.SrcDocInfo_SrcDocLine_EntityID IN (
                        SELECT sourceLine.ID
                        FROM PR_PR source
                        INNER JOIN PR_PRLine sourceLine ON sourceLine.PR=source.ID
                        LEFT JOIN CBO_Project sourceProject ON sourceProject.ID=sourceLine.Project
                        WHERE sourceProject.Code IN ({{codeList}}) OR sourceLine.SeiBanCode IN ({{codeList}})
                    )
                  )
            """;
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { SqlString = sql }), Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("token", Required(token, "U9C Token"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await SendAsync(request, "U9C采购跟踪查询", cancellationToken);
        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new PdmRuleException($"U9C采购跟踪查询请求失败：HTTP {(int)response.StatusCode}。");
        using var document = ParseJson(responseJson, "U9C采购跟踪查询响应不是有效JSON。");
        var root = document.RootElement;
        var responseCode = ReadInt(root, "ResCode") ?? throw new PdmRuleException("U9C采购跟踪查询响应缺少ResCode。");
        var success = ReadBool(root, "Success") ?? responseCode == 0;
        var rows = success ? ReadProcurementRows(root, organizationCode) : [];
        return new U9ProcurementQueryResult(responseCode, success, ReadMessage(root), rows);
    }

    public async Task<U9BusinessBatchResult> PostBatchAsync(
        string baseUrl,
        string path,
        string token,
        string payloadJson,
        CancellationToken cancellationToken)
    {
        var endpoint = BuildEndpoint(baseUrl, path);
        using var payload = ParseJson(payloadJson, "U9C业务请求不是有效JSON。");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payload.RootElement.GetRawText(), Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("token", Required(token, "U9C Token"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await SendAsync(request, "U9C业务", cancellationToken);
        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new PdmRuleException($"U9C业务请求失败：HTTP {(int)response.StatusCode}。");

        using var document = ParseJson(responseJson, "U9C业务响应不是有效JSON。");
        var root = document.RootElement;
        var responseCode = ReadInt(root, "ResCode") ?? throw new PdmRuleException("U9C业务响应缺少ResCode。");
        var message = ReadMessage(root);
        var rows = ReadRows(root);
        return new U9BusinessBatchResult(responseCode, message, rows);
    }

    public async Task<U9ItemQueryResult> QueryItemsAsync(
        string baseUrl,
        string path,
        string token,
        string payloadJson,
        CancellationToken cancellationToken)
    {
        var endpoint = BuildEndpoint(baseUrl, path);
        using var payload = ParseJson(payloadJson, "U9C料品查询请求不是有效JSON。");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payload.RootElement.GetRawText(), Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("token", Required(token, "U9C Token"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await SendAsync(request, "U9C料品查询", cancellationToken);
        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new PdmRuleException($"U9C料品查询请求失败：HTTP {(int)response.StatusCode}。");

        using var document = ParseJson(responseJson, "U9C料品查询响应不是有效JSON。");
        var root = document.RootElement;
        var responseCode = ReadInt(root, "ResCode") ?? throw new PdmRuleException("U9C料品查询响应缺少ResCode。");
        var items = ReadDataRows(root)
            .Select(row => new U9ItemReference(
                ReadString(row, "ItemID", "ItemId", "ID", "m_iD"),
                ReadString(row, "ItemCode", "Code", "m_code"),
                ReadString(row, "ItemName", "Name", "m_name"),
                ReadString(row, "SPECS", "Specs", "Specification", "m_sPECS", "m_specs"),
                ReadEntityString(row, "MainItemCategory", "Code"),
                ReadEntityString(row, "MainItemCategory", "Name"),
                ReadEntityString(row, "InventoryUOM", "Code"),
                ReadInt(row, "ItemFormAttribute", "m_itemFormAttribute"),
                ReadEntityString(row, "TradeMark", "Name")
                    ?? ReadEntityString(row, "DescFlexField", U9MaterialContract.BrandPublicSegment),
                ReadString(row, "Description", "m_description"),
                ReadScalarOrEntityName(row, "Material", "m_material")
                    ?? ReadEntityString(row, "DescFlexField", U9MaterialContract.MaterialPrivateSegment),
                ReadScalarOrEntityName(row, "SurfaceTreatment", "m_surfaceTreatment")
                    ?? ReadEntityString(row, "DescFlexField", U9MaterialContract.SurfaceTreatmentPrivateSegment),
                ReadDecimal(row, "Weight", "m_weight"),
                ReadEntityString(row, "WeightUom", "Code"),
                ReadEntityString(row, "DescFlexField", U9MaterialContract.PurchaseLinkPublicSegment))
                { CreationAttributes = U9MaterialCreationRules.ReadAttributes(row) })
            .Where(item => !string.IsNullOrWhiteSpace(item.U9ItemId) || !string.IsNullOrWhiteSpace(item.U9ItemCode))
            .ToArray();
        // The standard DTO omits IsExpandByOrder; query only IDs already returned by this authenticated lookup.
        var itemIds = items.Where(item => item.CreationAttributes.GetValueOrDefault("MfgInfo.DesignationRule") is not null)
            .Select(item => long.TryParse(item.U9ItemId, out var id) && id > 0 ? id : 0).Where(id => id > 0).Distinct().ToArray();
        if (responseCode == 0 && itemIds.Length > 0)
        {
            var flags = await QueryCreationFlagsAsync(baseUrl, token,
                $"SELECT i.ID AS ItemId,m.IsExpandByOrder FROM CBO_ItemMaster i JOIN CBO_MfgInfo m ON m.ID=i.MfgInfo WHERE i.ID IN ({string.Join(",", itemIds)})", cancellationToken);
            items = items.Select(item =>
            {
                var matches = flags.Where(row => ReadString(row, "ItemId") == item.U9ItemId).ToArray();
                var attributes = new Dictionary<string, string?>(item.CreationAttributes);
                attributes["MfgInfo.IsExpandByOrder"] = matches.Length == 1
                    ? ReadBool(matches[0], "IsExpandByOrder")?.ToString().ToLowerInvariant() : null;
                return item with { CreationAttributes = attributes };
            }).ToArray();
        }
        return new U9ItemQueryResult(responseCode, ReadMessage(root), items);
    }

    public async Task<U9UomQueryResult> QueryUomsAsync(
        string baseUrl,
        string token,
        string payloadJson,
        CancellationToken cancellationToken)
    {
        var endpoint = BuildEndpoint(baseUrl, U9MaterialContract.UomQueryPath);
        using var payload = ParseJson(payloadJson, "U9C计量单位查询请求不是有效JSON。");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payload.RootElement.GetRawText(), Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("token", Required(token, "U9C Token"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await SendAsync(request, "U9C计量单位查询", cancellationToken);
        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new PdmRuleException($"U9C计量单位查询请求失败：HTTP {(int)response.StatusCode}。");

        using var document = ParseJson(responseJson, "U9C计量单位查询响应不是有效JSON。");
        var root = document.RootElement;
        var responseCode = ReadInt(root, "ResCode") ?? throw new PdmRuleException("U9C计量单位查询响应缺少ResCode。");
        var units = ReadDataRows(root)
            .Select(row => new U9UomReference(
                ReadString(row, "UOMID", "UomID", "ID", "m_iD"),
                ReadString(row, "UOMCode", "Code", "m_code")))
            .Where(unit => !string.IsNullOrWhiteSpace(unit.U9UomId) || !string.IsNullOrWhiteSpace(unit.U9UomCode))
            .ToArray();
        return new U9UomQueryResult(responseCode, ReadMessage(root), units);
    }

    public async Task<U9CustomerQueryResult> QueryCustomerReferencesAsync(
        string baseUrl,
        string path,
        string token,
        string payloadJson,
        CancellationToken cancellationToken)
    {
        var endpoint = BuildEndpoint(baseUrl, path);
        using var payload = ParseJson(payloadJson, "U9C客户参照查询请求不是有效JSON。");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payload.RootElement.GetRawText(), Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("token", Required(token, "U9C Token"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await SendAsync(request, "U9C客户参照查询", cancellationToken);
        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new PdmRuleException($"U9C客户参照查询请求失败：HTTP {(int)response.StatusCode}。");

        using var document = ParseJson(responseJson, "U9C客户参照查询响应不是有效JSON。");
        var root = document.RootElement;
        var responseCode = ReadInt(root, "ResCode") ?? throw new PdmRuleException("U9C客户参照查询响应缺少ResCode。");
        var rows = ReadNestedDataRows(root);
        var customers = rows
            .Select(row => new U9CustomerReference(
                ReadString(row, "Code", "code") ?? string.Empty,
                ReadString(row, "Name", "name") ?? string.Empty))
            .Where(customer => !string.IsNullOrWhiteSpace(customer.Code) && !string.IsNullOrWhiteSpace(customer.Name))
            .DistinctBy(customer => customer.Code, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new U9CustomerQueryResult(responseCode, ReadMessage(root), customers, rows.Count);
    }

    public async Task<U9BomQueryResult> QueryBomsAsync(
        string baseUrl,
        string path,
        string token,
        string payloadJson,
        CancellationToken cancellationToken)
    {
        var endpoint = BuildEndpoint(baseUrl, path);
        using var payload = ParseJson(payloadJson, "U9C BOM查询请求不是有效JSON。");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payload.RootElement.GetRawText(), Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("token", Required(token, "U9C Token"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await SendAsync(request, "U9C BOM查询", cancellationToken);
        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new PdmRuleException($"U9C BOM查询请求失败：HTTP {(int)response.StatusCode}。");

        using var document = ParseJson(responseJson, "U9C BOM查询响应不是有效JSON。");
        var root = document.RootElement;
        var responseCode = ReadInt(root, "ResCode") ?? throw new PdmRuleException("U9C BOM查询响应缺少ResCode。");
        var boms = ReadDataRows(root).Select(ReadBom).ToArray();
        var motherIds = boms.Select(bom => long.TryParse(bom.ItemId, out var id) && id > 0 ? id : 0)
            .Where(id => id > 0).Distinct().ToArray();
        if (responseCode == 0 && motherIds.Length > 0 && boms.Any(bom => bom.Components.Count > 0))
        {
            var flags = await QueryCreationFlagsAsync(baseUrl, token,
                $"SELECT b.ItemMaster AS ItemId,b.BOMVersionCode,b.Lot,c.Sequence,c.IsIssueOrgFixed FROM CBO_BOMMaster b JOIN CBO_BOMComponent c ON c.BOMMaster=b.ID WHERE b.ItemMaster IN ({string.Join(",", motherIds)})", cancellationToken);
            boms = boms.Select(bom => bom with { Components = bom.Components.Select(component =>
            {
                var matches = flags.Where(row => ReadString(row, "ItemId") == bom.ItemId
                    && ReadString(row, "BOMVersionCode") == bom.BomVersionCode && ReadInt(row, "Lot") == bom.Lot
                    && ReadInt(row, "Sequence") == component.Sequence).ToArray();
                return component with { IsIssueOrgFixed = matches.Length == 1 ? ReadBool(matches[0], "IsIssueOrgFixed") : null };
            }).ToArray() }).ToArray();
        }
        return new U9BomQueryResult(responseCode, ReadMessage(root), boms);
    }

    private async Task<IReadOnlyList<JsonElement>> QueryCreationFlagsAsync(string baseUrl, string token, string sql, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildEndpoint(baseUrl, "/webapi/QueryCommon/QueryInfoBySql"))
        { Content = new StringContent(JsonSerializer.Serialize(new { SqlString = sql }), Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation("token", Required(token, "U9C Token"));
        using var response = await SendAsync(request, "U9C创建属性回查", cancellationToken);
        if (!response.IsSuccessStatusCode) throw new PdmRuleException($"U9C创建属性回查失败：HTTP {(int)response.StatusCode}。");
        using var document = ParseJson(await response.Content.ReadAsStringAsync(cancellationToken), "U9C创建属性回查不是有效JSON。");
        if (ReadInt(document.RootElement, "ResCode") != 0) throw new PdmRuleException("U9C创建属性回查失败，不能确认同步成功。");
        return ReadDataRows(document.RootElement).Select(row => row.Clone()).ToArray();
    }

    public async Task<U9BomOperationReference?> QueryBomOperationAsync(
        string baseUrl,
        string path,
        string token,
        string organizationCode,
        string itemCode,
        string bomVersionCode,
        string otherId,
        CancellationToken cancellationToken)
    {
        var endpoint = BuildEndpoint(baseUrl, path);
        var normalizedToken = Required(token, "U9C Token");
        var normalizedOrganizationCode = Required(organizationCode, "U9C组织编码");
        var normalizedItemCode = Required(itemCode, "BOM母件料号");
        var normalizedVersionCode = Required(bomVersionCode, "BOM版本");
        const int pageSize = 20;
        var pageIndex = 1;
        var pageCount = 1;

        do
        {
            var payloadJson = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["pageIndex"] = pageIndex,
                ["pageSize"] = pageSize,
                ["Orgcode"] = normalizedOrganizationCode,
                ["Condition"] = $"ItemMaster.Code = '{EscapeConditionValue(normalizedItemCode)}' and BOMVersionCode = '{EscapeConditionValue(normalizedVersionCode)}'"
            });
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(payloadJson, Encoding.UTF8, "application/json")
            };
            request.Headers.TryAddWithoutValidation("token", normalizedToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await SendAsync(request, "U9C BOM操作信息查询", cancellationToken);
            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new PdmRuleException($"U9C BOM操作信息查询失败：HTTP {(int)response.StatusCode}。");

            using var document = ParseJson(responseJson, "U9C BOM操作信息查询响应不是有效JSON。");
            var root = document.RootElement;
            var responseCode = ReadInt(root, "ResCode") ?? throw new PdmRuleException("U9C BOM操作信息查询响应缺少ResCode。");
            if (responseCode != 0)
                throw new PdmRuleException($"U9C BOM操作信息查询失败（ResCode={responseCode}）：{ReadMessage(root) ?? "未返回错误说明"}。");
            if (TryFindBomOperation(root, otherId, out var operation)) return operation;

            var candidates = new List<BomOperationCandidate>();
            CollectBomOperationCandidates(root, candidates);
            var matching = candidates.Distinct().Where(candidate =>
                    string.Equals(candidate.ItemCode, normalizedItemCode, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(candidate.BomVersionCode, normalizedVersionCode, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (matching.Length > 1)
                throw new PdmRuleException("U9C BOM操作信息查询返回多条相同母件和版本的记录，已停止后续操作。");
            if (matching.Length == 1)
                return new U9BomOperationReference(matching[0].Id, matching[0].CurrentSysVersion, otherId);
            var distinct = candidates.Distinct().ToArray();
            if (FindLongProperty(root, "RecordCount") == 1 && distinct.Length == 1)
                return new U9BomOperationReference(distinct[0].Id, distinct[0].CurrentSysVersion, otherId);

            pageCount = checked((int)(FindLongProperty(root, "PageCount") ?? 1));
            pageIndex++;
        } while (pageIndex <= pageCount && pageIndex <= 100);

        return null;
    }

    private static string EscapeConditionValue(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static Uri BuildEndpoint(string baseUrl, string path)
    {
        var normalizedBaseUrl = Required(baseUrl, "U9C地址").TrimEnd('/');
        if (!Uri.TryCreate(normalizedBaseUrl, UriKind.Absolute, out var baseUri) || baseUri.Scheme is not ("http" or "https"))
            throw new PdmRuleException("U9C地址必须是有效的HTTP或HTTPS地址。");
        var normalizedPath = Required(path, "U9C接口路径");
        if (Uri.TryCreate(normalizedPath, UriKind.Absolute, out _) || !normalizedPath.StartsWith("/webapi/", StringComparison.OrdinalIgnoreCase))
            throw new PdmRuleException("U9C接口路径必须是以/webapi/开头的相对路径。");
        return new Uri($"{normalizedBaseUrl}/{normalizedPath.TrimStart('/')}", UriKind.Absolute);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        string operation,
        CancellationToken cancellationToken)
    {
        try
        {
            return await httpClient.SendAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PdmRuleException($"{operation}请求超时。");
        }
        catch (HttpRequestException)
        {
            throw new PdmRuleException($"{operation}无法连接到已配置的U9C地址。");
        }
    }

    private static IReadOnlyList<U9BusinessRowResult> ReadRows(JsonElement root)
    {
        return ReadDataRows(root).Select(row => new U9BusinessRowResult(
            ReadBool(row, "IsSucess", "m_isSucess", "IsSucceed", "m_isSucceed") ?? false,
            ReadString(row, "ErrorMsg", "m_errorMsg", "ErrorMessage", "m_errorMessage"),
            ReadString(row, "ItemID", "ItemId", "ID", "m_iD"),
            ReadString(row, "ItemCode", "Code", "m_code"))).ToArray();
    }

    private static IReadOnlyList<JsonElement> ReadDataRows(JsonElement root)
    {
        if (!TryGet(root, "Data", out var data) || data.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return [];
        if (data.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(data.GetString()))
            return [];
        using var serialized = data.ValueKind == JsonValueKind.String
            ? ParseNestedJson(data.GetString() ?? "[]", "U9C业务响应Data不是有效JSON。")
            : JsonDocument.Parse(data.GetRawText());
        var value = serialized.RootElement;
        return value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(row => row.Clone()).ToArray()
            : [value.Clone()];
    }

    private static IReadOnlyList<U9InventorySourceRow> ReadInventoryRows(
        JsonElement root,
        string organizationCode,
        bool includeZeroStock)
    {
        if (!TryGet(root, "Data", out var data) || data.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return [];
        if (data.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(data.GetString())) return [];
        using var serialized = data.ValueKind == JsonValueKind.String
            ? ParseNestedJson(data.GetString() ?? "[]", "U9C库存响应Data不是有效JSON。")
            : JsonDocument.Parse(data.GetRawText());
        IEnumerable<JsonElement> values = serialized.RootElement.ValueKind == JsonValueKind.Array
            ? serialized.RootElement.EnumerateArray().ToArray()
            : [serialized.RootElement];
        var rows = new List<U9InventorySourceRow>();
        foreach (var row in values)
        {
            var materialCode = ReadString(row, "m_itemCode", "ItemCode");
            if (string.IsNullOrWhiteSpace(materialCode)) continue;
            var stockQuantity = ReadDecimal(row, "m_storeQty", "StoreQty") ?? 0m;
            if (!includeZeroStock && stockQuantity == 0m) continue;
            rows.Add(new(
                ReadString(row, "m_orgCode", "OrgCode") ?? organizationCode.Trim(),
                materialCode,
                ReadString(row, "m_itemName", "ItemName") ?? string.Empty,
                ReadString(row, "m_itemSPECS", "ItemSPECS", "SPECS"),
                ReadString(row, "m_whCode", "WhCode"),
                ReadString(row, "m_whName", "WhName") ?? ReadString(row, "m_whCode", "WhCode") ?? "—",
                ReadString(row, "m_binCode", "BinCode"),
                ReadString(row, "m_binName", "BinName"),
                ReadString(row, "m_storageType", "StorageType"),
                ReadString(row, "m_projectCode", "ProjectCode"),
                ReadString(row, "m_projectName", "ProjectName"),
                ReadString(row, "m_seiBanNo", "SeiBanNo", "Seiban"),
                stockQuantity,
                ReadDecimal(row, "m_canUseQty", "CanUseQty") ?? 0m,
                ReadDecimal(row, "m_reservQty", "ReservQty") ?? 0m,
                ReadDecimal(row, "m_notUseQty", "NotUseQty") ?? 0m));
        }
        return rows;
    }

    private static IReadOnlyList<U9ProcurementSourceRow> ReadProcurementRows(JsonElement root, string organizationCode)
    {
        return ReadDataRows(root)
            .Select(row => new U9ProcurementSourceRow(
                ReadString(row, "OrganizationCode") ?? organizationCode.Trim(),
                (ReadString(row, "RecordKind") ?? string.Empty).ToUpperInvariant(),
                ReadString(row, "LineId") ?? string.Empty,
                ReadString(row, "SourcePrLineId"),
                ReadString(row, "DocumentNumber") ?? string.Empty,
                ReadInt(row, "LineNumber") ?? 0,
                ReadInt(row, "LineStatus") ?? -1,
                ReadBool(row, "IsCanceled") ?? false,
                ReadDateTimeOffset(row, "BusinessDate"),
                ReadString(row, "MaterialCode") ?? string.Empty,
                ReadString(row, "ItemName") ?? string.Empty,
                ReadString(row, "Specification"),
                ReadString(row, "Brand"),
                ReadString(row, "ProjectCode"),
                ReadString(row, "ProjectName"),
                ReadString(row, "Subproject"),
                ReadDecimal(row, "RequestedQuantity") ?? 0m,
                ReadDecimal(row, "ApprovedQuantity") ?? 0m,
                ReadDecimal(row, "PurchaseQuantity") ?? 0m,
                ReadDecimal(row, "ArrivedQuantity") ?? 0m,
                ReadString(row, "PurchaseRemark"),
                ReadDateTimeOffset(row, "DeliveryDate"),
                ReadDateTimeOffset(row, "LatestDeliveryDate")))
            .Where(row => row.RecordKind is U9ProcurementRecordKinds.PurchaseRequisition or U9ProcurementRecordKinds.PurchaseOrder
                && !string.IsNullOrWhiteSpace(row.LineId)
                && !string.IsNullOrWhiteSpace(row.DocumentNumber)
                && !string.IsNullOrWhiteSpace(row.MaterialCode))
            .ToArray();
    }

    private static IReadOnlyList<JsonElement> ReadNestedDataRows(JsonElement root)
    {
        if (!TryGet(root, "Data", out var outerData) || outerData.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return [];
        if (outerData.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(outerData.GetString()))
            return [];
        using var outerDocument = outerData.ValueKind == JsonValueKind.String
            ? ParseNestedJson(outerData.GetString() ?? "{}", "U9C客户参照响应Data不是有效JSON。")
            : JsonDocument.Parse(outerData.GetRawText());
        var outer = outerDocument.RootElement;
        if (!TryGet(outer, "Data", out var rows) || rows.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return [];
        if (rows.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(rows.GetString()))
            return [];
        using var rowsDocument = rows.ValueKind == JsonValueKind.String
            ? ParseNestedJson(rows.GetString() ?? "[]", "U9C客户参照响应Data.Data不是有效JSON。")
            : JsonDocument.Parse(rows.GetRawText());
        var value = rowsDocument.RootElement;
        return value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(row => row.Clone()).ToArray()
            : value.ValueKind == JsonValueKind.Object ? [value.Clone()] : [];
    }

    private static U9BomReference ReadBom(JsonElement row) => new(
        ReadEntityString(row, "ItemMaster", "ID"),
        ReadEntityString(row, "ItemMaster", "Code"),
        ReadEntityString(row, "ItemMaster", "Name"),
        ReadString(row, "BOMVersionCode", "m_bOMVersionCode"),
        ReadEntityString(row, "Org", "Code"),
        ReadEntityString(row, "Org", "Name"),
        ReadInt(row, "AlternateType", "m_alternateType"),
        ReadInt(row, "Lot", "m_lot"),
        ReadEntityString(row, "ProductUOM", "Code"),
        ReadEntityString(row, "ProductUOM", "Name"),
        ReadDateTimeOffset(row, "EffectiveDate", "m_effectiveDate"),
        ReadDateTimeOffset(row, "DisableDate", "m_disableDate"),
        ReadInt(row, "Status", "m_status"),
        ReadInt(row, "BOMSort", "m_bOMSort"),
        ReadInt(row, "BOMType", "m_bOMType"),
        ReadString(row, "ProjectMapNum", "m_projectMapNum"),
        ReadString(row, "Explain", "m_explain"),
        ReadString(row, "ECOCode", "m_eCOCode"),
        ReadBool(row, "IsCostRoll", "m_isCostRoll"),
        ReadInt(row, "ItemSource", "m_itemSource"),
        ReadInt(row, "SysState", "sysState"),
        ReadArray(row, "BOMComponents", "m_bOMComponents").Select(ReadBomComponent).ToArray(),
        ReadString(row, "OtherID", "OtherId", "m_otherID", "m_otherId"));

    private static U9BomComponentReference ReadBomComponent(JsonElement row) => new(
        ReadInt(row, "Sequence", "m_sequence"),
        ReadEntityString(row, "ItemMaster", "ID"),
        ReadEntityString(row, "ItemMaster", "Code"),
        ReadEntityString(row, "ItemMaster", "Name"),
        ReadString(row, "ItemVersionCode", "m_itemVersionCode"),
        ReadDecimal(row, "UsageQty", "m_usageQty"),
        ReadEntityString(row, "IssueUOM", "Code"),
        ReadEntityString(row, "IssueUOM", "Name"),
        ReadDecimal(row, "ParentQty", "m_parentQty"),
        ReadInt(row, "ComponentType", "m_componentType"),
        ReadBool(row, "IsEffective", "m_isEffective"),
        ReadDateTimeOffset(row, "EffectiveDate", "m_effectiveDate"),
        ReadDateTimeOffset(row, "DisableDate", "m_disableDate"),
        ReadString(row, "Remark", "m_remark"),
        ReadString(row, "ProjectMapNum", "m_projectMapNum"),
        ReadInt(row, "IssueStyle", "m_issueStyle"),
        ReadInt(row, "SupplyStyle", "m_supplyStyle"),
        ReadBool(row, "IsPhantomPart", "m_isPhantomPart"),
        ReadBool(row, "IsDelete", "m_isDelete"))
        {
            UsageQtyType = ReadInt(row, "UsageQtyType", "m_usageQtyType"),
            IsSpecialUseItem = ReadBool(row, "IsSpecialUseItem", "m_isSpecialUseItem"),
            IsIssueOrgFixed = ReadBool(row, "IsIssueOrgFixed", "m_isIssueOrgFixed"),
            IssueOrgCode = ReadEntityString(row, "IssueOrg", "Code")
        };

    private static IReadOnlyList<JsonElement> ReadArray(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGet(element, name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) continue;
            using var document = value.ValueKind == JsonValueKind.String
                ? ParseJson(value.GetString() ?? "[]", $"U9C BOM响应字段{name}不是有效JSON。")
                : JsonDocument.Parse(value.GetRawText());
            return document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().Select(item => item.Clone()).ToArray()
                : [];
        }
        return [];
    }

    private static string? ReadToken(JsonElement root)
    {
        if (!TryGet(root, "Data", out var data)) return null;
        if (data.ValueKind == JsonValueKind.String) return data.GetString()?.Trim();
        return ReadString(data, "Token", "token", "access_token", "AccessToken");
    }

    private static int? ReadInt(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGet(element, name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
            if (int.TryParse(value.ToString(), out number)) return number;
        }
        return null;
    }

    private static long? ReadLong(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGet(element, name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
            if (long.TryParse(value.ToString(), out number)) return number;
        }
        return null;
    }

    private static bool TryFindBomOperation(
        JsonElement element,
        string expectedOtherId,
        out U9BomOperationReference? operation)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var otherId = ReadString(element, "OtherID", "OtherId", "m_otherID", "m_otherId");
            if (string.Equals(otherId, expectedOtherId, StringComparison.OrdinalIgnoreCase))
            {
                var id = ReadLong(element, "ID", "Id", "BomID", "m_iD", "m_id", "m_bomID");
                var currentSysVersion = ReadLong(element,
                    "CurrentSysVersion", "SysVersion", "m_currentSysVersion", "m_sysVersion");
                if (id is not null && currentSysVersion is not null)
                {
                    operation = new U9BomOperationReference(id.Value, currentSysVersion.Value, otherId!);
                    return true;
                }
            }

            foreach (var property in element.EnumerateObject())
            {
                if (TryFindBomOperation(property.Value, expectedOtherId, out operation)) return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (TryFindBomOperation(item, expectedOtherId, out operation)) return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.String)
        {
            var value = element.GetString()?.Trim();
            if (!string.IsNullOrWhiteSpace(value) && value[0] is '{' or '[')
            {
                try
                {
                    using var document = JsonDocument.Parse(value);
                    if (TryFindBomOperation(document.RootElement, expectedOtherId, out operation)) return true;
                }
                catch (JsonException)
                {
                    // Ordinary string fields are not query result containers.
                }
            }
        }

        operation = null;
        return false;
    }

    private static void CollectBomOperationCandidates(
        JsonElement element,
        ICollection<BomOperationCandidate> candidates)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var id = ReadLong(element, "ID", "Id", "BomID", "m_iD", "m_id", "m_bomID");
            var bomVersionCode = ReadString(element, "BOMVersionCode", "m_bOMVersionCode");
            if (id is not null && !string.IsNullOrWhiteSpace(bomVersionCode))
                candidates.Add(new(
                    id.Value,
                    ReadLong(element, "CurrentSysVersion", "SysVersion", "m_currentSysVersion", "m_sysVersion") ?? 0,
                    ReadEntityString(element, "ItemMaster", "Code")
                    ?? ReadString(element, "ItemCode", "m_itemCode"),
                    bomVersionCode));
            foreach (var property in element.EnumerateObject())
                CollectBomOperationCandidates(property.Value, candidates);
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                CollectBomOperationCandidates(item, candidates);
        }
        else if (element.ValueKind == JsonValueKind.String)
        {
            var value = element.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(value) || value[0] is not ('{' or '[')) return;
            try
            {
                using var document = JsonDocument.Parse(value);
                CollectBomOperationCandidates(document.RootElement, candidates);
            }
            catch (JsonException)
            {
                // Ordinary string fields are not query result containers.
            }
        }
    }

    private sealed record BomOperationCandidate(
        long Id,
        long CurrentSysVersion,
        string? ItemCode,
        string BomVersionCode);

    private static long? FindLongProperty(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var direct = ReadLong(element, name);
            if (direct is not null) return direct;
            foreach (var property in element.EnumerateObject())
            {
                var nested = FindLongProperty(property.Value, name);
                if (nested is not null) return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindLongProperty(item, name);
                if (nested is not null) return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.String)
        {
            var value = element.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(value) || value[0] is not ('{' or '[')) return null;
            try
            {
                using var document = JsonDocument.Parse(value);
                return FindLongProperty(document.RootElement, name);
            }
            catch (JsonException)
            {
                return null;
            }
        }
        return null;
    }


    private static decimal? ReadDecimal(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGet(element, name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)) return number;
            if (decimal.TryParse(value.ToString(), System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture, out number)) return number;
        }
        return null;
    }

    private static DateTimeOffset? ReadDateTimeOffset(JsonElement element, params string[] names)
    {
        var value = ReadString(element, names);
        return DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeLocal, out var parsed) ? parsed : null;
    }

    private static string? ReadScalarOrEntityName(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGet(element, name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) continue;
            if (value.ValueKind == JsonValueKind.Object)
                return ReadString(value, "Name", "m_name", "Code", "m_code");
            var result = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
            if (!string.IsNullOrWhiteSpace(result)) return result.Trim();
        }
        return null;
    }

    private static string? ReadEntityString(JsonElement element, string entityName, string fieldName)
    {
        var internalEntityName = $"m_{char.ToLowerInvariant(entityName[0])}{entityName[1..]}";
        if ((!TryGet(element, entityName, out var entity) && !TryGet(element, internalEntityName, out entity))
            || entity.ValueKind != JsonValueKind.Object)
            return null;
        return ReadString(entity, fieldName, $"m_{char.ToLowerInvariant(fieldName[0])}{fieldName[1..]}");
    }

    private static bool? ReadBool(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGet(element, name, out var value)) continue;
            if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) return value.GetBoolean();
            if (bool.TryParse(value.ToString(), out var parsed)) return parsed;
        }
        return null;
    }

    private static string EscapeSqlLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static string? ReadString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGet(element, name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) continue;
            var result = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
            if (!string.IsNullOrWhiteSpace(result)) return result.Trim();
        }
        return null;
    }

    private static string? ReadMessage(JsonElement root) =>
        ReadString(root, "ResMsg", "Message", "Msg", "ErrorMsg", "ErrorMessage");

    private static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }

    private static JsonDocument ParseJson(string value, string error)
    {
        try
        {
            return JsonDocument.Parse(value);
        }
        catch (JsonException)
        {
            throw new PdmRuleException(error);
        }
    }

    private static JsonDocument ParseNestedJson(string value, string error)
    {
        var normalized = value.Trim().TrimStart('\uFEFF');
        for (var depth = 0; depth < 3; depth++)
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(normalized, new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });
            }
            catch (JsonException)
            {
                try
                {
                    document = JsonDocument.Parse(RepairNestedJsonString(normalized), new JsonDocumentOptions
                    {
                        AllowTrailingCommas = true,
                        CommentHandling = JsonCommentHandling.Skip
                    });
                }
                catch (JsonException exception)
                {
                    if (TryUnescapeOverEscapedJson(normalized, out var unescaped))
                    {
                        normalized = unescaped;
                        continue;
                    }
                    throw new PdmRuleException($"{error.TrimEnd('。')}：{exception.Message}");
                }
            }

            if (document.RootElement.ValueKind != JsonValueKind.String)
                return document;
            var inner = document.RootElement.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(inner) || inner[0] is not ('{' or '[' or '"'))
                return document;
            document.Dispose();
            normalized = inner;
        }
        throw new PdmRuleException(error);
    }

    private static bool TryUnescapeOverEscapedJson(string value, out string unescaped)
    {
        unescaped = string.Empty;
        var trimmed = value.Trim();
        if (trimmed.Length < 3 || trimmed[0] is not ('{' or '[') || !trimmed.Contains("\\\"", StringComparison.Ordinal))
            return false;
        try
        {
            using var document = JsonDocument.Parse($"\"{trimmed}\"");
            unescaped = document.RootElement.GetString() ?? string.Empty;
            return unescaped.Length > 0 && !string.Equals(unescaped, trimmed, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string RepairNestedJsonString(string value)
    {
        var builder = new StringBuilder(value.Length + 16);
        var insideString = false;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (!insideString)
            {
                builder.Append(character);
                if (character == '"') insideString = true;
                continue;
            }

            if (character == '"')
            {
                builder.Append(character);
                insideString = false;
                continue;
            }
            if (character == '\\')
            {
                if (index + 1 < value.Length && IsValidJsonEscape(value, index + 1))
                {
                    builder.Append(character).Append(value[++index]);
                    if (value[index] == 'u')
                    {
                        for (var digit = 0; digit < 4 && index + 1 < value.Length; digit++)
                            builder.Append(value[++index]);
                    }
                }
                else
                {
                    builder.Append("\\\\");
                }
                continue;
            }

            builder.Append(character switch
            {
                '\b' => "\\b",
                '\f' => "\\f",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                < ' ' => $"\\u{(int)character:x4}",
                _ => character.ToString()
            });
        }
        return builder.ToString();
    }

    private static bool IsValidJsonEscape(string value, int escapeIndex)
    {
        var escape = value[escapeIndex];
        if (escape is '"' or '\\' or '/' or 'b' or 'f' or 'n' or 'r' or 't') return true;
        if (escape != 'u' || escapeIndex + 4 >= value.Length) return false;
        return value.AsSpan(escapeIndex + 1, 4).ToString().All(Uri.IsHexDigit);
    }

    private static string Required(string? value, string field)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? throw new PdmRuleException($"{field}不能为空。") : normalized;
    }
}
