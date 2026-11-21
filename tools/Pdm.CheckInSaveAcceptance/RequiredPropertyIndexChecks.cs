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

  ExactPropertyReaderScenario(output);
  SavedPropertySourceLeaseScenario(output);
 }

 static void ExactPropertyReaderScenario(string output)
 {
  foreach (var mode in new[] { "exact", "interactive-same-name", "owned-same-name", "stale-hash", "occupied-reader",
   "worker-distinct-view", "worker-source-changes" })
  {
   var folder = Path.Combine(Path.GetFullPath(output), "exact-reader", mode);
   Directory.CreateDirectory(folder);
   var node = LinkedNode(folder, "38261080290装配.SLDASM", "Assembly");
   var path = (string)Get(node, "FullPath");
   var foreignDirectory = Path.Combine(folder, "另一目录");
   Directory.CreateDirectory(foreignDirectory);
   var foreignPath = Path.Combine(foreignDirectory, Path.GetFileName(path));
   File.WriteAllText(foreignPath, "different saved assembly");
   var sourceBytes = File.ReadAllBytes(path);
   var sourceAttributes = File.GetAttributes(path);
   var foreignBytes = File.ReadAllBytes(foreignPath);
   var foreignAttributes = File.GetAttributes(foreignPath);
   var nativeHash = WorkingReuseHash(path);
   var workerMode = mode.StartsWith("worker-", StringComparison.Ordinal);
   var hash = mode == "stale-hash" || workerMode ? new string('0', 64) : nativeHash;

   var sameNameConflict = mode.EndsWith("same-name", StringComparison.Ordinal);
   var interactive = mode == "interactive-same-name";
   var plugin = New("PdmAddin");
   var addinType = Type("PdmAddin");
   var appType = addinType.GetField("application", Flags).FieldType;
   var interop = appType.Assembly;
   var opens = 0;
   var closes = 0;
   var reads = 0;
   var foreignModelReads = 0;
   var visible = true;
   var opened = false;
   var commandInProgress = false;
   var workingDirectory = string.Empty;
   var configurations = new[] { "默认", "维修" };
   var values = new Dictionary<string, string>
   {
    [""] = "全局值",
    [configurations[0]] = "默认配置值",
    [configurations[1]] = "维修配置值"
   };

   object Manager(string configuration) => new AssemblyProxy(
    interop.GetType("SolidWorks.Interop.sldworks.CustomPropertyManager", true), call =>
    {
     if (call.MethodName == "GetNames") return new[] { "物料名称" };
     if (call.MethodName == "get_Count") return 1;
     if (call.MethodName != "Get6" || !(bool)call.Args[1])
      throw new InvalidOperationException("只允许读取已保存的属性值。");
     reads++;
     if (mode == "worker-source-changes" && reads == 1)
     {
      var timestamp = File.GetLastWriteTimeUtc(path);
      var changed = File.ReadAllBytes(path);
      changed[0] ^= 1;
      File.WriteAllBytes(path, changed);
      File.SetLastWriteTimeUtc(path, timestamp);
     }
     var value = values[configuration];
     return new ReturnMessage(2, new object[] { "物料名称", true, value, value, true, false },
      6, call.LogicalCallContext, call);
    }).GetTransparentProxy();
   var extension = new AssemblyProxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDocExtension", true), call =>
    call.MethodName == "get_CustomPropertyManager" ? Manager((string)call.Args[0])
     : throw new InvalidOperationException("禁止修改装配体属性。")).GetTransparentProxy();
   var model = new AssemblyProxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2", true), call =>
   {
    if (call.MethodName == "GetPathName") return sameNameConflict ? foreignPath : path;
    if (sameNameConflict)
    {
     foreignModelReads++;
     throw new InvalidOperationException("禁止处理另一目录的同名装配体。");
    }
    switch (call.MethodName)
    {
     case "GetSaveFlag": return false;
     case "GetTitle": return Path.GetFileName(path);
     case "GetConfigurationNames": return configurations;
     case "IsOpenedReadOnly": return true;
     case "get_Extension": return extension;
     default: throw new InvalidOperationException("禁止修改装配体、切换配置或重建。");
    }
   }).GetTransparentProxy();
   var app = new AssemblyProxy(appType, call =>
   {
    switch (call.MethodName)
    {
     case "GetProcessID": return 32198;
     case "set_UserControlBackground": return null;
     case "get_CommandInProgress": return commandInProgress;
     case "set_CommandInProgress": commandInProgress = (bool)call.Args[0]; return null;
     case "GetDocuments": return interactive || opened ? new[] { model } : Array.Empty<object>();
     case "GetOpenDocumentByName": return opened ? model : null;
     case "GetDocumentCount": return opened || mode == "occupied-reader" ? 1 : 0;
     case "GetDocumentVisible": return visible;
     case "DocumentVisible": visible = (bool)call.Args[0]; return null;
     case "SetCurrentWorkingDirectory": workingDirectory = (string)call.Args[0]; return true;
     case "OpenDoc6":
      opens++;
      if (!string.Equals((string)call.Args[0], path, StringComparison.OrdinalIgnoreCase) || ((int)call.Args[2] & 3) != 3)
       throw new InvalidOperationException("只能按预期的完整路径静默只读打开装配体。");
      opened = true;
      return new ReturnMessage(model, new[] { call.Args[0], call.Args[1], call.Args[2], call.Args[3], (object)0, 0 },
       6, call.LogicalCallContext, call);
     case "CloseDoc":
      closes++;
      opened = false;
      return null;
     default: throw new InvalidOperationException("禁止操作其他图档。");
    }
   }).GetTransparentProxy();
   SetField(plugin, "application", app);
   var reader = addinType.GetMethod("ReadNativeArchiveSavedProperties", Flags)
    ?? throw new MissingMethodException("未找到指定会话的自定义属性读取入口。");
   IDictionary properties = null;
   Dictionary<string, object> saved = null;
   Exception failure = null;
   try
   {
    if (workerMode)
    {
     saved = ReadSavedPropertyWorkerFixture(app, path, Convert.ToInt32(Get(node, "Kind")), configurations, hash);
     properties = (IDictionary)saved["Properties"];
    }
    else properties = interactive ? (IDictionary)TreeCall(plugin, "ReadSavedNativeProperties", node, hash, null)
     : (IDictionary)reader.Invoke(reader.IsStatic ? null : plugin,
      new object[] { app, path, Convert.ToInt32(Get(node, "Kind")), configurations, hash });
   }
   catch (Exception error) { failure = error; }

   if (mode == "exact" || mode == "worker-distinct-view")
   {
    if (failure != null) throw new InvalidOperationException("独立会话未能读取指定装配体完整属性。", failure);
    if (properties == null || (string)properties["全局/物料名称"] != values[""]
     || (string)properties["配置:默认/物料名称"] != values["默认"]
     || (string)properties["配置:维修/物料名称"] != values["维修"]
     || reads != 3 || opens != 1 || closes != 1 || !visible || opened || commandInProgress || workingDirectory != folder)
     throw new InvalidOperationException("独立会话未按完整路径读取全局及指定配置属性，或临时窗口未释放。");
    if (workerMode && (!string.Equals((string)saved["Source"], path, StringComparison.OrdinalIgnoreCase)
     || (string)saved["SourceSha256"] != hash
     || !string.Equals((string)saved["ReaderSourceSha256"], nativeHash, StringComparison.OrdinalIgnoreCase)))
     throw new InvalidOperationException("独立读取应保留调用方校验值并独立返回读取进程的校验值。");
   }
   else if (mode == "worker-source-changes")
   {
    if (!(failure?.GetBaseException() is IOException) || !failure.GetBaseException().Message.Contains("变化")
     || properties != null || opens != 1 || closes != 1 || reads != 3 || opened || commandInProgress)
     throw new InvalidOperationException("独立进程必须阻止读取期间发生的同大小同时间图档内容变化。", failure);
    File.WriteAllBytes(path, sourceBytes);
   }
   else if (sameNameConflict)
   {
    var conflict = failure;
    while (conflict != null && conflict.GetType().Name != "ArchiveSavedPropertyPathConflictException")
     conflict = conflict.InnerException;
    if (conflict == null || !conflict.Message.Contains(path) || !conflict.Message.Contains(foreignPath)
     || properties != null || opens != 1 || reads != 0 || foreignModelReads != 0 || closes != 0 || !visible)
     throw new InvalidOperationException("同名装配体的路径冲突未准确报告，或错误窗口被读取或关闭。", failure);
   }
   else if (failure == null || properties != null || opens != 0 || closes != 0 || reads != 0)
    throw new InvalidOperationException("源文件指纹过期或独立会话已有图档时应在打开前停止。", failure);

   if (!sourceBytes.SequenceEqual(File.ReadAllBytes(path)) || !foreignBytes.SequenceEqual(File.ReadAllBytes(foreignPath))
    || File.GetAttributes(path) != sourceAttributes || File.GetAttributes(foreignPath) != foreignAttributes)
    throw new InvalidOperationException("源文件或同名文件的内容及属性发生变化。");
   Console.WriteLine("PASS: exact archive property reader " + mode);
  }
 }

 static Dictionary<string, object> ReadSavedPropertyWorkerFixture(object app, string path, int kind,
  string[] configurations, string callerHash)
 {
  var workspace = Type("ArchiveNativeProcessWorkspace");
  var fieldNames = new[] { "activeReader", "activeJob", "activeReaderId" };
  var fields = fieldNames.Select(name => workspace.GetField(name, Flags)).ToArray();
  var previous = fields.Select(field => field.GetValue(null)).ToArray();
  var jobId = Guid.NewGuid().ToString("N");
  var job = Path.Combine(Path.GetTempPath(), "UPTON-PDM", "native-isolation", "jobs", jobId);
  var json = new JavaScriptSerializer();
  Directory.CreateDirectory(job);
  try
  {
   fields[0].SetValue(null, app);
   fields[1].SetValue(null, job);
   fields[2].SetValue(null, 32198);
   File.WriteAllText(Path.Combine(job, "native-isolation-request.json"), json.Serialize(new
   {
    JobId = jobId,
    CallerSolidWorksId = 32197,
    SavedPropertyRead = new { Source = path, DocumentKind = kind, Configurations = configurations, SourceSha256 = callerHash }
   }));
   var arguments = new object[] { app, job, null };
   if (!(bool)workspace.GetMethod("TryReadSavedProperties", Flags).Invoke(null, arguments))
    throw new InvalidOperationException("独立属性工作进程未接受测试任务。");
   var response = json.Deserialize<Dictionary<string, object>>((string)arguments[2]);
   if (!(bool)response["Success"] || (string)response["JobId"] != jobId)
    throw new InvalidOperationException("独立属性读取结果与本次测试任务不一致。");
   return (Dictionary<string, object>)response["SavedProperties"];
  }
  finally
  {
   for (var index = 0; index < fields.Length; index++) fields[index].SetValue(null, previous[index]);
   foreach (var name in new[] { "native-isolation-request.json", "isolation-progress.json", "native-diagnostics.txt" })
    File.Delete(Path.Combine(job, name));
   Directory.Delete(job);
  }
 }

 static void SavedPropertySourceLeaseScenario(string output)
 {
  var path = Path.Combine(Path.GetFullPath(output), Guid.NewGuid().ToString("N") + ".SLDASM");
  File.WriteAllText(path, "readable source fixture");
  try
  {
   var protect = Type("ArchiveNativeProcessWorkspace").GetMethod("ProtectSavedPropertySource", Flags);
   using (var lease = (FileStream)protect.Invoke(null, new object[] { path }))
   {
    var readable = File.ReadAllText(path) == "readable source fixture";
    var deniedWrite = false;
    try { using (File.Open(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { } }
    catch (IOException) { deniedWrite = true; }
    var deniedDelete = false;
    try { File.Delete(path); }
    catch (IOException) { deniedDelete = true; }
    if (!readable || !deniedWrite || !deniedDelete)
     throw new InvalidOperationException("源文件保护应允许读取，并在读取期间拒绝写入和删除测试文件。");
   }
   File.WriteAllText(path, "writable after lease");
   Console.WriteLine("PASS: saved archive property source lease allows reads and protects against writes and deletion until disposed");
  }
  finally { File.Delete(path); }
 }
}
