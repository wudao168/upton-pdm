using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static partial class Program
{
 static async Task ArchiveSourceRepeatScenario(Form host,string output)
 {
  void Check(bool ok,string text){if(!ok)throw new Exception(text);Console.WriteLine("PASS: "+text);}
  var folder=Path.GetFullPath(Path.Combine(output,"source-identity-"+Guid.NewGuid().ToString("N")));Directory.CreateDirectory(folder);
  var store=Type("PdmDocumentIdentityStore");var bindings=store.GetField("bindingDirectory",Flags);var workspace=store.GetField("workspaceRootResolver",Flags);
  var beforeBindings=bindings.GetValue(null);var beforeWorkspace=workspace.GetValue(null);
  bindings.SetValue(null,Path.Combine(folder,"bindings"));workspace.SetValue(null,(Func<string>)(()=>Path.Combine(folder,"managed")));
  try
  {
   var path=Path.Combine(folder,"SOURCE.SLDASM");File.WriteAllText(path,"unchanged native saved source");var bytes=File.ReadAllBytes(path);string hash;
   using(var sha=SHA256.Create())hash=BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","");
   var project=Guid.NewGuid();var otherProject=Guid.NewGuid();var document=Guid.NewGuid();var versionId=Guid.NewGuid();
   var saved=(bool)store.GetMethod("TryWriteControlledVersion",Flags).Invoke(null,new object[]{path,document,project,versionId,"W1",hash,(long)bytes.Length});
   Check(saved,"共享源路径的成功存档回执可写入本机缓存");
   object Node(string file){var node=New("CadTreeNode");Set(node,"FullPath",file);Set(node,"FileName",Path.GetFileName(file));Set(node,"Kind",Enum.Parse(Type("CadDocumentKind"),"Assembly"));Set(node,"Configuration","Default");Set(node,"DrawingVersion","V1");return node;}
   var node=Node(path);Set(node,"DocumentId",document);Set(node,"ProvenanceDocumentId",document);Set(node,"ProvenanceProjectId",project);Set(node,"StoredVersionStateKnown",true);Set(node,"HasStoredVersion",true);
   Type("PdmAddin").GetMethod("RestorePersistedDocumentIdentities",Flags).Invoke(null,new[]{node});
   Check(Get(node,"DocumentId")==null&&(string)Get(node,"DrawingVersion")=="V1","复现再次进入存档时仍显示V1、共享源身份恢复为外部来源的旧行为");
   var restore=Type("ArchiveSourceIdentity").GetMethod("RestoreForProject",Flags);
   restore.Invoke(null,new object[]{node,project});
   Check(Equals(Get(node,"DocumentId"),document)&&Equals(Get(node,"OpenedVersionId"),versionId),"同项目原路径按成功回执恢复已有图档及版本身份");
   var fresh=Node(path);restore.Invoke(null,new object[]{fresh,project});
   Check(Equals(Get(fresh,"DocumentId"),document),"重新生成的关联图档节点也可从本机成功回执恢复身份");
   var copy=Path.Combine(folder,"COPY.SLDASM");File.Copy(path,copy);var copied=Node(copy);Set(copied,"ProvenanceDocumentId",document);Set(copied,"ProvenanceProjectId",project);
   restore.Invoke(null,new object[]{copied,project});Check(Get(copied,"DocumentId")==null,"复制到另一条路径的图档不会凭文件名或来源ID冒用旧身份");
   var pendingPath=Path.Combine(folder,"PENDING.SLDASM");File.WriteAllText(pendingPath,"not archived");store.GetMethod("TryWrite",Flags).Invoke(null,new object[]{pendingPath,Guid.NewGuid(),project});var pending=Node(pendingPath);
   restore.Invoke(null,new object[]{pending,project});Check(Get(pending,"DocumentId")==null,"只有登记、没有版本的失败批次仍按首次存档处理");
   var version=New("DocumentVersionDto");Set(version,"Id",versionId);Set(version,"Sha256",hash);Set(version,"FileLength",(long)bytes.Length);Set(version,"Revision",New("RevisionDto"));Set(Get(version,"Revision"),"Display","W1");
   var versionType=Type("DocumentVersionDto");var versions=Array.CreateInstance(versionType,1);versions.SetValue(version,0);
   Check(Type("PdmAddin").GetMethod("AnalyzeArchiveNode",Flags).Invoke(null,new object[]{node,"tester",versions,hash}).ToString()=="Unchanged","已有同内容模型分类为未变更");
   File.AppendAllText(path,"saved geometry change");string changedHash;using(var sha=SHA256.Create())changedHash=BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-","");
   restore.Invoke(null,new object[]{node,project});
   Check(Equals(Get(node,"DocumentId"),document)&&Type("PdmAddin").GetMethod("AnalyzeArchiveNode",Flags).Invoke(null,new object[]{node,"tester",versions,changedHash}).ToString()=="Changed","原路径真实修改沿用旧身份并识别变更，不误判为新增");
   File.WriteAllBytes(path,bytes);
   var embedded=Node(path);Set(embedded,"Status",Enum.Parse(Type("CadReferenceStatus"),"Virtual"));restore.Invoke(null,new object[]{embedded,project});
   Check(Get(embedded,"DocumentId")==null,"嵌入式组件继续随父装配保存，不独立恢复登记");
   var projects=Array.CreateInstance(Type("ProjectDto"),2);
   for(var index=0;index<2;index++){var item=New("ProjectDto");Set(item,"Id",index==0?project:otherProject);Set(item,"Code","TEST-"+(index+1));Set(item,"Name","测试项目");Set(item,"CanSubmitArchive",true);projects.SetValue(item,index);}
   var operation=New("BatchOperationItem",node,0);var items=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(Type("BatchOperationItem")));items.Add(operation);
   // Repeat the exact pre-dialog reset, then exercise the actual selection analysis.
   Type("PdmAddin").GetMethod("RestorePersistedDocumentIdentities",Flags).Invoke(null,new[]{node});
   using(var dialog=(Form)New("BatchOperationDialog",node,items,projects,project,"tester",Enum.Parse(Type("BatchOperationKind"),"CheckIn"),null,null,null,false,false,0))
   {
    var loader=dialog.GetType().GetProperty("LoadArchiveVersionsAsync",Flags);var delegateType=loader.PropertyType;var invoke=delegateType.GetMethod("Invoke");var listType=typeof(IReadOnlyList<>).MakeGenericType(versionType);
    var task=typeof(Task).GetMethod("FromResult").MakeGenericMethod(listType).Invoke(null,new object[]{versions});
    loader.SetValue(dialog,Expression.Lambda(delegateType,Expression.Constant(task,invoke.ReturnType),invoke.GetParameters().Select(parameter=>Expression.Parameter(parameter.ParameterType))).Compile());
    dialog.Opacity=0;dialog.ShowInTaskbar=false;dialog.Show(host);
    async Task WaitAnalysis(){var deadline=DateTime.UtcNow.AddSeconds(5);do{await Task.Delay(15);if(!(bool)Field(dialog,"archiveAnalysisInProgress"))return;}while(DateTime.UtcNow<deadline);throw new Exception("存档选择分析未结束");}
    await WaitAnalysis();
    Check(((IEnumerable)Get(dialog,"SelectedItems")).Cast<object>().Count()==0&&!((Button)Field(dialog,"execute")).Enabled,"成功后立即再次存档：未改动图档不自动勾选，确认存档禁用");
    Check(((IEnumerable)TreeCall(dialog,"GetNewDocumentConfirmationPaths")).Cast<object>().Count()==0,"已有图档不再触发新增项目归属确认");
    var selector=Field(dialog,"projectSelector");
    TreeCall(selector,"SelectProject",otherProject);((EventHandler)Field(selector,"SelectedProjectChanged"))?.Invoke(selector,EventArgs.Empty);await WaitAnalysis();
    Check(Get(node,"DocumentId")==null&&((IEnumerable)Get(dialog,"SelectedItems")).Cast<object>().Count()==1,"切换到另一项目时不复用原项目身份，仍保留独立登记语义");
    Check(((IEnumerable)TreeCall(dialog,"GetNewDocumentConfirmationPaths")).Cast<object>().Count()==1,"另一项目仍需要新增归属确认");
    TreeCall(selector,"SelectProject",project);((EventHandler)Field(selector,"SelectedProjectChanged"))?.Invoke(selector,EventArgs.Empty);await WaitAnalysis();
    Check(Equals(Get(node,"DocumentId"),document)&&((IEnumerable)Get(dialog,"SelectedItems")).Cast<object>().Count()==0,"切回原项目后重新核对为未变更，不重复存档");
    dialog.Close();
   }
   Check(File.ReadAllBytes(path).SequenceEqual(bytes),"身份恢复和选择分析不改写共享源文件");
  }
  finally{bindings.SetValue(null,beforeBindings);workspace.SetValue(null,beforeWorkspace);}
 }
}
