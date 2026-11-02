using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

internal static partial class Program
{
 static async Task VirtualAncestorReferenceScenario(string output)
 {
  void Check(bool ok,string message){if(!ok)throw new Exception(message);Console.WriteLine("PASS: "+message);}
  var policy=Type("ArchiveMissingReferencePolicy").GetProperty("BlockMissingReferences",Flags);var previous=policy.GetValue(null);
  foreach(var mode in new[]{"ancestor","old-user-context","nested","duplicate-instance","unselected-owner","unknown-cache","wrong-parent-key","ambiguous-owner","external-link","external-child-boundary","missing-owner","cancelled"})
  {
   var folder=Path.Combine(Path.GetFullPath(output),mode);Directory.CreateDirectory(folder);
   var root=LinkedNode(folder,"ROOT.SLDASM","Assembly");var owner=LinkedNode(folder,"70004449.F1.SLDASM","Assembly");
   var sub=LinkedNode(folder,"70004449.F1.01.01.SLDASM","Assembly");var part=LinkedNode(folder,"70004449.F1.01.01-01.SLDPRT","Part");
   void Add(object p,object c)=>((IList)Get(p,"Children")).Add(c);
   var cacheRoot=Path.Combine(Path.GetTempPath(),"swx-fixture-"+Guid.NewGuid().ToString("N"));
   var cache=Path.Combine(cacheRoot,"VC~~","70004449.F1","70004449.F1.01^70004449.F1.SLDASM");
   Directory.CreateDirectory(Path.GetDirectoryName(cache));File.WriteAllText(cache,"native virtual fixture");
   object Virtual(string path){var n=New("CadTreeNode");Set(n,"FullPath",path);Set(n,"FileName",Path.GetFileName(path));Set(n,"Kind",Enum.Parse(Type("CadDocumentKind"),"Assembly"));Set(n,"Status",Enum.Parse(Type("CadReferenceStatus"),"Virtual"));return n;}
   var inner=Virtual(cache);Add(root,owner);Add(owner,inner);Add(inner,sub);Add(sub,part);
   var reference=cache;
   if(mode=="old-user-context")reference=@"C:\Users\old-user\AppData\Local\Temp\swx8548\VC~~\70004449.F1\70004449.F1.01^70004449.F1.SLDASM";
   if(mode=="nested"){var outer=Virtual(Path.Combine(cacheRoot,"VC~~","70004449.F1","OUTER^70004449.F1.SLDASM"));((IList)Get(owner,"Children")).Clear();Add(owner,outer);Add(outer,inner);}
   if(mode=="duplicate-instance")Add(owner,Virtual(cache));
   if(mode=="unknown-cache")Set(inner,"Status",Enum.Parse(Type("CadReferenceStatus"),"Normal"));
   if(mode=="wrong-parent-key")Set(inner,"FullPath",cache.Replace("VC~~\\70004449.F1\\","VC~~\\OTHER\\"));
   if(mode=="ambiguous-owner"){var other=LinkedNode(folder,"OTHER.SLDASM","Assembly");Add(root,other);Add(other,Virtual(cache));}
   if(mode=="external-link")Set(inner,"Role",Enum.Parse(Type("CadReferenceRole"),"ExternalFileDependency"));
   if(mode=="external-child-boundary"){
    ((IList)Get(owner,"Children")).Clear();Add(owner,sub);((IList)Get(sub,"Children")).Clear();Add(sub,inner);((IList)Get(inner,"Children")).Clear();Add(inner,part);
   }
   if(mode=="missing-owner")File.Delete((string)Get(owner,"FullPath"));
   var selected=mode=="unselected-owner"||mode=="external-child-boundary"||mode=="missing-owner"?LinkedItems(part):LinkedItems(owner,part);
   var plugin=New("PdmAddin");SetField(plugin,"currentTree",root);
   var appType=Type("PdmAddin").GetField("application",Flags).FieldType;
   var reads=0;
   SetField(plugin,"application",new Proxy(appType,m=>{
    if(m.MethodName=="GetDocuments")return new object[0];
    if(m.MethodName=="GetDocumentDependencies2"){reads++;return (string)m.Args[0]==(string)Get(part,"FullPath")?new[]{"virtual-context",reference}:new string[0];}
    if(m.MethodName=="GetCurrentWorkingDirectory")return folder;
    if(m.MethodName=="SetCurrentWorkingDirectory")return true;
    throw new Exception("禁止打开、解析、保存、登记或上传："+m.MethodName);
   }).GetTransparentProxy());
   var snapshots=Directory.GetFiles(folder).ToDictionary(p=>p,WorkingReuseHash);var cacheHash=WorkingReuseHash(cache);
   try{
    policy.SetValue(null,true); // A proven virtual reference is not a missing-file exception.
    var token=mode=="cancelled"?new CancellationToken(true):CancellationToken.None;
    Exception failure=null;object result=null;
    try{
     if(mode!="cancelled")TreeCall(plugin,"ValidatePdmNativeDependencyPaths",selected,CancellationToken.None);
     var task=(Task)TreeCall(plugin,"CompletePdmCheckInDependenciesAsync",selected,Guid.NewGuid(),token);await task;result=Get(task,"Result");
    }catch(Exception e){failure=e;}
    var allowed=new[]{"ancestor","old-user-context","nested","duplicate-instance"}.Contains(mode);
    Check(allowed?failure==null:failure!=null,mode+"：两个阶段使用一致的虚拟保存归属边界"+(allowed&&failure!=null?" "+failure:""));
    if(allowed){
     Check(((IEnumerable)result).Cast<object>().Count()==selected.Length,"保持本次勾选数量："+mode);
     Check(((IList)Get(part,"Children")).Count==0&&Get(inner,"DocumentId")==null,"不独立登记虚拟祖先、不制造零件到父装配的普通循环："+mode);
     Check(((IList)Get(part,"ReferenceWarnings")).Count==0,"有效虚拟祖先不误报缺失："+mode);
     Check(reads<=6,"仅复用结构，不增加原生引用读取："+mode);
    }
    Check(snapshots.All(p=>WorkingReuseHash(p.Key)==p.Value)&&WorkingReuseHash(cache)==cacheHash,"原图与虚拟缓存字节不变："+mode);
   }finally{
    policy.SetValue(null,previous);
    var full=Path.GetFullPath(cacheRoot);var temp=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
    if(full.StartsWith(temp,StringComparison.OrdinalIgnoreCase)&&Path.GetFileName(full).StartsWith("swx-fixture-",StringComparison.Ordinal))Directory.Delete(full,true);
   }
  }
 }
}
