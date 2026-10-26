using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Remoting.Messaging;

internal static partial class Program
{
 static void ArchiveNativeReferenceScenario(string fixture)
 {
  Directory.CreateDirectory(fixture);
  var owner=Path.Combine(fixture,"OWNER.SLDPRT");File.WriteAllText(owner,"saved native owner");
  var appType=Type("PdmAddin").GetField("application",Flags).FieldType;
  foreach(var path in new[]{@"C:\Users\engineer\swx222352\IC~~\import.SLDASM",@"C:\Users\engineer\AppData\Local\Temp\swx222352\IC~~\import.SLDASM"})
  {
   if(!(bool)Type("NativeArchiveManifest").GetMethod("IsInterconnectCachePath",Flags).Invoke(null,new object[]{path}))throw new Exception("未识别历史原生缓存位置");
   Console.WriteLine("PASS: known historical native cache shape is recognized");
  }
  if((bool)Type("NativeArchiveManifest").GetMethod("IsInterconnectCachePath",Flags).Invoke(null,new object[]{@"C:\Users\engineer\project\IC~~\part.SLDPRT"}))throw new Exception("普通项目路径被误认为缓存");
  Console.WriteLine("PASS: ordinary project files are not classified by IC folder name alone");
  var cacheFolder=Path.Combine(Path.GetTempPath(),"swx-preflight-"+Guid.NewGuid().ToString("N"),"IC~~");Directory.CreateDirectory(cacheFolder);
  try
  {
   foreach(var mode in new[]{"missing-import","loaded-import","existing-cache","ordinary-component","suppressed-import","lightweight-import"})
   {
    var cache=Path.Combine(cacheFolder,"SOURCE.SLDASM");if(mode=="existing-cache")File.WriteAllText(cache,"saved cache");else if(File.Exists(cache))File.Delete(cache);
    var interop=appType.Assembly;
    var component=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.Component2",true),call=>call.MethodName=="GetSuppression2"?(mode=="suppressed-import"?0:mode=="lightweight-import"?1:2):call.MethodName=="GetPathName"?(mode=="ordinary-component"?dependencyPath():cache):call.MethodName=="GetModelDoc2"?(mode=="loaded-import"?new object():null):call.MethodName=="GetImportedPath"?@"Z:\original\SOURCE.STEP":call.MethodName=="get_Name2"?"SOURCE.STEP-1":throw new Exception(call.MethodName)).GetTransparentProxy();
    string dependencyPath()=>Path.Combine(fixture,"ordinary-missing.SLDPRT");
    var root=new AssemblyProxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true),call=>call.MethodName=="GetComponents"?new[]{component}:throw new Exception("预检不应写入模型："+call.MethodName)).GetTransparentProxy();
    var readOnlyApp=new Proxy(appType,call=>call.MethodName=="GetOpenDocumentByName"?root:throw new Exception("预检不应改变窗口："+call.MethodName)).GetTransparentProxy();
    Exception failure=null;try{Type("ArchiveNativeCopyWorkspace").GetMethod("ValidateLoadedImports",Flags).Invoke(null,new object[]{readOnlyApp,new[]{owner}});}catch(TargetInvocationException error){failure=error.InnerException;}
    if((failure!=null)!=(mode=="missing-import")||failure!=null&&(!failure.Message.Contains("未上传任何文件")||failure.Message.Contains("被占用")))throw new Exception("导入模型预检范围不正确："+mode,failure);
    Console.WriteLine("PASS: read-only early import preflight handles "+mode);
   }
  }
  finally{Directory.Delete(Path.GetDirectoryName(cacheFolder),true);}
  foreach(var mode in new[]{"step","parameter","native-failure","native-restoration"})
  {
   var dependency=Path.Combine(fixture,mode=="step"?"SOURCE.STEP":mode=="parameter"?"SOURCE.txt":"SOURCE.SLDPRT");
   File.WriteAllText(dependency,"saved source");int calls=0;string isolatedReference=null;
   object References(IMethodCallMessage call)
   {
    if(call.MethodName=="ReplaceReferencedDocument")
    {
     calls++;if(mode=="native-failure")return false;
     isolatedReference=(string)call.Args[2];return true;
    }
    var path=(string)call.Args[0];if(Path.GetFileName(path).EndsWith("SOURCE.SLDPRT",StringComparison.OrdinalIgnoreCase))return new string[0];
    if(mode=="native-restoration"&&isolatedReference!=null)return new[]{"SOURCE",isolatedReference};
    return new[]{"SOURCE",dependency};
   }
   var app=ArchiveNativeMock(appType,call=>call.MethodName=="GetDocuments"?new object[0]:null,References);
   Exception failure=null;
   try{using(var workspace=(IDisposable)New("ArchiveNativeCopyWorkspace",app,new[]{owner,dependency},new Dictionary<string,string>{{owner,owner}},null,null)){} }
   catch(TargetInvocationException error){failure=error.InnerException;}
   if(mode=="native-failure")
   {
    if(failure is not InvalidOperationException||!failure.Message.Contains("OWNER.SLDPRT")||!failure.Message.Contains("SOURCE.SLDPRT")||!failure.Message.Contains("未开始上传"))throw new Exception("原生引用定位失败未提供具体中文原因",failure);
    var visible=(string)Type("PdmAddin").GetMethod("ArchivePreflightError",Flags).Invoke(null,new[]{failure});
    if(visible!=failure.Message||visible.Contains("被占用"))throw new Exception("引用失败仍误报文件占用");
    Console.WriteLine("PASS: native reference failure retains owner, dependency and no-upload Chinese message");
   }
   else
   {
    if(failure!=null)throw failure;
    if(calls!=(mode=="native-restoration"?2:0))throw new Exception("替换接口调用范围不正确："+mode+"；"+calls);
    Console.WriteLine("PASS: "+mode+" uses the proper native reference handling");
   }
   if(File.ReadAllText(owner)!="saved native owner"||File.ReadAllText(dependency)!="saved source")throw new Exception("原文件被修改");
   Console.WriteLine("PASS: "+mode+" preserves original file bytes");
  }
  var leaf=Path.Combine(fixture,"UNCHANGED-ALL-CONFIGURATIONS.SLDPRT");File.WriteAllText(leaf,"unchanged native configuration and body streams");
  var expensiveReads=0;
  var leafApp=ArchiveNativeMock(appType,call=>call.MethodName=="GetDocuments"?new object[0]:null,null,name=>{
   if(name=="GetConfigurationNames"||name=="ForceRebuild3"||name=="Save3")expensiveReads++;
  });
  using(var workspace=(IDisposable)New("ArchiveNativeCopyWorkspace",leafApp,new[]{leaf},new Dictionary<string,string>{{leaf,leaf}},null,null))
  {
   var file=((System.Collections.IEnumerable)workspace.GetType().GetProperty("Files",Flags).GetValue(workspace)).Cast<object>().Single();
   if(File.ReadAllText((string)file.GetType().GetField("Path",Flags).GetValue(file))!=File.ReadAllText(leaf)||expensiveReads!=0)
    throw new Exception("原生字节未变化的叶子文件仍被逐配置重建或重存");
  }
  Console.WriteLine("PASS: byte-identical native leaves retain all configuration bytes without redundant rebuild or save");
 }
}
