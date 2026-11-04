using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api.Tests;

public sealed class BomIntegerAllocationApiTests
{
    [Fact]
    public async Task FlatQuantityEditKeepsThePublishedSnapshotAndSeparateNewMaterialUnchanged()
    {
        await using var factory = new PdmApiFactory(); using var client = factory.CreateClient();
        var token = new JwtSecurityToken("upton-pdm", "upton-pdm-clients", [new Claim(ClaimTypes.Name,"admin"),new Claim(ClaimTypes.Role,"Administrator")],
            expires:DateTime.UtcNow.AddMinutes(10),signingCredentials:new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")),SecurityAlgorithms.HmacSha256));
        client.DefaultRequestHeaders.Authorization=new("Bearer",new JwtSecurityTokenHandler().WriteToken(token));
        using var scope=factory.Services.CreateScope();var provider=scope.ServiceProvider;
        var repo=provider.GetRequiredService<IPdmRepository>(); var relations=provider.GetRequiredService<IMaterialRelationRepository>();
        await provider.GetRequiredService<IMaterialBrandRepository>().SaveAsync(new(Guid.NewGuid(),"整数测试",true,0,[]),null,default);
        var service=provider.GetRequiredService<MaterialService>();
        async Task<PdmMaterial> CreateMaterial(string name) => await service.CreateAsync(new(null,name,MaterialKind.Standard,MaterialSupplyMode.Purchase,"001",name+"-MODEL",null,null,"整数测试",null,null,null,CategoryCode:"0102"),"admin",UserRole.Administrator,default);
        var master=await CreateMaterial("整数主料");var accessoryMaster=await CreateMaterial("原配件");
        var sample=new BomItem(Guid.NewGuid(),Guid.Empty,BomKind.Standard,1,master.MaterialCode,master.Name,1,"001",null,master.Specification,"W1",true){Brand=master.Brand};
        var project=await repo.CreateProjectAsync(new("INTEGER-"+Guid.NewGuid().ToString("N"),"计件数量修复验收","admin","D:/qa","D:/release"),"admin",default);
        var sources=Enumerable.Range(0,4).Select(index=> {
            var id=Guid.NewGuid();return sample with {Id=id,ReleaseTrackingId=id,ProjectId=project.Id,Sequence=index+1,Quantity=0.5m,SourceDocumentId=Guid.NewGuid(),SourceInstancePath=$"ASM/PART-{index}",ParentDrawingNumber="ASM",Source="Auto"};
        }).ToArray();
        var addedId=Guid.NewGuid();var added=sample with {Id=addedId,ReleaseTrackingId=addedId,ProjectId=project.Id,Sequence=5,Quantity=2,Source="Manual"};
        await repo.ReplaceBomAsync(project.Id,BomKind.Standard,[..sources,added],default);
        var frozen=await repo.CreateReleasePackageAsync(new(Guid.NewGuid(),project.Id,"RP-OLD",ReleasePackageState.Published,null,"","",[],DateTimeOffset.UtcNow,DateTimeOffset.UtcNow,"")
            {Scope=ReleaseScope.StandardFormal,StandardBomRevision="S-TEST-B05",StandardBomSnapshot=sources},default);
        var options=new JsonSerializerOptions(JsonSerializerDefaults.Web);options.Converters.Add(new JsonStringEnumConverter());
        var url=$"/api/projects/{project.Id}/boms/Standard?summary=true";
        var groups=(await client.GetFromJsonAsync<BomItem[]>(url,options))!;Assert.Equal(2,groups.Length);
        var group=groups.Single(item=>item.IsPublishedInBom==true);Assert.Equal(2,group.Quantity);
        var values=new BomItemInput(1,group.DrawingNumber,group.Name,4,group.Unit,group.Material,group.Specification,group.Revision,true,Brand:group.Brand);
        var edit=new BomSummaryEdit(group.SummaryItemIds!,group.SummaryFingerprint!,["quantity"],values);
        var invalid=await client.PutAsJsonAsync(url,new {items=Array.Empty<BomItemInput>(),summaryRows=new[]{edit with {Values=values with {Quantity=4.5m}}}});
        Assert.Equal(HttpStatusCode.BadRequest,invalid.StatusCode);
        var response=await client.PutAsJsonAsync(url,new {items=Array.Empty<BomItemInput>(),summaryRows=new[]{edit}});
        Assert.True(response.IsSuccessStatusCode,await response.Content.ReadAsStringAsync());
        var saved=await repo.GetBomAsync(project.Id,BomKind.Standard,default);Assert.Equal(2,saved.Count);
        var changed=saved.Single(item=>item.Id!=addedId);Assert.Equal(4,changed.Quantity);Assert.True(changed.IsSummaryQuantity);Assert.Equal("Auto",changed.Source);Assert.Null(changed.ParentDrawingNumber);
        Assert.Equal(2,saved.Single(item=>item.Id==addedId).Quantity);
        Assert.Equal(sources,(await repo.FindReleasePackageAsync(frozen.Id,default))!.StandardBomSnapshot);
        var reloaded=(await client.GetFromJsonAsync<BomItem[]>(url,options))!;Assert.Equal(2,reloaded.Length);
        Assert.Equal("未发布",reloaded.Single(item=>item.Id==addedId).DisplayRevision);Assert.Equal(4,reloaded.Single(item=>item.Id!=addedId).Quantity);
    }
}
