using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Upton.Pdm.Api;

public sealed record DingTalkStreamStatus(string State, string Message, DateTimeOffset UpdatedAt);
public sealed class DingTalkStreamStatusCache(TimeProvider clock)
{
    private readonly ConcurrentDictionary<Guid,DingTalkStreamStatus> states = new();
    public DingTalkStreamStatus Read(Guid companyId) => states.GetValueOrDefault(companyId) ?? new("unconfigured","填写应用凭证和RobotCode并启用后，系统会自动建立群登记连接。",clock.GetUtcNow());
    public void Set(Guid id,string state,string message) => states[id]=new(state,message,clock.GetUtcNow());
}

public sealed class DingTalkStreamService(IServiceScopeFactory scopes, IHttpClientFactory clients, DingTalkStreamStatusCache status, ILogger<DingTalkStreamService> logger) : BackgroundService
{
    private sealed record Connection(string Key, CancellationTokenSource Stop, Task Task);
    private readonly Dictionary<Guid,Connection> connections = [];
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope=scopes.CreateScope();
                    var settings=await scope.ServiceProvider.GetRequiredService<DingTalkConfigurationService>().ListStreamConfigurationsAsync(stoppingToken);
                    foreach(var id in connections.Keys.Except(settings.Select(x=>x.CompanyId)).ToArray())
                    { await StopAsync(id); status.Set(id,"disabled","企业机器人连接已停用。"); }
                    foreach(var setting in settings)
                    {
                        var key=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(setting.CorpId+"\n"+setting.AppKey+"\n"+setting.AppSecret+"\n"+setting.RobotCode)));
                        if(connections.TryGetValue(setting.CompanyId,out var existing) && existing.Key==key && !existing.Task.IsCompleted) continue;
                        await StopAsync(setting.CompanyId);
                        var stop=CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                        connections[setting.CompanyId]=new(key,stop,RunAsync(setting,stop.Token));
                    }
                }
                catch(Exception error) when(!stoppingToken.IsCancellationRequested) { logger.LogWarning("钉钉连接配置读取失败，原因类型 {ErrorType}",error.GetType().Name); }
                await Task.Delay(TimeSpan.FromSeconds(15),stoppingToken);
            }
        }
        catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested) { }
        finally { foreach(var id in connections.Keys.ToArray()) await StopAsync(id); }
    }
    private async Task StopAsync(Guid id)
    {
        if(!connections.Remove(id,out var old)) return;
        old.Stop.Cancel(); try { await old.Task; } catch(OperationCanceledException) { } catch(Exception error) { logger.LogWarning("钉钉连接结束，原因类型 {ErrorType}",error.GetType().Name); } finally { old.Stop.Dispose(); }
    }
    public static Uri ConnectionUri(string endpoint,string ticket)
    {
        if(!Uri.TryCreate(endpoint,UriKind.Absolute,out var uri) || uri.Scheme!="wss" || !(uri.Host=="dingtalk.com" || uri.Host.EndsWith(".dingtalk.com",StringComparison.OrdinalIgnoreCase)) || uri.UserInfo.Length>0 || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || ticket.Length is 0 or > 4096)
            throw new InvalidOperationException("钉钉返回的连接地址无效。");
        return new Uri(endpoint+"?ticket="+Uri.EscapeDataString(ticket));
    }
    public static async Task<byte[]?> HandleFrameAsync(byte[] message,Func<JsonElement,Task> register,CancellationToken ct)
    {
        using var frame=JsonDocument.Parse(message);
        var root=frame.RootElement; var type=root.GetProperty("type").GetString(); var headers=root.GetProperty("headers"); var topic=headers.GetProperty("topic").GetString();
        if(type=="SYSTEM" && topic=="disconnect") return null;
        var code=200; var data="{\"response\":null}";
        if(type=="SYSTEM" && topic=="ping") data=root.GetProperty("data").GetString()!;
        else if(type=="CALLBACK" && topic=="/v1.0/im/bot/messages/get")
        {
            try { using var body=JsonDocument.Parse(root.GetProperty("data").GetString()!); await register(body.RootElement); }
            catch(Exception) when(!ct.IsCancellationRequested) { code=500; }
        }
        else code=404;
        return JsonSerializer.SerializeToUtf8Bytes(new {code,message=code==200?"OK":"ERROR",headers=new {contentType="application/json",messageId=headers.GetProperty("messageId").GetString()},data});
    }
    private async Task RunAsync(DingTalkOplOptions setting,CancellationToken ct)
    {
        var delay=5;
        while(!ct.IsCancellationRequested)
        {
            try
            {
                status.Set(setting.CompanyId,"connecting","正在连接钉钉群登记服务…");
                using var http=clients.CreateClient("dingtalk-stream");
                using var response=await http.PostAsJsonAsync("https://api.dingtalk.com/v1.0/gateway/connections/open",new {
                    clientId=setting.AppKey,clientSecret=setting.AppSecret,ua="upton-plm/1.0",subscriptions=new[]{new {type="CALLBACK",topic="/v1.0/im/bot/messages/get"}}
                },ct);
                response.EnsureSuccessStatusCode();
                using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                var uri=ConnectionUri(json.RootElement.GetProperty("endpoint").GetString()!,json.RootElement.GetProperty("ticket").GetString()!);
                using var socket=new ClientWebSocket();
                socket.Options.KeepAliveInterval=TimeSpan.FromSeconds(30); socket.Options.KeepAliveTimeout=TimeSpan.FromSeconds(30);
                using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(30));
                await socket.ConnectAsync(uri,timeout.Token);
                status.Set(setting.CompanyId,"connected","已连接。将机器人加入项目群，@机器人发送“登记项目群”，即可在项目设置中选择该群。");
                delay=5;
                while(socket.State==WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    using var message=new MemoryStream(); var buffer=new byte[8192]; WebSocketReceiveResult received;
                    do {
                        received=await socket.ReceiveAsync(new ArraySegment<byte>(buffer),ct);
                        if(received.MessageType==WebSocketMessageType.Close) break;
                        if(received.MessageType!=WebSocketMessageType.Text || message.Length+received.Count>1024*1024) throw new InvalidOperationException("钉钉消息格式异常。");
                        message.Write(buffer,0,received.Count);
                    } while(!received.EndOfMessage);
                    if(received.MessageType==WebSocketMessageType.Close) break;
                    var ack=await HandleFrameAsync(message.ToArray(), async body => {
                        try {
                            using var scope=scopes.CreateScope();
                            await scope.ServiceProvider.GetRequiredService<DingTalkConfigurationService>().RegisterGroupAsync(setting.CompanyId,setting.CorpId,setting.AppKey,setting.RobotCode,body,ct);
                        } catch(Exception error) when(!ct.IsCancellationRequested) {
                            logger.LogWarning("钉钉群登记未完成，公司 {CompanyId}，原因类型 {ErrorType}",setting.CompanyId,error.GetType().Name);
                            throw;
                        }
                    },ct);
                    if(ack is null) break;
                    await socket.SendAsync(new ArraySegment<byte>(ack),WebSocketMessageType.Text,true,ct);
                }
            }
            catch(Exception error) when(!ct.IsCancellationRequested)
            { logger.LogWarning("钉钉Stream连接中断，公司 {CompanyId}，原因类型 {ErrorType}",setting.CompanyId,error.GetType().Name); }
            if(ct.IsCancellationRequested) break;
            status.Set(setting.CompanyId,"retrying","钉钉连接未就绪，正在自动重试。请检查机器人是否启用Stream模式，以及服务器能否访问钉钉。");
            await Task.Delay(TimeSpan.FromSeconds(delay),ct); delay=Math.Min(delay*2,60);
        }
    }
}
