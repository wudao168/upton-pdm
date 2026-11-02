using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

internal static partial class Program
{
 static async Task MissingCachePolicyScenario(string output)
 {
  void Check(bool ok,string message){if(!ok)throw new Exception(message);Console.WriteLine("PASS: "+message);}
  var policy=Type("ArchiveMissingReferencePolicy");var block=policy.GetProperty("BlockMissingReferences",Flags);var previous=block.GetValue(null);
  foreach(var mode in new[]{"warning","strict","selected-missing","no-confirmed-scope","existing-unverified","verified-embedded"})
  {
   var folder=Path.Combine(Path.GetFullPath(output),mode);Directory.CreateDirectory(folder);
   var root=LinkedNode(folder,"NP60-D.SLDASM","Assembly");var path=(string)Get(root,"FullPath");var hash=WorkingReuseHash(path);
   var cacheRoot=Path.Combine(Path.GetTempPath(),"swx-fixture-"+Guid.NewGuid().ToString("N"));var cache=Path.Combine(cacheRoot,"IC~~","NP60-D.stp.SLDASM");
   var child=New("CadTreeNode");Set(child,"FullPath",cache);Set(child,"FileName",Path.GetFileName(cache));Set(child,"Kind",Enum.Parse(Type("CadDocumentKind"),"Assembly"));Set(child,"Status",Enum.Parse(Type("CadReferenceStatus"),"Missing"));((IList)Get(root,"Children")).Add(child);
   var plugin=New("PdmAddin");var appType=Type("PdmAddin").GetField("application",Flags).FieldType;
   SetField(plugin,"currentTree",root);SetField(plugin,"application",new Proxy(appType,m=>{
    if(m.MethodName=="GetDocuments")return new object[0];
    if(m.MethodName=="GetDocumentDependencies2")return new[]{"NP60-D.stp",cache};
    if(m.MethodName=="GetCurrentWorkingDirectory")return folder;
    if(m.MethodName=="SetCurrentWorkingDirectory")return true;
    throw new Exception("引用检查不得打开、解析、保存或改图："+m.MethodName);
   }).GetTransparentProxy());
   try
   {
    block.SetValue(null,false);
    if(mode=="existing-unverified"){Directory.CreateDirectory(Path.GetDirectoryName(cache));File.WriteAllText(cache,"unverified cache");}
    if(mode=="verified-embedded")Set(child,"ImportedStorageOwnerPath",path);
    if(mode!="no-confirmed-scope"){
     var paths=new HashSet<string>(StringComparer.OrdinalIgnoreCase){path};if(mode=="selected-missing")paths.Add(cache);Set(root,"ConfirmedArchivePaths",paths);
    }
    if(mode=="warning"){
     Check((bool)policy.GetMethod("TryOmitMissing",Flags).Invoke(null,new object[]{root,cache,child}),"提前检查已将未选缺失缓存记为引用提示");
    }
    block.SetValue(null,mode=="strict");
    Exception failure=null;object result=null;
    try{var task=(Task)TreeCall(plugin,"CompletePdmCheckInDependenciesAsync",LinkedItems(root),Guid.NewGuid(),CancellationToken.None);await task;result=Get(task,"Result");}catch(Exception error){failure=error;}
    if(mode=="warning"||mode=="verified-embedded"){
     Check(failure==null&&((IEnumerable)result).Cast<object>().Count()==1,mode+"：后续引用检查保持原提交范围");
     Check(((IList)Get(root,"ReferenceWarnings")).Count==(mode=="warning"?1:0),mode+"：提示不重复，已证实嵌入的不误报缺失");
     if(mode=="warning")Check(!(bool)Get(child,"IsStoredImport")&&(bool)Get(root,"StructureIndexPending"),"缺失提示不会伪造嵌入保存证明，结构待补全状态保留");
    }else{
     Check(failure!=null,mode+"：保护条件仍在上传前阻止");
     Check(mode=="strict"?failure.Message.Contains("管理员已启用"):failure.Message.Contains("保存归属尚未确认"),mode+"：不会以缺失警告绕过选取、配置或保存归属");
    }
    Check(WorkingReuseHash(path)==hash&&Get(child,"DocumentId")==null,"原图字节与引用身份不变："+mode);
   }
   finally{
    block.SetValue(null,previous);
    var full=Path.GetFullPath(cacheRoot);var temp=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
    if(Directory.Exists(full)&&full.StartsWith(temp,StringComparison.OrdinalIgnoreCase)&&Path.GetFileName(full).StartsWith("swx-fixture-",StringComparison.Ordinal))Directory.Delete(full,true);
   }
  }
 }
}
