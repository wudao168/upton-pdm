using System.Text.Json;
namespace Upton.Pdm.Api.Tests;
public sealed class DingTalkStreamTests
{
    [Fact]
    public void GatewayAddressMustRemainOnDingTalkAndTicketIsEscaped()
    {
        Assert.Equal("wss://stream.dingtalk.com/connect?ticket=a%26b",DingTalkStreamService.ConnectionUri("wss://stream.dingtalk.com/connect","a&b").AbsoluteUri);
        foreach(var endpoint in new[]{"ws://stream.dingtalk.com/connect","wss://dingtalk.com.evil.example/connect","wss://user@dingtalk.com/connect","wss://dingtalk.com/connect?x=1"})
            Assert.Throws<InvalidOperationException>(()=>DingTalkStreamService.ConnectionUri(endpoint,"ticket"));
    }
    [Fact]
    public async Task PingEchoesOpaqueDataAndCallbackAcknowledgesOnlyAfterPersistence()
    {
        static byte[] Frame(string type,string topic,string data)=>JsonSerializer.SerializeToUtf8Bytes(new{type,headers=new{topic,messageId="message-1"},data});
        var calls=0;
        using var ping=JsonDocument.Parse((await DingTalkStreamService.HandleFrameAsync(Frame("SYSTEM","ping","{\"opaque\":\"token\"}"),_=>{calls++;return Task.CompletedTask;},default))!);
        Assert.Equal("{\"opaque\":\"token\"}",ping.RootElement.GetProperty("data").GetString()); Assert.Equal(0,calls);
        using var ack=JsonDocument.Parse((await DingTalkStreamService.HandleFrameAsync(Frame("CALLBACK","/v1.0/im/bot/messages/get","{\"conversationId\":\"cid\"}"),body=>{Assert.Equal("cid",body.GetProperty("conversationId").GetString());calls++;return Task.CompletedTask;},default))!);
        Assert.Equal(1,calls);Assert.Equal(200,ack.RootElement.GetProperty("code").GetInt32());Assert.Equal("message-1",ack.RootElement.GetProperty("headers").GetProperty("messageId").GetString());
        using var failed=JsonDocument.Parse((await DingTalkStreamService.HandleFrameAsync(Frame("CALLBACK","/v1.0/im/bot/messages/get","{}"),_=>throw new IOException(),default))!);
        Assert.Equal(500,failed.RootElement.GetProperty("code").GetInt32());
        Assert.Null(await DingTalkStreamService.HandleFrameAsync(Frame("SYSTEM","disconnect","{}"),_=>Task.CompletedTask,default));
    }
}
