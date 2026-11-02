using Microsoft.AspNetCore.Http.Features;

namespace Upton.Pdm.Api;

internal static class ArchiveBatchPayloadLimit
{
    internal static async Task<ArchiveBatchRequest> ReadAsync(HttpContext context, long maximumBytes, CancellationToken token)
    {
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
            feature.MaxRequestBodySize = maximumBytes;
        if (context.Request.ContentLength > maximumBytes)
            throw new BadHttpRequestException("整批提交数据超过管理员设置的上限，本批尚未提交。", StatusCodes.Status413PayloadTooLarge);
        return await context.Request.ReadFromJsonAsync<ArchiveBatchRequest>(token)
            ?? throw new System.Text.Json.JsonException("整批提交数据为空。");
    }
}
