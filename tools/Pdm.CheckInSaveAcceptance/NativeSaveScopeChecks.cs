using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

internal static partial class Program
{
 static async Task NativeSaveScopeScenario(string output)
 {
  output=Path.GetFullPath(output);Directory.CreateDirectory(output);
  void Check(bool ok,string text){if(!ok)throw new Exception(text);Console.WriteLine("PASS: "+text);}
  foreach(var mode in new[]{"readonly-dirty","writable-dirty","clean-readonly","copy-failure"})
  {
   var folder=Path.Combine(output,mode);Directory.CreateDirectory(folder);
   var node=LinkedNode(folder,"总装.SLDASM","Assembly");var path=(string)Get(node,"FullPath");var original=File.ReadAllBytes(path);var originalHash=WorkingReuseHash(path);
   var plugin=New("PdmAddin");var appType=Type("PdmAddin").GetField("application",Flags).FieldType;var modelType=appType.Assembly.GetType("SolidWorks.Interop.sldworks.IModelDoc2",true);var events=new List<string>();
   var model=new Model(modelType,path,mode!="clean-readonly",events,mode!="writable-dirty"){MemoryPayload="current model and embedded content",CopyError=mode=="copy-failure"?1:0};
   var otherPath=Path.Combine(folder,"未选图档.SLDPRT");File.WriteAllText(otherPath,"other saved bytes");var other=new Model(modelType,otherPath,true,events);
   var cwd=folder;model.OnSave=()=>cwd=Path.Combine(folder,"copy-directory");
   SetField(plugin,"application",new Proxy(appType,m=>{
    if(m.MethodName=="GetDocuments")return new[]{model.GetTransparentProxy(),other.GetTransparentProxy()};
    if(m.MethodName=="GetCurrentWorkingDirectory")return cwd;
    if(m.MethodName=="SetCurrentWorkingDirectory"){cwd=(string)m.Args[0];return true;}
    throw new Exception("不得打开或修改其他图档："+m.MethodName);
   }).GetTransparentProxy());SetField(plugin,"currentTree",node);
   if(mode!="writable-dirty")File.SetAttributes(path,FileAttributes.ReadOnly);
   var attributes=File.GetAttributes(path);
   using(var client=(IDisposable)New("PdmApiClient","http://fixture.invalid/"))
   using(var handler=new ReferenceRecoveryHandler(request=>new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("[]")}))
   using(var http=new HttpClient(handler){BaseAddress=new Uri("http://fixture.invalid/")})
   {
    SetField(client,"httpClient",http);SetField(plugin,"apiClient",client);
    try
    {
     Exception failure=null;
     try{await (Task)TreeCall(plugin,"EnsureBatchCheckInSavedStateAsync",LinkedItems(node),Guid.NewGuid(),CancellationToken.None);}catch(Exception error){failure=error;}
     if(mode=="copy-failure")
     {
      Check(failure is InvalidOperationException&&failure.Message.Contains("总装.SLDASM")&&failure.Message.Contains("提交副本"),"副本生成失败在上传前明确报告具体图档");
      Check((string)Type("PdmAddin").GetMethod("ArchivePreflightError",Flags).Invoke(null,new[]{failure})==failure.Message,"原生副本错误不被简化成文件被占用");
     }
     else if(mode=="clean-readonly")Check(events.Count==0&&((IDictionary)Field(plugin,"archiveMemoryCopies")).Count==0,"已保存只读文件不保存、不生成多余内存副本");
     else
     {
      if(failure!=null)throw failure;
      var snapshot=TreeCall(plugin,"MemoryCopy",node);var copy=(string)Field(snapshot,"CopyPath");
      Check(File.ReadAllText(copy)==model.MemoryPayload&&Path.GetFileName(copy)==Path.GetFileName(path),mode+"：同名提交副本来自当前内存内容");
      Check(model.Dirty&&events.SequenceEqual(new[]{"copy:总装.SLDASM"}),mode+"：不执行原文件保存或解除只读，原待保存标记保留");
      var preparedTask=(Task)TreeCall(plugin,"PrepareBatchCheckInFileAsync",node,CancellationToken.None,null,null,null,null);await preparedTask;var prepared=Get(preparedTask,"Result");
      Check((bool)Get(prepared,"MemorySnapshot")&&(string)Get(prepared,"SourceHash")==originalHash&&(string)Get(prepared,"UploadCopySha256")==WorkingReuseHash(copy),"原文件校验与当前内容副本校验分开，不将旧源文件当作新版本");
      TreeCall(plugin,"ValidateBatchSavedState",LinkedItems(node),new Dictionary<string,bool>{{path,true}},false);
      Check(true,"原模型保留待保存标记时，已核对的内存副本仍可继续提交");
      Check(model.SelectionCount==2&&cwd==folder,"生成完整模型副本后恢复原选择列表和工作目录");
      model.Hide();TreeCall(plugin,"ValidateCurrentMemoryCopy",node);
      Check(true,"仅隐藏组件窗口不使仍在总装内的模型副本失效");
      var count=events.Count;await (Task)TreeCall(plugin,"EnsureBatchCheckInSavedStateAsync",LinkedItems(node),Guid.NewGuid(),CancellationToken.None);
      Check(events.Count==count&&(string)Field(TreeCall(plugin,"MemoryCopy",node),"CopyPath")==copy,"同一打开会话中未再修改时复用副本，避免重复保存");
      var receipt=New("DocumentVersionDto");var versionId=Guid.NewGuid();Set(receipt,"Id",versionId);Set(receipt,"Sha256",WorkingReuseHash(copy));Set(receipt,"Revision",LinkedRevision("V2"));
      SetField(snapshot,"CommittedVersion",receipt);SetField(snapshot,"CommittedDocumentId",Get(node,"DocumentId"));SetField(snapshot,"CommittedUser",Field(plugin,"authenticatedUsername"));SetField(snapshot,"CommittedScope",Get(client,"EditJournalScope"));
      var nodeArray=Array.CreateInstance(Type("CadTreeNode"),1);nodeArray.SetValue(node,0);var diskHashes=new Dictionary<string,string>{{path,originalHash}};
      Set(node,"IsModifiedInSolidWorks",true);
      var effective=(IReadOnlyDictionary<string,string>)TreeCall(plugin,"ApplyCommittedMemoryState",nodeArray,diskHashes);
      Check(effective[path]==WorkingReuseHash(copy)&&diskHashes[path]==originalHash&&!(bool)Get(node,"IsModifiedInSolidWorks")&&(Guid?)Get(node,"OpenedVersionId")==versionId,"当前已提交内存状态可复用回执，未把旧磁盘哈希改成新版本");
      model.MemoryPayload="changed geometry";model.Modify();
      var rejected=false;try{TreeCall(plugin,"ValidateCurrentMemoryCopy",node);}catch(System.Reflection.TargetInvocationException e)when(e.InnerException is InvalidOperationException){rejected=true;}
      Check(rejected,"生成副本后的几何修改使旧副本失效");
      await (Task)TreeCall(plugin,"EnsureBatchCheckInSavedStateAsync",LinkedItems(node),Guid.NewGuid(),CancellationToken.None);
      snapshot=TreeCall(plugin,"MemoryCopy",node);copy=(string)Field(snapshot,"CopyPath");var stamp=model.UpdateStamp;
      Set(node,"IsModifiedInSolidWorks",true);
      effective=(IReadOnlyDictionary<string,string>)TreeCall(plugin,"ApplyCommittedMemoryState",nodeArray,diskHashes);
      Check(effective[path]==(string)Get(receipt,"Sha256")&&(bool)Get(node,"IsModifiedInSolidWorks"),"新副本保持待提交，并沿用当前会话已提交基准，不冒充已存档");
      model.MemoryPayload="changed property only";model.Modify(false);
      rejected=false;try{TreeCall(plugin,"ValidateCurrentMemoryCopy",node);}catch(System.Reflection.TargetInvocationException e)when(e.InnerException is InvalidOperationException){rejected=true;}
      Check(rejected&&model.UpdateStamp==stamp,"不改变更新戳的属性修改也能使旧副本失效");
      await (Task)TreeCall(plugin,"EnsureBatchCheckInSavedStateAsync",LinkedItems(node),Guid.NewGuid(),CancellationToken.None);
      snapshot=TreeCall(plugin,"MemoryCopy",node);copy=(string)Field(snapshot,"CopyPath");
      var locked=false;try{File.Delete(copy);}catch(IOException){locked=true;}
      Check(locked&&File.Exists(copy),"当前会话使用中的提交副本不会被临时清理误删");
      File.SetAttributes(path,FileAttributes.Normal);File.AppendAllText(path," external source change");
      rejected=false;try{await (Task)TreeCall(plugin,"EnsureBatchCheckInSavedStateAsync",LinkedItems(node),Guid.NewGuid(),CancellationToken.None);}catch(InvalidOperationException){rejected=true;}
      Check(rejected,"同一存档期间源文件变化会停止，不悄悄替换已确认内容");File.WriteAllBytes(path,original);File.SetAttributes(path,attributes);
      model.Reload();TreeCall(plugin,"PruneArchiveMemoryCopies",false);
      Check(File.Exists(copy)&&File.Exists(Path.Combine(Path.GetDirectoryName(copy),"memory-source.json"))&&((IDictionary)Field(plugin,"archiveMemoryCopies")).Count==0,"重新加载时不沿用旧会话证明；未提交副本保留恢复记录");
      Type("PdmAddin").GetMethod("DeleteCheckInUploadCopy",Flags).Invoke(null,new object[]{copy});
      await (Task)TreeCall(plugin,"EnsureBatchCheckInSavedStateAsync",LinkedItems(node),Guid.NewGuid(),CancellationToken.None);
      snapshot=TreeCall(plugin,"MemoryCopy",node);copy=(string)Field(snapshot,"CopyPath");
      var committed=New("DocumentVersionDto");Set(committed,"Id",Guid.NewGuid());Set(committed,"Sha256",WorkingReuseHash(copy));SetField(snapshot,"CommittedVersion",committed);
      model.Destroy();TreeCall(plugin,"PruneArchiveMemoryCopies",false);
      Check(!File.Exists(copy),"实际关闭模型后清理已提交副本，不无限累积缓存");
     }
     Check(model.SelectionCount==2&&cwd==folder,"失败或完成均恢复选择范围和工作目录："+mode);
     Check(original.SequenceEqual(File.ReadAllBytes(path))&&File.GetAttributes(path)==attributes,"原文件字节和权限不变："+mode);
     Check(other.Dirty&&File.ReadAllText(otherPath)=="other saved bytes"&&!events.Any(e=>e.Contains("未选图档")),"未选图档不保存、不改变内存状态："+mode);
    }
    finally{TreeCall(plugin,"PruneArchiveMemoryCopies",true);File.SetAttributes(path,FileAttributes.Normal);}
   }
  }
 }
}
