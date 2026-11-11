using System.IdentityModel.Tokens.Jwt;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api.Tests;

public sealed class SparePartApiTests
{
    [Fact]
    public async Task CompanyPricingAndProjectRelationsRespectDirectApiGrantsAndRevocation()
    {
        await using var factory = new PdmApiFactory();
        using var client = factory.CreateClient();
        const string prefix = "/api/spare-parts";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(prefix)).StatusCode);
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        var directory = await repository.GetOrganizationDirectoryAsync(default);
        var company = directory.Organizations[0].Id;
        var otherCompany = directory.Organizations[1].Id;
        var customer = (await repository.ListCustomersAsync(true, default)).First().Id;
        var storage = Path.Combine(AppContext.BaseDirectory, ".data", "spare-part-tests", Guid.NewGuid().ToString("N"));
        var system = await repository.GetSystemSettingsAsync(default);
        await repository.UpdateSystemSettingsAsync(system with { VaultRoot = storage }, default);
        const string actor = "spare-qa";
        const string role = "spare-qa-role";
        await repository.CreateUserAsync(new(Guid.NewGuid(), actor, "备件验收", "unused", UserRole.Engineer, true, RoleCode: role, CompanyId: company), default);
        await repository.SetUserCompanyScopeAsync(actor, company, false, [], "admin", default);
        var project = await CreateProject(company, "本公司备件");
        var other = await CreateProject(otherCompany, "外公司备件");
        client.DefaultRequestHeaders.Add("X-Company-Id", company.ToString());
        Login(client, actor);

        var common = new[] { PermissionCodes.ProjectView, PermissionCodes.ProjectContentView, PermissionCodes.RoleSettingsView };
        var settings = await repository.GetAuthorizationSettingsAsync(default);
        var template = new AuthorizationTemplate(role, "备件验收", "", Grants(common), role);
        settings = await repository.SaveAuthorizationSettingsAsync(settings with { Templates = [template] }, settings.Revision, default);
        var catalog = await client.GetFromJsonAsync<JsonElement>("/api/role-permissions");
        var permissions = RolePermissionCatalog.Permissions.Where(item => item.Module == "备品备件").ToArray();
        Assert.Equal(8, permissions.Length);
        Assert.All(permissions, permission => Assert.Contains(catalog.GetProperty("permissions").EnumerateArray(),
            item => item.GetProperty("code").GetString() == permission.Code));
        Assert.DoesNotContain(SparePartPermissions.View, RolePermissionCatalog.InitialPermissions("Administrator", UserRole.Administrator));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(prefix)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(prefix + "/categories")).StatusCode);

        var allowed = common.Concat(permissions.Select(item => item.Code)).Concat([PermissionCodes.RoleSettingsEdit,
            ProjectCenterPermissions.Tab("bom"), "bom.mechanical.row.view", "bom.electrical.row.view", "document.view",
            ProjectCenterPermissions.Download]).ToArray();
        settings = await repository.SaveAuthorizationSettingsAsync(settings with { Templates = [template with { Grants = Grants(allowed) }] }, settings.Revision, default);
        var categoryPath = prefix + "/categories";
        var defaults = (await client.GetFromJsonAsync<SpareCategorySettings>(categoryPath))!;
        Assert.Equal(["易损件", "耗材", "备件", "其他"], defaults.Categories.Select(item => item.Name));
        var edited = defaults with { Categories = defaults.Categories.Select(item => item.Id == "Other" ? item with { IsActive = false } : item)
            .Append(new("", "保养件", true)).ToArray() };
        await Success(await client.PutAsJsonAsync(categoryPath, edited));
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync(categoryPath, edited)).StatusCode);
        var categories = (await client.GetFromJsonAsync<SpareCategorySettings>(categoryPath))!;
        var custom = categories.Categories.Single(item => item.Name == "保养件");
        Assert.NotEmpty(custom.Id);
        var pricing = new SparePricing(0, 1.2m, [new(0, 120, 2), new(120, null, 1.8m)], "QA", "CNY");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(prefix + "/pricing", pricing with { Bands = [new(0, 100, 2), new(120, null, 1.8m)] })).StatusCode);
        await Success(await client.PutAsJsonAsync(prefix + "/pricing", pricing));
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync(prefix + "/pricing", pricing)).StatusCode);
        var manual = new SpareManualInput(project.Id, "QA-100", "测试备件", "规格", "品牌", "件", 1, custom.Id, true, null, null, null, 3);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(prefix + "/manual", manual with { Category = "Other" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(prefix + "/manual", manual with { QuantityPerSet = 1.5m })).StatusCode);
        await Success(await client.PostAsJsonAsync(prefix + "/manual", manual));
        await Success(await client.PostAsJsonAsync(prefix + "/manual", manual));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(prefix + "/manual", manual with { ProjectId = other.Id })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(prefix + "?projectId=" + other.Id)).StatusCode);
        var store = (ISparePartStore)repository;
        var now = DateTimeOffset.UtcNow;
        await store.SavePricesAsync(company, "QA", "CNY", [new("QA-100", 100m, "001", "件", "PO-QA", now, now)], default);
        var row = Assert.Single((await List()).Items);
        Assert.Equal("保养件", row.CategoryName);
        Assert.Equal(3, row.Part.QuantityPerSet);
        Assert.Equal(1, row.Part.Quantity);
        Assert.Equal(project.DeviceModel, row.Project.DeviceModel);
        Assert.Equal(project.SerialNumbers.ToArray(), row.Project.SerialNumbers.ToArray());
        Assert.Equal(defaults.Categories, (await store.CategoriesAsync(otherCompany, default)).Categories);
        Assert.Equal(120m, row.Cost);
        Assert.Equal(216m, row.Price);
        Assert.Equal(100m, row.RawCost);
        await Success(await client.PutAsJsonAsync(prefix + "/" + row.Part.Id + "/price", new SparePriceInput(row.Part.RowVersion, 230m)));
        row = Assert.Single((await List()).Items);
        Assert.Equal(230m, row.Price);
        Assert.Equal("手动定价", row.PriceSource);
        await Success(await client.PutAsJsonAsync(prefix + "/" + row.Part.Id + "/price", new SparePriceInput(row.Part.RowVersion, null)));
        Assert.Equal(216m, Assert.Single((await List()).Items).Price);

        foreach (var kind in new[] { BomKind.Standard, BomKind.NonStandard, BomKind.Electrical })
        {
            var source = new BomItem(Guid.NewGuid(), project.Id, kind, 1, "BOM-" + kind, "关联备件", 2, "件", null, null, "A", true);
            var stored = Assert.Single(await repository.ReplaceBomAsync(project.Id, kind, [source], default));
            var request = new SpareBomInput(project.Id, kind, null, [stored.Id], "Wear");
            await Success(await client.PostAsJsonAsync(prefix + "/from-bom", request));
            await Success(await client.PostAsJsonAsync(prefix + "/from-bom", request));
        }
        var page = await List();
        Assert.Equal(4, page.Total);
        Assert.All(page.Items.Where(item => item.Part.SourceKind is not null), item => Assert.Equal(2, item.Part.QuantityPerSet));

        var selection = "ids=" + row.Part.Id;
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(prefix + "/export?quotation=true")).StatusCode);
        await store.AddAsync(otherCompany, [row.Part with { Id = Guid.NewGuid(), ProjectId = other.Id }], default);
        var foreign = Assert.Single(await store.ListAsync(otherCompany, [other.Id], default));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(prefix + "/export?quotation=true&" + selection + "&ids=" + foreign.Id)).StatusCode);
        using (var zip = new ZipArchive(new MemoryStream(await client.GetByteArrayAsync(prefix + "/export?quotation=true&" + selection))))
        {
            Assert.NotNull(zip.GetEntry("xl/media/uplm-company-logo.png"));
            using var reader = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
            var sheet = await reader.ReadToEndAsync();
            Assert.Contains("未税售价", sheet);
            Assert.DoesNotContain("未税成本", sheet);
            Assert.DoesNotContain("PO-QA", sheet);
            Assert.Contains("QA-100", sheet);
            Assert.DoesNotContain("BOM-Standard", sheet);
            Assert.Contains("设备型号", sheet);
            Assert.Contains("设备序列号", sheet);
            Assert.Contains("单套数量", sheet);
        }
        using (var zip = new ZipArchive(new MemoryStream(await client.GetByteArrayAsync(prefix + "/export?" + selection))))
        {
            using var reader = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
            Assert.Contains("保养件", await reader.ReadToEndAsync());
        }

        settings = await repository.SaveAuthorizationSettingsAsync(settings with
        {
            Templates = [template with { Grants = Grants(common.Concat([SparePartPermissions.View, SparePartPermissions.Export])) }]
        }, settings.Revision, default);
        row = (await List()).Items.Single(value => value.Part.MaterialCode == "QA-100");
        Assert.Null(row.Cost);
        Assert.Null(row.RawCost);
        Assert.Null(row.PurchaseDocument);
        Assert.Equal(216m, row.Price);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(prefix + "/pricing")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(prefix + "/export?" + selection)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(categoryPath)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync(categoryPath, categories)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync(prefix + "/" + row.Part.Id + "/price", new SparePriceInput(row.Part.RowVersion, 1))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(prefix + "/" + row.Part.Id + "/drawings/0/download")).StatusCode);
        await repository.SaveAuthorizationSettingsAsync(settings with { Templates = [template] }, settings.Revision, default);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(prefix)).StatusCode);

        async Task<Project> CreateProject(Guid companyId, string name) =>
            await repository.CreateNumberedProjectAsync(new(companyId, "P", 2, customer, name, null, null, 1, actor, storage, Path.Combine(storage, "release")), default);
        async Task<SparePartPage> List() => (await client.GetFromJsonAsync<SparePartPage>(prefix))!;
    }

    private static AuthorizationGrant[] Grants(IEnumerable<string> codes) =>
        codes.Distinct().Select(code => new AuthorizationGrant(code, ["company"])).ToArray();

    private static async Task Success(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    private static void Login(HttpClient client, string actor)
    {
        var token = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("upton-pdm", "upton-pdm-clients",
            [new(ClaimTypes.Name, actor), new(ClaimTypes.Role, "Engineer")], expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256)));
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
    }
}
