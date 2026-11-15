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

public sealed class MergedReleaseReviewApiTests
{
    [Fact]
    public async Task DirectEndpointChecksActualNodeRevocationAndProjectCompanyScope()
    {
        await using var factory=new PdmApiFactory();
        using var client=factory.CreateClient();
        var repo=factory.Services.GetRequiredService<IPdmRepository>();
        var projectId=Guid.Parse("11111111-1111-1111-1111-111111111111");
        var drawing=await repo.RegisterDocumentAsync(new(projectId,"API-MERGED","接口验收图纸","API-MERGED.SLDDRW",DocumentKind.Drawing),"admin",default);
        var session=Guid.NewGuid(); var now=DateTimeOffset.UtcNow;
        await repo.CheckoutAsync(drawing.Id,"admin",session,"TEST",now.AddMinutes(15),null,default);
        var node=new DocumentReferenceNode(Guid.NewGuid(),drawing.Id,"API-MERGED",drawing.FileName,drawing.Name,DocumentKind.Drawing,"默认",1,ReferenceNodeStatus.Normal,drawing.Revision,null,[]);
        var version=(await repo.CheckInVersionAsync(drawing.Id,"admin",session,new(new("source.slddrw",10,"hash",now),"存档",new Dictionary<string,string?>(),new(Guid.NewGuid(),projectId,drawing.Id,now,"admin",node,"root"),[],[]),default)).Version!;
        var id=Guid.NewGuid();var taskId=Guid.NewGuid();
        var package=new ReleasePackage(id,projectId,"RP-API-MERGED",ReleasePackageState.ProcessReview,null,"","",[
            new(taskId,id,ApprovalStage.MainDesigner,"admin",null,null,null,null){StepOrder=1}],now,null,null)
        {Scope=ReleaseScope.NonStandardWithDrawing,DrawingReview=new(1,[new(drawing.Id,version.Id,"hash",drawing.FileName,"token",[],true,"Pending",[])])};
        await repo.CreateReleasePackageAsync(package,default);
        var url=$"/api/release-packages/{id}/drawing-review";
        var body=new {approvalTaskId=taskId,roundId=Guid.Empty,documentId=drawing.Id,token="token",status="Issue",comment="尺寸待核对"};
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.PostAsJsonAsync(url,body)).StatusCode);
        Sign(client,"engineer","Administrator"); // JWT中的角色不能改变真实授权。
        Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsJsonAsync(url,body)).StatusCode);
        Sign(client,"admin","Administrator");
        (await client.PostAsJsonAsync(url,body)).EnsureSuccessStatusCode();
        var previewResponse=await client.PostAsJsonAsync($"/api/release-packages/{id}/drawing-review/{drawing.Id}/preview",new {});
        Assert.Equal(HttpStatusCode.BadRequest,previewResponse.StatusCode);
        Assert.Contains("最终发布阶段",await previewResponse.Content.ReadAsStringAsync());
        (await client.GetAsync($"/api/projects/{projectId}/release-drawing-reviews")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden,(await client.GetAsync($"/api/projects/{Guid.NewGuid()}/release-drawing-reviews")).StatusCode);
        var nodeUrl=$"/api/release-packages/{id}/drawing-review/node";
        Assert.Equal(HttpStatusCode.Conflict,(await client.PostAsJsonAsync(nodeUrl,new {approvalTaskId=taskId,roundId=Guid.NewGuid(),decision=0,comment="旧轮次"})).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsJsonAsync(nodeUrl,new {approvalTaskId=taskId,roundId=Guid.Empty,decision=0,comment="未完成不能直接通过"})).StatusCode);
        var blankRejection = new { approvalTaskId=taskId, roundId=Guid.Empty, decision=1, comment=" " };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(nodeUrl, blankRejection)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(nodeUrl+"/emergency", blankRejection)).StatusCode);
        Sign(client,"engineer","Administrator");
        var bareApproval = new { approvalTaskId=taskId, roundId=Guid.Empty, documentId=drawing.Id, token="token", status="Confirmed", comment="" };
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(url+"/emergency", bareApproval)).StatusCode);
        Sign(client,"admin","Administrator");
        Assert.Equal("Issue",(await repo.FindReleasePackageAsync(id,default))!.DrawingReview!.Items[0].Status);
        var gate=await client.PostAsJsonAsync($"/api/approval-tasks/{taskId}/decision",new {decision=0,comment="通过"});
        Assert.Equal(HttpStatusCode.BadRequest,gate.StatusCode);
        Assert.Contains("等待修改存档或撤回退回",await gate.Content.ReadAsStringAsync());
        var unknown=await client.PostAsJsonAsync(url,new {approvalTaskId=taskId,roundId=Guid.Empty,documentId=Guid.NewGuid(),token="token",status="Issue",comment="不能操作别的图纸"});
        Assert.Equal(HttpStatusCode.NotFound,unknown.StatusCode);
        var settings=await repo.GetAuthorizationSettingsAsync(default);
        var revoked=settings with {Templates=settings.Templates.Select(t=>t.RoleCode=="Administrator" ? t with {Grants=t.Grants.Where(g=>g.Permission!=PermissionCodes.ApprovalDecide).ToArray()} : t).ToArray()};
        await repo.SaveAuthorizationSettingsAsync(revoked,settings.Revision,default);
        Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsJsonAsync(url,body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsJsonAsync(nodeUrl,new {approvalTaskId=taskId,roundId=Guid.Empty,decision=1,comment="撤权后不能退回"})).StatusCode);
        Assert.False((await repo.GetUserPermissionsAsync("admin",UserRole.Administrator,default)).Contains(PermissionCodes.ApprovalDecide));
        await repo.SaveAuthorizationSettingsAsync(settings,(await repo.GetAuthorizationSettingsAsync(default)).Revision,default);
        client.DefaultRequestHeaders.Add("X-Company-Id",Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.PostAsJsonAsync(url,body)).StatusCode);
        client.DefaultRequestHeaders.Remove("X-Company-Id");
        var retired=await client.PostAsJsonAsync($"/api/projects/{projectId}/drawing-reviews",new { });
        Assert.Equal(HttpStatusCode.Conflict,retired.StatusCode);
        Assert.Contains("已并入非标件发布流程",await retired.Content.ReadAsStringAsync());
        Assert.Single((await repo.FindReleasePackageAsync(id,default))!.DrawingReview!.Items[0].Notes);
    }
    private static void Sign(HttpClient client,string actor,string role)
    {
        var token=new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("upton-pdm","upton-pdm-clients",
            [new Claim(ClaimTypes.Name,actor),new Claim(ClaimTypes.Role,role)],expires:DateTime.UtcNow.AddMinutes(10),
            signingCredentials:new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")),SecurityAlgorithms.HmacSha256)));
        client.DefaultRequestHeaders.Authorization=new("Bearer",token);
    }
}
