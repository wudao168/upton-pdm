using System.IO.Compression;
using System.Net;
using System.Text.Json;

namespace Upton.Pdm.Api.Tests;

public sealed class ResponseCompressionTests : IClassFixture<PdmApiFactory>
{
    private readonly PdmApiFactory factory;
    public ResponseCompressionTests(PdmApiFactory factory) => this.factory = factory;

    [Fact]
    public async Task JsonResponseSupportsGzipOnLanHttp()
    {
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("http://192.168.2.8") });
        client.DefaultRequestHeaders.AcceptEncoding.ParseAdd("gzip");
        using var response = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("gzip", response.Content.Headers.ContentEncoding);
        Assert.Contains("Accept-Encoding", response.Headers.Vary);
        await using var compressed = await response.Content.ReadAsStreamAsync();
        await using var decoded = new GZipStream(compressed, CompressionMode.Decompress);
        using var json = await JsonDocument.ParseAsync(decoded);
        Assert.Equal("ok", json.RootElement.GetProperty("status").GetString());
    }
}
