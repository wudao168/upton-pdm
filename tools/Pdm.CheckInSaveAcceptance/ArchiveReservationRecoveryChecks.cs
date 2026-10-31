using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static partial class Program
{
    sealed class LostReservationHandler : HttpMessageHandler
    {
        internal bool CleanupUnavailable;
        internal int Reservations, Cleanups, Uploads;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path=request.RequestUri.AbsolutePath;
            if(path.EndsWith("/registrations")){Reservations++;throw new HttpRequestException("模拟服务器已收到预留请求，但响应丢失");}
            if(path.EndsWith("/cancel-registration")){Cleanups++;if(CleanupUnavailable)throw new HttpRequestException("模拟连接仍未恢复");}
            if(path.Contains("/uploads")||path.EndsWith("/archive-batches"))Uploads++;
            if(path.EndsWith("/reference-tree"))return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound){Content=new StringContent("{}")});
            var body=path.EndsWith("/archive-warning-policy")?"{\"Revision\":0,\"BlockAll\":false,\"BlockingCodes\":[]}":"{}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(body)});
        }
    }

    static async Task ArchiveReservationRecoveryScenario(string output)
    {
        void Check(bool ok,string message){if(!ok)throw new Exception(message);Console.WriteLine("PASS: "+message);}
        foreach(var offline in new[]{false,true})
        {
            var folder=Path.GetFullPath(Path.Combine(output,offline?"offline":"cleanup"));Directory.CreateDirectory(folder);
            var source=Path.Combine(folder,"新零件.SLDPRT");File.WriteAllText(source,"已保存的原生图档测试字节");var hash=WorkingReuseHash(source);
            var node=New("CadTreeNode");Set(node,"FullPath",source);Set(node,"FileName",Path.GetFileName(source));Set(node,"DisplayName","新零件");
            Set(node,"Kind",Enum.Parse(Type("CadDocumentKind"),"Part"));Set(node,"Role",Enum.Parse(Type("CadReferenceRole"),"ExternalFileDependency"));
            Set(node,"NativeFileScopeVerified",true);Set(node,"NativeFileScopePaths",new HashSet<string>(StringComparer.OrdinalIgnoreCase){source});
            var journal=Type("ArchiveBatchJournal").GetField("directory",Flags);var previous=journal.GetValue(null);journal.SetValue(null,Path.Combine(folder,"journal"));
            try
            {
                var plugin=New("PdmAddin");var project=Guid.NewGuid();var appType=Type("PdmAddin").GetField("application",Flags).FieldType;var nativeCalls=0;
                SetField(plugin,"application",new Proxy(appType,call=>{
                    if(call.MethodName=="GetDocuments")return new object[0];if(call.MethodName=="GetDocumentDependencies2")return new string[0];
                    if(call.MethodName=="get_ActiveDoc")return null;if(call.MethodName=="GetUserPreferenceStringValue")return "";
                    nativeCalls++;throw new Exception("不得操作CAD："+call.MethodName);
                }).GetTransparentProxy());
                SetField(plugin,"currentTree",node);SetField(plugin,"currentProjectId",project);SetField(plugin,"authenticatedUsername","tester");
                var decisions=(IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string),Type("PdmAddin+RegistrationDecision")));
                decisions.Add(source,New("PdmAddin+RegistrationDecision",hash,false,""));
                using(var client=(IDisposable)New("PdmApiClient","http://recovery.invalid/"))
                using(var handler=new LostReservationHandler{CleanupUnavailable=offline})
                using(var http=new HttpClient(handler){BaseAddress=new Uri("http://recovery.invalid/")})
                using(var pane=(Control)New("PdmTaskPaneControl","隔离恢复验证"))
                using(var lifetime=new CancellationTokenSource())
                {
                    pane.CreateControl();SetField(plugin,"taskPaneControl",pane);SetField(plugin,"lifetime",lifetime);
                    http.DefaultRequestHeaders.Authorization=new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer","fixture");
                    SetField(client,"httpClient",http);SetField(client,"controlledOpenHttpClient",http);Set(client,"EditJournalScope","recovery-fixture");SetField(plugin,"apiClient",client);
                    Exception failure=null;
                    try{await (Task)TreeCall(plugin,"CheckInBatchAsync",LinkedItems(node),project,"恢复测试",null,CancellationToken.None,null,null,decisions,null,false,null);}
                    catch(Exception error){failure=error;}
                    Check(failure!=null&&handler.Reservations==1,"预留响应丢失时停止，不盲目重试写请求");
                    Check(handler.Cleanups==1,"即使尚未收到任何图档ID，也按批次清理服务器预留");
                    var records=Directory.GetFiles(Path.Combine(folder,"journal"),"*.json");
                    Check(records.Length==(offline?1:0),offline?"清理连接失败时保留恢复记录，不丢失未确认预留":"清理已确认后才移除恢复记录");
                    Check(handler.Uploads==0&&nativeCalls==0&&WorkingReuseHash(source)==hash&&Get(node,"DocumentId")==null,"失败未上传、未改图、未伪造登记成功");
                }
            }
            finally{journal.SetValue(null,previous);}
        }
        await ArchiveRegistrationOrderScenario(Path.Combine(output,"registration-order"));
    }

    static async Task ArchiveRegistrationOrderScenario(string output)
    {
        Directory.CreateDirectory(output);var project=Guid.NewGuid();var batch=Guid.NewGuid();var plugin=New("PdmAddin");
        var checkout=(Guid)Field(plugin,"checkoutSessionId");var nodes=new List<object>();
        object Node(string name,string kind){var path=Path.GetFullPath(Path.Combine(output,name));File.WriteAllText(path,"saved "+name);var node=New("CadTreeNode");Set(node,"FullPath",path);Set(node,"FileName",name);Set(node,"Kind",Enum.Parse(Type("CadDocumentKind"),kind));return node;}
        var root=Node("显示总装.SLDASM","Assembly");
        for(var index=0;index<2;index++){var model=Node("P"+index+".SLDPRT","Part");var drawing=Node("P"+index+".SLDDRW","Drawing");((IList)Get(drawing,"ReferencedModels")).Add(model);nodes.Add(model);nodes.Add(drawing);((IList)Get(root,"Children")).Add(model);((IList)Get(model,"Children")).Add(drawing);}
        var ids=nodes.ToDictionary(node=>(string)Get(node,"FileName"),node=>Guid.NewGuid());var registered=new HashSet<Guid>();var modelLinks=0;
        var decisions=(IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string),Type("PdmAddin+RegistrationDecision")));
        foreach(var node in nodes)decisions.Add(Get(node,"FullPath"),New("PdmAddin+RegistrationDecision",WorkingReuseHash((string)Get(node,"FullPath")),false,""));
        var journal=Type("ArchiveBatchJournal").GetField("directory",Flags);var old=journal.GetValue(null);journal.SetValue(null,Path.Combine(output,"batches"));
        try
        {
            Type("ArchiveBatchJournal").GetMethod("Begin",Flags).Invoke(null,new object[]{batch,checkout,"registration-fixture","tester"});
            using(var client=(IDisposable)New("PdmApiClient","http://registration.invalid/"))
            using(var handler=new ReferenceRecoveryHandler(request=>{
                var json=new JavaScriptSerializer();var payload=json.Deserialize<Dictionary<string,object>>(request.Content.ReadAsStringAsync().GetAwaiter().GetResult());var doc=(Dictionary<string,object>)payload["document"];
                var name=(string)doc["fileName"];var id=ids[name];
                if(name.EndsWith(".SLDDRW")){var model=Guid.Parse(Convert.ToString(doc["relatedModelDocumentId"]));if(model!=ids[Path.ChangeExtension(name,".SLDPRT")]||!registered.Contains(model))throw new Exception("工程图早于模型身份或关联错误");modelLinks++;}
                if((string)doc["sourceSha256"]!=WorkingReuseHash(Path.GetFullPath(Path.Combine(output,name))))throw new Exception("登记内容指纹改变");
                registered.Add(id);return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(json.Serialize(new{id,projectId=project,fileName=name,kind=doc["kind"],checkedOutBy="tester",checkoutSessionId=checkout,checkedOutAt=DateTime.UtcNow}))};
            }))
            using(var http=new HttpClient(handler){BaseAddress=new Uri("http://registration.invalid/")})
            {
                http.DefaultRequestHeaders.Authorization=new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer","fixture");SetField(client,"httpClient",http);Set(client,"EditJournalScope","registration-fixture");SetField(plugin,"apiClient",client);
                SetField(plugin,"authenticatedUsername","tester");SetField(plugin,"currentTree",root);SetField(plugin,"editJournalScope","registration-fixture|tester");SetField(plugin,"editJournal",New("EditDraftJournal",Path.Combine(output,"drafts"),"registration-fixture|tester"));
                var reserved=new List<Guid>();var task=(Task)TreeCall(plugin,"RegisterPendingArchiveDocumentsAsync",LinkedItems(nodes.ToArray()),project,batch,decisions,reserved,CancellationToken.None);await task;
                if((int)Get(task,"Result")!=4||reserved.Count!=4||nodes.Any(node=>(Guid)Get(node,"DocumentId")!=ids[(string)Get(node,"FileName")]))throw new Exception("登记结果与文件身份不一致");
                Console.WriteLine("PASS: 新模型及工程图批量登记身份逐项匹配");
                if(modelLinks!=2)throw new Exception("图模关系未检查");Console.WriteLine("PASS: 工程图登记前模型身份已就绪，双向关联入口保持原语义");
                var saved=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(Path.Combine(output,"batches",batch.ToString("N")+".json")));
                if(((IEnumerable)saved["ReservedDocumentIds"]).Cast<object>().Count()!=4)throw new Exception("恢复记录遗漏身份");Console.WriteLine("PASS: 每份成功预留身份均保留在持久恢复记录中");
            }
        }
        finally{journal.SetValue(null,old);}
    }
}
