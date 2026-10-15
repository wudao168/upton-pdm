using System.Security.Cryptography;
using Dapper;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

internal static class ArchiveSafetyChecks
{
    public static async Task RunAsync(MySqlPdmRepository repository, string connectionString, string workspace)
    {
        var database = new MySqlConnectionStringBuilder(connectionString).Database;
        if (!database.StartsWith("pdm_atomic_qa_", StringComparison.Ordinal) || database.Length != 46) throw new Exception("Unsafe fixture database");
        var fixtureRoot = Path.GetFullPath(Path.Combine(workspace,"tmp","archive-safety-fixture",Guid.NewGuid().ToString("N")));
        var allowedRoot = Path.GetFullPath(Path.Combine(workspace,"tmp","archive-safety-fixture")) + Path.DirectorySeparatorChar;
        if (!fixtureRoot.StartsWith(allowedRoot,StringComparison.OrdinalIgnoreCase)) throw new Exception("Unsafe fixture file root");
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        var project = await repository.CreateProjectAsync(new("QA-SAFETY-"+Guid.NewGuid().ToString("N"),"存档安全回归","qa",Path.Combine(fixtureRoot,"vault"),Path.Combine(fixtureRoot,"release")),"qa",default);
        var options = Options.Create(new PdmStorageOptions { UploadTempRoot=Path.Combine(fixtureRoot,"uploads"),ChunkSizeBytes=4 });
        var storage = new LocalFileStorage(options,repository,TimeProvider.System);
        var workflow = new PdmWorkflowService(repository,storage,new UnusedPublisher(),TimeProvider.System);
        var session = Guid.NewGuid();
        var batch = Guid.NewGuid();
        var context = new ArchiveRegistrationContext(batch,session,"QA");
        async Task<PdmDocument> Reserve(string name,DocumentKind kind,Guid? model=null) => await workflow.RegisterDocumentAsync(
            new(project.Id,name,name,name+(kind==DocumentKind.Drawing?".SLDDRW":".SLDPRT"),kind,RelatedModelDocumentId:model,SourceSha256:new string('A',64)),"qa",UserRole.Administrator,default,context);
        async Task<StoredFile> Upload(PdmDocument document,Guid archiveBatch,string content)
        {
            var bytes=System.Text.Encoding.UTF8.GetBytes(content);
            var upload=await storage.StartArchiveUploadAsync(project.Id,document.FileName,bytes.Length,Convert.ToHexString(SHA256.HashData(bytes)),"qa",new(archiveBatch,document.Id,session),default);
            for(var offset=0;offset<bytes.Length;offset+=upload.ChunkSize)
                await storage.WriteChunkAsync(upload.Id,offset/upload.ChunkSize,new MemoryStream(bytes[offset..Math.Min(bytes.Length,offset+upload.ChunkSize)]),default);
            return await storage.CompleteUploadAsync(upload.Id,upload.TargetRelativePath!,default);
        }
        ArchiveBatchInput Input(PdmDocument document,StoredFile file,IReadOnlyList<DocumentReferenceNode> children,Guid? expected=null) => new(
            document.Id,file,new Dictionary<string,string?>(),new(Guid.NewGuid(),project.Id,document.Id,DateTimeOffset.UtcNow,"qa",Node(document) with {Children=children},new string('B',64)),"隔离测试",false,expected);
        try
        {
            var a=await Reserve("MODEL-A",DocumentKind.Part);
            var b=await Reserve("MODEL-B",DocumentKind.Part);
            var drawing=await Reserve("DRAWING",DocumentKind.Drawing,a.Id);
            var aFile=await Upload(a,batch,"model-a");var bFile=await Upload(b,batch,"model-b");var drawingFile=await Upload(drawing,batch,"drawing-a");
            var receipt=await workflow.CheckInBatchAsync(batch,"first","qa",UserRole.Administrator,session,[Input(a,aFile,[]),Input(b,bFile,[]),Input(drawing,drawingFile,[Node(a)])],default);
            Require(receipt.FollowupPending,"follow-up was not committed with receipt");
            var first=receipt.Results.Single(result=>result.Document.Id==drawing.Id).Version!;
            Require((await repository.ListDocumentRelationsAsync(project.Id,default)).Single().ModelDocumentId==a.Id,"pending model relation missing");
            Console.WriteLine("PASS MySQL same-batch pending model registration and atomic upload adoption");

            drawing=await repository.CheckoutAsync(drawing.Id,"qa",session,"QA",DateTimeOffset.UtcNow.AddHours(1),default);
            var secondBatch=Guid.NewGuid();var secondFile=await Upload(drawing,secondBatch,"drawing-b");
            var second=(await workflow.CheckInBatchAsync(secondBatch,"second","qa",UserRole.Administrator,session,[Input(drawing,secondFile,[Node(b)],first.Id)],default)).Results.Single().Version!;
            var operation=Guid.NewGuid();
            var restored=await workflow.RestoreVersionAsync(drawing.Id,first.Id,"qa",UserRole.Administrator,"恢复模型A",default,operation,second.Id);
            var replay=await workflow.RestoreVersionAsync(drawing.Id,first.Id,"qa",UserRole.Administrator,"恢复模型A",default,operation,second.Id);
            Require(restored.Version.Id==replay.Version.Id && (await repository.ListDocumentVersionsAsync(drawing.Id,default)).Count==3,"restore replay duplicated version");
            Require((await repository.ListDocumentRelationsAsync(project.Id,default)).Single().ModelDocumentId==a.Id,"restore did not update drawing relation");
            await ExpectConflict(()=>workflow.RestoreVersionAsync(drawing.Id,first.Id,"qa",UserRole.Administrator,"旧基准",default,Guid.NewGuid(),second.Id));
            await connection.ExecuteAsync("UPDATE document SET lifecycle_state='InReview' WHERE id=@Id",new{drawing.Id});
            await ExpectConflict(()=>workflow.RestoreVersionAsync(drawing.Id,first.Id,"qa",UserRole.Administrator,"审核中",default,Guid.NewGuid(),restored.Version.Id));
            await connection.ExecuteAsync("UPDATE document SET lifecycle_state='Work' WHERE id=@Id",new{drawing.Id});
            Console.WriteLine("PASS MySQL restore idempotency, base-version conflict, review guard and 2D/3D relation");

            // A fresh repository/service must recover work persisted before a process disappeared.
            var fresh=new MySqlPdmRepository(Options.Create(new PdmDatabaseOptions{Provider="MySql",ConnectionString=connectionString}),TimeProvider.System);
            var recovery=new PdmWorkflowService(fresh,new LocalFileStorage(options,fresh,TimeProvider.System),new UnusedPublisher(),TimeProvider.System);
            foreach(var work in (await fresh.ListArchiveBatchFollowupsAsync(20,default)).Where(work=>work.Receipt.ProjectId==project.Id))
                await recovery.CompleteArchiveBatchFollowupAsync(work,default);
            Require((await fresh.FindArchiveBatchAsync(operation,default))!.FollowupPending==false,"durable follow-up not acknowledged");
            Require((await repository.ListProjectAuditAsync(project.Id,100,default)).Any(entry=>entry.Action=="document.version.restore"),"bounded project audit missed restore");
            Console.WriteLine("PASS MySQL follow-up recovery across service instances and indexed project audit");

            context=new(Guid.NewGuid(),session,"QA");
            var orphan=await Reserve("ORPHAN",DocumentKind.Part);
            var orphanFile=await Upload(orphan,context.BatchId,"orphan-bytes");
            var legacy=await storage.StartUploadAsync(project.Id,"legacy.SLDPRT",4,Convert.ToHexString(SHA256.HashData("old!"u8)),default,"qa");
            await storage.WriteChunkAsync(legacy.Id,0,new MemoryStream("old!"u8.ToArray()),default);
            var legacyFile=await storage.CompleteUploadAsync(legacy.Id,".versions/legacy/keep.SLDPRT",default);
            await connection.ExecuteAsync("UPDATE archive_upload SET expires_at=UTC_TIMESTAMP(6)-INTERVAL 1 SECOND WHERE project_id=@Id; UPDATE pending_archive_document SET expires_at=UTC_TIMESTAMP(6)-INTERVAL 1 SECOND WHERE project_id=@Id",new{Id=project.Id});
            await storage.CleanupExpiredArchiveUploadsAsync(default);
            Require(!File.Exists(Path.Combine(project.VaultLocation,orphanFile.RelativePath)),"owned orphan file was retained");
            Require(File.Exists(Path.Combine(project.VaultLocation,drawingFile.RelativePath)) && File.Exists(Path.Combine(project.VaultLocation,legacyFile.RelativePath)),"cleanup removed adopted or unowned data");
            Require(await repository.FindPendingArchiveDocumentAsync(orphan.Id,default)==null,"expired new registration was not collected");
            Require(await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM archive_upload WHERE batch_id=@Id AND state='Collected'",new{Id=context.BatchId})==1,"cleanup ownership tombstone missing");
            Console.WriteLine("PASS MySQL expired owned uploads collected; adopted/legacy files preserved; expired reservations removed");
        }
        finally
        {
            if(Directory.Exists(fixtureRoot))
            {
                foreach(var path in Directory.EnumerateFiles(fixtureRoot,"*",SearchOption.AllDirectories))File.SetAttributes(path,FileAttributes.Normal);
                Directory.Delete(fixtureRoot,true);
            }
        }
    }

    private static DocumentReferenceNode Node(PdmDocument document)=>new(Guid.NewGuid(),document.Id,document.DrawingNumber,document.FileName,document.Name,document.Kind,"Default",1,ReferenceNodeStatus.Normal,document.Revision,null,[]);
    private static void Require(bool condition,string detail){if(!condition)throw new Exception(detail);}
    private static async Task ExpectConflict<T>(Func<Task<T>> action){try{await action();}catch(PdmConflictException){return;}throw new Exception("Expected archive conflict");}
    private sealed class UnusedPublisher:IReleasePackagePublisher
    {
        public Task PrepareAsync(ReleasePackage package,Project project,CancellationToken token)=>throw new NotSupportedException();
        public Task ValidateAsync(ReleasePackage package,Project project,CancellationToken token)=>throw new NotSupportedException();
        public Task<ReleasePublication> PublishAsync(ReleasePackage package,Project project,IReadOnlyList<ReleasePreviewSource> sources,CancellationToken token)=>throw new NotSupportedException();
    }
}
