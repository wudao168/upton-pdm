using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Web.Script.Serialization;

internal static partial class Program
{
 static void ContextStateTransitionScenario(string output,string statePath,string proofPath)
 {
  var json=new JavaScriptSerializer{MaxJsonLength=int.MaxValue};
  var state=(Dictionary<string,object>)json.DeserializeObject(File.ReadAllText(statePath));
  var rows=((object[])((Dictionary<string,object>)((object[])state["Roots"])[0])["Nodes"]).Cast<Dictionary<string,object>>().ToArray();
  var actual=rows.Single(row=>Path.GetFileName((string)row["Path"])=="70005399.F1.01.SLDASM"&&(bool)row["Context"]&&(bool)row["Unverified"]);
  var ownerRow=rows.Single(row=>((object[])row["Children"]).Contains(actual["Address"]));
  var proof=(Dictionary<string,object>)json.DeserializeObject(File.ReadAllText(proofPath));
  void Check(bool ok,string text){if(!ok)throw new Exception(text);Console.WriteLine("PASS: "+text);}
  Check((string)proof["File"]==(string)ownerRow["Path"]&&(string)proof["Context"]==(string)actual["Path"]&&(int)proof["Status"]==4,"现场双标记记录与父装配沉头孔上下文证据对应");
  var apply=Type("ArchiveContextOwnership").GetMethod("Apply",Flags);var capture=Type("PdmApiClient").GetMethod("CaptureArchiveSnapshot",Flags);
  var snapshotType=Type("PdmApiClient+ArchiveReferenceSnapshot");var validate=Type("FrozenArchiveSubmissionGraph").GetMethod("Validate",Flags);
  foreach(var mode in new[]{"actual","source-alias","repeat","direct-owner","ambiguous-owner","no-proof","malformed-children","malformed-virtual"})
  {
   var folder=Path.Combine(Path.GetFullPath(output),mode);Directory.CreateDirectory(folder);
   var parent=LinkedNode(folder,Path.GetFileName((string)actual["Path"]),"Assembly");var owner=LinkedNode(folder,Path.GetFileName((string)ownerRow["Path"]),"Part");
   foreach(var n in new[]{parent,owner}){Set(n,"DocumentId",(Guid?)Guid.NewGuid());Set(n,"Revision","W1");Set(n,"CurrentRevision","W1");Set(n,"NativeFileScopeVerified",true);}
   Set(owner,"ComponentSelectionName",ownerRow["Selection"]);Set(owner,"InstancePath",ownerRow["Instance"]);
   var parentPath=(string)Get(parent,"FullPath");var partPath=(string)Get(owner,"FullPath");
   var link=New("CadTreeNode");Set(link,"FullPath",parentPath);Set(link,"SourceReferencePath",parentPath);Set(link,"FileName",Get(parent,"FileName"));Set(link,"Kind",Enum.Parse(Type("CadDocumentKind"),"Assembly"));Set(link,"Role",Enum.Parse(Type("CadReferenceRole"),"ExternalFileDependency"));
   Set(link,"DocumentId",Get(parent,"DocumentId"));Set(link,"CurrentRevision","W1");Set(link,"InstancePath",actual["Instance"]);Set(link,"IsContextReference",true);Set(link,"ReferenceTypeUnverified",true);
   ((IList)Get(parent,"Children")).Add(owner);((IList)Get(owner,"Children")).Add(link);
   var scope=new HashSet<string>(StringComparer.OrdinalIgnoreCase){parentPath,partPath};foreach(var n in new[]{parent,owner,link})Set(n,"NativeFileScopePaths",scope);
   var before=new[]{parentPath,partPath}.ToDictionary(path=>path,WorkingReuseHash);
   object SnapshotRoots(string fileName){
    var snapshots=(IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(Guid),snapshotType));var roots=new List<object>();
    foreach(var n in new[]{parent,owner}){var snap=capture.Invoke(null,new object[]{n,scope});snapshots.Add((Guid)Get(n,"DocumentId"),snap);roots.Add(TreeCall(snap,"CreateRoot"));}
    File.WriteAllText(Path.Combine(folder,fileName+".json"),json.Serialize(roots));return snapshots;
   }
   if(mode=="actual"){
    try{validate.Invoke(null,new[]{SnapshotRoots("before")});throw new Exception("未复现双标记拦截");}catch(TargetInvocationException e){Check(e.InnerException.Message.Contains("引用类型记录不完整"),"现场双标记在真实快照入口复现同一报错");}
   }
   var direct=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
   if(mode=="direct-owner"){direct.Add(partPath);Set(link,"IsContextReference",false);Set(link,"ReferenceTypeUnverified",false);Set(link,"ReferenceTypeVerified",true);}
   if(mode=="ambiguous-owner"||mode=="no-proof")Set(link,"IsContextReference",false);
   if(mode=="ambiguous-owner"){
    Directory.CreateDirectory(Path.Combine(folder,"other"));
    var duplicate=LinkedNode(Path.Combine(folder,"other"),Path.GetFileName(partPath),"Part");Set(duplicate,"ComponentSelectionName",ownerRow["Selection"]);((IList)Get(parent,"Children")).Add(duplicate);
   }
   var context=mode=="source-alias"?Path.Combine(folder,"old-context",Path.GetFileName(parentPath)):parentPath;
   if(mode=="source-alias")Set(link,"SourceReferencePath",context);
   var evidenceOwner=Activator.CreateInstance(Type("NativeDesignDependencies+ContextOwner"),true);
   foreach(var pair in new[]{new KeyValuePair<string,object>("Context",context),new KeyValuePair<string,object>("Owner",proof["Owner"]),new KeyValuePair<string,object>("Feature",proof["Feature"]),new KeyValuePair<string,object>("Status",proof["Status"])})Type("NativeDesignDependencies+ContextOwner").GetField(pair.Key,Flags).SetValue(evidenceOwner,pair.Value);
   var evidenceOwners=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(Type("NativeDesignDependencies+ContextOwner")));if(mode!="no-proof")evidenceOwners.Add(evidenceOwner);
   var evidence=Activator.CreateInstance(Type("NativeDesignDependencies+CheckInReferences"),Flags,null,new object[]{new[]{context},new HashSet<string>(StringComparer.OrdinalIgnoreCase){context},evidenceOwners},null);
   apply.Invoke(null,new object[]{parent,evidence,new HashSet<string>(),direct});if(mode=="repeat")apply.Invoke(null,new object[]{parent,evidence,new HashSet<string>(),direct});
   if(mode=="direct-owner")Check(!(bool)Get(link,"IsContextReference")&&(bool)Get(link,"ReferenceTypeVerified"),"父装配证据不覆盖零件自身已核实的普通来源");
   else if(mode=="ambiguous-owner"||mode=="no-proof")Check(!(bool)Get(link,"IsContextReference")&&(bool)Get(link,"ReferenceTypeUnverified"),mode+"：缺少唯一归属证明时不清除未知标记");
   else Check((bool)Get(link,"IsContextReference")&&(bool)Get(link,"ReferenceTypeVerified")&&!(bool)Get(link,"ReferenceTypeUnverified"),mode+"：明确父装配证据同步转换三个互斥状态");
   if(mode=="malformed-children")((IList)Get(link,"Children")).Add(LinkedNode(folder,"UNEXPECTED.SLDPRT","Part"));
   if(mode=="malformed-virtual")Set(link,"Status",Enum.Parse(Type("CadReferenceStatus"),"Virtual"));
   var shouldPass=mode!="direct-owner"&&mode!="malformed-children"&&mode!="malformed-virtual";
   try{validate.Invoke(null,new[]{SnapshotRoots("after")});Check(shouldPass,mode+"：生成的最终快照通过原有校验");}
   catch(TargetInvocationException e){Check(!shouldPass&&(mode=="direct-owner"?e.InnerException.Message.Contains("循环引用"):e.InnerException.Message.Contains("引用类型记录不完整")),mode+"：真实循环或非法结构保护仍有效");}
   Check(before.All(pair=>WorkingReuseHash(pair.Key)==pair.Value)&&((IList)Get(owner,"Children")).Contains(link),mode+"：原生文件字节和原关联保留");
  }
 }
}
