using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

internal static partial class Program
{
 static async Task ArchiveStructureReuseScenario()
 {
  foreach(var mode in new[]{"unchanged","changed-file","changed-configuration","other-path","other-instance","target-dirty","source-dirty","rename","pending-index","changed-after-reuse","refresh-handoff","deferred-display"})
  {
   var folder=Path.Combine(Path.GetTempPath(),"pdm-archive-structure-reuse-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
   object AssemblyNode(string path,string instance="root/suppressed")
   {
    var node=New("CadTreeNode");Set(node,"Kind",Enum.Parse(Type("CadDocumentKind"),"Assembly"));Set(node,"FullPath",path);Set(node,"FileName",Path.GetFileName(path));Set(node,"Configuration","Default");Set(node,"InstancePath",instance);Set(node,"Status",Enum.Parse(Type("CadReferenceStatus"),"Suppressed"));return node;
   }
   var path=Path.Combine(folder,"SS5Y-8位电磁阀座.SLDASM");File.WriteAllText(path,"unchanged archived suppressed assembly");
   var source=AssemblyNode(path);var target=AssemblyNode(path);var root=New("CadTreeNode");Set(root,"Kind",Enum.Parse(Type("CadDocumentKind"),"Assembly"));Set(root,"FullPath",Path.Combine(folder,"CW0030002.10.SLDASM"));Set(root,"Configuration","Default");
   var cacheType=Type("SuppressedAssemblyLocalStructure");var calls=0;
   object Structure(object node){calls++;var result=AssemblyNode((string)Get(node,"FullPath"),(string)Get(node,"InstancePath"));Set(result,"Configuration",Get(node,"Configuration"));var child=New("CadTreeNode");Set(child,"FullPath",Path.Combine(folder,"EMBEDDED.SLDPRT"));Set(child,"FileName","嵌入子件");Set(child,"Kind",Enum.Parse(Type("CadDocumentKind"),"Part"));Set(child,"InstancePath","root/suppressed/embedded");((IList)Get(result,"Children")).Add(child);return result;}
   var read=Delegate.CreateDelegate(cacheType.GetMethod("ExpandAsync",Flags).GetParameters()[1].ParameterType,new StructureReuseReader(Structure),typeof(StructureReuseReader).GetMethod("Read").MakeGenericMethod(Type("CadTreeNode")));
   await (Task)cacheType.GetMethod("ExpandAsync",Flags).Invoke(null,new object[]{source,read,CancellationToken.None});if(calls!=1)throw new Exception("首次必须读取真实结构");
   switch(mode){case "changed-file":File.AppendAllText(path," later saved structural edit");break;case "changed-configuration":Set(target,"Configuration","Other");break;case "other-path":var copy=Path.Combine(folder,"different.SLDASM");File.Copy(path,copy);Set(target,"FullPath",copy);break;case "other-instance":Set(target,"InstancePath","other/suppressed");break;case "target-dirty":Set(target,"IsModifiedInSolidWorks",true);break;case "source-dirty":Set(source,"IsModifiedInSolidWorks",true);break;case "rename":Set(target,"IsRenamePendingSave",true);break;case "pending-index":Set(source,"StructureIndexPending",true);break;}
   var method=cacheType.GetMethod("TryReuse",Flags);bool reused;
   if(mode=="refresh-handoff"||mode=="deferred-display")
   {
    if(mode=="deferred-display")Set(source,"LocalStructureDeferred",true);
    Type("PdmAddin").GetMethod("ReuseVerifiedArchiveStructures",Flags).Invoke(null,new[]{source,target});
    reused=((IList)Get(target,"Children")).Count>0;
   }
   else reused=method!=null&&(bool)method.Invoke(null,new[]{source,target});
   var shouldReuse=new[]{"unchanged","pending-index","changed-after-reuse","refresh-handoff"}.Contains(mode);
   if(reused!=shouldReuse)throw new Exception("压缩结构重复读取或错误复用："+mode);
   if(mode=="changed-after-reuse")File.AppendAllText(path," concurrent save after reuse");
   ((IList)Get(root,"Children")).Add(target);var beforeCalls=calls;
   await (Task)cacheType.GetMethod("ExpandAsync",Flags).Invoke(null,new object[]{root,read,CancellationToken.None});
   if(calls-beforeCalls!=(reused&&mode!="changed-after-reuse"?0:1))throw new Exception("复核阶段错误跳过变化或重复打开未修改子装配："+mode);
   if(mode=="pending-index"&&!(bool)Get(target,"StructureIndexPending"))throw new Exception("复用丢失了待补全状态");
   Console.WriteLine("PASS: archive structure reuse "+mode+"; unchanged native reads=0, changed paths/configurations/bytes retain fresh verification");
  }
 }
 sealed class StructureReuseReader
 {
  readonly Func<object,object> read;public StructureReuseReader(Func<object,object> read){this.read=read;}
  public Task<T> Read<T>(T node)=>Task.FromResult((T)read(node));
 }
}
