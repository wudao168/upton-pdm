using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Threading;
internal static partial class Program
{
 static void ArchiveConfigCatalogScenario(string output)
 {
  Directory.CreateDirectory(output);
  void Check(bool ok,string message){if(!ok)throw new Exception(message);Console.WriteLine("PASS: "+message);}
  var appType=Type("PdmAddin").GetField("application",Flags).FieldType;var interop=appType.Assembly;
  foreach(var strict in new[]{false,true})
  foreach(var mode in new[]{"closed","loaded","missing","unreadable","outside"})
  {
   var path=Path.GetFullPath(Path.Combine(output,mode+".SLDPRT"));File.WriteAllText(path,"原始测试字节");var hash=WorkingReuseHash(path);
   var node=New("CadTreeNode");Set(node,"FullPath",path);Set(node,"FileName",Path.GetFileName(path));Set(node,"Configuration","PreviewCfg");Set(node,"Kind",Enum.Parse(Type("CadDocumentKind"),"Part"));
   if(mode=="outside")Set(node,"IsOutsideNativeFileScope",true);
   var names=mode=="missing"?new[]{"默认"}:mode=="unreadable"?null:new[]{"默认","PreviewCfg"};var reads=0;var forbidden=0;
   var model=new AssemblyProxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true),call=>{
    if(call.MethodName=="GetPathName")return path;
    if(call.MethodName=="GetConfigurationNames"){reads++;return names;}
    forbidden++;throw new Exception("禁止配置对象展开、切换或保存："+call.MethodName);
   }).GetTransparentProxy();
   var app=new Proxy(appType,call=>{
    if(call.MethodName=="GetDocuments")return mode=="loaded"?new[]{model}:new object[0];
    if(call.MethodName=="GetConfigurationNames"){reads++;return names;}
    forbidden++;throw new Exception("禁止完整打开或修改文件："+call.MethodName);
   }).GetTransparentProxy();
   var policy=Activator.CreateInstance(assembly.GetType("Upton.Pdm.Domain.ArchiveWarningPolicy",true));Set(policy,"BlockAll",strict);
   Type("ArchiveWarningGate").GetMethod("Configure",Flags).Invoke(null,new[]{policy});
   var plugin=New("PdmAddin");SetField(plugin,"application",app);
   var nodes=(IList)Activator.CreateInstance(typeof(System.Collections.Generic.List<>).MakeGenericType(Type("CadTreeNode")));nodes.Add(node);nodes.Add(node);
   Exception failure=null;try{TreeCall(plugin,"ValidateSavedModelConfigurations",nodes,CancellationToken.None);}catch(Exception e){failure=e;}
   if(mode=="missing"&&strict)Check(failure!=null&&failure.ToString().Contains("PreviewCfg")&&failure.ToString().Contains("可用配置：默认"),"实际缺失配置在完整打开前报告，并提供可用配置");
   else if(mode=="unreadable"&&strict)Check(failure!=null&&failure.ToString().Contains("核对未完成")&&!failure.ToString().Contains("未找到“PreviewCfg”"),"无法读取目录不被误报为配置确定不存在");
   else Check(failure==null,mode+"：有效配置目录通过，不依赖配置COM对象");
   if(mode=="missing"||mode=="unreadable")Check(((IList)Get(node,"ReferenceWarnings")).Count==1,"警告随图档保留且去重");
   Check(reads==(mode=="outside"?0:1),mode+"：同一文件多个实例只读一次配置目录");
   Check(forbidden==0&&WorkingReuseHash(path)==hash&&(string)Get(node,"Configuration")=="PreviewCfg",mode+"：未打开、修改或替换引用配置");
  }
 }
}
