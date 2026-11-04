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

public sealed class MaterialRelationAllocationApiTests
{
    [Fact]
    public async Task AllocationBodyPersistsAndInvalidTotalIsRejected()
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
        var project = await repository.CreateProjectAsync(new("AL-API-" + Guid.NewGuid().ToString("N"), "组合接口测试", "admin", "D:/test", "D:/release"), "admin", default);
        var main = example with { Id = Guid.NewGuid(), ProjectId = project.Id, Quantity = 2, Sequence = 1, Source = "Manual" };
        await repository.ReplaceBomAsync(project.Id, BomKind.Standard, [main], default);
        var group = new MaterialRelationGroup(Guid.NewGuid(), "附件", false, MaterialRelationSelectionMode.Single, 0, 1, false, 1, []);
        var template = await relations.SaveDraftAsync(null, new(Guid.NewGuid(), "组合检查", null, null, []), main.DrawingNumber, main.Name, [group], "admin", DateTimeOffset.UtcNow, default);
        await relations.PublishAsync(template.Id, template.DraftRevision!.Id, template.DraftRevision.RowVersion, "admin", DateTimeOffset.UtcNow, default);
        object Body(decimal second) => new { mainMaterials = new[] { new { mainBomItemId = main.Id, choices = Array.Empty<object>(),
            allocations = new[] { new { id = Guid.NewGuid(), name = "组合甲", quantity = 1m, choices = new[] { new { groupId = group.Id, optionIds = Array.Empty<Guid>(), confirmNoAccessory = true } } },
                new { id = Guid.NewGuid(), name = "组合乙", quantity = second, choices = new[] { new { groupId = group.Id, optionIds = Array.Empty<Guid>(), confirmNoAccessory = true } } } } } } };
        var url = $"/api/material-relations/projects/{project.Id}/apply";
        var response = await client.PostAsJsonAsync(url, Body(1));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("isComplete").GetBoolean());
        Assert.Equal(2, body.RootElement.GetProperty("mainMaterials")[0].GetProperty("allocations").GetArrayLength());
        var invalid = await client.PostAsJsonAsync(url, Body(2));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Contains("合计必须等于2", await invalid.Content.ReadAsStringAsync());
        Assert.Equal(2, (await repository.GetBomAsync(project.Id, BomKind.Standard, default)).Single().RelationAllocations.Count);
    }
}
