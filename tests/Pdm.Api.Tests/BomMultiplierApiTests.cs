using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api.Tests;

public sealed class BomMultiplierApiTests(PdmApiFactory factory) : IClassFixture<PdmApiFactory>
{
    [Fact]
    public async Task MultiplierIsSavedAsBomMetadataAndProtectsAgainstStaleOrInvalidUpdates()
    {
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        var project = (await repository.ListProjectsAsync(default)).First();
        using var client = factory.CreateClient();
        var token = new JwtSecurityToken("upton-pdm","upton-pdm-clients",
            [new Claim(ClaimTypes.Name,"admin"),new Claim(ClaimTypes.Role,"Administrator")],
            expires:DateTime.UtcNow.AddMinutes(10),signingCredentials:new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")),SecurityAlgorithms.HmacSha256));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",new JwtSecurityTokenHandler().WriteToken(token));
        var original = await repository.GetBomAsync(project.Id,BomKind.Standard,default);
        var endpoint = $"/api/projects/{project.Id}/boms/Standard/multiplier";
        var multiplier = project.Quantity + 2;
        var saved = await client.PutAsJsonAsync(endpoint,new UpdateBomMultiplierRequest(multiplier,project.Quantity));
        Assert.Equal(HttpStatusCode.OK,saved.StatusCode);
        var draft = await saved.Content.ReadFromJsonAsync<BomVersion>();
        Assert.Equal(multiplier,draft!.WholeSetMultiplier);
        Assert.True(draft.IsMultiplierOverridden);
        Assert.Equal(BomVersionState.Draft,draft.State);
        var versions = await client.GetFromJsonAsync<BomVersion[]>($"/api/projects/{project.Id}/bom-versions?kind=Standard&summary=true");
        Assert.Equal(multiplier,versions!.Single(version=>version.Id==draft.Id).WholeSetMultiplier);
        Assert.Equal(HttpStatusCode.Conflict,(await client.PutAsJsonAsync(endpoint,new UpdateBomMultiplierRequest(multiplier+1,project.Quantity))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await client.PutAsJsonAsync(endpoint,new UpdateBomMultiplierRequest(0,multiplier))).StatusCode);
        Assert.Equal(original.Select(item=>(item.Id,item.Quantity)),
            (await repository.GetBomAsync(project.Id,BomKind.Standard,default)).Select(item=>(item.Id,item.Quantity)));
        client.DefaultRequestHeaders.Authorization = null;
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.PutAsJsonAsync(endpoint,new UpdateBomMultiplierRequest(multiplier+1,multiplier))).StatusCode);
    }
}
