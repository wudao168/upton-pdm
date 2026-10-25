using System;
using System.Collections.Generic;
using System.IO;
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
 }
}
