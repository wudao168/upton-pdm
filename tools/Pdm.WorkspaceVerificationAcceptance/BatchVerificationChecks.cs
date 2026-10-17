using System.Net;
using System.Net.Http;
using System.Text;
using System.Web.Script.Serialization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Upton.Pdm.Desktop;
using Upton.Pdm.SolidWorks;

internal static partial class Program
{
    private static async Task VerifyLiveBatchEndpoint(string root)
    {
        var serializer=new JavaScriptSerializer{MaxJsonLength=1024*1024};
        var input=serializer.Deserialize<LiveVerificationInput>(File.ReadAllText(Path.Combine(root,"tmp/batch-verification-live-input.json")));
        var secrets=serializer.Deserialize<Dictionary<string,object>>(File.ReadAllText(Path.Combine(root,".local/secrets/pdm-secrets.json")));
        string Encode(byte[] bytes)=>Convert.ToBase64String(bytes).TrimEnd('=').Replace('+','-').Replace('/','_');
        var now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var payload=new Dictionary<string,object>{{"iss","upton-pdm"},{"aud","upton-pdm-clients"},
            {"http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name","admin"},
            {"http://schemas.microsoft.com/ws/2008/06/identity/claims/role","Administrator"},
            {"token_version",input.TokenVersion.ToString()},{"nbf",now-30},{"exp",now+120}};
        var unsigned=Encode(Encoding.UTF8.GetBytes("{\"alg\":\"HS256\",\"typ\":\"JWT\"}"))+"."+Encode(Encoding.UTF8.GetBytes(serializer.Serialize(payload)));
        string token;
        using(var hash=new HMACSHA256(Encoding.UTF8.GetBytes((string)secrets["jwtSigningKey"])))token=unsigned+"."+Encode(hash.ComputeHash(Encoding.UTF8.GetBytes(unsigned)));
        var calls=0;
        using(var client=new HttpClient())
        {
            var watch=System.Diagnostics.Stopwatch.StartNew();
            var rows=await WorkspaceVersionVerificationClient.ReadAsync(client,()=>{
                calls++;var request=new HttpRequestMessage(HttpMethod.Post,"http://192.168.2.8:5173/api/documents/version-verifications");
                request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token);
                if(input.CompanyId.HasValue)request.Headers.Add("X-Company-Id",input.CompanyId.Value.ToString());
                return request;
            },input.Files,default);
            watch.Stop();
            foreach(var row in rows){var expected=input.Expected.Single(item=>item.DocumentId==row.DocumentId);
                if(!row.Accessible || row.Latest?.Id!=expected.Latest?.Id || row.Latest?.Sha256!=expected.Latest?.Sha256
                    || row.Latest?.SourceFileSha256!=expected.Latest?.SourceFileSha256 || row.Latest?.FileLength!=expected.Latest?.FileLength)
                    throw new Exception("内网接口返回的版本或完整校验值不一致");}
            var report=new {Documents=rows.Count,Requests=calls,Milliseconds=watch.ElapsedMilliseconds,
                VersionIdsLengthsAndFullHashesMatch=true,UsedDesktopBatchClient=true,ContentOrBindingWritten=false};
            var json=serializer.Serialize(report);File.WriteAllText(Path.Combine(root,"tmp/batch-verification-live-api-check.json"),json);
            Console.WriteLine(json);
        }
    }
    private sealed class LiveVerificationInput
    {
        public Guid? CompanyId{get;set;}
        public long TokenVersion{get;set;}
        public WorkspaceLocalFileState[] Files{get;set;}
        public WorkspaceVersionVerificationResult[] Expected{get;set;}
    }

    private static async Task VerifyBatches(string root)
    {
        HttpRequestMessage Request() => new(HttpMethod.Post, "http://verification-fixture/api/documents/version-verifications");
        var files = Enumerable.Range(0, 227).Select(_ => new WorkspaceLocalFileState { DocumentId=Guid.NewGuid() }).ToArray();
        using(var handler=new BatchHandler("good"))
        using(var client=new HttpClient(handler))
        {
            var rows=await WorkspaceVersionVerificationClient.ReadAsync(client,Request,files,default);
            Check(rows.Count==227 && handler.Counts.SequenceEqual(new[]{100,100,27}), "227个图档只发送3次轻量批量请求，每批最多100个");
            Check(rows.Select(row=>row.DocumentId).OrderBy(id=>id).SequenceEqual(files.Select(file=>file.DocumentId).OrderBy(id=>id)),
                "服务器响应乱序时仍按图档身份正确匹配");
            Check(handler.OnlyIdentifiers, "批量请求只携带图档及原绑定版本编号，不传结构、BOM或本地文件内容");
        }
        foreach(var mode in new[]{"missing","duplicate","foreign-document","foreign-version","wrong-bound","invalid-hash","denied-with-data"})
        {
            using(var client=new HttpClient(new BatchHandler(mode)))
            {
                var rejected=false;
                try{await WorkspaceVersionVerificationClient.ReadAsync(client,Request,files.Take(2).ToArray(),default);}
                catch(InvalidOperationException){rejected=true;}
                Check(rejected,"拒绝不完整或不可信的批量响应："+mode);
            }
        }
        using(var client=new HttpClient(new BatchHandler("denied")))
        {
            var rows=await WorkspaceVersionVerificationClient.ReadAsync(client,Request,files.Take(1).ToArray(),default);
            Check(!rows.Single().Accessible && rows.Single().Latest==null, "无权图档保持待核对，不伪造校验通过");
        }
        using(var handler=new BatchHandler("duplicate-version"))
        using(var client=new HttpClient(handler))
        {
            var id=Guid.NewGuid();handler.VersionId=id;
            var rejected=false;
            try{await WorkspaceVersionVerificationClient.ReadAsync(client,Request,new[]{new WorkspaceLocalFileState{DocumentId=Guid.NewGuid(),BoundVersionId=id}},default);}
            catch(InvalidOperationException){rejected=true;}
            Check(rejected,"同一版本在最新和历史摘要中重复出现时拒绝歧义响应");
        }
        using(var handler=new BatchHandler("good"))
        using(var client=new HttpClient(handler))
        {
            var cancelled=false;
            try{await WorkspaceVersionVerificationClient.ReadAsync(client,Request,files,new CancellationToken(true));}
            catch(OperationCanceledException){cancelled=true;}
            Check(cancelled && handler.Counts.Count==0, "取消批量校验不会发送后续请求");
        }
        using(var client=new HttpClient(new BatchHandler("server-error")))
        {
            var failed=false;
            try{await WorkspaceVersionVerificationClient.ReadAsync(client,Request,files,default);}
            catch(HttpRequestException){failed=true;}
            Check(failed, "服务器失败时不使用空响应认定本地内容一致");
        }

        var project=Guid.NewGuid();var document=Guid.NewGuid();var bound=Guid.NewGuid();
        var folder=Path.Combine(root,"View","BATCH-CONTENT");Directory.CreateDirectory(folder);
        var path=Path.Combine(folder,"part.SLDPRT");File.WriteAllText(path,"committed-A-content");
        var committedHash=Hash(path);var length=new FileInfo(path).Length;var timestamp=File.GetLastWriteTimeUtc(path);
        Check(PdmDocumentIdentityStore.TryWriteControlledVersion(path,document,project,bound,"W1",new string('0',64),length), "建立批量校验的隔离文件与旧记录");
        File.SetAttributes(path,FileAttributes.ReadOnly);
        var state=WorkspaceLocalStateReader.Read(root,project,"BATCH-CONTENT","tester",new[]{new WorkspaceDocumentStateRequest{DocumentId=document,LatestRevision="W1"}}).Items.Single();
        using(var handler=new BatchHandler("good"){Hash=committedHash,Length=length})
        using(var client=new HttpClient(handler))
        {
            var summary=(await WorkspaceVersionVerificationClient.ReadAsync(client,Request,new[]{state},default)).Single().Latest;
            File.SetAttributes(path,FileAttributes.Normal);File.WriteAllText(path,"committed-B-content");
            File.SetLastWriteTimeUtc(path,timestamp);File.SetAttributes(path,FileAttributes.ReadOnly);
            Check(!WorkspaceLocalStateReader.RecoverArchivedBinding(state,project,summary.Id,summary.Revision,summary.Sha256,summary.SourceFileSha256),
                "批量响应等待期间产生的真实修改仍被拦截，即使长度和时间戳相同");
            PdmDocumentIdentityStore.TryReadControlledMetadata(path,out var metadata);
            Check(metadata.VersionId==bound && File.ReadAllText(path)=="committed-B-content", "拒绝恢复后保留原绑定和真实修改内容");
        }
    }

    private sealed class BatchHandler(string mode) : HttpMessageHandler
    {
        public List<int> Counts {get;}=new();
        public bool OnlyIdentifiers {get;private set;}=true;
        public string Hash {get;set;}=new string('A',64);
        public long Length {get;set;}=128;
        public Guid? VersionId {get;set;}
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            if(mode=="server-error")return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            var json=new JavaScriptSerializer();
            var payload=(Dictionary<string,object>)json.DeserializeObject(await request.Content.ReadAsStringAsync());
            var inputs=((object[])payload["documents"]).Cast<Dictionary<string,object>>().ToArray();
            Counts.Add(inputs.Length);
            OnlyIdentifiers &= payload.Keys.SequenceEqual(new[]{"documents"}) && inputs.All(item=>item.Count==2 && item.ContainsKey("documentId") && item.ContainsKey("boundVersionId"));
            var rows=inputs.Select(item=>{
                var id=Guid.Parse((string)item["documentId"]);
                return new WorkspaceVersionVerificationResult {DocumentId=id,Accessible=true,Latest=new WorkspaceVersionVerification{
                    Id=VersionId??Guid.NewGuid(),DocumentId=id,Revision="W1",FileLength=Length,Sha256=Hash,SourceFileSha256=Hash}};
            }).Reverse().ToList();
            if(mode=="missing")rows.RemoveAt(0);
            if(mode=="duplicate")rows[1]=rows[0];
            if(mode=="foreign-document")rows[0].DocumentId=Guid.NewGuid();
            if(mode=="foreign-version")rows[0].Latest.DocumentId=Guid.NewGuid();
            if(mode=="wrong-bound")rows[0].Bound=rows[0].Latest;
            if(mode=="duplicate-version")rows[0].Bound=rows[0].Latest;
            if(mode=="invalid-hash")rows[0].Latest.Sha256=new string('X',64);
            if(mode=="denied-with-data")rows[0].Accessible=false;
            if(mode=="denied"){rows[0].Accessible=false;rows[0].Latest=null;}
            return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(json.Serialize(rows),Encoding.UTF8,"application/json")};
        }
    }
}
