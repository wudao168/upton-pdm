using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

internal static partial class Program
{
 static void ArchiveDependencyPathScenario(string output)
 {
  Directory.CreateDirectory(output);
  var folder=Path.Combine(Path.GetFullPath(output),"files");Directory.CreateDirectory(folder);
  var owner=Path.Combine(folder,"CW0030002.10.SLDASM");
  var current=Path.Combine(folder,"CW0030002.101.SLDASM");
  var other=Path.Combine(folder,"OTHER.SLDPRT");
  var imported=Path.Combine(folder,"来源.STEP");
  foreach(var path in new[]{owner,current,other,imported})File.WriteAllText(path,"dependency regression fixture; never opened by CAD");
  var old=Path.Combine(folder,"CopyDesignTemp","CW0030002.101.SLDASM");
  var original=Path.Combine(folder,"previous-working-directory");Directory.CreateDirectory(original);
  var model=Type("PdmAddin");var build=model.GetMethod("BuildPdmDependencyAliases",Flags);
  var saved=new[]{"CW0030002.101",old};var resolved=new[]{"CW0030002.101",current};
  var scope=new HashSet<string>(new[]{owner,current,other,imported},StringComparer.OrdinalIgnoreCase);
  IDictionary Aliases(Array before,Array after,ISet<string> files=null)=>(IDictionary)build.Invoke(null,new object[]{before,after,owner,files??scope});
  void Pass(string text){Console.WriteLine("PASS: "+text);}
  void Require(bool ok,string text){if(!ok)throw new Exception(text);Pass(text);}
  void Reject(Action action,string text){try{action();}catch(TargetInvocationException error)when(error.InnerException is InvalidOperationException){Pass(text);return;}throw new Exception(text);}
  Require((string)Aliases(saved,resolved)[old]==current,"旧CopyDesignTemp地址映射到本批原生解析目标");
  Require((string)Aliases(new[]{"CW0030002.101",old,"OTHER",Path.Combine(folder,"old","OTHER.SLDPRT")},
    new[]{"OTHER",other,"CW0030002.101",current})[old]==current,"按原生引用键匹配，解析返回次序变化不误配");
  Require((string)Aliases(saved,new[]{"CW0030002.101",current,"CW0030002.101",current})[old]==current,"同一文件多个实例不构成歧义");
  Require(Aliases(saved,resolved,new HashSet<string>{owner,other}).Count==0,"原生解析到清单外同名文件不能替代当前引用");
  Require(Aliases(saved,new[]{"UNRELATED",current}).Count==0,"引用键不同，即使文件同名也不替换");
  Require(Aliases(new[]{"CW0030002.101",current},resolved).Count==0,"当前正确地址保持不变");
  var duplicate=Path.Combine(folder,"another","CW0030002.101.SLDASM");Directory.CreateDirectory(Path.GetDirectoryName(duplicate));File.WriteAllText(duplicate,"different CAD identity");
  Reject(()=>Aliases(saved,new[]{"CW0030002.101",current,"CW0030002.101",duplicate}),"同一引用解析出不同文件时提前拒绝");
  Require(Aliases(saved,new[]{"CW0030002.101",Path.Combine(folder,"missing","CW0030002.101.SLDASM")}).Count==0,"不存在的解析目标不算已收集");
  Reject(()=>Aliases(new[]{"incomplete"},resolved),"原生清单不完整时拒绝继续");
  var oldImport=Path.Combine(folder,"CopyDesignTemp","来源.STEP");
  var both=Aliases(new[]{"CW0030002.101",old+"|"+oldImport},new[]{"CW0030002.101",current+"|"+imported});
  Require((string)both[old]==current&&(string)both[oldImport]==imported,"导入路径按各自原名与原生引用键关联");
  var node=Activator.CreateInstance(Type("CadTreeNode"));Set(node,"FullPath",owner);Set(node,"FileName",Path.GetFileName(owner));
  var reader=model.GetMethod("ReadPdmResolvedDependencyAliases",Flags);
  var appType=model.GetField("application",Flags).FieldType;
  foreach(var fail in new[]{false,true})
  {
   var directory=original;var restores=0;var searches=0;
   var app=new Proxy(appType,call=>{
    switch(call.MethodName){
     case "GetCurrentWorkingDirectory":return directory;
     case "SetCurrentWorkingDirectory":directory=(string)call.Args[0];restores++;return true;
     case "GetDocumentDependencies2":
      if((bool)call.Args[1]||!(bool)call.Args[2]||(bool)call.Args[3])throw new Exception("引用解析参数不正确");
      searches++;directory=folder;if(fail)throw new InvalidOperationException("模拟原生引用读取失败");return resolved;
     default:throw new Exception("路径检查不能打开、关闭、保存或改写CAD："+call.MethodName);
    }
   }).GetTransparentProxy();
   var addin=Activator.CreateInstance(model);model.GetField("application",Flags).SetValue(addin,app);
   if(fail)Reject(()=>reader.Invoke(addin,new object[]{node,saved,scope}),"原生解析异常原样中止，不跳过引用");
   else Require((string)((IDictionary)reader.Invoke(addin,new object[]{node,saved,scope}))[old]==current,"原生读取调用已启用搜索且只接受确认范围内目标");
   Require(directory==original&&restores==1&&searches==1,fail?"异常后恢复SolidWorks原工作目录":"成功后恢复SolidWorks原工作目录");
  }
  var child=Activator.CreateInstance(Type("CadTreeNode"));Set(child,"FullPath",current);Set(child,"FileName",Path.GetFileName(current));
  ((IList)Get(node,"Children")).Add(child);
  var item=Activator.CreateInstance(Type("BatchOperationItem"),node,0);
  var selection=Array.CreateInstance(Type("BatchOperationItem"),1);selection.SetValue(item,0);
  var early=model.GetMethod("ValidatePdmNativeDependencyPaths",Flags);
  foreach(var mode in new[]{"resolved","outside","dirty"})
  {
   var directory=original;var reads=0;
   var dirty=new Proxy(appType.Assembly.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true),call=>
    call.MethodName=="GetPathName"?owner:call.MethodName=="GetSaveFlag"?(object)true:throw new Exception(call.MethodName)).GetTransparentProxy();
   var app=new Proxy(appType,call=>{
    switch(call.MethodName){
     case "GetDocuments":return mode=="dirty"?new[]{dirty}:new object[0];
     case "GetOpenDocumentByName":return null;
     case "GetCurrentWorkingDirectory":return directory;
     case "SetCurrentWorkingDirectory":directory=(string)call.Args[0];return true;
     case "GetDocumentDependencies2":reads++;if(!(bool)call.Args[2])return saved;
      directory=folder;return mode=="outside"?new[]{"CW0030002.101",duplicate}:resolved;
     default:throw new Exception("提前检查不应执行CAD文档准备或变更："+call.MethodName);
    }
   }).GetTransparentProxy();
   var addin=Activator.CreateInstance(model);model.GetField("application",Flags).SetValue(addin,app);
   if(mode=="outside")Reject(()=>early.Invoke(addin,new object[]{selection,CancellationToken.None}),"清单外目标在耗时图档准备前拒绝");
   else{early.Invoke(addin,new object[]{selection,CancellationToken.None});Pass(mode=="dirty"?"未保存文档留到保存后核对，避免按旧磁盘结构误报":"提前检查接受本次已确认的原生解析目标");}
   Require(directory==original&&reads==(mode=="dirty"?0:2),"提前检查保持工作目录且不打开、保存或关闭文档："+mode);
  }
  Require(File.ReadAllText(current).StartsWith("dependency regression fixture")&&!Directory.Exists(Path.GetDirectoryName(old)),"检查不生成旧目录、不改名、不改写原文件");
 }
}
