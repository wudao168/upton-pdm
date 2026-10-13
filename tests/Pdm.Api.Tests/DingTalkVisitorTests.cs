using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Upton.Pdm.Api;
using Upton.Pdm.Application;

namespace Upton.Pdm.Api.Tests;

public sealed class DingTalkVisitorTests
{
    private sealed class Handler : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            var body = request.RequestUri!.AbsolutePath switch {
                "/v1.0/oauth2/accessToken" => "{\"accessToken\":\"token\"}",
                "/topapi/v2/user/getuserinfo" => "{\"errcode\":0,\"result\":{\"userid\":\"no-plm-account\",\"name\":\"钉钉访客王工\"}}",
                "/v1.0/im/v1.0/robot/interactiveCards/send" => "{\"processQueryKey\":\"ok\"}",
                _ => throw new Exception("Unexpected DingTalk API: " + request.RequestUri.AbsolutePath)
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
    private sealed class Storage : IProjectOplStorage
    {
        public Dictionary<Guid, byte[]> Files = [];
        public Task WriteAsync(string root, Guid projectId, Guid id, byte[] content, CancellationToken ct) { Files[id]=content; return Task.CompletedTask; }
        public Task<Stream> OpenAsync(string root, Guid projectId, Guid id, CancellationToken ct) => Task.FromResult<Stream>(new MemoryStream(Files[id]));
        public void Delete(string root, Guid projectId, Guid id) => Files.Remove(id);
    }
    private static MultipartFormDataContent Body(string description, Guid? child = null)
    {
        var body = new MultipartFormDataContent();
        body.Add(new StringContent(JsonSerializer.Serialize(new { description, relatedProjectId = child, priority = "高", status = "处理中", owner = "responsible", dueDate = "2026-10-20", solution = "现场检查中", createdBy = "伪造姓名" })), "data");
        body.Add(new ByteArrayContent([1,2,3]),"files","现场.txt"); return body;
    }
    [Fact]
    public async Task UnmappedVisitorSubmitsWithVerifiedNameAndCannotReadOrMutateOpl()
    {
        using var baseFactory = new PdmApiFactory();
        var repo = baseFactory.Services.GetRequiredService<IPdmRepository>();
        var numbering = await repo.GetProjectNumberingOptionsAsync(default); var company = numbering.Organizations.First().Id;
        var customer = (await repo.ListCustomersAsync(false,default)).First().Id;
        await repo.CreateUserAsync(new(Guid.NewGuid(),"responsible","负责人","unused",Upton.Pdm.Domain.UserRole.Administrator,true),default);
        await repo.SetUserCompanyScopeAsync("responsible",company,false,[],"admin",default);
        var type = numbering.ProjectTypes.First().Code; var equipment = numbering.EquipmentTypes.First().Code;
        var root = await repo.CreateNumberedProjectAsync(new(company,type,equipment,customer,"访客项目一",null,new(2026,10,13),1,"admin",Path.GetTempPath(),Path.GetTempPath()),default);
        var second = await repo.CreateNumberedProjectAsync(new(company,type,equipment,customer,"访客项目二",null,new(2026,10,13),1,"admin",Path.GetTempPath(),Path.GetTempPath()),default);
        var unrelated = await repo.CreateNumberedProjectAsync(new(company,type,equipment,customer,"其他群项目",null,new(2026,10,13),1,"admin",Path.GetTempPath(),Path.GetTempPath()),default);
        var child = await repo.CreateSubprojectAsync(new(root.Id,"子项目",null,1),default);
        var otherChild = await repo.CreateSubprojectAsync(new(second.Id,"其他子项目",null,1),default);
        var group = Guid.NewGuid(); var otherGroup = Guid.NewGuid(); var handler = new Handler(); var storage = new Storage();
        var options = new DingTalkOplOptions { Enabled=true,CorpId="corp",AppKey="key",AppSecret="secret",RobotCode="robot",CompanyId=company,PublicUrl="http://192.168.2.8:5173",
            Groups = new() { [group]=new() { Name="共享群",OpenConversationId="cid-group",AccessToken="robot",SigningSecret="SEC-secret" }, [otherGroup]=new() {Name="其他群"} },
            Bindings = new() { [root.Id]=group,[second.Id]=group,[unrelated.Id]=otherGroup } };
        using var factory = baseFactory.WithWebHostBuilder(builder=>builder.ConfigureServices(services=>{
            services.AddSingleton(repo); services.AddSingleton<IProjectOplStorage>(storage);
            services.AddSingleton(new DingTalkOplIntegration(new(handler),Options.Create(options),TimeProvider.System));
        }));
        using var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/integrations/dingtalk/opl/login", new { code="auth-code",companyId=company,groupId=group });
        Assert.Equal(HttpStatusCode.OK,login.StatusCode);
        var session = await login.Content.ReadFromJsonAsync<JsonElement>(); Assert.False(session.TryGetProperty("accessToken",out _));
        var ticket = session.GetProperty("guestToken").GetString()!;
        client.DefaultRequestHeaders.Add("X-DingTalk-Session",ticket);
        var projects = await client.GetFromJsonAsync<JsonElement>("/api/integrations/dingtalk/opl/guest/projects"); Assert.Equal(2,projects.GetArrayLength());
        Assert.DoesNotContain("rows",projects.GetRawText()); Assert.DoesNotContain("owner",projects.GetRawText());
        var response = await client.PostAsync($"/api/integrations/dingtalk/opl/guest/projects/{root.Id}/submit",Body("现场问题",child.Id));
        Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        var receipt = await response.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal("钉钉访客王工",receipt.GetProperty("submittedBy").GetString()); Assert.True(receipt.GetProperty("notificationSent").GetBoolean());
        var issue = Assert.Single(await factory.Services.GetRequiredService<IProjectOplRepository>().ListAsync(root.Id,default));
        Assert.Equal("钉钉访客王工",issue.CreatedByDisplayName); Assert.StartsWith("dingtalk:",issue.CreatedBy); Assert.Equal("处理中",issue.Status); Assert.Equal("高",issue.Priority); Assert.Equal("现场检查中",issue.Solution); Assert.Equal("responsible",issue.Owner); Assert.Equal(new DateOnly(2026,10,20),issue.DueDate); Assert.Equal(child.Id,issue.RelatedProjectId); Assert.Single(storage.Files);
        Assert.Null(await repo.FindUserAsync("no-plm-account",default));
        var updated = await new ProjectOplService(repo, factory.Services.GetRequiredService<IProjectOplRepository>(), storage, TimeProvider.System)
            .SaveAsync(root.Id, issue.Id, new("经理补充问题描述", "普通", "处理中", "", null, "处理中", issue.RowVersion, issue.Attachments.Select(file => file.Id).ToArray(), child.Id), [], "admin", Upton.Pdm.Domain.UserRole.Administrator, default);
        Assert.Equal("钉钉访客王工", updated.CreatedByDisplayName);
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.GetAsync($"/api/projects/{root.Id}/opl")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.PostAsJsonAsync($"/api/projects/{root.Id}/opl/{issue.Id}/complete",new{expectedRowVersion=1})).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.PostAsync($"/api/projects/{root.Id}/opl/{issue.Id}/notify",null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsync($"/api/integrations/dingtalk/opl/guest/projects/{unrelated.Id}/submit",Body("错误入口"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsync($"/api/integrations/dingtalk/opl/guest/projects/{root.Id}/submit",Body("错误子项目",otherChild.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsync($"/api/integrations/dingtalk/opl/guest/projects/{root.Id}/submit",Body(""))).StatusCode);
        Assert.Single(storage.Files);
        client.DefaultRequestHeaders.Authorization = new("Bearer",ticket);
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.GetAsync($"/api/projects/{root.Id}/opl")).StatusCode);
        client.DefaultRequestHeaders.Authorization = null; client.DefaultRequestHeaders.Remove("X-DingTalk-Session"); client.DefaultRequestHeaders.Add("X-DingTalk-Session",ticket+"tampered");
        Assert.Equal(HttpStatusCode.Forbidden,(await client.GetAsync("/api/integrations/dingtalk/opl/guest/projects")).StatusCode);
        Assert.Equal(4,handler.Calls);
    }
    private sealed class Clock : TimeProvider { public DateTimeOffset Now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow()=>Now; }
    [Fact]
    public void VisitorSessionExpiresAndIsBoundToItsOriginalCompanyAndGroup()
    {
        var clock=new Clock(); var service=new DingTalkVisitorSession(new EphemeralDataProtectionProvider(),clock);
        var company=Guid.NewGuid(); var group=Guid.NewGuid(); var token=service.Issue(new(){CompanyId=company,CorpId="corp"},group,new("userid","张三"));
        var context=new DefaultHttpContext(); context.Request.Headers["X-DingTalk-Session"]=token;
        var read=service.Read(context); Assert.Equal(company,read.CompanyId);Assert.Equal(group,read.GroupId);Assert.Equal("张三",read.Name);
        clock.Now=clock.Now.AddHours(3); Assert.Throws<UnauthorizedAccessException>(()=>service.Read(context));
    }
}
