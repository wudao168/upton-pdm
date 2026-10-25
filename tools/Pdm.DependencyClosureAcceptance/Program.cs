using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Upton.Pdm.Domain;
using Upton.Pdm.Application;
using Upton.Pdm.Infrastructure;

try
{
var json=new JsonSerializerOptions(JsonSerializerDefaults.Web){WriteIndented=true};
var catalog=JsonSerializer.Deserialize<SourceCatalog>(File.ReadAllText(args[0]),json)!;
if(!Directory.Exists(catalog.Raw)&&Directory.Exists(catalog.Raw+"-offline"))
 catalog=catalog with{Catalog=catalog.Catalog.Select(file=>file with{Path=System.IO.Path.Combine(catalog.Raw+"-offline",file.FileName)}).ToList()};
var output=Path.GetFullPath(args[1]);Directory.CreateDirectory(output);
IPdmRepository repository;var provider="InMemory";
if(args.Length>5&&args[4]=="--mysql")
{
 var workspace=Path.GetFullPath(args[5]);using var config=JsonDocument.Parse(File.ReadAllText(Path.Combine(workspace,".local/api/appsettings.json")));
 var settings=new MySqlConnectionStringBuilder(config.RootElement.GetProperty("ConnectionStrings").GetProperty("Pdm").GetString()??throw new Exception("缺少本机数据库连接配置"));
 var secret=Environment.GetEnvironmentVariable("PDM_DB_PASSWORD")??Environment.GetEnvironmentVariable("PDM_DB_PASSWORD",EnvironmentVariableTarget.User);
 if(!string.IsNullOrWhiteSpace(secret))settings.Password=secret;
 if(string.IsNullOrWhiteSpace(settings.Password)){using var secrets=JsonDocument.Parse(File.ReadAllText(Path.Combine(workspace,".local/secrets/pdm-secrets.json")));settings.Password=secrets.RootElement.GetProperty("databasePassword").GetString();}
 using(var secrets=JsonDocument.Parse(File.ReadAllText(Path.Combine(workspace,".local/secrets/pdm-secrets.json"))))
 {
  settings.UserID="root";settings.Password=secrets.RootElement.GetProperty("mysqlRootPassword").GetString();
 }
 var productionName=settings.Database;var database=args.Length>6?args[6]:"pdm_closure_20261025_"+Guid.NewGuid().ToString("N").Substring(0,10);
 if(database==productionName||!System.Text.RegularExpressions.Regex.IsMatch(database,"^pdm_closure_20261025_[a-f0-9]{10}$"))throw new Exception("隔离数据库名称不安全");
 settings.Database="";await using(var server=new MySqlConnection(settings.ConnectionString)){await server.OpenAsync();using var create=new MySqlCommand("CREATE DATABASE IF NOT EXISTS `"+database+"` CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci",server);await create.ExecuteNonQueryAsync();}
 settings.Database=database;var options=Options.Create(new PdmDatabaseOptions{Provider="MySql",RunMigrations=true,ConnectionString=settings.ConnectionString});
 await File.WriteAllTextAsync(Path.Combine(output,"isolated-database.json"),JsonSerializer.Serialize(new{database,settings.Server,settings.Port,productionName,productionDatabaseWritten=false},json));
 await new MySqlMigrationRunner(options,NullLogger<MySqlMigrationRunner>.Instance,TimeProvider.System).RunAsync(default);
 repository=new MySqlPdmRepository(options,TimeProvider.System);provider="MySql";
}
else repository=new InMemoryPdmRepository(TimeProvider.System);
if(await repository.FindUserAsync("admin",default)==null)
{
 var company=(await repository.GetOrganizationDirectoryAsync(default)).Organizations.First().Id;
 await repository.CreateUserAsync(new(Guid.NewGuid(),"admin","隔离验证管理员","isolated-unused",UserRole.Administrator,true,CompanyId:company),default);
}
var vault=Path.Combine(output,"server-vault");Directory.CreateDirectory(vault);
var project=await repository.CreateProjectAsync(new("CLOSURE-"+Guid.NewGuid().ToString("N"),"真实大装配文件闭合隔离验证","admin",vault,Path.Combine(output,"release")),"admin",default);
var storage=new LocalFileStorage(Options.Create(new PdmStorageOptions{UploadTempRoot=Path.Combine(output,"uploads"),ProgramTemplateRoot=Path.Combine(output,"templates")}),repository,TimeProvider.System);
var service=new PdmWorkflowService(repository,storage,new NoPublisher(),TimeProvider.System);
var session=Guid.NewGuid();var docs=new Dictionary<string,PdmDocument>(StringComparer.OrdinalIgnoreCase);
var sourceByName=catalog.Catalog.ToDictionary(file=>file.FileName,StringComparer.OrdinalIgnoreCase);
using var audit=JsonDocument.Parse(File.ReadAllText(args[2]));
var missing=audit.RootElement.GetProperty("rows").EnumerateArray().Select(row=>row.GetProperty("fileName").GetString()!).ToHashSet(StringComparer.OrdinalIgnoreCase);
var ownerName="SS5Y-8位电磁阀座.SLDASM";var mainName="CW0030002.10.SLDASM";
var dependencies=JsonSerializer.Deserialize<Dictionary<string,string[]>>(File.ReadAllText(args[3]),json)!;
var ownerFiles=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
void AddDependencies(string name){foreach(var dependency in dependencies.GetValueOrDefault(name)??[])
 if(!dependency.Equals(ownerName,StringComparison.OrdinalIgnoreCase)&&ownerFiles.Add(dependency))AddDependencies(dependency);}
AddDependencies(ownerName);
var passed=0;
void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);passed++;Console.WriteLine("PASS: "+message);}
DocumentKind Kind(string file)=>Path.GetExtension(file).ToUpperInvariant() switch{".SLDPRT"=>DocumentKind.Part,".SLDASM"=>DocumentKind.Assembly,".SLDDRW"=>DocumentKind.Drawing,".PDF"=>DocumentKind.Pdf,".DWG"=>DocumentKind.Dwg,_=>DocumentKind.Other};
DocumentReferenceNode Node(PdmDocument doc)=>new(Guid.NewGuid(),doc.Id,doc.FileName,doc.FileName,doc.Name,doc.Kind,
 sourceByName[doc.FileName].Configuration,1,ReferenceNodeStatus.Normal,doc.Revision,null,[]);
DocumentReferenceNode Inventory(PdmDocument owner,IEnumerable<PdmDocument> files,bool pending=false)=>Node(owner) with{
 UsesNativeFileInventory=true,StructureIndexPending=pending,
 Children=files.Where(doc=>doc.Id!=owner.Id).Select(doc=>Node(doc) with{Role=ReferenceRole.ExternalFileDependency,
  InstancePath=owner.FileName+"/native-file/"+doc.Id.ToString("N")}).ToArray()};
StoredFile Stored(PdmDocument doc)
{
 var source=sourceByName[doc.FileName];var relative=Path.Combine(".versions",doc.Id.ToString("N"),"native",doc.FileName);
 var target=Path.Combine(vault,relative);Directory.CreateDirectory(Path.GetDirectoryName(target)!);if(!File.Exists(target))File.Copy(source.Path,target);File.SetAttributes(target,FileAttributes.ReadOnly);
 return new(relative,source.Length,source.Sha256,DateTimeOffset.UtcNow);
}
DocumentVersionCommit Commit(PdmDocument doc,DocumentReferenceNode root,Guid? expected,bool independent)=>new(
 Stored(doc),"依赖完整性隔离验证",new Dictionary<string,string?>(),
 new(Guid.NewGuid(),project.Id,doc.Id,DateTimeOffset.UtcNow,"admin",root,Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(root,json)))),[],[],IsProjectRoot:independent)
 {ExpectedVersionId=expected,RequireBaseVersion=true};
async Task<PdmDocument> Register(SourceFile source,bool reserve=false,Guid? batch=null)
{
 var command=new RegisterDocumentCommand(project.Id,Path.GetFileNameWithoutExtension(source.FileName),source.FileName,source.FileName,Kind(source.FileName),SourceSha256:source.Sha256,AllowDuplicateContent:true,DuplicateReason:"同一隔离样本中的独立原生文件");
 var doc=reserve?await repository.ReserveArchiveDocumentAsync(command,"admin",new(batch!.Value,session,"CLOSURE-QA"),default)
  :await repository.RegisterDocumentAsync(command,"admin",default);
 docs[source.FileName]=doc;
 return reserve?doc:await repository.CheckoutAsync(doc.Id,"admin",session,"CLOSURE-QA",DateTimeOffset.UtcNow.AddMinutes(30),null,default);
}
foreach(var source in catalog.Catalog.Where(file=>!missing.Contains(file.FileName)))await Register(source);
var initial=docs.Values.Select(doc=>new ArchiveBatchFile(doc.Id,Commit(doc,
 doc.FileName==mainName?Inventory(doc,docs.Values,true):Node(doc),null,doc.FileName==mainName||doc.FileName==ownerName))).ToArray();
await repository.CheckInBatchVersionsAsync(Guid.NewGuid(),"initial","admin",session,initial,default);
var before=await repository.ListProjectDocumentVersionsAsync(project.Id,default);
var bases=before.ToDictionary(version=>version.DocumentId);
var oldOwner=bases[docs[ownerName].Id];var oldOwnerSnapshot=JsonSerializer.Serialize(oldOwner.ReferenceSnapshot,json);
var oldManifest=await service.CreateControlledOpenManifestAsync(docs[ownerName].Id,null,false,false,"admin",UserRole.Administrator,default);
Check(oldManifest.Files.Count==1,"旧缺档场景复现：子装配原生文件已有版本，清单漏掉八个零件");
var batchId=Guid.NewGuid();foreach(var source in catalog.Catalog.Where(file=>missing.Contains(file.FileName)))await Register(source,true,batchId);
foreach(var name in new[]{ownerName,mainName})docs[name]=await repository.CheckoutAsync(docs[name].Id,"admin",session,"CLOSURE-QA",DateTimeOffset.UtcNow.AddMinutes(30),null,default);
var owner=docs[ownerName];var ownerRoot=Inventory(owner,docs.Values.Where(doc=>ownerFiles.Contains(doc.FileName)));
var directModels=dependencies[ownerName].Where(name=>Kind(name) is DocumentKind.Part or DocumentKind.Assembly).ToHashSet(StringComparer.OrdinalIgnoreCase);
ownerRoot=ownerRoot with{Children=docs.Values.Where(doc=>directModels.Contains(doc.FileName)).Select(Node).Concat(ownerRoot.Children).ToArray()};
var commits=docs.Values.Where(doc=>missing.Contains(doc.FileName)).Select(doc=>new ArchiveBatchFile(doc.Id,Commit(doc,Node(doc) with{UsesNativeFileInventory=true},null,false))).ToList();
commits.Add(new(owner.Id,Commit(owner,ownerRoot,oldOwner.Id,true)));
var main=docs[mainName];var mainRoot=Inventory(main,docs.Values,true);
commits.Add(new(main.Id,Commit(main,mainRoot,bases[main.Id].Id,true)));
var receipt=await repository.CheckInBatchVersionsAsync(batchId,"repair","admin",session,commits,default);
Check(receipt.Results.Count(result=>result.VersionCreated)==8,"整批只补齐八个文件版本，不给原子装配和总装误升版");
Check((await repository.ListDocumentVersionsAsync(owner.Id,default)).Single().Id==oldOwner.Id
 &&JsonSerializer.Serialize((await repository.ListDocumentVersionsAsync(owner.Id,default)).Single().ReferenceSnapshot,json)==oldOwnerSnapshot,
 "原子装配的原生版本编号、文件和历史引用快照不改写");
Check((await repository.ListDocumentVersionsAsync(main.Id,default)).Single().Id==bases[main.Id].Id,"总装原生文件未修改时版本保持不变");
var indexes=await repository.ListNativeReferenceIndexesAsync(project.Id,default);
Check(indexes.Any(index=>index.DocumentId==owner.Id&&index.Children.Count==ownerFiles.Count+directModels.Count),"历史依赖索引独立保存，完整保留已有组件与八个漏档组件");
var ownerManifest=await service.CreateControlledOpenManifestAsync(owner.Id,null,false,false,"admin",UserRole.Administrator,default);
Check(ownerManifest.Files.Count==ownerFiles.Count+1,"子装配打开最新返回全部实际依赖，包含已有零件和八个补齐零件");
var mainManifest=await service.CreateControlledOpenManifestAsync(main.Id,null,false,false,"admin",UserRole.Administrator,default);
Check(mainManifest.Files.Count==catalog.Catalog.Count,"大装配打开最新返回完整原生文件集合");
Check((await repository.FindArchiveBatchAsync(batchId,default))!.Results.Count==commits.Count,"服务端完整回执可查询，不误报部分成功");
await repository.CheckInBatchVersionsAsync(batchId,"repair","admin",session,commits,default);
Check((await repository.ListProjectDocumentVersionsAsync(project.Id,default)).Count==before.Count+8,"重放同一批次不重复登记或升版");
foreach(var name in new[]{ownerName,mainName})docs[name]=await repository.CheckoutAsync(docs[name].Id,"admin",session,"CLOSURE-QA",DateTimeOffset.UtcNow.AddMinutes(30),null,default);
await repository.CheckInBatchVersionsAsync(Guid.NewGuid(),"unchanged","admin",session,
 [new(owner.Id,Commit(docs[ownerName],ownerRoot,oldOwner.Id,true)),new(main.Id,Commit(docs[mainName],mainRoot,bases[main.Id].Id,true))],default);
Check((await repository.ListProjectDocumentVersionsAsync(project.Id,default)).Count==before.Count+8,"无修改再次存档不新增工程版本");
// A failed batch must not leave an effective index from its early successful files.
async Task<string> IndexState()=>JsonSerializer.Serialize((await repository.ListNativeReferenceIndexesAsync(project.Id,default))
 .OrderBy(index=>index.DocumentId).ThenBy(index=>index.Configuration).ThenBy(index=>index.Revision?.Display).ToArray(),json);
var rootIndexBefore=await IndexState();
foreach(var name in new[]{ownerName,mainName})docs[name]=await repository.CheckoutAsync(docs[name].Id,"admin",session,"CLOSURE-QA",DateTimeOffset.UtcNow.AddMinutes(30),null,default);
var invalid=mainRoot with{Children=mainRoot.Children.Append(Node(main) with{DocumentId=Guid.NewGuid(),FileName="MISSING.SLDPRT"}).ToArray()};
var failed=false;try{await repository.CheckInBatchVersionsAsync(Guid.NewGuid(),"failure","admin",session,
 [new(owner.Id,Commit(docs[ownerName],ownerRoot with{StructureIndexPending=true},oldOwner.Id,true)),new(main.Id,Commit(docs[mainName],invalid,bases[main.Id].Id,true))],default);}catch(Exception e) when(e is PdmRuleException or PdmNotFoundException){failed=true;}
var rootIndexAfter=await IndexState();
if(!failed||rootIndexAfter!=rootIndexBefore){await File.WriteAllTextAsync(Path.Combine(output,"rollback-before.json"),rootIndexBefore);await File.WriteAllTextAsync(Path.Combine(output,"rollback-after.json"),rootIndexAfter);}
Check(failed&&rootIndexAfter==rootIndexBefore,"后续文件失败时依赖索引也整批回滚");
var isolatedName="PSU-08OG-20250301014805833.SLDPRT";
var isolatedDocument=await repository.CheckoutAsync(docs[isolatedName].Id,"admin",session,"CLOSURE-QA",DateTimeOffset.UtcNow.AddMinutes(30),null,default);
var isolatedBase=(await repository.ListDocumentVersionsAsync(isolatedDocument.Id,default)).First();
string RequiredFile(string option){var index=Array.IndexOf(args,option);if(index<0||index+1>=args.Length)throw new Exception("请指定验收文件参数："+option);return Path.GetFullPath(args[index+1]);}
var verifiedBody=RequiredFile("--verified-body");
var bodyPath=Path.Combine(".versions",isolatedDocument.Id.ToString("N"),"native-isolated",isolatedName);
var bodyTarget=Path.Combine(vault,bodyPath);Directory.CreateDirectory(Path.GetDirectoryName(bodyTarget)!);File.Copy(verifiedBody,bodyTarget);File.SetAttributes(bodyTarget,FileAttributes.ReadOnly);
var bodyHash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(bodyTarget)));
var nativeEvidence=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(RequiredFile("--native-evidence"))));
var nativeRoot=Node(isolatedDocument) with{UsesNativeFileInventory=true,NativeIsolation=new(isolatedBase.Id,isolatedBase.Sha256,bodyPath,new FileInfo(bodyTarget).Length,bodyHash,nativeEvidence)};
var nativeCommit=Commit(isolatedDocument,nativeRoot,isolatedBase.Id,true) with{File=new(bodyPath,new FileInfo(bodyTarget).Length,bodyHash,DateTimeOffset.UtcNow)};
var projectionBatch=Guid.NewGuid();
await repository.CheckInBatchVersionsAsync(projectionBatch,"isolation","admin",session,[new(isolatedDocument.Id,nativeCommit)],default);
var afterIsolation=await repository.ListDocumentVersionsAsync(isolatedDocument.Id,default);
Check(afterIsolation.Count==1&&afterIsolation[0].Id==isolatedBase.Id&&afterIsolation[0].Sha256==isolatedBase.Sha256
 &&afterIsolation[0].StorageRelativePath==isolatedBase.StorageRelativePath,"真实固化副本单独保存，原工程版本、原文及其哈希不改写或升版");
var independent=await service.CreateControlledOpenManifestAsync(isolatedDocument.Id,null,false,false,"admin",UserRole.Administrator,default);
var independentFile=independent.Files.Single(file=>file.IsRoot);
Check(independentFile.NativeIsolation?.Sha256==bodyHash&&independentFile.Sha256==isolatedBase.Sha256
 &&independentFile.NativeIsolation.ReferenceSnapshotId!=Guid.Empty,"受控打开明确返回固化副本及不可变来源版本，不混淆两种哈希");
var bodySnapshot=await repository.FindReferenceSnapshotAsync(independentFile.NativeIsolation!.ReferenceSnapshotId,default);
Check(bodySnapshot?.RootDocumentId==isolatedDocument.Id&&bodySnapshot.Root.NativeIsolation?.SourceVersionId==isolatedBase.Id,
 "固化文件下载凭证可追溯到本项目、本图档、本来源版本");
Check(bodySnapshot!.Sha256==ArchiveSafety.Rehash(bodySnapshot).Sha256,"服务端补全固化凭证后重新计算快照哈希，记录与校验值一致");
isolatedDocument=await repository.CheckoutAsync(isolatedDocument.Id,"admin",session,"CLOSURE-QA",DateTimeOffset.UtcNow.AddMinutes(30),null,default);
var sourceRejected=false;
try{await repository.CheckInBatchVersionsAsync(Guid.NewGuid(),"wrong-isolation-source","admin",session,
 [new(isolatedDocument.Id,nativeCommit with{ReferenceSnapshot=nativeCommit.ReferenceSnapshot with{Root=nativeRoot with{NativeIsolation=nativeRoot.NativeIsolation! with{SourceVersionId=Guid.NewGuid()}}}})],default);}
catch(PdmRuleException){sourceRejected=true;}
Check(sourceRejected&&(await repository.ListDocumentVersionsAsync(isolatedDocument.Id,default)).Count==1,"错误来源的固化副本提前拒绝，不生成任何工程版本");
var projectedBefore=await IndexState();var projectionRolledBack=false;
try{await repository.CheckInBatchVersionsAsync(Guid.NewGuid(),"projection-rollback","admin",session,
 [new(isolatedDocument.Id,nativeCommit with{ReferenceSnapshot=nativeCommit.ReferenceSnapshot with{SnapshotId=Guid.NewGuid(),Root=nativeRoot with{NativeIsolation=nativeRoot.NativeIsolation! with{GeometryEvidenceSha256=new string('A',64)}}}}),
  new(main.Id,Commit(docs[mainName],invalid,bases[main.Id].Id,true))],default);}
catch(Exception e)when(e is PdmRuleException or PdmNotFoundException){projectionRolledBack=true;}
Check(projectionRolledBack&&projectedBefore==await IndexState(),"后续文件失败时技术固化副本索引整批回滚，不泄漏成功记录");
var releaseFixture=new ReleasePackage(Guid.NewGuid(),project.Id,"CLOSURE-NATIVE-QA",ReleasePackageState.Publishing,null,"","",[],DateTimeOffset.UtcNow,null,null)
 {VersionBindings=[new(isolatedDocument.Id,isolatedBase.Id,isolatedBase.Revision.Display,isolatedBase.Sha256,true,isolatedName)]};
var releaseSource=(await ReleaseBoundSourceReader.ReadAsync(repository,releaseFixture,true,default)).Single();
Check(releaseSource.StorageRelativePath==bodyPath&&releaseSource.Sha256==bodyHash&&releaseSource.SourceSha256==isolatedBase.Sha256,
 "发布转图读取已固化的真实文件，仍保留不可变工程来源指纹");
var copyTarget=await repository.CreateProjectAsync(new("CLOSURE-COPY-"+Guid.NewGuid().ToString("N"),"独立原生副本复制验收","admin",Path.Combine(output,"copy-vault"),Path.Combine(output,"copy-release")),"admin",default);
var copyOptions=Options.Create(new PdmStorageOptions{UploadTempRoot=Path.Combine(output,"copy-uploads")});
var copyService=new ProjectCopyService(repository,new LocalFileStorage(copyOptions,repository,TimeProvider.System),
 new InMemoryProjectFileRepository(TimeProvider.System),new LocalProjectFileStorage(copyOptions,TimeProvider.System),new InMemoryValidationPlanRepository(),TimeProvider.System);
await copyService.ExecuteAsync(project.Id,copyTarget.Id,new(true,true,false,false,[]),"admin",UserRole.Administrator,default);
var copiedDocument=(await repository.ListDocumentsAsync(copyTarget.Id,default)).Single(document=>document.FileName==isolatedName);
var copiedVersion=(await repository.ListDocumentVersionsAsync(copiedDocument.Id,default)).Single();
Check(copiedVersion.Sha256==bodyHash&&copiedVersion.ReferenceSnapshot.NativeIsolationVerified&&copiedVersion.ReferenceSnapshot.NativeIsolation==null,
 "项目复制使用固化文件并建立本项目的新原生来源，不残留原项目固化凭证");
Check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(copyTarget.VaultLocation,copiedVersion.StorageRelativePath))))==bodyHash,
 "复制后的服务器原生文件实际哈希与固化内容一致");
var allVersions=await repository.ListProjectDocumentVersionsAsync(project.Id,default);
object Package(string name,ControlledOpenManifest manifest)=>new{name,manifest,storedFiles=manifest.Files.Select(file=>new{
 versionId=file.VersionId,serverPath=Path.Combine(vault,allVersions.Single(version=>version.Id==file.VersionId).StorageRelativePath)}).ToArray()};
await File.WriteAllTextAsync(Path.Combine(output,"native-download-export.json"),JsonSerializer.Serialize(new{
 rawSource=catalog.Raw,packages=new[]{Package("ssy",ownerManifest),Package("large",mainManifest)}},json));
await File.WriteAllTextAsync(Path.Combine(output,"backend-results.json"),JsonSerializer.Serialize(new{passed=true,checks=passed,provider,productionDataWritten=false,
 projectId=project.Id,initialFiles=before.Count,historicalFilesAdded=8,engineeringVersionsAdded=0,nativeFiles=catalog.Catalog.Count},json));
Console.WriteLine("Passed="+passed+"; provider="+provider+"; production data not written.");
}
catch(Exception error){Console.Error.WriteLine("FAIL: "+error);Environment.ExitCode=1;}

sealed record SourceCatalog(string Raw,List<SourceFile> Catalog);
sealed record SourceFile(string FileName,string Path,string Configuration,string Sha256,long Length);
sealed class NoPublisher:IReleasePackagePublisher
{
 public Task PrepareAsync(ReleasePackage p,Project q,CancellationToken t)=>throw new NotSupportedException();
 public Task ValidateAsync(ReleasePackage p,Project q,CancellationToken t)=>throw new NotSupportedException();
 public Task<ReleasePublication> PublishAsync(ReleasePackage p,Project q,IReadOnlyList<ReleasePreviewSource> sources,CancellationToken t)=>throw new NotSupportedException();
}
