using System.IdentityModel.Tokens.Jwt;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api.Tests;

public sealed class StandardLibraryImportApiTests
{
    private static string Token(string name, string role) => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("upton-pdm", "upton-pdm-clients",
        [new(ClaimTypes.Name, name), new(ClaimTypes.Role, role)], expires: DateTime.UtcNow.AddMinutes(10),
        signingCredentials: new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256)));

    [Fact]
    public async Task ImportExcelAndErpCodesWorksThroughAuthorizedApiWithoutErpWrites()
    {
        using var root = new PdmApiFactory();
        var erp = new ReadOnlyErp();
        using var factory = root.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        { services.RemoveAll<IU9OpenApiClient>(); services.AddSingleton<IU9OpenApiClient>(erp); }));
        using var client = factory.CreateClient();
        var materials = factory.Services.GetRequiredService<IMaterialRepository>();
        var configuration = await materials.GetIntegrationConfigurationAsync(default);
        await materials.SaveIntegrationConfigurationAsync(configuration with { ClientSecretCiphertext = factory.Services.GetRequiredService<IU9SecretProtector>().Protect("test"), ItemQueryPath = U9MaterialContract.QueryPath, WriteEnabled = false }, default);
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token("admin", "Administrator"));
        var categories = (await client.GetFromJsonAsync<StandardLibraryCategory[]>("/api/standard-library/categories"))!;
        var uncategorized = Assert.Single(categories, StandardLibraryDefaults.IsUncategorized);
        using var workbook = new MemoryStream();
        using (var archive = new ZipArchive(workbook, ZipArchiveMode.Create, true))
        {
            using var writer = new StreamWriter(archive.CreateEntry("xl/worksheets/sheet1.xml").Open());
            writer.Write("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData><row><c r=\"A1\" t=\"inlineStr\"><is><t>料号</t></is></c></row><row><c r=\"A2\" t=\"inlineStr\"><is><t>01020000001</t></is></c></row></sheetData></worksheet>");
        }
        using var form = new MultipartFormDataContent(); form.Add(new ByteArrayContent(workbook.ToArray()), "file", "料号.xlsx");
        var parsed = await client.PostAsync("/api/standard-library/imports/parse", form); parsed.EnsureSuccessStatusCode();
        Assert.Contains("01020000001", await parsed.Content.ReadAsStringAsync());
        var response = await client.PostAsJsonAsync("/api/standard-library/imports", new { materialCodes = new[] { "01020000001", "01020000001", "missing" } }); response.EnsureSuccessStatusCode();
        var result = (await response.Content.ReadFromJsonAsync<StandardLibraryImportResult>())!;
        Assert.Equal(1, result.ImportedCount); Assert.Equal(1, result.FailedCount);
        var page = await client.GetFromJsonAsync<JsonElement>($"/api/standard-library/materials?categoryId={uncategorized.Id}");
        Assert.Equal("ERP测试物料", page.GetProperty("items")[0].GetProperty("material").GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.DeleteAsync($"/api/standard-library/categories/{uncategorized.Id}?expectedRowVersion=1")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token("engineer", "Engineer"));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/standard-library/imports", new { materialCodes = new[] { "01020000001" } })).StatusCode);
        Assert.Equal(0, erp.Writes);
    }
    private sealed class ReadOnlyErp : IU9OpenApiClient
    {
        public int Writes;
        public Task<U9AuthenticationResult> AuthenticateAsync(U9AuthenticationRequest request, CancellationToken ct) => Task.FromResult(new U9AuthenticationResult("test"));
        public Task<U9ItemQueryResult> QueryItemsAsync(string url, string path, string token, string payload, CancellationToken ct) => Task.FromResult(new U9ItemQueryResult(0, null, [new("test-id", "01020000001", "ERP测试物料", "型号", "0102", "机械外购件", "001", 9, "品牌")]));
        public Task<U9BusinessBatchResult> PostBatchAsync(string url, string path, string token, string payload, CancellationToken ct) { Writes++; throw new Exception("不应写ERP"); }
        public Task<U9UomQueryResult> QueryUomsAsync(string url, string token, string payload, CancellationToken ct) => throw new NotSupportedException();
        public Task<U9CustomerQueryResult> QueryCustomerReferencesAsync(string url, string path, string token, string payload, CancellationToken ct) => throw new NotSupportedException();
    }
}
