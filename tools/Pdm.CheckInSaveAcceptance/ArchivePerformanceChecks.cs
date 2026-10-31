using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static partial class Program
{
    sealed class ArchiveBatchHandler : HttpMessageHandler
    {
        internal int Calls, Active, Maximum, FailuresRemaining;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            var active = Interlocked.Increment(ref Active);
            int old;
            do { old = Maximum; if (old >= active) break; } while (Interlocked.CompareExchange(ref Maximum, active, old) != old);
            try
            {
                await Task.Delay(25, token).ConfigureAwait(false);
                if (Interlocked.Decrement(ref FailuresRemaining) >= 0)
                    throw new HttpRequestException("模拟短暂连接中断");
                var id = Guid.Parse(request.RequestUri.Segments[3].Trim('/'));
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[{\"Id\":\"" + id + "\"}]") };
            }
            finally { Interlocked.Decrement(ref Active); }
        }
    }

    static async Task ArchivePerformanceScenario(Form host, string output)
    {
        Directory.CreateDirectory(output);
        void Check(bool ok,string message){if(!ok)throw new Exception(message);Console.WriteLine("PASS: "+message);}
        var originalThread = Thread.CurrentThread.ManagedThreadId;
        foreach (var mode in new[] { "batch", "retry", "cancel" })
        using (var client = (IDisposable)New("PdmApiClient", "http://performance.invalid/"))
        using (var handler = new ArchiveBatchHandler { FailuresRemaining = mode == "retry" ? 1 : 0 })
        using (var http = new HttpClient(handler) { BaseAddress = new Uri("http://performance.invalid/") })
        using (var cancel = new CancellationTokenSource())
        {
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "fixture");
            SetField(client,"httpClient",http); Set(client,"EditJournalScope","perf-fixture");
            var ids = Enumerable.Range(0, mode == "batch" ? 20 : 1).Select(_ => Guid.NewGuid()).ToArray();
            var progress = new List<int>();var wrongThread=false;
            Action<int,int,string> report = (done,total,name) => { lock(progress){progress.Add(done);wrongThread |= Thread.CurrentThread.ManagedThreadId == originalThread;} };
            if(mode=="cancel")cancel.Cancel();
            Exception error=null;object result=null;
            try { var task=(Task)TreeCall(client,"ReadArchiveHistoriesAsync",ids,report,cancel.Token);await task;result=Get(task,"Result"); }
            catch(Exception caught){error=caught;}
            if(mode=="cancel") { Check(error is OperationCanceledException&&handler.Calls==0,"取消的批量读取不再发送请求"); continue; }
            if(error!=null)throw error;
            var map=(IDictionary)result;
            Check(map.Count==ids.Length&&ids.All(id=>(Guid)Get(((IEnumerable)map[id]).Cast<object>().Single(),"Id")==id),mode+"：批量结果按身份匹配，不串用版本");
            Check(handler.Maximum<=4&&(mode!="batch"||handler.Maximum>1),mode+"：网络并发受限，未逐文件等待主线程");
            Check(!wrongThread&&progress.Count==ids.Length&&progress.Max()==ids.Length,mode+"：后台进度完整、无需逐文件回到CAD线程");
            Check(handler.Calls==ids.Length+(mode=="retry"?1:0),mode+"：只读短暂失败有限重试，无重复成功查询");
        }

        var plugin=New("PdmAddin");var appType=Type("PdmAddin").GetField("application",Flags).FieldType;var reads=0;
        var path=Path.GetFullPath(Path.Combine(output,"已加载.SLDPRT"));
        var model=new Proxy(appType.Assembly.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true),call=>{
            if(Thread.CurrentThread.ManagedThreadId!=originalThread)throw new Exception("CAD读取离开所属线程");
            return call.MethodName=="GetPathName"?path:throw new Exception("不得读写模型："+call.MethodName);
        }).GetTransparentProxy();
        var app=new Proxy(appType,call=>{if(call.MethodName=="GetDocuments"){reads++;return new[]{model};}throw new Exception(call.MethodName);}).GetTransparentProxy();
        SetField(plugin,"application",app);var context=New("PdmAddin+ArchiveReadContext",plugin);
        for(var index=0;index<100;index++)TreeCall(context,"FindLoaded",path);
        Check(reads==1,"同一批100次查询只枚举一次已加载文档");
        TreeCall(context,"FindByName",Path.GetFileName(path));Check(reads==1,"同名候选查询复用同一加载索引");
        SetField(plugin,"nativeLoadEpoch",1);TreeCall(context,"FindLoaded",path);Check(reads==2,"加载状态变化后立即失效并重建索引");
        TreeCall(context,"Invalidate");TreeCall(context,"FindLoaded",path);Check(reads==3,"保存或结构变化可显式失效缓存，不沿用旧文档列表");

        await ArchiveCopyBatchScenario(Path.Combine(output,"copies"));
        await ArchiveHistoryRefreshScenario();
        await ArchiveHistoryChunkScenario();

        var session=Type("ArchiveSession").GetMethod("Start",Flags).Invoke(null,new object[]{host.Handle,CancellationToken.None});
        await (Task)Get(session,"Ready");
        try
        {
            TreeCall(session,"SetStep",6,"准备副本",609);
            TreeCall(session,"ReportFile",15,609,"A.SLDPRT","副本准备");TreeCall(session,"SetStage","读取详细信息");
            var text=ArchiveUi(session,()=>((Control)Field(ArchiveWindow(session),"progress")).Text);
            Check(text.Contains("15/609")&&!text.Contains("处理中"),"消息更新不会把已知进度退回处理中");
            var stage=ArchiveUi(session,()=>((Control)Field(ArchiveWindow(session),"stage")).Text);
            Check(stage.Contains("6/10"),"显示十个步骤，业务事务阶段不变");
            Check((int)Get(session,"Phase")==1,"展示步骤不改变底层事务阶段");
            TreeCall(session,"ReportFile",20,609,"B.SLDPRT","完成");TreeCall(session,"ReportFile",19,609,"C.SLDPRT","完成");
            Check(ArchiveUi(session,()=>((Control)Field(ArchiveWindow(session),"progress")).Text).Contains("20/609"),"乱序后台进度不会倒退");
            TreeCall(session,"SetStep",9,"等待整批确认",1);
            Check(ArchiveUi(session,()=>((Control)Field(ArchiveWindow(session),"progress")).Text).Contains("0/1"),"整批提交按批计数，不伪造逐文件提交进度");
            TreeCall(session,"SetStep",10,"同步结果",609);TreeCall(session,"ReportFile",609,609,null,"完成");
            Check(ArchiveUi(session,()=>((Control)Field(ArchiveWindow(session),"stage")).Text).Contains("10/10"),"最终结果刷新独立显示");
            ArchiveUi(session,()=>{using(var image=new System.Drawing.Bitmap(ArchiveWindow(session).Width,ArchiveWindow(session).Height)){
                ArchiveWindow(session).DrawToBitmap(image,new System.Drawing.Rectangle(0,0,image.Width,image.Height));image.Save(Path.Combine(output,"ten-step-progress.png"));}return true;});
        }
        finally { ArchiveClose(session); await (Task)Get(session,"Closed");((IDisposable)session).Dispose(); }
    }

    static async Task ArchiveCopyBatchScenario(string output)
    {
        void Check(bool ok,string message){if(!ok)throw new Exception(message);Console.WriteLine("PASS: "+message);}
        foreach(var mode in new[]{"normal","changed","cancel"})
        {
            var folder=Path.GetFullPath(Path.Combine(output,mode));Directory.CreateDirectory(folder);
            var plugin=New("PdmAddin");var prepared=(IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string),Type("PdmAddin+PreparedArchiveFile")));
            var nodes=new List<object>();
            for(var i=0;i<2;i++){var path=Path.Combine(folder,"文件"+i+".SLDPRT");File.WriteAllText(path,"完整原文件"+i);var node=New("CadTreeNode");Set(node,"FullPath",path);Set(node,"FileName",Path.GetFileName(path));Set(node,"DocumentId",Guid.NewGuid());nodes.Add(node);prepared.Add(path,New("PdmAddin+PreparedArchiveFile",node,new Dictionary<string,string>(),"",WorkingReuseHash(path),null,null));}
            if(mode=="changed")File.AppendAllText((string)Get(nodes[0],"FullPath"),"外部修改");
            using(var cancel=new CancellationTokenSource())
            {
                if(mode=="cancel")cancel.Cancel();Exception error=null;
                try{await (Task)TreeCall(plugin,"CompleteDeferredArchiveCopiesAsync",prepared,cancel.Token);}catch(Exception failure){error=failure;}
                Check((error==null)==(mode=="normal"),mode+"：批量副本保留变化和取消保护");
                foreach(var node in nodes)
                {
                    var original=(string)Get(node,"FullPath");var item=prepared[original];var copy=(string)Get(item,"UploadCopyPath");
                    if(mode=="normal"){
                        Check(File.ReadAllBytes(copy).SequenceEqual(File.ReadAllBytes(original))&&Path.GetFileName(copy)==Path.GetFileName(original),"后台批量副本保持文件名及所有字节一致");
                        Type("PdmAddin").GetMethod("DeleteCheckInUploadCopy",Flags).Invoke(null,new object[]{copy});
                    }else{
                        var cache=Path.Combine(Path.GetTempPath(),"UPTON-PDM","checkin",((Guid)Get(node,"DocumentId")).ToString("N"));
                        Check(!Directory.Exists(cache)||Directory.GetFiles(cache,"*",SearchOption.AllDirectories).Length==0,"中止后不遗留半成品副本，不改写源文件");
                    }
                }
            }
        }
    }

    static async Task ArchiveHistoryRefreshScenario()
    {
        var ids=new[]{Guid.NewGuid(),Guid.NewGuid()};var versionType=Type("DocumentVersionDto");var listType=typeof(List<>).MakeGenericType(versionType);
        var valueType=typeof(IReadOnlyList<>).MakeGenericType(versionType);var histories=(IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(Guid),valueType));
        foreach(var id in ids){var values=(IList)Activator.CreateInstance(listType);var version=New("DocumentVersionDto");Set(version,"Id",id);values.Add(version);histories.Add(id,values);}
        var changed=Guid.NewGuid();var batchReads=0;var detailReads=0;
        using(var client=(IDisposable)New("PdmApiClient","http://history.invalid/"))
        using(var handler=new ReferenceRecoveryHandler(request=>{
            object response;if(request.RequestUri.AbsolutePath.EndsWith("/version-verifications")){batchReads++;response=new[]{new{documentId=ids[0],accessible=true,latest=new{id=ids[0]}},new{documentId=ids[1],accessible=true,latest=new{id=changed}}};}
            else{detailReads++;if(!request.RequestUri.AbsolutePath.Contains(ids[1].ToString()))throw new Exception("重复查询未变化版本");response=new[]{new{id=changed}};}
            return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(new JavaScriptSerializer().Serialize(response))};
        }))
        using(var http=new HttpClient(handler){BaseAddress=new Uri("http://history.invalid/")})
        {
            http.DefaultRequestHeaders.Authorization=new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer","fixture");SetField(client,"httpClient",http);Set(client,"EditJournalScope","history-fixture");
            await (Task)TreeCall(client,"RefreshArchiveHistoriesAsync",histories,CancellationToken.None);
            if(batchReads!=1||detailReads!=1||(Guid)Get(((IEnumerable)histories[ids[1]]).Cast<object>().Single(),"Id")!=changed)throw new Exception("批量版本复核未保留变化检查");
            Console.WriteLine("PASS: 一次批量复核保留版本并发保护，仅重读发生变化的版本历史");
        }
    }

    static async Task ArchiveHistoryChunkScenario()
    {
        var ids=Enumerable.Range(0,205).Select(_=>Guid.NewGuid()).ToArray();var versionType=Type("DocumentVersionDto");
        var histories=(IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(Guid),typeof(IReadOnlyList<>).MakeGenericType(versionType)));
        foreach(var id in ids)histories.Add(id,Activator.CreateInstance(typeof(List<>).MakeGenericType(versionType)));
        var calls=0;
        using(var client=(IDisposable)New("PdmApiClient","http://chunks.invalid/"))
        using(var handler=new ReferenceRecoveryHandler(request=>{
            if(!request.RequestUri.AbsolutePath.EndsWith("/version-verifications"))throw new Exception("未变化的新图不应重复读取版本历史");
            var json=new JavaScriptSerializer();var body=json.Deserialize<Dictionary<string,object>>(request.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            var rows=((IEnumerable)body["Documents"]).Cast<Dictionary<string,object>>().ToArray();if(rows.Length>100||rows.Length==0)throw new Exception("超过服务器每批100项限制");calls++;
            return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(json.Serialize(rows.Select(row=>new{documentId=Guid.Parse((string)row["DocumentId"]),accessible=true,latest=(object)null}).ToArray()))};
        }))
        using(var http=new HttpClient(handler){BaseAddress=new Uri("http://chunks.invalid/")})
        {
            http.DefaultRequestHeaders.Authorization=new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer","fixture");SetField(client,"httpClient",http);
            await (Task)TreeCall(client,"RefreshArchiveHistoriesAsync",histories,CancellationToken.None);
            if(calls!=3)throw new Exception("批量版本核对分组数量错误");Console.WriteLine("PASS: 205个无历史图档按100项限制复核，仅3次请求且不重复拉取空历史");
        }
    }
}
