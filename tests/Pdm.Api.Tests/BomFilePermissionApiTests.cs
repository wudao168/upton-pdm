using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api.Tests;

public sealed class BomFilePermissionApiTests(PdmApiFactory factory) : IClassFixture<PdmApiFactory>
{
    [Fact]
    public async Task EngineerCanReadButCannotBypassStandardLibraryAttachmentWriteRestrictions()
    {
        using var scope=factory.Services.CreateScope();
        var services=scope.ServiceProvider;
        var repository=services.GetRequiredService<IPdmRepository>();
        var project=await repository.CreateProjectAsync(new("FILES-PERM-"+Guid.NewGuid().ToString("N"),"附件权限验收","admin","vault","release"),"admin",default);
        await repository.SetChildProjectDesignersAsync(project.Id,["engineer"],"admin",default);
        var material=await services.GetRequiredService<MaterialService>().CreateAsync(new(null,"标准库附件权限",MaterialKind.Standard,MaterialSupplyMode.Purchase,
            "001","PERM-"+Guid.NewGuid(),null,null,"UPTON",null,null,null,CategoryCode:"0102"),"admin",UserRole.Administrator,default);
        var item=new BomItem(Guid.NewGuid(),project.Id,BomKind.Standard,1,material.MaterialCode,material.Name,1,"001",null,material.Specification,"W1",true);
        await repository.ReplaceBomAsync(project.Id,BomKind.Standard,[item],default);
        using var client=factory.CreateClient();
        void Login(string actor,UserRole role)
        {
            var token=new JwtSecurityToken("upton-pdm","upton-pdm-clients",[new Claim(ClaimTypes.Name,actor),new Claim(ClaimTypes.Role,role.ToString())],
                expires:DateTime.UtcNow.AddMinutes(10),signingCredentials:new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")),SecurityAlgorithms.HmacSha256));
            client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",new JwtSecurityTokenHandler().WriteToken(token));
        }
        Login("engineer",UserRole.Engineer);
        var url=$"/api/projects/{project.Id}/bom-files/{item.Id}";
        var before=await client.GetFromJsonAsync<JsonElement>(url);
        Assert.True(before.GetProperty("canEditFiles").GetBoolean());
        await services.GetRequiredService<IStandardLibraryRepository>().AddMembershipsAsync([new(StandardLibraryDefaults.UncategorizedId,material.Id,"admin",DateTimeOffset.UtcNow)],default);
        var read=await client.GetFromJsonAsync<JsonElement>(url);
        Assert.False(read.GetProperty("canEditFiles").GetBoolean());
        Assert.True(read.GetProperty("isStandardLibraryMaterial").GetBoolean());
        var sessionId=Guid.NewGuid();
        var responses=new[]{
            await client.PostAsJsonAsync(url+"/uploads",new{fileName="配图.jpg",totalLength=1,sha256=new string('A',64)}),
            await client.PutAsync(url+$"/uploads/{sessionId}/chunks/0",new ByteArrayContent([1])),
            await client.PostAsJsonAsync(url+$"/uploads/{sessionId}/complete",new{purpose="配图"}),
            await client.PostAsJsonAsync(url+"/adopt-library",new{expectedVersion=0}),
            await client.DeleteAsync(url+$"/{Guid.NewGuid()}?expectedVersion=0")};
        foreach(var response in responses)
        {
            using(response){Assert.Equal(HttpStatusCode.Forbidden,response.StatusCode);Assert.Contains("标准库",await response.Content.ReadAsStringAsync());}
        }
        Login("admin",UserRole.Administrator);
        Assert.True((await client.GetFromJsonAsync<JsonElement>(url)).GetProperty("canEditFiles").GetBoolean());
    }
}
