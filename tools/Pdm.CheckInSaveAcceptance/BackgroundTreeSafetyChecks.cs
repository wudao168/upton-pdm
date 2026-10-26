using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Remoting.Messaging;

internal static partial class Program
{
 static void BackgroundTreeSafetyScenario(string output)
 {
  Directory.CreateDirectory(output);
  var files=Path.Combine(Path.GetFullPath(output),"files");Directory.CreateDirectory(files);
  var owner=Path.Combine(files,"总装.SLDASM");File.WriteAllText(owner,"mock CAD; never opened in SolidWorks");
  var old=Path.Combine(files,"old","子装.SLDASM");
  var embedded=Path.Combine(files,"嵌入^总装.SLDASM");
  var interop=Type("PdmAddin").GetField("application",Flags).FieldType.Assembly;
  object Mock(string name,Func<IMethodCallMessage,object> call)=>new Proxy(interop.GetType("SolidWorks.Interop.sldworks."+name,true),call).GetTransparentProxy();
  void Check(bool value,string message){if(!value)throw new Exception(message);Console.WriteLine("PASS: "+message);}
  foreach(var mode in new[]{"unready","loaded","strict-unready"})
  {
   var calls=new List<string>();object model=null,root=null;
   object Component(string path,bool isRoot,bool isVirtual)=>Mock("Component2",call=>{
    switch(call.MethodName){
     case "GetPathName":return path;
     case "get_Name2":return Path.GetFileNameWithoutExtension(path)+ (isRoot?"":"-1");
     case "get_ReferencedConfiguration":return "默认";
     case "GetModelDoc2":return isRoot?model:null;
     case "get_IsVirtual":return isVirtual;
     case "get_IsSpeedPak":return false;
     case "GetSuppression2":return isRoot?2:isVirtual?0:1;
     case "IsHidden":return false;
     case "GetChildren":return isRoot?new[]{Component(old,false,false),Component(embedded,false,true)}:new object[0];
     default:throw new Exception("禁止的组件操作："+call.MethodName);
    }
   });
   var configuration=Mock("Configuration",call=>{
    if(call.MethodName=="get_Name")return "默认";
    if(call.MethodName=="GetRootComponent3"){calls.Add("Resolve="+call.Args[0]);return mode=="loaded"?root:null;}
    throw new Exception("禁止的配置操作："+call.MethodName);
   });
   var manager=Mock("ConfigurationManager",call=>call.MethodName=="get_ActiveConfiguration"?configuration:throw new Exception(call.MethodName));
   model=Mock("ModelDoc2",call=>{
    switch(call.MethodName){
     case "GetPathName":return owner;
     case "GetTitle":return Path.GetFileName(owner);
     case "GetType":return 2;
     case "GetSaveFlag":return false;
     case "get_ConfigurationManager":return manager;
     default:calls.Add("ModelMutation:"+call.MethodName);throw new Exception(call.MethodName);
    }
   });
   root=Component(owner,true,false);
   var appType=Type("PdmAddin").GetField("application",Flags).FieldType;
   var app=new Proxy(appType,call=>{
    switch(call.MethodName){
     case "get_ActiveDoc":return model;
     case "get_Visible":return false;
     case "GetCurrentWorkingDirectory":return files;
     case "GetOpenDocumentByName":return null;
     case "GetDocuments":return new object[0];
     default:calls.Add("AppMutation:"+call.MethodName);throw new Exception("自动设计树不能加载或切换图档："+call.MethodName);
    }
   }).GetTransparentProxy();
   var scanner=Type("SolidWorksReferenceTreeScanner").GetConstructors(BindingFlags.Public|BindingFlags.Instance).Single().Invoke(new[]{app});
   var before=File.ReadAllBytes(owner);
   if(mode=="strict-unready")
   {
    var rejected=false;try{TreeCall(scanner,"ScanDocument",model,true);}catch(TargetInvocationException error)when(error.InnerException is InvalidOperationException){rejected=true;}
    Check(rejected,"存档完整检查仍拒绝尚未就绪的装配结构");
    Check(calls.SequenceEqual(new[]{"Resolve=True"}),"只有显式完整检查允许加载配置所需组件");
   }
   else
   {
    var tree=TreeCall(scanner,"ScanDocument",model,false);
    Check(calls.Contains("Resolve=False")&&!calls.Contains("Resolve=True"),"自动刷新不加载额外组件："+mode);
    Check(!calls.Any(call=>call.StartsWith("AppMutation:")||call.StartsWith("ModelMutation:")),"自动刷新不搜索解析、打开、关闭、保存或激活图档："+mode);
    if(mode=="unready")Check((bool)Get(tree,"StructureIndexPending"),"尚未就绪的根组件明确显示待补全，不伪装空装配");
    else
    {
     var children=((IEnumerable)Get(tree,"Children")).Cast<object>().ToArray();
     Check(children.Length==2,"当前可读取的轻量子装配和嵌入层级均保留");
     var pending=children.Single(child=>(string)Get(child,"FullPath")==embedded);
     Check((bool)Get(pending,"StructureIndexPending"),"未加载的嵌入子装配显示待补全，后台不建立或打开副本");
     Check((string)Get(children.Single(child=>(string)Get(child,"FullPath")==old),"SourceReferencePath")==old,"轻量组件保留原始引用位置，存档再完整校验");
    }
   }
   Check(before.SequenceEqual(File.ReadAllBytes(owner)),"自动刷新和失败检查不改写原文件："+mode);
  }
  VisualReferenceCycleScenario();
  StrictScanMatrix();
  StoredImportCacheScenario(files,owner);
 }

 static void StoredImportCacheScenario(string files,string owner)
 {
  var root=New("CadTreeNode");Set(root,"FullPath",owner);
  var source=Path.Combine(files,"LHH.STEP");
  var old=@"C:\Users\li.pw\AppData\Local\Temp\swx14872\IC~~\LHH.STEP.SLDASM";
  var current=Path.Combine(Path.GetTempPath(),"swx99999","IC~~","LHH.STEP.SLDASM");
  var node=New("CadTreeNode");Set(node,"FullPath",current);Set(node,"ImportedStorageOwnerPath",owner);
  ((IList)Get(root,"Children")).Add(node);
  bool Verified(string path)=>((IEnumerable)Type("NativeArchiveManifest").GetMethod("VerifiedEmbeddedReferences",Flags)
   .Invoke(null,new object[]{root,new[]{path}})).Cast<string>().Contains(path);
  void Check(bool value,string text){if(!value)throw new Exception(text);Console.WriteLine("PASS: "+text);}
  Check(Verified(old),"跨用户的旧IC缓存已确认嵌入本父装配，不要求独立补齐");
  Check(!Verified(source),"嵌入缓存的豁免不跳过真实STEP源文件");
  Check(!Verified(Path.Combine(files,"IC~~","LHH.STEP.SLDASM")),"普通目录同名文件不能冒充SolidWorks缓存");
  Set(node,"ImportedStorageOwnerPath",Path.Combine(files,"OTHER.SLDASM"));
  Check(!Verified(old),"其他父装配的嵌入归属不能用于当前图档");
  Set(node,"ImportedStorageOwnerPath","");Check(!Verified(old),"未核实保存归属的缓存不能忽略");
  Set(node,"ImportedStorageOwnerPath",owner);
  var other=New("CadTreeNode");Set(other,"FullPath",Path.Combine(Path.GetTempPath(),"swx88888","IC~~","LHH.STEP.SLDASM"));Set(other,"ImportedStorageOwnerPath",owner);
  ((IList)Get(root,"Children")).Add(other);
  Check(!Verified(old),"同一缓存身份对应多个实际组件时不猜测");
 }
}
