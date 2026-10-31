using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

internal static partial class Program
{
 static async Task ControlledCacheScenario(string output)
 {
  output=Path.GetFullPath(output);Directory.CreateDirectory(output);
  var identity=Type("PdmDocumentIdentityStore");var bindings=identity.GetField("bindingDirectory",Flags);var resolver=identity.GetField("workspaceRootResolver",Flags);
  var savedBindings=bindings.GetValue(null);var savedResolver=resolver.GetValue(null);
  bindings.SetValue(null,Path.Combine(output,"bindings"));resolver.SetValue(null,new Func<string>(()=>output));
  void Check(bool ok,string text){if(!ok)throw new Exception(text);Console.WriteLine("PASS: "+text);}
  async Task<string> Prepare(object manager,object manifest,bool snapshot=false)
  {
   var task=(Task)TreeCall(manager,snapshot?"PrepareReadOnlyAsync":"PrepareWorkingCopyAsync",manifest,snapshot?(object)false:new Guid[0],CancellationToken.None);
   await task;return (string)Get(task,"Result");
  }
  object Manifest(string rootName,params string[] names)
  {
   var result=New("ControlledOpenManifestDto");Set(result,"Id",Guid.NewGuid());Set(result,"ProjectId",Guid.NewGuid());Set(result,"ProjectCode","QA");Set(result,"RootRelativePath",rootName);
   var files=(IList)Activator.CreateInstance(Property(result,"Files").PropertyType);
   foreach(var name in names){var file=New("ControlledOpenFileDto");Set(file,"DocumentId",Guid.NewGuid());Set(file,"VersionId",Guid.NewGuid());Set(file,"FileName",name);Set(file,"RelativePath",name);Set(file,"Revision","W1");files.Add(file);}
   Set(result,"Files",files);Set(result,"RootDocumentId",Get(files[0],"DocumentId"));Set(result,"RootVersionId",Get(files[0],"VersionId"));return result;
  }
  string Hash(string value){using(var sha=System.Security.Cryptography.SHA256.Create())return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-","");}
  void Content(object file,string value){Set(file,"Sha256",Hash(value));Set(file,"FileLength",(long)Encoding.UTF8.GetByteCount(value));}
  object Metadata(string path){var args=new object[]{path,null};Check((bool)identity.GetMethod("TryReadLocalMetadata",Flags).Invoke(null,args),"副本保留路径对应的版本记录");return args[1];}
  try
  {
   var root=Path.Combine(output,"reuse");var manifest=Manifest("ROOT.SLDASM","ROOT.SLDASM","PART.SLDPRT","DRAWING.SLDDRW");var files=((IEnumerable)Get(manifest,"Files")).Cast<object>().ToArray();
   var handler=new CacheDownloadHandler();foreach(var file in files){var value="server bytes "+Get(file,"FileName");Content(file,value);handler.Bytes[(Guid)Get(file,"DocumentId")]=Encoding.UTF8.GetBytes(value);}
   using(var client=(IDisposable)New("PdmApiClient","http://cache.invalid/"))
   using(var http=new HttpClient(handler){BaseAddress=new Uri("http://cache.invalid/")})
   {
    SetField(client,"httpClient",http);SetField(client,"controlledOpenHttpClient",http);
    object Manager()=>New("ControlledWorkspaceManager",client,root);
    var manager=Manager();var first=await Prepare(manager,manifest);var directory=Path.GetDirectoryName(first);var part=Path.Combine(directory,"PART.SLDPRT");
    Check(handler.Requests==3&&File.Exists(first),"首次打开只取回所需文件");
    Check(!Directory.Exists(Path.Combine(root,".uplm","Snapshots")),"普通最新打开不另存一套完整快照");
    var before=Directory.GetFiles(root,"*",SearchOption.AllDirectories).Length;var again=await Prepare(manager,manifest);
    Check(first==again&&handler.Requests==3&&before==Directory.GetFiles(root,"*",SearchOption.AllDirectories).Length,"重复打开复用原目录且零下载、零新增副本");
    manager=Manager();Check(await Prepare(manager,manifest)==first&&handler.Requests==3,"重建管理器模拟重启后仍复用固定目录");
    var rootBytes=File.ReadAllBytes(first);Set(files[1],"VersionId",Guid.NewGuid());Content(files[1],"next part version");handler.Bytes[(Guid)Get(files[1],"DocumentId")]=Encoding.UTF8.GetBytes("next part version");
    Check(await Prepare(manager,manifest)==first&&handler.Requests==4&&File.ReadAllText(part)=="next part version","总装版本不变、仅零件升级时只更新该零件");
    Check(rootBytes.SequenceEqual(File.ReadAllBytes(first)),"更新子件不改写未变化的总装字节");
    Check((Guid)Get(Metadata(part),"VersionId")== (Guid)Get(files[1],"VersionId"),"按文件更新后同步精确版本绑定");
    File.SetAttributes(part,FileAttributes.Normal);File.AppendAllText(part,"|technical reference relocation");var metadata=Metadata(part);
    Check((bool)identity.GetMethod("TryRecordResolvedReferences",Flags).Invoke(null,new[]{(object)part,metadata,WorkingReuseHash(part)}),"记录引用调整的技术指纹");File.SetAttributes(part,FileAttributes.ReadOnly);
    Check(await Prepare(manager,manifest)==first&&handler.Requests==4,"已核对的引用调整不误判为未提交修改或触发下载");
    // Real unsubmitted content in the old context is retained once. New server view
    // uses a deterministic directory across sessions, not a new GUID on every open.
    File.SetAttributes(part,FileAttributes.Normal);File.AppendAllText(part,"|unsubmitted feature change");File.SetAttributes(part,FileAttributes.ReadOnly);var local=File.ReadAllText(part);
    var server=await Prepare(manager,manifest);Check(server!=first&&File.ReadAllText(part)==local,"旧工作区真实差异保留，服务器内容在独立固定目录准备");
    Check(((string)Get(manager,"LocalCacheNotice")).Contains(directory),"明确告知保留修改的旧目录");
    var count=handler.Requests;var contexts=Directory.GetDirectories(Path.Combine(root,"View","QA","装配工作区")).Length;
    Check(await Prepare(Manager(),manifest)==server&&handler.Requests==count&&contexts==Directory.GetDirectories(Path.Combine(root,"View","QA","装配工作区")).Length,"重新启动后仍复用服务器目录，不持续增加整套缓存");
    var serverPart=Path.Combine(Path.GetDirectoryName(server),"PART.SLDPRT");File.SetAttributes(serverPart,FileAttributes.Normal);File.AppendAllText(serverPart,"|new local edit");File.SetAttributes(serverPart,FileAttributes.ReadOnly);
    Exception failure=null;try{await Prepare(Manager(),manifest);}catch(Exception error){failure=error;}
    Check(failure is InvalidDataException&&handler.Requests==count,"固定目录再次有真实修改时提前停止，既不下载也不再建第三套目录");
    Check(File.ReadAllText(serverPart).Contains("new local edit")&&File.ReadAllText(part)==local,"冲突时两处修改均保留");
    var message=(string)Type("PdmAddin").GetMethod("OpenDocumentErrorMessage",Flags).Invoke(null,new object[]{new InvalidDataException("工作区有未提交修改，已保留文件。")});
    Check(message=="工作区有未提交修改，已保留文件。","本地内容冲突显示实际中文原因，不误报插件未加载");
    var technical=(string)Type("PdmAddin").GetMethod("OpenDocumentErrorMessage",Flags).Invoke(null,new object[]{new InvalidDataException("invalid payload")});
    Check(technical.Contains("完整性校验")&&!technical.Contains("invalid payload")&&!technical.Contains("插件已加载"),"英文完整性异常保留日志、界面转换为准确中文");
    var plugin=New("PdmAddin");var pending=New("LocalEditDraft");Set(pending,"DocumentId",Get(files[1],"DocumentId"));Set(pending,"ProjectId",Get(manifest,"ProjectId"));Set(pending,"SourcePath",part);Set(pending,"Closed",false);Set(pending,"RestoreOnOpen",true);
    ((IDictionary)Field(plugin,"localEdits")).Add(Get(files[1],"DocumentId"),pending);TreeCall(plugin,"RetireLocalRecoveryForPlmOpenCore",manifest,null);
    Check(((IDictionary)Field(plugin,"localEdits")).Contains(Get(files[1],"DocumentId"))&&(bool)Get(pending,"RestoreOnOpen"),"另开服务器内容不注销旧工作区尚未处理的恢复记录");
   }
   foreach(var mode in new[]{"loaded","failure","cancelled"})
   {
    var folder=Path.Combine(output,mode);var package=Manifest("ROOT.SLDASM","ROOT.SLDASM","PART.SLDPRT");var entries=((IEnumerable)Get(package,"Files")).Cast<object>().ToArray();
    var handler2=new CacheDownloadHandler();foreach(var entry in entries){Content(entry,"old saved bytes");handler2.Bytes[(Guid)Get(entry,"DocumentId")]=Encoding.UTF8.GetBytes("old saved bytes");}
    using(var client=(IDisposable)New("PdmApiClient","http://cache.invalid/"))using(var http=new HttpClient(handler2){BaseAddress=new Uri("http://cache.invalid/")})
    {
     SetField(client,"httpClient",http);SetField(client,"controlledOpenHttpClient",http);var manager=New("ControlledWorkspaceManager",client,folder);var path=await Prepare(manager,package);var child=Path.Combine(Path.GetDirectoryName(path),"PART.SLDPRT");
     Set(entries[1],"VersionId",Guid.NewGuid());Content(entries[1],"next saved bytes");handler2.Bytes[(Guid)Get(entries[1],"DocumentId")]=Encoding.UTF8.GetBytes("next saved bytes");
     if(mode=="loaded")Set(manager,"ProtectedPaths",new[]{child});if(mode=="failure")handler2.Fail=true;Exception failure=null;
     try{if(mode=="cancelled"){var task=(Task)TreeCall(manager,"PrepareWorkingCopyAsync",package,new Guid[0],new CancellationToken(true));await task;}else await Prepare(manager,package);}catch(Exception error){failure=error;}
     Check(failure!=null&&File.ReadAllText(child)=="old saved bytes",mode+"：文件占用、下载失败或取消均不改写原有工作副本");
     Check(!Directory.Exists(Path.Combine(folder,".uplm","Staging"))||Directory.GetFileSystemEntries(Path.Combine(folder,".uplm","Staging")).Length==0,mode+"：操作结束不遗留下载临时目录");
    }
   }
   var snapRoot=Path.Combine(output,"snapshot-bindings");var snap=Manifest("ROOT.SLDASM","ROOT.SLDASM");var snapFile=((IList)Get(snap,"Files"))[0];Content(snapFile,"server native bytes");
   using(var client=(IDisposable)New("PdmApiClient","http://cache.invalid/"))using(var http=new HttpClient(new CacheDownloadHandler{Bytes={{(Guid)Get(snapFile,"DocumentId"),Encoding.UTF8.GetBytes("server native bytes")}}}){BaseAddress=new Uri("http://cache.invalid/")})
   {
    SetField(client,"httpClient",http);SetField(client,"controlledOpenHttpClient",http);var manager=New("ControlledWorkspaceManager",client,snapRoot);var path=await Prepare(manager,snap,true);
    Check((Guid)Get(Metadata(path),"VersionId")== (Guid)Get(snapFile,"VersionId"),"临时目录移动到快照后重建正确的路径版本绑定");
    Check(await Prepare(manager,snap,true)==path&&Directory.GetDirectories(Path.GetDirectoryName(Path.GetDirectoryName(path))).Length==1,"同一快照重复使用，不新增隔离副本");
   }
   var isolatedRoot=Path.Combine(output,"isolated-snapshot");var isolatedManifest=Manifest("ROOT.SLDASM","ROOT.SLDASM");var isolatedFile=((IList)Get(isolatedManifest,"Files"))[0];Content(isolatedFile,"source native bytes");
   var descriptor=New("NativeFileIsolationDto");Set(descriptor,"SourceVersionId",Get(isolatedFile,"VersionId"));Set(descriptor,"SourceSha256",Get(isolatedFile,"Sha256"));
   Set(descriptor,"ReferenceSnapshotId",Guid.NewGuid());Set(descriptor,"Sha256",Hash("independent native body"));Set(descriptor,"Length",(long)Encoding.UTF8.GetByteCount("independent native body"));Set(isolatedFile,"NativeIsolation",descriptor);
   using(var client=(IDisposable)New("PdmApiClient","http://cache.invalid/"))using(var http=new HttpClient(new CacheDownloadHandler{Bytes={{(Guid)Get(isolatedFile,"DocumentId"),Encoding.UTF8.GetBytes("independent native body")}}}){BaseAddress=new Uri("http://cache.invalid/")})
   {
    SetField(client,"httpClient",http);SetField(client,"controlledOpenHttpClient",http);var manager=New("ControlledWorkspaceManager",client,isolatedRoot);var path=await Prepare(manager,isolatedManifest,true);
    Check((Guid)Get(Metadata(path),"NativeIsolationSnapshotId")== (Guid)Get(descriptor,"ReferenceSnapshotId"),"快照移动后保留独立原生副本代次，不退回源文件指纹");
    Check(await Prepare(manager,isolatedManifest,true)==path,"独立原生快照同样可重复复用");
   }
   var cleaner=Type("ControlledCacheMaintenance");
   object Record(string rootFolder,string directory,object package,string kind){cleaner.GetMethod("Record",Flags).Invoke(null,new object[]{rootFolder,directory,package,kind});return package;}
   object Clean(string rootFolder,string[] paths,Guid[] ids,long limit,bool manual,Func<bool> busy=null)=>cleaner.GetMethod("Clean",Flags).Invoke(null,new object[]{rootFolder,paths,ids,limit,manual,busy??(()=>false)});
   foreach(var mode in new[]{"normal","protected-reference","checkout","writable","tampered","locked","extra-file","busy","original","recovery"})
   {
    var folder=Path.Combine(output,"cleanup-"+mode);var package=Manifest("ROOT.SLDASM","ROOT.SLDASM","PART.SLDPRT");var entries=((IEnumerable)Get(package,"Files")).Cast<object>().ToArray();
    var dir=Path.Combine(folder,"View","QA","装配工作区",Guid.NewGuid().ToString("N"));if(mode=="original")dir=Path.Combine(folder,"original");if(mode=="recovery")dir=Path.Combine(folder,".uplm","Recovery","failed-operation");Directory.CreateDirectory(dir);
    foreach(var entry in entries){var path=Path.Combine(dir,(string)Get(entry,"FileName"));File.WriteAllText(path,"verified cached bytes",new UTF8Encoding(false));Content(entry,"verified cached bytes");File.SetAttributes(path,FileAttributes.ReadOnly);}
    var child=Path.Combine(dir,"PART.SLDPRT");if(mode=="writable")File.SetAttributes(child,FileAttributes.Normal);
    if(mode=="tampered"){File.SetAttributes(child,FileAttributes.Normal);File.AppendAllText(child,"local modification");File.SetAttributes(child,FileAttributes.ReadOnly);}
    if(mode=="extra-file")File.WriteAllText(Path.Combine(dir,"NEW.SLDDRW"),"new drawing draft");
    Record(folder,dir,package,"working");Exception failure=null;object result=null;
    using(var held=mode=="locked"?new FileStream(child,FileMode.Open,FileAccess.Read,FileShare.Read):null)
     try{result=Clean(folder,mode=="protected-reference"?new[]{child}:new string[0],mode=="checkout"?new[]{(Guid)Get(entries[1],"DocumentId")}:new Guid[0],0,true,()=>mode=="busy");}catch(System.Reflection.TargetInvocationException error){failure=error.InnerException;}
    Check(File.Exists(child)==(mode!="normal"),mode+"：只清理已确认、闲置且无修改的受控缓存");
    if(mode=="normal")Check((long)Get(result,"FreedBytes")>0&&!Directory.Exists(dir),"实际删除成功才统计释放空间并移除空目录");
    if(mode=="busy")Check(failure is InvalidOperationException,"操作进行中拒绝缓存清理");
   }
   var quotaRoot=Path.Combine(output,"quota");var quotaManifest=Manifest("ROOT.SLDASM","ROOT.SLDASM");Content(((IList)Get(quotaManifest,"Files"))[0],"quota cache bytes");var quotaDirs=new List<string>();
   for(var index=0;index<3;index++)
   {var dir=Path.Combine(quotaRoot,"View","QA","装配工作区",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);var path=Path.Combine(dir,"ROOT.SLDASM");File.WriteAllText(path,"quota cache bytes",new UTF8Encoding(false));File.SetAttributes(path,FileAttributes.ReadOnly);Record(quotaRoot,dir,quotaManifest,"working");quotaDirs.Add(dir);}
   var quotaResult=Clean(quotaRoot,new[]{Path.Combine(quotaDirs[2],"ROOT.SLDASM")},new Guid[0],34,false);
   Check(!Directory.Exists(quotaDirs[0])&&Directory.Exists(quotaDirs[1])&&Directory.Exists(quotaDirs[2]),"超过容量后按最近使用顺序回收闲置副本，保留使用中的目录");
   var obsoleteRoot=Path.Combine(output,"obsolete");var obsoletePackage=Manifest("ROOT.SLDASM","ROOT.SLDASM");Content(((IList)Get(obsoletePackage,"Files"))[0],"cache snapshot bytes");
   var obsoleteSnapshot=Path.Combine(obsoleteRoot,".uplm","Snapshots","QA",((Guid)Get(obsoletePackage,"RootDocumentId")).ToString("N"),"Latest","old-combination");
   var currentWorking=Path.Combine(obsoleteRoot,"View","QA","装配工作区",Guid.NewGuid().ToString("N"));
   foreach(var dir in new[]{obsoleteSnapshot,currentWorking}){Directory.CreateDirectory(dir);var path=Path.Combine(dir,"ROOT.SLDASM");File.WriteAllText(path,"cache snapshot bytes",new UTF8Encoding(false));File.SetAttributes(path,FileAttributes.ReadOnly);}
   cleaner.GetMethod("RegisterLegacySnapshots",Flags).Invoke(null,new[]{(object)obsoleteRoot,obsoletePackage});Record(obsoleteRoot,currentWorking,obsoletePackage,"working");
   Clean(obsoleteRoot,new[]{Path.Combine(currentWorking,"ROOT.SLDASM")},new Guid[0],long.MaxValue,false);
   Check(!Directory.Exists(obsoleteSnapshot)&&Directory.Exists(currentWorking),"旧快照核对通过后自动回收，当前打开的工作区保留");
   var junctionRoot=Path.Combine(output,"junctions");var external=Path.Combine(junctionRoot,"original");Directory.CreateDirectory(external);var sentinel=Path.Combine(external,"ROOT.SLDASM");File.WriteAllText(sentinel,"cache snapshot bytes",new UTF8Encoding(false));File.SetAttributes(sentinel,FileAttributes.ReadOnly);
   var junctionParent=Path.Combine(junctionRoot,"View","QA","装配工作区");Directory.CreateDirectory(junctionParent);var junction=Path.Combine(junctionParent,Guid.NewGuid().ToString("N"));
   var command="New-Item -ItemType Junction -Path '"+junction.Replace("'","''")+"' -Target '"+external.Replace("'","''")+"' | Out-Null";
   using(var process=System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(@"C:\Program Files\PowerShell\7\pwsh.exe","-NoProfile -EncodedCommand "+Convert.ToBase64String(Encoding.Unicode.GetBytes(command))){UseShellExecute=false,CreateNoWindow=true,WindowStyle=System.Diagnostics.ProcessWindowStyle.Hidden}))
   {if(!process.WaitForExit(10000)||process.ExitCode!=0)throw new Exception("缓存目录链接回归准备失败");}
   Record(junctionRoot,junction,obsoletePackage,"working");Exception linkFailure=null;object linkResult=null;try{linkResult=Clean(junctionRoot,new string[0],new Guid[0],0,true);}catch(System.Reflection.TargetInvocationException error){linkFailure=error.InnerException;}
   Check((linkFailure is IOException||linkResult!=null&&(long)Get(linkResult,"FreedBytes")==0)
    &&File.Exists(sentinel)&&Directory.Exists(junction)&&(File.GetAttributes(sentinel)&FileAttributes.ReadOnly)!=0,"缓存目录为链接时拒绝清理，包外原图与属性保持原样");Directory.Delete(junction,false);
   var settings=New("PluginSettings");Set(settings,"CacheLimitGigabytes",0);TreeCall(settings,"Normalize");Check((int)Get(settings,"CacheLimitGigabytes")==20,"旧设置兼容默认20GB，可设置容量限制");
  }
  finally{bindings.SetValue(null,savedBindings);resolver.SetValue(null,savedResolver);}
 }
 sealed class CacheDownloadHandler:HttpMessageHandler
 {
  internal readonly Dictionary<Guid,byte[]> Bytes=new Dictionary<Guid,byte[]>();internal int Requests;internal bool Fail;
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
  {
   token.ThrowIfCancellationRequested();Interlocked.Increment(ref Requests);if(Fail)return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
   var id=Guid.Parse(request.RequestUri.AbsolutePath.Split('/')[3]);return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(Bytes[id])});
  }
 }
}
