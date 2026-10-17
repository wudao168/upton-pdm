using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Remoting.Messaging;
using System.Windows.Forms;

internal static partial class Program
{
 static void ComponentRenameAndFillScenario(Form host)
 {
  foreach(var mode in new[]{"loaded","closed","lightweight","ancestor-lightweight","suppressed","not-attached","wrong-path","read-only","patterned","held-tree","embedded-unloaded"})
   ComponentRenameCheck(host,mode);
  ModelFillCheck(false);ModelFillCheck(true);PropertyReadFailureCheck();
 }

 static void ComponentRenameCheck(Form host,string mode)
 {
  var interop=Type("PdmAddin").GetField("application",Flags).FieldType.Assembly;
  var renameErrors=Type("PdmAddin").GetMethod("RenameDocumentErrorText",Flags).GetParameters()[0].ParameterType;
  var patternedError=Convert.ToInt32(Enum.Parse(renameErrors,"swRenameDocumentError_PatternedComponent"));
  var unloadedError=Convert.ToInt32(Enum.Parse(renameErrors,"swRenameDocumentError_NoModelLoaded"));
  var folder=Path.Combine(Path.GetTempPath(),"pdm-component-rename-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
  var rootPath=Path.Combine(folder,"ROOT.SLDASM");File.WriteAllText(rootPath,"root original");
  var paths=new[]{Path.Combine(folder,"FIRST.SLDPRT"),Path.Combine(folder,"SECOND.SLDPRT")};
  if(mode=="patterned")paths[1]=paths[0];
  foreach(var path in paths)File.WriteAllText(path,"part original");
  var ready=new[]{true,mode=="loaded"||mode=="held-tree"};
  if(mode=="patterned")ready[1]=false;
  var states=new[]{2,mode=="lightweight"||mode=="patterned"?1:mode=="suppressed"?0:2};
  var modelPaths=(string[])paths.Clone();var selected=-1;var renames=0;var opens=0;var resolved=0;var activations=0;var parentState=1;
  var models=new object[2];var components=new object[2];object active=null;object rootModel=null;
  for(var index=0;index<2;index++)
  {
   var current=index;
   models[index]=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true),call=>
   {
    if(call.MethodName=="GetPathName")return current==1&&mode=="wrong-path"?Path.Combine(folder,"OTHER.SLDPRT"):modelPaths[current];
    if(call.MethodName=="IsOpenedReadOnly")return current==1&&mode=="read-only";
    if(call.MethodName=="SetReadOnlyState")return false;
    if(call.MethodName=="GetSaveFlag")return false;
    throw new Exception("Unexpected model mutation "+call.MethodName);
   }).GetTransparentProxy();
   object parent=null;
   if(index==1&&mode=="ancestor-lightweight")parent=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.Component2",true),call=>
   {if(call.MethodName=="GetParent")return null;if(call.MethodName=="GetSuppression")return parentState;if(call.MethodName=="get_Name2")return "SUB-1";if(call.MethodName=="SetSuppression2"){parentState=(int)call.Args[0];ready[1]=true;resolved++;return 0;}throw new Exception(call.MethodName);}).GetTransparentProxy();
   components[index]=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.Component2",true),call=>
   {
    if(call.MethodName=="get_Name2")return (current==0?"FIRST":"SECOND")+"-1";
    if(call.MethodName=="GetPathName")return modelPaths[current];
    if(call.MethodName=="GetModelDoc2")return ready[current]?models[current]:null;
    if(call.MethodName=="GetParent")return parent;
    if(call.MethodName=="GetSuppression")return states[current];
    if(call.MethodName=="SetSuppression2"){states[current]=(int)call.Args[0];ready[current]=true;resolved++;return 0;}
    if(call.MethodName=="Select4"){selected=current;return true;}
    if(call.MethodName=="IsPatternInstance")return mode=="patterned"&&current==0;
    throw new Exception(call.MethodName);
   }).GetTransparentProxy();
  }
  var extension=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDocExtension",true),call=>
  {
   if(call.MethodName!="RenameDocument")throw new Exception(call.MethodName);
   if(mode=="patterned"&&selected==0)return patternedError;
   if(selected<0)throw new Exception("No component selected");
   if(!ready[selected])return unloadedError;
   renames++;var old=modelPaths[selected];var renamed=Path.Combine(folder,(string)call.Args[0]+".SLDPRT");
   for(var i=0;i<2;i++)if(modelPaths[i]==old)modelPaths[i]=renamed;
   return 0;
  }).GetTransparentProxy();
  rootModel=new AssemblyProxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true),call=>
  {
   if(call.MethodName=="GetType")return 2;if(call.MethodName=="GetPathName")return rootPath;if(call.MethodName=="GetTitle")return "ROOT.SLDASM";
   if(call.MethodName=="GetComponents")return components;if(call.MethodName=="get_Extension")return extension;if(call.MethodName=="get_SelectionManager")return null;
   if(call.MethodName=="ClearSelection2"){selected=-1;return null;}if(call.MethodName=="IsOpenedReadOnly"||call.MethodName=="GetSaveFlag")return false;
   throw new Exception(call.MethodName);
  }).GetTransparentProxy();active=rootModel;
  var appType=Type("PdmAddin").GetField("application",Flags).FieldType;
  var app=new Proxy(appType,call=>
  {
   if(call.MethodName=="get_ActiveDoc")return active;
   if(call.MethodName=="GetDocuments")return new[]{rootModel}.Concat(models.Where((model,index)=>ready[index])).ToArray();
   if(call.MethodName=="GetDocumentVisible")return true;if(call.MethodName=="DocumentVisible")return null;
   if(call.MethodName=="OpenDoc6"){opens++;if((string)call.Args[0]!=paths[1])throw new Exception("Opened another file");if(mode!="not-attached")ready[1]=true;active=models[1];return models[1];}
   if(call.MethodName=="ActivateDoc3"){activations++;active=rootModel;return rootModel;}
   throw new Exception("Unexpected native operation "+call.MethodName);
  }).GetTransparentProxy();
  var root=New("CadTreeNode");Set(root,"FullPath",rootPath);Set(root,"FileName","ROOT.SLDASM");Set(root,"Kind",Enum.ToObject(Type("CadDocumentKind"),0));
  var requests=Array.CreateInstance(Type("BatchDocumentRenameRequest"),mode=="patterned"?1:2);var nodes=new object[2];
  for(var index=0;index<2;index++)
  {
   nodes[index]=New("CadTreeNode");Set(nodes[index],"FullPath",paths[index]);Set(nodes[index],"FileName",Path.GetFileName(paths[index]));Set(nodes[index],"Kind",Enum.ToObject(Type("CadDocumentKind"),1));Set(nodes[index],"Configuration","默认");Set(nodes[index],"ComponentSelectionName",(index==0?"FIRST":"SECOND")+"-1");
   if(index==1&&mode=="embedded-unloaded")Set(nodes[index],"Status",Enum.ToObject(Type("CadReferenceStatus"),4));
   var treeNode=nodes[index];
   if(mode=="held-tree"){treeNode=New("CadTreeNode");Set(treeNode,"FullPath",paths[index]);Set(treeNode,"FileName",Path.GetFileName(paths[index]));Set(treeNode,"Kind",Enum.ToObject(Type("CadDocumentKind"),1));}
   ((IList)Get(root,"Children")).Add(treeNode);
   var item=RenamePropertyItem(nodes[index],new Dictionary<string,string>());
   if(index<requests.Length)requests.SetValue(New("BatchDocumentRenameRequest",item,"RENAMED-"+index),index);
  }
  var plugin=New("PdmAddin");SetField(plugin,"application",app);SetField(plugin,"currentTree",root);
  using(var pane=(Control)New("PdmTaskPaneControl","改名回归"))
  {
   host.Controls.Add(pane);SetField(plugin,"taskPaneControl",pane);
   var result=(IDictionary)Type("PdmAddin").GetMethod("ExecuteBatchDocumentRenames",Flags).Invoke(plugin,new object[]{requests});
   var shouldFail=mode=="suppressed"||mode=="not-attached"||mode=="wrong-path"||mode=="read-only"||mode=="embedded-unloaded";
   var successes=result.Values.Cast<string>().Count(status=>status=="已完成");
   if(shouldFail?(renames!=0||successes!=0):(renames!=requests.Length||successes!=requests.Length))throw new Exception("Rename outcome "+mode+": "+string.Join(";",result.Values.Cast<string>()));
   if(paths.Any(path=>File.ReadAllText(path)!="part original")||File.ReadAllText(rootPath)!="root original")throw new Exception("Preflight or pending rename wrote source bytes");
   if(mode=="closed"&&(opens!=1||activations!=1))throw new Exception("Closed model was not safely loaded and root restored");
   if((mode=="lightweight"||mode=="ancestor-lightweight")&&resolved==0)throw new Exception("Lightweight model was not resolved");
   if(active!=rootModel)throw new Exception("Preparation left another document active after success/failure");
   if(mode=="embedded-unloaded"&&opens!=0)throw new Exception("Embedded virtual model opened as an independent file");
   if(mode=="held-tree")for(var i=0;i<2;i++)if((string)Get(nodes[i],"FullPath")!=modelPaths[i]||(string)Get(((IList)Get(root,"Children"))[i],"FullPath")!=modelPaths[i])throw new Exception("Held dialog row or refreshed tree retained old name");
  }
  Console.WriteLine("PASS: component rename validates every model before the first rename "+mode);
 }

 static object RenamePropertyItem(object node,Dictionary<string,string> values) => New("BatchPropertyEditItem",New("BatchOperationItem",node,1),"","","默认",values,new Dictionary<string,string>(),values.Keys.ToArray(),"","");

 static void ModelFillCheck(bool conflictingSameName)
 {
  var folder=Path.Combine(Path.GetTempPath(),"pdm-model-fill-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
  var values=new[]{new Dictionary<string,string>{{"文档名称","已读取型号"}},new Dictionary<string,string>(),new Dictionary<string,string>(),new Dictionary<string,string>(),new Dictionary<string,string>()};
  var names=new[]{"FIRST.SLDPRT","LM30UU.SLDPRT","不勾选.SLDPRT","按钮（黄色）.SLDPRT",conflictingSameName?"LM30UU.SLDDRW":"不同名工程图.SLDDRW"};
  var items=Array.CreateInstance(Type("BatchPropertyEditItem"),names.Length);object first=null;
  for(var index=0;index<names.Length;index++)
  {
   var node=New("CadTreeNode");Set(node,"FileName",names[index]);Set(node,"FullPath",Path.Combine(folder,names[index]));Set(node,"Kind",Enum.ToObject(Type("CadDocumentKind"),index>=4?2:1));Set(node,"DocumentId",Guid.NewGuid());
   File.WriteAllText((string)Get(node,"FullPath"),"saved original");
   if(index==0)first=node;if(index==4)Set(node,"RelatedModelDocumentId",Get(first,"DocumentId"));
   var item=RenamePropertyItem(node,values[index]);if(index==2)Set(item,"Selected",false);items.SetValue(item,index);
  }
  using(var dialog=(Form)New("BatchPropertyEditDialog",items,Array.CreateInstance(Type("PropertyWritebackPreviewItem"),0),0,Enum.ToObject(Type("PropertyOperationMode"),0),null,null,null,null,null,null,null,null,IntPtr.Zero))
  {
   Type("BatchPropertyEditDialog").GetMethod("FillDocumentNameForSelectedRows",Flags).Invoke(dialog,new object[]{"型号"});
   var expected=new[]{"已读取型号","LM30UU","","按钮（黄色）","已读取型号"};
   for(var index=0;index<items.Length;index++)if((string)Get(items.GetValue(index),"Model")!=expected[index])throw new Exception("Model fill skipped or changed row "+index);
   var label=(Label)Field(dialog,"plmSyncStatus");if(!label.Text.Contains("已填充 4")||!label.Text.Contains("未填充 0"))throw new Exception("Fill result was not counted accurately");
   if(Directory.GetFiles(folder).Any(path=>File.ReadAllText(path)!="saved original"))throw new Exception("Preview fill saved files without batch write");
  }
  Console.WriteLine("PASS: model fill covers missing document properties, unchecked rows and exact linked drawings without saving preview values "+conflictingSameName);
 }

 static void PropertyReadFailureCheck()
 {
  var appType=Type("PdmAddin").GetField("application",Flags).FieldType;var interop=appType.Assembly;
  foreach(var failed in new[]{false,true})
  {
   var path=Path.Combine(Path.GetTempPath(),"pdm-property-read-"+Guid.NewGuid().ToString("N")+".SLDPRT");File.WriteAllText(path,"original file");
   var manager=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.CustomPropertyManager",true),call=>
   {if(call.MethodName=="GetNames"){if(failed)throw new IOException("fixture read failed");return null;}if(call.MethodName=="get_Count")return 0;throw new Exception(call.MethodName);}).GetTransparentProxy();
   var extension=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDocExtension",true),call=>call.MethodName=="get_CustomPropertyManager"?manager:throw new Exception(call.MethodName)).GetTransparentProxy();
   var model=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true),call=>call.MethodName=="GetPathName"?path:call.MethodName=="get_Extension"?extension:throw new Exception("Unexpected model write "+call.MethodName)).GetTransparentProxy();
   var app=new Proxy(appType,call=>call.MethodName=="GetDocuments"?new[]{model}:throw new Exception(call.MethodName)).GetTransparentProxy();
   var node=New("CadTreeNode");Set(node,"FullPath",path);Set(node,"FileName",Path.GetFileName(path));Set(node,"Kind",Enum.ToObject(Type("CadDocumentKind"),1));Set(node,"Configuration","默认");
   var plugin=New("PdmAddin");SetField(plugin,"application",app);var rejected=false;
   try{var result=(IDictionary)Type("PdmAddin").GetMethod("ReadBatchPropertyValues",Flags).Invoke(plugin,new[]{node});if(result.Count!=0)throw new Exception("Empty property set misread");}
   catch(TargetInvocationException error)when(error.InnerException is InvalidOperationException){rejected=error.InnerException.Message.Contains("未完整读取");}
   if(rejected!=failed||File.ReadAllText(path)!="original file")throw new Exception("Read failure treated as an empty set or changed source");
   Console.WriteLine("PASS: unreadable properties stop editing instead of masquerading as empty values "+failed);
  }
 }
}
