using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed class U9OpenApiClient(HttpClient httpClient) : IU9OpenApiClient
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
                ReadInt(row, "ItemFormAttribute", "m_itemFormAttribute")))
            .Where(item => !string.IsNullOrWhiteSpace(item.U9ItemId) || !string.IsNullOrWhiteSpace(item.U9ItemCode))
            .ToArray();
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
        string token,
        string payloadJson,
        CancellationToken cancellationToken)
    {
        var endpoint = BuildEndpoint(baseUrl, U9MaterialContract.CustomerReferencePath);
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
        using var serialized = data.ValueKind == JsonValueKind.String
            ? ParseJson(data.GetString() ?? "[]", "U9C业务响应Data不是有效JSON。")
            : JsonDocument.Parse(data.GetRawText());
        var value = serialized.RootElement;
        return value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(row => row.Clone()).ToArray()
            : [value.Clone()];
    }

    private static IReadOnlyList<JsonElement> ReadNestedDataRows(JsonElement root)
    {
        if (!TryGet(root, "Data", out var outerData) || outerData.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return [];
        using var outerDocument = outerData.ValueKind == JsonValueKind.String
            ? ParseJson(outerData.GetString() ?? "{}", "U9C客户参照响应Data不是有效JSON。")
            : JsonDocument.Parse(outerData.GetRawText());
        var outer = outerDocument.RootElement;
        if (!TryGet(outer, "Data", out var rows) || rows.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return [];
        using var rowsDocument = rows.ValueKind == JsonValueKind.String
            ? ParseJson(rows.GetString() ?? "[]", "U9C客户参照响应Data.Data不是有效JSON。")
            : JsonDocument.Parse(rows.GetRawText());
        var value = rowsDocument.RootElement;
        return value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(row => row.Clone()).ToArray()
            : value.ValueKind == JsonValueKind.Object ? [value.Clone()] : [];
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

    private static string Required(string? value, string field)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? throw new PdmRuleException($"{field}不能为空。") : normalized;
    }
}
