using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

internal static class Program
{
    const BindingFlags Flags=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    static int Main(string[] args)
    {
        AppDomain.CurrentDomain.AssemblyResolve+=(_,e)=>{var path=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[0])),new AssemblyName(e.Name).Name+".dll");return File.Exists(path)?Assembly.LoadFrom(path):null;};
        try{Run(Assembly.LoadFrom(Path.GetFullPath(args[0]))).GetAwaiter().GetResult();return 0;}catch(Exception error){Console.WriteLine(error);return 1;}
    }
    static async Task Run(Assembly assembly)
    {
        var type=assembly.GetType("Upton.Pdm.SolidWorks.PdmApiClient",true);
        foreach(var mode in new[]{"slow-commit","lost-response","cancel","partial"})
        using(var cancellation=new CancellationTokenSource())
        using(var handler=new Handler(mode))
        using(var client=(IDisposable)Activator.CreateInstance(type,Flags,null,new object[]{"http://fixture.invalid/"},null))
        {
            var ordinaryField=type.GetField("httpClient",Flags);var longField=type.GetField("controlledOpenHttpClient",Flags);
            if(((HttpClient)ordinaryField.GetValue(client)).Timeout!=TimeSpan.FromSeconds(30)||((HttpClient)longField.GetValue(client)).Timeout!=TimeSpan.FromMinutes(15))throw new Exception("Ordinary and long-operation deadlines changed unexpectedly");
            ((HttpClient)ordinaryField.GetValue(client)).Dispose();((HttpClient)longField.GetValue(client)).Dispose();
            ordinaryField.SetValue(client,new HttpClient(handler,false){BaseAddress=new Uri("http://fixture.invalid/"),Timeout=TimeSpan.FromMilliseconds(40)});
            longField.SetValue(client,new HttpClient(handler,false){BaseAddress=new Uri("http://fixture.invalid/"),Timeout=TimeSpan.FromSeconds(5)});
            var batch=Guid.NewGuid();handler.Batch=batch;handler.Ids=Enumerable.Range(0,6).Select(_=>Guid.NewGuid()).ToArray();
            var files=handler.Ids.Select(id=>(object)new{documentId=id}).ToArray();
            if(mode=="cancel")cancellation.CancelAfter(30);
            Exception failure=null;object receipt=null;
            try{var task=(Task)type.GetMethod("CommitArchiveBatchAsync",Flags).Invoke(client,new object[]{batch,Guid.NewGuid(),files,cancellation.Token});await task.ConfigureAwait(false);receipt=task.GetType().GetProperty("Result").GetValue(task);}
            catch(Exception error){failure=error;}
            if(mode=="cancel")
            {if(!(failure is OperationCanceledException)||handler.Posts!=1)throw new Exception("User cancellation retried or lost cancellation semantics");}
            else if(mode=="partial")
            {if(!(failure is InvalidOperationException)||failure.Data["ArchiveBatchNotCommitted"] is bool known&&known)throw new Exception("Partial receipt was accepted or declared definitely uncommitted");}
            else
            {if(failure!=null)throw failure;if(((IList)receipt.GetType().GetProperty("Results").GetValue(receipt)).Count!=6)throw new Exception("Receipt omitted files");}
            if(mode=="lost-response"&&(handler.Posts!=2||handler.Payloads.Distinct().Count()!=1))throw new Exception("Transport replay changed batch content or duplicated logical commit");
            Console.WriteLine("PASS "+mode+"; ordinary 40ms timeout does not abort the whole-batch request");
        }
    }
    sealed class Handler:HttpMessageHandler
    {
        readonly string mode;public Guid Batch;public Guid[] Ids;public int Posts;public readonly System.Collections.Generic.List<string> Payloads=new System.Collections.Generic.List<string>();
        public Handler(string mode){this.mode=mode;}
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            if(request.Method==HttpMethod.Get)return new HttpResponseMessage(HttpStatusCode.NotFound){Content=new StringContent("{}")};
            if(request.RequestUri.AbsolutePath!="/api/archive-batches")throw new Exception("Unexpected business write");
            Posts++;Payloads.Add(await request.Content.ReadAsStringAsync().ConfigureAwait(false));
            if(mode=="lost-response"&&Posts==1)throw new HttpRequestException("Response lost after logical commit");
            await Task.Delay(120,token).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(new JavaScriptSerializer().Serialize(new{batchId=Batch,results=Ids.Take(mode=="partial"?4:6).Select(id=>new{document=new{id}}).ToArray()}))};
        }
    }
}
