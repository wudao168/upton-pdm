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
            var body = await request.Content!.ReadAsStringAsync(ct); Calls.Add((request.RequestUri!,body));
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
                "/topapi/v2/user/getbymobile" => "{\"errcode\":0,\"result\":{\"userid\":\"ding-owner\"}}",
                _ => throw new Exception("Unexpected endpoint: "+path)
            }) };
        }
    }
    private static async Task<(DingTalkOplIntegration Integration,OplIssue Issue,Handler Handler)> Setup(bool imageFails=false,bool messageFails=false,bool phone=true)
    {
        var repo=new InMemoryPdmRepository(TimeProvider.System);
        await repo.CreateUserAsync(new(Guid.NewGuid(),"owner","负责工程师","unused",UserRole.Engineer,true),default);
        await repo.UpdateUserProfileAsync("owner",null,"unspecified",null,phone?"+86 13800000000":null,null,null,default);
        var group=Guid.NewGuid();
        var options=new DingTalkOplOptions { Enabled=true,CorpId="corp",AppKey="app",AppSecret="secret",RobotCode="robot-code",CompanyId=Guid.NewGuid(),PublicUrl="http://192.168.2.8:5173",
            Groups=new(){[group]=new(){Name="项目群",OpenConversationId="cid-test",AccessToken="webhook",SigningSecret="SEC-test"}},Bindings=new(){[ProjectId]=group} };
        var handler=new Handler(imageFails,messageFails);
        var integration=new DingTalkOplIntegration(new(handler),Options.Create(options),TimeProvider.System,projects:repo,storage:new Storage());
        var issue=new OplIssue(Guid.NewGuid(),ProjectId,12,"现场问题全部内容","高","处理中","owner",new(2026,10,20),"解决措施全部内容","visitor",DateTimeOffset.UtcNow,null,"visitor",DateTimeOffset.UtcNow,1,[new(Guid.NewGuid(),"现场.png",Storage.Png.Length,ProjectId)],CreatedByDisplayName:"钉钉张工");
        return (integration,issue,handler);
    }
    [Fact]
    public async Task SingleRobotCardReceivesAllFieldsUploadedImageAndRealMention()
    {
        var (integration,issue,handler)=await Setup();
        var result=await integration.NotifyAsync(issue,false,default); Assert.True(result.Sent); Assert.Equal("群通知已发送。",result.Message);
        var delivery=JsonDocument.Parse(handler.Calls.Single(x=>x.Url.AbsolutePath=="/v1.0/im/v1.0/robot/interactiveCards/send").Body);
        Assert.Equal("robot-code",delivery.RootElement.GetProperty("robotCode").GetString()); Assert.Equal("cid-test",delivery.RootElement.GetProperty("openConversationId").GetString());
        Assert.Equal("StandardCard",delivery.RootElement.GetProperty("cardTemplateId").GetString());
        using var data=JsonDocument.Parse(delivery.RootElement.GetProperty("cardData").GetString()!); var markdown=data.RootElement.GetProperty("contents")[0].GetProperty("text").GetString()!;
        foreach(var content in new[]{issue.Description,issue.Solution,"负责工程师","2026-10-20","钉钉张工","高","处理中"}) Assert.Contains(content,markdown);
        Assert.DoesNotContain("/attachments/",markdown);
        Assert.Equal("@image-media",data.RootElement.GetProperty("contents")[1].GetProperty("images")[0].GetString());
        using var mention=JsonDocument.Parse(delivery.RootElement.GetProperty("sendOptions").GetProperty("atUserListJson").GetString()!);
        Assert.Equal("ding-owner",mention.RootElement[0].GetProperty("userId").GetString());
        Assert.Equal("负责工程师",mention.RootElement[0].GetProperty("nickName").GetString());
        Assert.False(delivery.RootElement.GetProperty("sendOptions").GetProperty("atAll").GetBoolean());
        Assert.DoesNotContain(handler.Calls,x=>x.Url.AbsolutePath=="/robot/send");
        using var lookup=JsonDocument.Parse(handler.Calls.Single(x=>x.Url.AbsolutePath=="/topapi/v2/user/getbymobile").Body);
        Assert.Equal("13800000000",lookup.RootElement.GetProperty("mobile").GetString());
    }
    [Fact]
    public async Task PartialDeliveryAndMissingMentionInformationAreReported()
    {
        var (integration,issue,handler)=await Setup(imageFails:true,phone:false);
        var result=await integration.NotifyAsync(issue,false,default); Assert.True(result.Sent); Assert.Contains("图片",result.Message); Assert.Contains("@未发送",result.Message);
        Assert.DoesNotContain(handler.Calls,x=>x.Url.AbsolutePath=="/robot/send");
        (integration,issue,handler)=await Setup(messageFails:true);
        result=await integration.NotifyAsync(issue,false,default); Assert.False(result.Sent); Assert.DoesNotContain(handler.Calls,x=>x.Url.AbsolutePath=="/robot/send");
    }
    [Fact]
    public async Task LongDescriptionsAndSolutionsAreNotTruncated()
    {
        var (integration,issue,handler)=await Setup();
        issue=issue with {Description=new string('描',2000),Solution=new string('解',4000)};
        Assert.True((await integration.NotifyAsync(issue,false,default)).Sent);
        var messages=handler.Calls.Where(x=>x.Url.AbsolutePath=="/v1.0/im/v1.0/robot/interactiveCards/send").Select(x=> {
            using var outer=JsonDocument.Parse(x.Body); using var inner=JsonDocument.Parse(outer.RootElement.GetProperty("cardData").GetString()!); return inner.RootElement.GetProperty("contents")[0].GetProperty("text").GetString()!;
        }).ToArray();
        Assert.True(messages.Length>1); Assert.All(messages,text=>Assert.True(Encoding.UTF8.GetByteCount(text)<20000));
        Assert.Contains(issue.Description,string.Join("",messages)); Assert.Contains(issue.Solution,string.Join("",messages));
    }
}
