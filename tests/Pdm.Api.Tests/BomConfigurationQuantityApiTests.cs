using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api.Tests;

public sealed class BomConfigurationQuantityApiTests
{
    [Fact]
    public async Task SaveRequiresConfirmationOfTheActualPartsAndUpdatesElectricalAccessoriesWithTheMechanicalMain()
    {
        await using var factory=new PdmApiFactory();using var client=factory.CreateClient();using var scope=factory.Services.CreateScope();var provider=scope.ServiceProvider;
        var jwt=new JwtSecurityToken("upton-pdm","upton-pdm-clients",[new(ClaimTypes.Name,"admin"),new(ClaimTypes.Role,"Administrator")],expires:DateTime.UtcNow.AddMinutes(10),signingCredentials:new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")),SecurityAlgorithms.HmacSha256));
        client.DefaultRequestHeaders.Authorization=new("Bearer",new JwtSecurityTokenHandler().WriteToken(jwt));
        var repo=provider.GetRequiredService<IPdmRepository>();var materialRepo=provider.GetRequiredService<IMaterialRepository>();var materials=provider.GetRequiredService<MaterialService>();var relations=provider.GetRequiredService<MaterialRelationService>();
        var brand="数量确认API";await provider.GetRequiredService<IMaterialBrandRepository>().SaveAsync(new(Guid.NewGuid(),brand,true,0,[]),null,default);
        async Task<PdmMaterial> Material(string name,MaterialKind kind) {
            var material=await materials.CreateAsync(new(null,name,kind,MaterialSupplyMode.Purchase,"001",name+"-MODEL",null,null,brand,null,null,null,CategoryCode:kind==MaterialKind.Electrical?"0101":"0102"),"admin",UserRole.Administrator,default);
            material=(await materials.ApproveAsync(material.Id,material.RowVersion,"admin",UserRole.Administrator,default)).Material;
            return await materialRepo.UpdateMaterialAsync(material with {U9SyncConfirmed=true,SyncStatus=MaterialSyncStatus.Succeeded},material.RowVersion,default);
        }
        var motor=await Material("主料",MaterialKind.Standard);var drive=await Material("电气配件",MaterialKind.Electrical);
        var category=await provider.GetRequiredService<StandardLibraryService>().SaveCategoryAsync(null,new("数量联动验收分类",null,1,true),"admin",UserRole.Administrator,default);
        await relations.AssignMainCategoryAsync(motor.Id,category.Id,"admin",UserRole.Administrator,default);
        var project=await ApiTestAuthorization.CreateProjectAsync(repo, new("CONFIRM-"+Guid.NewGuid().ToString("N"),"联动数量API验收","admin","vault","release"),"admin",default);
        var draft=await relations.SaveDraftAsync(null,new(motor.Id,"数量确认",null,null,[new("配件",true,MaterialRelationSelectionMode.Single,1,1,false,1,[new(drive.Id,MaterialRelationQuantityMode.PerMainQuantity,2,false,1)])]),"admin",UserRole.Administrator,default);
        var template=await relations.PublishAsync(draft.Id,draft.DraftRevision!.Id,draft.DraftRevision.RowVersion,"admin",UserRole.Administrator,default);var group=template.PublishedRevision!.Groups.Single();
        var main=new BomItem(Guid.NewGuid(),project.Id,BomKind.Standard,1,motor.MaterialCode,motor.Name,2,"001",null,motor.Specification,"W1",true){Source="Manual",Brand=brand};
        await repo.ReplaceBomAsync(project.Id,BomKind.Standard,[main],default);
        await relations.ApplyAsync(project.Id,[new(main.Id,[new(group.Id,[group.Options[0].Id])])],"admin",UserRole.Administrator,default);
        var current=Assert.Single(await repo.GetBomAsync(project.Id,BomKind.Standard,default));
        var change=new BomConfigurationQuantityChange([new(main.Id,current.RelationAllocations.Single().Id)],4,2,true);
        var previewResponse=await client.PostAsJsonAsync($"/api/material-relations/projects/{project.Id}/quantity-preview",new[]{change});previewResponse.EnsureSuccessStatusCode();
        var preview=(await previewResponse.Content.ReadFromJsonAsync<BomConfigurationQuantityPreview>())!;
        Assert.Contains(preview.Changes,row=>row.MaterialCode==drive.MaterialCode && row.Before==4 && row.After==8);
        var url=$"/api/projects/{project.Id}/boms/Standard?summary=true";
        object Body(BomConfigurationQuantityChange command)=>new {items=Array.Empty<BomItemInput>(),summaryRows=Array.Empty<BomSummaryEdit>(),configurationQuantities=new[]{command}};
        var denied=await client.PutAsJsonAsync(url,Body(change with {Confirmed=false,ConfirmationToken=preview.ConfirmationToken}));Assert.Equal(HttpStatusCode.BadRequest,denied.StatusCode);
        var oldDrive=Assert.Single(await repo.GetBomAsync(project.Id,BomKind.Electrical,default));
        await repo.ReplaceBomAsync(project.Id,BomKind.Electrical,[oldDrive with {Quantity=5}],default);
        var stale=await client.PutAsJsonAsync(url,Body(change with {ConfirmationToken=preview.ConfirmationToken}));Assert.Equal(HttpStatusCode.Conflict,stale.StatusCode);
        Assert.Equal(2,Assert.Single(await repo.GetBomAsync(project.Id,BomKind.Standard,default)).Quantity);
        previewResponse=await client.PostAsJsonAsync($"/api/material-relations/projects/{project.Id}/quantity-preview",new[]{change});previewResponse.EnsureSuccessStatusCode();
        preview=(await previewResponse.Content.ReadFromJsonAsync<BomConfigurationQuantityPreview>())!;
        var saved=await client.PutAsJsonAsync(url,Body(change with {ConfirmationToken=preview.ConfirmationToken}));Assert.True(saved.IsSuccessStatusCode,await saved.Content.ReadAsStringAsync());
        Assert.Equal(4,Assert.Single(await repo.GetBomAsync(project.Id,BomKind.Standard,default)).Quantity);
        Assert.Equal(8,Assert.Single(await repo.GetBomAsync(project.Id,BomKind.Electrical,default)).Quantity);
        Assert.True((await relations.GetCompletenessAsync(project.Id,"admin",UserRole.Administrator,default)).IsComplete);
    }
}
