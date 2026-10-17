using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Upton.Pdm.Api;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Api.Tests;

public sealed class DingTalkRobotNotificationTests
{
    private static readonly Guid ProjectId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private sealed class Storage : IProjectOplStorage
    {
        public static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl6N1sAAAAASUVORK5CYII=");
        public Task<Stream> OpenAsync(string root, Guid project, Guid id, CancellationToken ct) => Task.FromResult<Stream>(new MemoryStream(Png));
        public Task WriteAsync(string root, Guid project, Guid id, byte[] content, CancellationToken ct) => throw new NotSupportedException();
        public void Delete(string root, Guid project, Guid id) => throw new NotSupportedException();
    }
    private sealed class Handler(bool imageFails = false, bool messageFails = false) : HttpMessageHandler
    {
        public List<(Uri Url, string Body)> Calls = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct); Calls.Add((request.RequestUri!,body));
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/media/upload")
            {
                var part = Assert.Single(Assert.IsType<MultipartFormDataContent>(request.Content));
                Assert.Equal("现场.png",part.Headers.ContentDisposition!.FileNameStar);
                Assert.Equal("image/png",part.Headers.ContentType!.MediaType);
                Assert.Equal(Storage.Png,await part.ReadAsByteArrayAsync(ct));
            }
            if (path == "/v1.0/im/v1.0/robot/interactiveCards/send") Assert.Equal("app-token",request.Headers.GetValues("x-acs-dingtalk-access-token").Single());
            return new(path == "/v1.0/im/v1.0/robot/interactiveCards/send" && messageFails ? HttpStatusCode.BadRequest : HttpStatusCode.OK) { Content = new StringContent(path switch {
                "/v1.0/oauth2/accessToken" => "{\"accessToken\":\"app-token\"}",
                "/media/upload" => imageFails ? "{\"errcode\":1}" : "{\"errcode\":0,\"media_id\":\"@image-media\"}",
                "/v1.0/im/v1.0/robot/interactiveCards/send" => "{\"processQueryKey\":\"accepted\"}",
                _ => throw new Exception("Unexpected endpoint: "+path)
            }) };
        }
    }
    private static async Task<(DingTalkOplIntegration Integration,OplIssue Issue,Handler Handler)> Setup(bool imageFails=false,bool messageFails=false)
    {
        var repo=new InMemoryPdmRepository(TimeProvider.System);
        await repo.CreateUserAsync(new(Guid.NewGuid(),"owner","负责工程师","unused",UserRole.Engineer,true),default);
        var group=Guid.NewGuid();
        var options=new DingTalkOplOptions { Enabled=true,CorpId="corp",AppKey="app",AppSecret="secret",RobotCode="robot-code",CompanyId=Guid.NewGuid(),PublicUrl="http://192.168.2.8:5173",
            Groups=new(){[group]=new(){Name="项目群",OpenConversationId="cid-test",AccessToken="webhook",SigningSecret="SEC-test"}},Bindings=new(){[ProjectId]=group} };
        var handler=new Handler(imageFails,messageFails);
        var integration=new DingTalkOplIntegration(new(handler),Options.Create(options),TimeProvider.System,projects:repo,storage:new Storage());
        var issue=new OplIssue(Guid.NewGuid(),ProjectId,12,"现场问题全部内容","高","处理中","owner",new(2026,10,20),"解决措施全部内容","visitor",DateTimeOffset.UtcNow,null,"visitor",DateTimeOffset.UtcNow,1,[new(Guid.NewGuid(),"现场.png",Storage.Png.Length,ProjectId)],CreatedByDisplayName:"钉钉张工");
        return (integration,issue,handler);
    }
    [Fact]
    public async Task DefaultCardUsesLabelledFieldsOmitsCrossedOutContentAndKeepsImageWithoutMentioningAnyone()
    {
        var (integration,issue,handler)=await Setup();
        var result=await integration.NotifyAsync(issue,false,default); Assert.True(result.Sent); Assert.Equal("群通知已发送。",result.Message);
        var delivery=JsonDocument.Parse(handler.Calls.Single(x=>x.Url.AbsolutePath=="/v1.0/im/v1.0/robot/interactiveCards/send").Body);
        Assert.Equal("robot-code",delivery.RootElement.GetProperty("robotCode").GetString()); Assert.Equal("cid-test",delivery.RootElement.GetProperty("openConversationId").GetString());
        Assert.Equal("StandardCard",delivery.RootElement.GetProperty("cardTemplateId").GetString());
        using var data=JsonDocument.Parse(delivery.RootElement.GetProperty("cardData").GetString()!); var markdown=data.RootElement.GetProperty("contents")[0].GetProperty("text").GetString()!;
        foreach(var content in new[]{"问题描述："+issue.Description,"负责人：负责工程师","提交人：钉钉张工","优先级：高","实施状态：处理中"}) Assert.Contains(content,markdown);
        foreach(var content in new[]{issue.Solution,"2026-10-20","提交日期","需求完成日期","要求完成日期","###","变更内容："}) Assert.DoesNotContain(content,markdown);
        Assert.DoesNotContain("/attachments/",markdown);
        Assert.Equal("@image-media",data.RootElement.GetProperty("contents")[1].GetProperty("images")[0].GetString());
        using var mention=JsonDocument.Parse(delivery.RootElement.GetProperty("sendOptions").GetProperty("atUserListJson").GetString()!);
        Assert.Empty(mention.RootElement.EnumerateArray());
        Assert.False(delivery.RootElement.GetProperty("sendOptions").GetProperty("atAll").GetBoolean());
        Assert.DoesNotContain(handler.Calls,x=>x.Url.AbsolutePath=="/robot/send");
        Assert.Equal(3,handler.Calls.Count); // 仅取应用凭证、上传图片和发卡片，不查人员或成员。
        Assert.DoesNotContain(handler.Calls,x=>x.Url.AbsolutePath.Contains("sceneGroups") || x.Body.Contains("coolAppCode"));
        var link=data.RootElement.GetProperty("contents")[2].GetProperty("actions")[0].GetProperty("url").GetProperty("all").GetString()!;
        Assert.Contains("groupId="+integration.Settings.Bindings[ProjectId],link);
        Assert.Contains("dingtalkOpl="+ProjectId,link);
        Assert.DoesNotContain(handler.Calls,x=>x.Url.AbsolutePath=="/topapi/v2/user/getbymobile");
    }
    [Fact]
    public async Task ImageFailureIsReportedAndCardFailureDoesNotUseLegacyWebhook()
    {
        var (integration,issue,handler)=await Setup(imageFails:true);
        var result=await integration.NotifyAsync(issue,false,default); Assert.True(result.Sent); Assert.Contains("图片",result.Message); Assert.DoesNotContain("@",result.Message);
        Assert.DoesNotContain(handler.Calls,x=>x.Url.AbsolutePath=="/robot/send");
        (integration,issue,handler)=await Setup(messageFails:true);
        result=await integration.NotifyAsync(issue,false,default); Assert.False(result.Sent); Assert.DoesNotContain(handler.Calls,x=>x.Url.AbsolutePath=="/robot/send");
    }
    [Fact]
    public async Task LongDescriptionsAndSolutionsAreNotTruncated()
    {
        var (integration,issue,handler)=await Setup();
        integration.Settings.NotificationTemplate = "问题描述：{问题描述}\n解决措施：{解决措施}";
        issue=issue with {Description=new string('描',2000),Solution=new string('解',4000)};
        Assert.True((await integration.NotifyAsync(issue,false,default)).Sent);
        var messages=handler.Calls.Where(x=>x.Url.AbsolutePath=="/v1.0/im/v1.0/robot/interactiveCards/send").Select(x=> {
            using var outer=JsonDocument.Parse(x.Body); using var inner=JsonDocument.Parse(outer.RootElement.GetProperty("cardData").GetString()!); return inner.RootElement.GetProperty("contents")[0].GetProperty("text").GetString()!;
        }).ToArray();
        Assert.True(messages.Length>1); Assert.All(messages,text=>Assert.True(Encoding.UTF8.GetByteCount(text)<20000));
        Assert.Contains(issue.Description,string.Join("",messages)); Assert.Contains(issue.Solution,string.Join("",messages));
        Assert.All(handler.Calls.Where(x=>x.Url.AbsolutePath=="/v1.0/im/v1.0/robot/interactiveCards/send"),x=> {
            using var outer=JsonDocument.Parse(x.Body); using var at=JsonDocument.Parse(outer.RootElement.GetProperty("sendOptions").GetProperty("atUserListJson").GetString()!); Assert.Empty(at.RootElement.EnumerateArray());
        });
    }
    [Fact]
    public async Task CompletionUsesUpdateTitleAndChangeSummaryAndDoesNotNotifyForUnchangedContent()
    {
        var (integration,issue,handler)=await Setup();
        var completed=issue with {Status="已完成",CompletedAt=DateTimeOffset.UtcNow};
        Assert.True((await integration.NotifyAsync(completed,false,default,issue)).Sent);
        using var outer=JsonDocument.Parse(handler.Calls.Single(x=>x.Url.AbsolutePath=="/v1.0/im/v1.0/robot/interactiveCards/send").Body);
        using var card=JsonDocument.Parse(outer.RootElement.GetProperty("cardData").GetString()!);
        Assert.Equal("OPL 问题已完成",card.RootElement.GetProperty("header").GetProperty("title").GetProperty("text").GetString());
        Assert.Contains("变更内容：实施状态由处理中改为已完成",card.RootElement.GetProperty("contents")[0].GetProperty("text").GetString());
        var calls=handler.Calls.Count;
        Assert.False((await integration.NotifyAsync(completed,false,default,completed)).Sent);
        Assert.Equal(calls,handler.Calls.Count);
    }
    [Fact]
    public async Task AssignedOwnerAndLegacyUserMappingDoNotTriggerPeopleQueriesOrMentions()
    {
        var (integration,issue,handler)=await Setup();
        integration.Settings.Users["mapped-user"]="owner";
        Assert.Equal("群通知已发送。",(await integration.NotifyAsync(issue,false,default)).Message);
        Assert.DoesNotContain(handler.Calls,x=>x.Url.AbsolutePath=="/topapi/v2/user/getbymobile");
        using var card=JsonDocument.Parse(handler.Calls.Single(x=>x.Url.AbsolutePath=="/v1.0/im/v1.0/robot/interactiveCards/send").Body);
        using var at=JsonDocument.Parse(card.RootElement.GetProperty("sendOptions").GetProperty("atUserListJson").GetString()!);
        Assert.Empty(at.RootElement.EnumerateArray());
        Assert.Equal(3,handler.Calls.Count);
    }
    [Fact]
    public async Task RobotEntryRetainsItsGroupBinding()
    {
        var (integration,_,handler)=await Setup();
        var groupId=integration.Settings.Bindings[ProjectId];
        Assert.True((await integration.PublishEntryAsync(groupId,default)).Sent);
        using var outer=JsonDocument.Parse(handler.Calls.Single(x=>x.Url.AbsolutePath=="/v1.0/im/v1.0/robot/interactiveCards/send").Body);
        using var card=JsonDocument.Parse(outer.RootElement.GetProperty("cardData").GetString()!);
        var link=card.RootElement.GetProperty("contents")[1].GetProperty("actions")[0].GetProperty("url").GetProperty("all").GetString()!;
        Assert.Contains("groupId="+groupId,link);
        Assert.Contains("companyId="+integration.Settings.CompanyId,link);
        Assert.Equal("cid-test",outer.RootElement.GetProperty("openConversationId").GetString());
    }
}
