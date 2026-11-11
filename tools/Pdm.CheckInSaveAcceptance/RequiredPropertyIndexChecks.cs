using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Remoting.Messaging;
using System.Web.Script.Serialization;

internal static partial class Program
{
 static void RequiredPropertyIndexScenario(string output)
 {
  void Check(bool ok,string description){if(!ok)throw new Exception(description);Console.WriteLine("PASS: "+description);}
  foreach(var kind in new[]{"Part","Assembly","Drawing"})
  foreach(var mode in new[]{"metadata-touch","unloaded","loaded","readonly","partial","missing-value","cached-value","name-count","empty","dirty","wrong-path","open-dirty","open-stamp-changes","open-source-changes","changed","missing-config","multi-config"})
  {
   var folder=Path.Combine(Path.GetFullPath(output),kind,mode);Directory.CreateDirectory(folder);
   var node=LinkedNode(folder,"图档"+(kind=="Part"?".SLDPRT":kind=="Assembly"?".SLDASM":".SLDDRW"),kind);
   Set(node,"Configuration",kind=="Drawing"?"":"A");var path=(string)Get(node,"FullPath");
   var original=File.ReadAllBytes(path);var hash=WorkingReuseHash(path);
   if(mode=="readonly")File.SetAttributes(path,FileAttributes.ReadOnly);
   var attributes=File.GetAttributes(path);var calls=new List<string>();var reads=0;var opened=false;var closed=0;var nativeOptions=0;
   var isLoaded=mode!="unloaded"&&mode!="readonly"&&!mode.StartsWith("open-");
   var all=new Dictionary<string,Dictionary<string,string>>{
    [""]=new Dictionary<string,string>{{"物料分类","非标件"},{"物料名称","70006186.00-10"},{"型号","70006186.00-10"},{"表面处理","烤漆RAL7035 橙纹(灰白色)"},{"备注",""}},
    ["A"]=new Dictionary<string,string>{{"物料名称","配置A名称"}},["B"]=new Dictionary<string,string>{{"物料名称","配置B名称"}}};
   if(mode=="empty")foreach(var values in all.Values)values.Clear();
   var plugin=New("PdmAddin");var appType=Type("PdmAddin").GetField("application",Flags).FieldType;var interop=appType.Assembly;
   object Manager(string configuration)=>new Proxy(interop.GetType("SolidWorks.Interop.sldworks.CustomPropertyManager",true),call=>{
    var values=all[configuration];if(call.MethodName=="GetNames")return values.Keys.ToArray();if(call.MethodName=="get_Count")return values.Count+(mode=="name-count"?1:0);
    if(call.MethodName=="Get6"){
     if(!(bool)call.Args[1])throw new Exception("禁止激活配置求值");reads++;var name=(string)call.Args[0];
     if(mode=="partial"&&name=="物料分类")throw new IOException("模拟读取失败");
     if((mode=="changed"||mode=="open-source-changes")&&reads==1)File.AppendAllText(path,"模拟其他进程修改");
     if(mode=="metadata-touch"&&reads==1)File.SetLastWriteTimeUtc(path,File.GetLastWriteTimeUtc(path).AddSeconds(2));
     return new ReturnMessage(mode=="missing-value"?1:mode=="cached-value"?0:2,new object[]{name,true,values[name],values[name],false,false},6,call.LogicalCallContext,call);
    }
    throw new Exception("禁止写属性："+call.MethodName);
   }).GetTransparentProxy();
   var extension=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDocExtension",true),call=>
    call.MethodName=="get_CustomPropertyManager"?Manager((string)call.Args[0]):throw new Exception("禁止改写图档："+call.MethodName)).GetTransparentProxy();
   var model=new AssemblyProxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true),call=>{
    if(call.MethodName=="GetPathName")return mode=="wrong-path"?Path.Combine(folder,"其他图档.SLDPRT"):path;
    if(call.MethodName=="GetSaveFlag")return mode=="dirty"||mode.StartsWith("open-");
    if(call.MethodName=="GetUpdateStamp")return mode=="open-stamp-changes"?reads:13;
    if(call.MethodName=="GetConfigurationNames")return mode=="missing-config"?new[]{"B"}:new[]{"A","B"};
    if(call.MethodName=="IsOpenedReadOnly")return opened;
    if(call.MethodName=="get_Extension")return extension;
    if(call.MethodName=="GetTitle")return Path.GetFileName(path);
    throw new Exception("禁止重建、保存、切换配置："+call.MethodName);
   }).GetTransparentProxy();
   SetField(plugin,"application",new AssemblyProxy(appType,call=>{
    calls.Add(call.MethodName);
    if(call.MethodName=="GetDocuments")return isLoaded||opened?new[]{model}:new object[0];
    if(call.MethodName=="GetOpenDocumentByName")return isLoaded||opened?model:null;
    if(call.MethodName=="GetDocumentVisible")return true;
    if(call.MethodName=="DocumentVisible")return null;
    if(call.MethodName=="OpenDoc6"){
     nativeOptions=(int)call.Args[2];opened=true;
     return new ReturnMessage(model,new object[]{call.Args[0],call.Args[1],call.Args[2],call.Args[3],0,0},6,call.LogicalCallContext,call);
    }
    if(call.MethodName=="CloseDoc"){closed++;return null;}
    throw new Exception("禁止改变其他图档："+call.MethodName);
   }).GetTransparentProxy());
   if(mode=="multi-config"&&kind!="Drawing"){
    var other=New("CadTreeNode");Set(other,"Configuration","B");var array=Array.CreateInstance(Type("CadTreeNode"),2);array.SetValue(node,0);array.SetValue(other,1);
    var index=(IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string),Type("CadTreeNode").MakeArrayType()));index.Add(path,array);SetField(plugin,"archiveCurrentInstances",index);
   }
   IDictionary properties=null;Exception failure=null;
   try{properties=(IDictionary)TreeCall(plugin,"ReadSavedNativeProperties",node,hash,null);}catch(Exception error){failure=error;}
   var shouldFail=mode=="partial"||mode=="missing-value"||mode=="name-count"||mode=="dirty"||mode=="open-stamp-changes"||mode=="open-source-changes"||mode=="wrong-path"||mode=="changed";
   if(shouldFail)Check(failure!=null&&(failure.ToString().Contains("源文件内容发生变化")||failure.ToString().Contains("自定义属性")||failure.ToString().Contains("尚未固定到提交副本的内存修改")),kind+"/"+mode+"：读取失败在上传前明确失败，不生成残缺属性索引");
   else{
    if(failure!=null)throw failure;
    Check((string)properties["UPLM.CustomPropertiesRead"]=="1",kind+"/"+mode+"：完成读取与空属性有明确证据");
    if(mode!="empty"){
     Check((string)properties["全局/物料分类"]=="非标件"&&(string)properties["全局/型号"]=="70006186.00-10"&&(string)properties["全局/表面处理"]==all[""]["表面处理"],kind+"/"+mode+"：分类、名称、型号及任意自定义属性完整进入索引");
     if(kind!="Drawing"&&mode!="missing-config")Check((string)properties["配置:A/物料名称"]=="配置A名称",kind+"/"+mode+"：保留引用配置属性，不用全局名称覆盖");
     if(mode=="multi-config"&&kind!="Drawing")Check((string)properties["配置:B/物料名称"]=="配置B名称",kind+"：同一文件所有引用配置均被采集");
    }else Check(!properties.Contains("全局/物料分类"),kind+"：确实无属性合法，不伪造分类或强制填写");
    Check(((IList)Get(node,"ReferenceWarnings")).Count==0,kind+"/"+mode+"：已读属性不再留待补齐提示");
    var beforeReads=reads;var beforeCalls=calls.Count;
    Set(node,"ArchiveInspectedProperties",properties);Set(node,"ArchiveInspectedPropertiesSha256",hash);
    TreeCall(plugin,"ReadSavedNativeProperties",node,hash,null);
    Check(reads==beforeReads&&calls.Count==beforeCalls,kind+"/"+mode+"：同一字节及配置的已读索引复用，避免预检和提交重复打开");
    if(kind=="Part"&&mode=="loaded"){
     Set(node,"Configuration","B");var changedConfig=(IDictionary)TreeCall(plugin,"ReadSavedNativeProperties",node,hash,null);
     Check((string)changedConfig["配置:B/物料名称"]=="配置B名称"&&reads>beforeReads,"新增引用配置不错误复用只包含配置A的索引");
     var serializable=properties.Keys.Cast<string>().ToDictionary(key=>key,key=>(string)properties[key]);
     var read=assembly.GetType("Upton.Pdm.Domain.CadPropertyCardSnapshot",true).GetMethod("Read");
     Check((string)read.Invoke(null,new object[]{serializable,"A","物料分类"})=="非标件"
      &&(string)read.Invoke(null,new object[]{serializable,"A","型号"})=="70006186.00-10"
      &&(string)read.Invoke(null,new object[]{serializable,"A","物料名称"})=="配置A名称","存档索引可被BOM同源属性读取器识别，配置优先、全局回退");
     File.WriteAllText(Path.Combine(Path.GetFullPath(output),"bom-properties.json"),new JavaScriptSerializer().Serialize(serializable));
    }
   }
   if(opened){
    Check((nativeOptions&3)==3,kind+"/"+mode+"：仅静默只读打开，不取得源文件写权限");
    if(kind=="Assembly")Check((nativeOptions&192)==192,"装配体属性读取使用轻化加载，不解析全部组件");
    if(kind=="Drawing")Check((nativeOptions&1024)==1024,"工程图属性读取使用出详图模式，不要求加载视图模型");
    Check(closed==1,kind+"/"+mode+"：仅关闭本次新增的临时属性窗口");
   }else Check(closed==0,kind+"/"+mode+"：已有用户窗口和同名冲突窗口均不关闭");
   if(mode!="changed"&&mode!="open-source-changes")Check(File.ReadAllBytes(path).SequenceEqual(original)&&File.GetAttributes(path)==attributes,kind+"/"+mode+"：源文件内容及只读状态不变");
   File.SetAttributes(path,FileAttributes.Normal);
  }
 }
}
