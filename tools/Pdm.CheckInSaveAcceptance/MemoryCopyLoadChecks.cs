using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

internal static partial class Program
{
 static async Task MemoryCopyLoadScenario(string output)
 {
  void Check(bool ok,string message){if(!ok)throw new Exception(message);Console.WriteLine("PASS: "+message);}
  foreach(var mode in new[]{"exact-lookup","orphan-import","multiple-imports","normal-post","no-load","preexisting-load","active-command","startup-loading","ui-command","new-load-in-query","new-load-in-cwd","query-error","copy-error","changed-path","disconnecting"})
  {
   var folder=Path.Combine(Path.GetFullPath(output),mode);Directory.CreateDirectory(folder);
   var node=LinkedNode(folder,"总装.SLDASM","Assembly");var path=(string)Get(node,"FullPath");
   var bytes=File.ReadAllBytes(path);File.SetAttributes(path,FileAttributes.ReadOnly);
   var plugin=New("PdmAddin");var appType=Type("PdmAddin").GetField("application",Flags).FieldType;
   var modelType=appType.Assembly.GetType("SolidWorks.Interop.sldworks.IModelDoc2",true);
   var events=new List<string>();var model=new Model(modelType,path,true,events);var saved=false;var cwdCalls=0;
   Action opening=()=>TreeCall(plugin,"OnFileOpening",Path.Combine(folder,"临时导入.stp.SLDASM"));
   SetField(plugin,"batchDialogInProgress",1);
   SetField(plugin,"application",new Proxy(appType,m=>{
    if(m.MethodName=="GetDocuments")return mode=="exact-lookup"?new object[0]:new[]{model.GetTransparentProxy()};
    if(m.MethodName=="GetOpenDocumentByName"&&mode=="exact-lookup"&&Equals(m.Args[0],path))return model.GetTransparentProxy();
    if(m.MethodName=="GetCurrentWorkingDirectory"){
     if(saved&&++cwdCalls==1&&mode=="new-load-in-cwd")opening();return folder;
    }
    if(m.MethodName=="GetRunningCommandInfo"){
     if(mode=="query-error")throw new COMException("fixture RPC failure");
     if(mode=="new-load-in-query")opening();
     return new System.Runtime.Remoting.Messaging.ReturnMessage(null,new object[]{-3,null,mode=="active-command"},3,m.LogicalCallContext,m);
    }
    if(m.MethodName=="get_StartupProcessCompleted")return mode!="startup-loading";
    throw new Exception("Unexpected native call: "+m.MethodName);
   }).GetTransparentProxy());
   SetField(plugin,"currentTree",node);
   model.OnSave=()=>{
    saved=true;
    if(mode!="no-load")opening();
    if(mode=="multiple-imports"){opening();opening();}
    if(mode=="normal-post")TreeCall(plugin,"OnFileOpened",path);
    if(mode=="ui-command")SetField(plugin,"nativeUiCommandId",42);
    if(mode=="disconnecting")SetField(plugin,"disconnecting",true);
    if(mode=="changed-path")model.Path=Path.Combine(folder,"另一个图档.SLDASM");
    if(mode=="copy-error")throw new IOException("fixture native copy failure after load notification");
   };
   if(mode=="preexisting-load")opening();
   var shouldPass=new[]{"exact-lookup","orphan-import","multiple-imports","normal-post","no-load"}.Contains(mode);
   Exception failure=null;
   try
   {
    try{await (Task)TreeCall(plugin,"EnsureBatchCheckInSavedStateAsync",LinkedItems(node),Guid.NewGuid(),CancellationToken.None);}catch(Exception e){failure=e;}
    var loading=(int)Field(plugin,"nativeFileOpenInProgress");
    if(shouldPass){
     Check(failure==null&&loading==0,mode+"：成功副本的内部加载通知不阻塞后续存档");
     Check(!(bool)TreeCall(plugin,"IsSolidWorksCommandActive"),mode+"：存档命令检查可以继续，无需重启或额外保存");
     Check(TreeCall(plugin,"MemoryCopy",node)!=null,mode+"：当前状态副本仍被保留供原提交流程使用");
    }else{
     Check(failure!=null&&loading==1,mode+"：无法确认完成时保留加载保护，不误放行");
     Check((bool)TreeCall(plugin,"IsSolidWorksCommandActive"),mode+"：其他原生操作仍被原有命令检查保护");
     if(mode=="preexisting-load")Check(!saved&&events.Count==0,"已有文件加载时不启动新的副本保存");
     else if(mode!="copy-error")Check(failure.Message.Contains("加载状态尚未确认"),mode+"：明确结束本次提交而非无限等待");
    }
    Check(File.ReadAllBytes(path).SequenceEqual(bytes)&&File.GetAttributes(path)==FileAttributes.ReadOnly,mode+"：源文件内容和只读权限保持不变");
    Check((int)Field(plugin,"refreshSuppressionDepth")==0&&model.SelectionCount==2&&model.Dirty,mode+"：恢复后台抑制、选择和原待保存状态");
   }
   finally{
    TreeCall(plugin,"PruneArchiveMemoryCopies",true);File.SetAttributes(path,FileAttributes.Normal);
   }
  }
 }
}
