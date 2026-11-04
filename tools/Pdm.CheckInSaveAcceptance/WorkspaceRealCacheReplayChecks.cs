using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

internal static partial class Program
{
 static async Task WorkspaceRealCacheReplayScenario(string output,string sample)
 {
  Directory.CreateDirectory(output);
  var rows=((object[])new JavaScriptSerializer().DeserializeObject(File.ReadAllText(System.IO.Path.Combine(sample,"sample.json")))).Cast<Dictionary<string,object>>().ToArray();
  var rootName=rows.Single(row=>row["File"].ToString().EndsWith(".SLDPRT",StringComparison.OrdinalIgnoreCase))["File"].ToString();
  var drawingName=rows.Single(row=>row["File"].ToString().EndsWith(".SLDDRW",StringComparison.OrdinalIgnoreCase))["File"].ToString();
  using(var f=new WorkspaceRecoveryFixture(output,false,rootName,drawingName))
  {
   var files=((IEnumerable)Get(f.Manifest,"Files")).Cast<object>().ToArray();
   foreach(var row in rows)
   {
    var name=row["File"].ToString();var path=name==rootName?f.Path:f.PartPath;var file=files.Single(item=>Get(item,"FileName").ToString()==name);
    var binding=(Dictionary<string,object>)row["Binding"];File.WriteAllBytes(path,File.ReadAllBytes(System.IO.Path.Combine(sample,"local",name)));
    var canonical=File.ReadAllBytes(System.IO.Path.Combine(sample,"server",name));f.Handler.Bytes[(Guid)Get(file,"DocumentId")]=canonical;
    Set(file,"Sha256",row["ServerSha256"].ToString());Set(file,"FileLength",(long)canonical.Length);Set(file,"Revision","W3");
    if(name!=rootName)Set(file,"VersionId",Guid.NewGuid());
    var id=Get(file,"DocumentId");var identity=Type("PdmDocumentIdentityStore");
    if(!(bool)identity.GetMethod("TryWriteControlledVersion",Flags).Invoke(null,new object[]{path,id,f.Project,Guid.Parse(binding["VersionId"].ToString()),"W2",binding["Sha256"],Convert.ToInt64(binding["FileLength"])}))throw new Exception("Replay metadata seed failed");
    var args=new object[]{path,null};identity.GetMethod("TryReadLocalMetadata",Flags).Invoke(null,args);
    if(!(bool)identity.GetMethod("TryRecordResolvedReferences",Flags).Invoke(null,new[]{(object)path,args[1],binding["ResolvedReferenceSha256"]}))throw new Exception("Replay projection seed failed");
   }
   Exception conflict=null;try{await f.Prepare();}catch(Exception error){conflict=error;}
   TreeCheck(conflict?.GetType().Name=="WorkspaceCacheConflictException","真实V2/V3字节副本未识别出缓存差异");
   var details=((IEnumerable)Get(conflict,"Files")).Cast<object>().ToArray();
   TreeCheck(details.Length==1&&Get(details[0],"FileName").ToString()==rootName,"单纯旧版可写工程图被错误认定为未提交设计修改");
   var originalHash=WorkingReuseHash(f.Path);var backup=DiscardCall(f.Manager,"BackupWorkingConflicts",f.Manifest,conflict,CancellationToken.None);
   await (Task)DiscardCall(f.Manager,"PrepareBackedUpWorkingCopyAsync",f.Manifest,new Guid[0],backup,CancellationToken.None);
   TreeCheck(f.Handler.Downloads==2&&f.Handler.OtherMutations==0,"真实副本重放扩大了下载或服务器修改范围");
   foreach(var row in rows)
   {
    var target=row["File"].ToString()==rootName?f.Path:f.PartPath;TreeCheck(WorkingReuseHash(target)==row["ServerSha256"].ToString(),"真实副本未更新为正确的PLM V3字节");
    var binding=(Dictionary<string,object>)row["Binding"];TreeCheck(WorkingReuseHash(binding["FullPath"].ToString())==row["LocalSha256"].ToString(),"原业务文件在隔离重放期间发生变化");
   }
   TreeCheck(WorkingReuseHash(System.IO.Path.Combine((string)Get(backup,"RecoveryDirectory"),rootName))==originalHash,"真实差异文件恢复备份不完整");
   Console.WriteLine("PASS: 真实标签机钣金V2/V3字节副本：区分旧缓存与实际差异、保留备份、只更新两份文件；原业务文件及服务器不变");
  }
 }
}
