using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Remoting.Messaging;
using System.Windows.Forms;

internal static partial class Program
{
 static void PartialPropertyCardScenario()
 {
  var folder=Path.Combine(Path.GetTempPath(),"pdm-partial-property-card-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
  var cardPath=Path.Combine(folder,"fixture.prtprp");
  File.WriteAllText(cardPath,"<Root><Control PropName=\"材料\" Label=\"材料\"/><Control PropName=\"型号\" Label=\"型号\"/></Root>");
  var template=Type("NativePropertyCardTemplate").GetMethod("Load",Flags).Invoke(null,new object[]{cardPath});
  var appType=Type("PdmAddin").GetField("application",Flags).FieldType;var interop=appType.Assembly;
  var models=new object[2];var nodes=new object[2];var items=Array.CreateInstance(Type("BatchPropertyEditItem"),2);
  var nativeCalls=new int[2];var saves=new int[2];var templateWrites=new int[2];var propertyWrites=new int[2];
  var actualModels=new[]{"原型号","正常型号"};var configuredFolders="";
  for(var index=0;index<2;index++)
  {
   var current=index;var path=Path.Combine(folder,index==0?"UNREAD.SLDPRT":"READABLE.SLDPRT");File.WriteAllText(path,"unchanged native bytes");
   var manager=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.CustomPropertyManager",true),call=>{
    nativeCalls[current]++;
    if(call.MethodName=="get_Count")return 2;
    if(call.MethodName=="GetNames")return new[]{"材料","型号"};
    if(call.MethodName=="Get6"){
     var name=(string)call.Args[0];if(current==0&&name=="材料")throw new IOException("unread material");
     var value=name=="材料"?"原材料":actualModels[current];
     return new ReturnMessage(2,new object[]{name,call.Args[1],value,value,true,false},6,call.LogicalCallContext,call);
    }
    if(call.MethodName=="Set2"){
     if((string)call.Args[0]!="型号")throw new Exception("Unread field was written");
     actualModels[current]=(string)call.Args[1];propertyWrites[current]++;return 0;
    }
    throw new Exception("Unexpected field mutation: "+call.MethodName);
   }).GetTransparentProxy();
   var extension=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDocExtension",true),call=>{
    nativeCalls[current]++;
    if(call.MethodName=="get_CustomPropertyManager")return manager;
    if(call.MethodName=="get_CustomPropertyBuilderTemplate")return cardPath;
    if(call.MethodName=="set_CustomPropertyBuilderTemplate"){templateWrites[current]++;return null;}
    throw new Exception(call.MethodName);
   }).GetTransparentProxy();
   models[index]=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true),call=>{
    nativeCalls[current]++;
    if(call.MethodName=="GetPathName")return path;
    if(call.MethodName=="get_Extension")return extension;
    if(call.MethodName=="IsOpenedReadOnly")return false;
    if(call.MethodName=="Save3"){saves[current]++;return new ReturnMessage(true,new object[]{call.Args[0],0,0},3,call.LogicalCallContext,call);}
    throw new Exception("Unexpected model mutation: "+call.MethodName);
   }).GetTransparentProxy();
   nodes[index]=New("CadTreeNode");Set(nodes[index],"FullPath",path);Set(nodes[index],"FileName",Path.GetFileName(path));Set(nodes[index],"Kind",Enum.ToObject(Type("CadDocumentKind"),1));
   var item=New("BatchPropertyEditItem",New("BatchOperationItem",nodes[index],1),"","","",new Dictionary<string,string>{{"型号",actualModels[index]}},new Dictionary<string,string>(),new[]{"材料","型号"},"","");
   Type("BatchPropertyEditItem").GetMethod("SetActivePropertyCard",Flags).Invoke(item,new[]{template});
   if(index==0){
    var unread=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    Type("PdmAddin").GetMethod("ReadEditableProperties",Flags).Invoke(null,new object[]{models[index],"",false,unread});
    Type("BatchPropertyEditItem").GetMethod("SetUnreadProperties",Flags).Invoke(item,new object[]{unread});
    Type("BatchPropertyEditItem").GetMethod("SetPropertyValue",Flags).Invoke(item,new object[]{"型号","已读型号修改"});
   }
   Type("BatchPropertyEditItem").GetMethod("ApplyPropertyCard",Flags).Invoke(item,new[]{template});
   items.SetValue(item,index);
  }
  var plugin=New("PdmAddin");SetField(plugin,"application",new Proxy(appType,call=>{
   if(call.MethodName=="GetDocuments")return models;
   if(call.MethodName=="SetUserPreferenceStringValue"){configuredFolders=(string)call.Args[1];return true;}
   if(call.MethodName=="GetUserPreferenceStringValue")return configuredFolders;
   throw new Exception("Unexpected application operation: "+call.MethodName);
  }).GetTransparentProxy());
  SetField(plugin,"currentTree",nodes[0]);
  var assign=Type("PdmAddin").GetMethod("ApplyNativePropertyCardAssignment",Flags);
  nativeCalls[0]=0;
  if((bool)assign.Invoke(plugin,new[]{items.GetValue(0)})||nativeCalls[0]!=0)throw new Exception("Direct assignment bypassed unread-field protection");
  Console.WriteLine("PASS: unread material plus a changed readable model field cannot assign a card or call native APIs");
  using(var pane=(Control)New("PdmTaskPaneControl","属性卡保护回归"))
  {
   SetField(plugin,"taskPaneControl",pane);
   var message=(string)Type("PdmAddin").GetMethod("ExecuteLocalBatchPropertyOperation",Flags).Invoke(plugin,new object[]{items,true,null});
   if(!message.Contains("成功：1个")||!message.Contains("失败：0个")||!message.Contains("跳过：1个")||!message.Contains("UNREAD.SLDPRT"))throw new Exception("Mixed assignment counts skipped row as success or fails the whole batch: "+message);
  }
  if(templateWrites[0]!=0||saves[0]!=0||propertyWrites[0]!=0||templateWrites[1]!=1||saves[1]!=1)throw new Exception("Skipped assignment changed fields, template or saved the model");
  if(!(bool)Get(items.GetValue(0),"HasChanges"))throw new Exception("Skipped row lost its pending readable-field edit");
  Console.WriteLine("PASS: mixed property-card batch skips only the unread row, reports exact counts, and preserves its pending edit");
  Type("PdmAddin").GetMethod("ApplyBatchPropertyEdit",Flags).Invoke(plugin,new[]{items.GetValue(0)});
  if(actualModels[0]!="已读型号修改"||propertyWrites[0]!=1||saves[0]!=1||templateWrites[0]!=0)throw new Exception("Ordinary readable-field editing failed or touched the unread material/template");
  Console.WriteLine("PASS: ordinary batch edit still writes and saves the readable field while preserving the unread field and card");
  // Direct callers must also fail closed if no new card was confirmed.
  var unconfirmed=New("BatchPropertyEditItem",New("BatchOperationItem",nodes[1],1),"","","",new Dictionary<string,string>(),new Dictionary<string,string>(),new string[0],"","");
  Type("BatchPropertyEditItem").GetMethod("SetActivePropertyCard",Flags).Invoke(unconfirmed,new[]{template});
  nativeCalls[1]=0;
  if((bool)assign.Invoke(plugin,new[]{unconfirmed})||nativeCalls[1]!=0)throw new Exception("Unconfirmed card assignment invoked native operations");
  Type("BatchPropertyEditItem").GetMethod("ApplyPropertyCard",Flags).Invoke(unconfirmed,new[]{template});
  Type("BatchPropertyEditItem").GetMethod("SetUnreadProperties",Flags).Invoke(unconfirmed,new object[]{new[]{"材料"}});
  if((bool)assign.Invoke(plugin,new[]{unconfirmed})||nativeCalls[1]!=0)throw new Exception("A later read failure bypassed an earlier card confirmation");
  Console.WriteLine("PASS: direct assignment guards both unconfirmed cards and fields that become unread after confirmation");
 }
}
