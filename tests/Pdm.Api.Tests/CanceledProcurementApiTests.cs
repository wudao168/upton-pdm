using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api.Tests;

public sealed class CanceledProcurementApiTests
{
    [Fact]
    public async Task EmptyPublishedBomRetainsCanceledMaterialInPreparationResponse()
    {
        await using var factory=new PdmApiFactory();using var client=factory.CreateClient();using var scope=factory.Services.CreateScope();
        var token=new JwtSecurityToken("upton-pdm","upton-pdm-clients",[new(ClaimTypes.Name,"admin"),new(ClaimTypes.Role,"Administrator")],expires:DateTime.UtcNow.AddMinutes(10),
            signingCredentials:new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")),SecurityAlgorithms.HmacSha256));
        client.DefaultRequestHeaders.Authorization=new("Bearer",new JwtSecurityTokenHandler().WriteToken(token));
        var repository=scope.ServiceProvider.GetRequiredService<IPdmRepository>();var now=DateTimeOffset.UtcNow;
        var project=await ApiTestAuthorization.CreateProjectAsync(repository, new("CANCEL-"+Guid.NewGuid().ToString("N"),"取消备料接口验收","admin","vault","release"),"admin",default);
        var item=new BomItem(Guid.NewGuid(),project.Id,BomKind.Standard,1,"01021000192","6004ZZ",2,"001",null,"6004ZZ","W1",true);
        await repository.CreateReleasePackageAsync(new(Guid.NewGuid(),project.Id,"RP-ORIGINAL",ReleasePackageState.Published,null,"","",[],now.AddMinutes(-3),now.AddMinutes(-2),null)
            {Scope=ReleaseScope.StandardFormal,StandardBomSnapshot=[item]},default);
        await repository.CreateReleasePackageAsync(new(Guid.NewGuid(),project.Id,"RP-CANCELED",ReleasePackageState.Published,null,"","",[],now.AddMinutes(-1),now,null)
            {Scope=ReleaseScope.StandardFormal,StandardBomSnapshot=[],ConfirmedRemovedBomItemIds=[item.Id]},default);
        var response=await client.GetAsync($"/api/projects/{project.Id}/procurement-tracking");response.EnsureSuccessStatusCode();
        var result=(await response.Content.ReadFromJsonAsync<ProjectProcurementTrackingResult>())!;
        var row=Assert.Single(result.Items);Assert.Equal(item.DrawingNumber,row.MaterialCode);Assert.Equal(item.Name,row.MaterialName);
        Assert.True(row.IsDemandCanceled);Assert.Equal("已取消",row.PurchaseRequisitionStatus);Assert.Equal(0,row.Quantity);Assert.True(result.HasPublishedBom);
        var original=(await repository.ListReleasePackagesAsync(project.Id,default)).Single(package=>package.Number=="RP-ORIGINAL");
        Assert.Equal(2,Assert.Single(original.StandardBomSnapshot).Quantity);
    }
}
