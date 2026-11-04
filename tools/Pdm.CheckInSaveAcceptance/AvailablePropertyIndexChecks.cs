using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Remoting.Messaging;

internal static partial class Program
{
 static void AvailablePropertyIndexScenario(string output)
 {
  void Check(bool ok,string text){if(!ok)throw new Exception(text);Console.WriteLine("PASS: "+text);}
  foreach(var mode in new[]{"loaded","readonly","multi-config","partial","no-properties","unloaded","wrong-path","dirty"}){
   var folder=Path.Combine(Path.GetFullPath(output),mode);Directory.CreateDirectory(folder);var node=LinkedNode(folder,"PART.SLDPRT","Part");var path=(string)Get(node,"FullPath");Set(node,"Configuration","A");
   var before=WorkingReuseHash(path);var attributes=File.GetAttributes(path);if(mode=="readonly"){File.SetAttributes(path,attributes|FileAttributes.ReadOnly);attributes=File.GetAttributes(path);}
   var plugin=New("PdmAddin");var appType=Type("PdmAddin").GetField("application",Flags).FieldType;var interop=appType.Assembly;var calls=new List<string>();var readNames=new List<string>();
   var all=new Dictionary<string,Dictionary<string,string>>{
    [""]=new Dictionary<string,string>{{"物料名称","全局名称"},{"物料分类","标准件"},{"品牌","测试品牌"}},
    ["A"]=new Dictionary<string,string>{{"物料名称","配置A名称"}},["B"]=new Dictionary<string,string>{{"物料名称","配置B名称"}}};
   if(mode=="no-properties")foreach(var values in all.Values)values.Clear();
   object Manager(string configuration)=>new Proxy(interop.GetType("SolidWorks.Interop.sldworks.CustomPropertyManager",true),call=>{
    if(!all.TryGetValue(configuration,out var values))throw new Exception("未知配置管理器");
    if(call.MethodName=="GetNames")return values.Keys.ToArray();if(call.MethodName=="get_Count")return values.Count;
    if(call.MethodName=="Get6"){
     Check((bool)call.Args[1],"仅使用缓存读取，不激活配置："+mode);
     var name=(string)call.Args[0];readNames.Add(configuration+"/"+name);
     if(mode=="partial"&&name=="品牌")throw new Exception("模拟单项属性暂时不可读");
     return new ReturnMessage(2,new object[]{name,true,"raw",values[name],false,false},6,call.LogicalCallContext,call);
    }
    throw new Exception("禁止写属性："+call.MethodName);
   }).GetTransparentProxy();
   var ext=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDocExtension",true),call=>call.MethodName=="get_CustomPropertyManager"?Manager((string)call.Args[0]):throw new Exception("禁止修改或保存："+call.MethodName)).GetTransparentProxy();
   var model=new AssemblyProxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true),call=>call.MethodName=="GetPathName"?mode=="wrong-path"?Path.Combine(folder,"OTHER.SLDPRT"):path:call.MethodName=="GetSaveFlag"?(object)(mode=="dirty"):call.MethodName=="get_Extension"?ext:throw new Exception("禁止激活配置、重建或保存："+call.MethodName)).GetTransparentProxy();
   SetField(plugin,"application",new Proxy(appType,call=>{
    calls.Add(call.MethodName);if(call.MethodName=="GetDocuments")return mode=="unloaded"?new object[0]:new[]{model};if(call.MethodName=="GetOpenDocumentByName")return mode=="unloaded"?null:model;
    throw new Exception("禁止打开或解析图档："+call.MethodName);
   }).GetTransparentProxy());
   if(mode=="multi-config"){
    var other=New("CadTreeNode");Set(other,"Configuration","B");var array=Array.CreateInstance(Type("CadTreeNode"),2);array.SetValue(node,0);array.SetValue(other,1);
    var index=(IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string),Type("CadTreeNode").MakeArrayType()));index.Add(path,array);SetField(plugin,"archiveCurrentInstances",index);
   }
   var props=(IDictionary)TreeCall(plugin,"ReadSavedNativeProperties",node,before,null);
   var available=mode!="unloaded"&&mode!="wrong-path"&&mode!="dirty";
   if(available&&mode!="no-properties"){
    Check((string)props["全局/物料分类"]=="标准件"&&(string)props["配置:A/物料名称"]=="配置A名称",mode+"：全局和配置专用属性进入提交索引");
    if(mode=="multi-config")Check((string)props["配置:B/物料名称"]=="配置B名称"&&readNames.Count(name=>name=="/物料分类")==1,"同文件多个引用配置一次读取全局属性，分别保留配置值");
    if(mode=="partial")Check(!props.Contains("全局/品牌")&&((IList)Get(node,"ReferenceWarnings")).Count>0,"单项失败仍保留其他可读属性，并提示索引待补齐");
   }else Check(readNames.Count==0,mode+"：无属性、未加载、路径不匹配或未提交状态不制造业务属性");
   if(mode=="no-properties")Check(((IList)Get(node,"ReferenceWarnings")).Count==0,"没有编辑属性卡也是合法空索引，不阻止原文件存档");
   Check(calls.All(name=>name=="GetDocuments"||name=="GetOpenDocumentByName"),mode+"：不为提取属性打开、保存或解析图档");
   Check(WorkingReuseHash(path)==before&&File.GetAttributes(path)==attributes,mode+"：原文件字节及只读属性不变");
   if(mode=="readonly")File.SetAttributes(path,FileAttributes.Normal);
  }
 }
}
