using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed class CrmCustomerClient(HttpClient httpClient) : ICrmCustomerClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<CrmCustomerBatch> ListCustomersAsync(
        string baseUrl,
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        try
        {
            var root = new Uri(baseUrl.TrimEnd('/') + "/", UriKind.Absolute);
            using var loginResponse = await httpClient.PostAsJsonAsync(
                new Uri(root, "api/auth/login"),
                new { username, password },
                JsonOptions,
                cancellationToken);
            var login = await ReadEnvelopeAsync<CrmLoginResponse>(loginResponse, "CRM登录", cancellationToken);
            if (string.IsNullOrWhiteSpace(login.Token)) throw new PdmRuleException("CRM登录成功但未返回Token。");

            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(root, "api/open/v1/customers"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);
            using var customerResponse = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var customers = await ReadEnvelopeAsync<List<CrmCustomerResponse>>(customerResponse, "CRM客户读取", cancellationToken);
            var normalized = new Dictionary<string, CrmCustomerRecord>(StringComparer.OrdinalIgnoreCase);
            var skippedCount = 0;
            foreach (var customer in customers)
            {
                var code = customer.CustomerCode?.Trim().ToUpperInvariant() ?? string.Empty;
                var name = customer.CustomerName?.Trim() ?? string.Empty;
                if (code.Length is < 1 or > 30 || name.Length is < 1 or > 200)
                {
                    skippedCount++;
                    continue;
                }
                if (code.Any(character => !char.IsLetterOrDigit(character) && character is not ('-' or '_')))
                {
                    skippedCount++;
                    continue;
                }
                if (normalized.TryGetValue(code, out var existing))
                {
                    if (!string.Equals(existing.Name, name, StringComparison.Ordinal)) skippedCount++;
                    continue;
                }
                normalized[code] = new(code, name);
            }
            return new(
                normalized.Values.OrderBy(customer => customer.Code, StringComparer.OrdinalIgnoreCase).ToArray(),
                skippedCount);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PdmRuleException("连接CRM超时，请检查服务地址和网络状态。");
        }
        catch (HttpRequestException exception)
        {
            throw new PdmRuleException($"无法连接CRM服务：{exception.Message}");
        }
    }

    private static async Task<T> ReadEnvelopeAsync<T>(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            var message = await ReadErrorMessageAsync(response, cancellationToken);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new PdmRuleException($"{operation}失败，请检查集成账号密码及客户查看权限。{message}");
            throw new PdmRuleException($"{operation}失败，CRM返回HTTP {(int)response.StatusCode}。{message}");
        }
        var envelope = await response.Content.ReadFromJsonAsync<CrmEnvelope<T>>(JsonOptions, cancellationToken)
            ?? throw new PdmRuleException($"{operation}失败，CRM返回内容为空。");
        if (!envelope.Success || envelope.Data is null)
            throw new PdmRuleException($"{operation}失败：{envelope.Message ?? "CRM未返回原因"}");
        return envelope.Data;
    }

    private static async Task<string> ReadErrorMessageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var json = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, cancellationToken);
            return json.ValueKind == JsonValueKind.Object
                && json.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String
                ? $" {message.GetString()}"
                : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private sealed record CrmEnvelope<T>(bool Success, T? Data, string? Message);
    private sealed record CrmLoginResponse(string Token);
    private sealed record CrmCustomerResponse(string CustomerCode, string CustomerName);
}
