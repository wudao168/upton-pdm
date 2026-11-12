using System.IdentityModel.Tokens.Jwt;
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

public sealed class MaterialRelationReleaseApiTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DirectSubmissionCannotBypassRelationReviewAndEmptySaveDoesNotConfirmNoAccessory(bool required)
    {
        await using var factory = new PdmApiFactory();
        using var client = factory.CreateClient();
        var token = new JwtSecurityToken("upton-pdm", "upton-pdm-clients", [new Claim(ClaimTypes.Name, "admin"), new Claim(ClaimTypes.Role, "Administrator")],
            expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256));
        client.DefaultRequestHeaders.Authorization = new("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        var relations = factory.Services.GetRequiredService<IMaterialRelationRepository>();
        var seed = (await repository.ListProjectsAsync(default)).First();
        var example = (await repository.GetBomAsync(seed.Id, BomKind.Standard, default)).First();
        var project = await ApiTestAuthorization.CreateProjectAsync(repository, new("REL-API-" + Guid.NewGuid().ToString("N"), "关联发布接口测试", "admin", "D:/test", "D:/release"), "admin", default);
        var main = new BomItem(Guid.NewGuid(), project.Id, BomKind.Standard, 1, example.DrawingNumber, example.Name, 1, "001", example.Material, example.Specification, "W1", true) { Brand = example.Brand, Source = "Manual" };
        await repository.ReplaceBomAsync(project.Id, BomKind.Standard, [main], default);
        var group = new MaterialRelationGroup(Guid.NewGuid(), "发布必查配件", required, MaterialRelationSelectionMode.Single, required ? 1 : 0, 1, false, 1, []);
        var template = await relations.SaveDraftAsync(null, new(Guid.NewGuid(), "发布检查", null, null, []), main.DrawingNumber, main.Name, [group], "admin", DateTimeOffset.UtcNow, default);
        await relations.PublishAsync(template.Id, template.DraftRevision!.Id, template.DraftRevision.RowVersion, "admin", DateTimeOffset.UtcNow, default);
        var package = await repository.CreateReleasePackageAsync(new(Guid.NewGuid(), project.Id, "RP-REL-GATE", ReleasePackageState.Draft,
            null, "", "", [], DateTimeOffset.UtcNow, null, "") { Scope = ReleaseScope.StandardFormal, SelectedBomItemIds = [main.Id], StandardBomSnapshot = [main] }, default);
        var submitUrl = $"/api/release-packages/{package.Id}/submit";
        async Task AssertBlocked()
        {
            var response = await client.PostAsync(submitUrl, null);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var message = await response.Content.ReadAsStringAsync();
            Assert.Contains("关联物料未处理", message);
            Assert.Contains(main.DrawingNumber, message);
            Assert.Contains(main.Name, message);
            Assert.Contains(group.Name, message);
            Assert.Equal(ReleasePackageState.Draft, (await repository.FindReleasePackageAsync(package.Id, default))!.State);
        }
        await AssertBlocked();
        var applyUrl = $"/api/material-relations/projects/{project.Id}/apply";
        async Task<bool> Apply(bool noAccessory)
        {
            var response = await client.PostAsJsonAsync(applyUrl, new { mainMaterials = new[] { new { mainBomItemId = main.Id,
                choices = new[] { new { groupId = group.Id, optionIds = Array.Empty<Guid>(), confirmNoAccessory = noAccessory } } } } });
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return body.RootElement.GetProperty("isComplete").GetBoolean();
        }
        Assert.False(await Apply(false));
        await AssertBlocked();
        Assert.True(await Apply(true));
        await repository.ReplaceBomAsync(project.Id, BomKind.Standard, [main, main with { Id = Guid.NewGuid(), Sequence = 2 }], default);
        using var scoped = JsonDocument.Parse(await client.GetStringAsync($"/api/material-relations/projects/{project.Id}/completeness?releasePackageId={package.Id}"));
        Assert.True(scoped.RootElement.GetProperty("isComplete").GetBoolean());
        Assert.Equal(1, scoped.RootElement.GetProperty("mainMaterialCount").GetInt32());
        using var whole = JsonDocument.Parse(await client.GetStringAsync($"/api/material-relations/projects/{project.Id}/completeness"));
        Assert.False(whole.RootElement.GetProperty("isComplete").GetBoolean());
        var afterConfirmation = await client.PostAsync(submitUrl, null);
        Assert.True(afterConfirmation.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict);
        Assert.DoesNotContain("关联物料未处理", await afterConfirmation.Content.ReadAsStringAsync());
    }
}
