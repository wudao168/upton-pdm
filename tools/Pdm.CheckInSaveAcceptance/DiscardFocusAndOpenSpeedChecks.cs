using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static partial class Program
{
 static async Task DiscardFocusScenario(bool permissionOnly=false)
 {
  foreach(var mode in permissionOnly?new[]{"assembly-handler","assembly-handler-structural","assembly-handler-cache-missing"}:new[]{"assembly-clean","assembly-modified","assembly-batch","standalone-modified","restore-failure"})
  using(var fixture=new DiscardPathFixture())
  using(var pane=(Control)New("PdmTaskPaneControl","放弃编辑窗口回归"))
  {
   SetField(fixture.Plugin,"taskPaneControl",pane);
   SetField(fixture.Client,"controlledOpenHttpClient",Field(fixture.Client,"httpClient"));
   var nodes=new List<object>{fixture.Node};for(var i=1;i<214;i++)nodes.Add(fixture.AddWorkingPart("UNSELECTED-"+i+".SLDPRT"));
   var selected=mode=="assembly-batch"?nodes.Take(2).ToArray():new[]{fixture.Node};
   foreach(var node in selected){Set(node,"CheckedOutBy","tester");Set(node,"CheckoutSessionId",Field(fixture.Plugin,"checkoutSessionId"));}
   foreach(var node in nodes.Skip(1))File.SetAttributes((string)Get(node,"FullPath"),FileAttributes.ReadOnly);
   var modified=mode.Contains("modified")||mode=="restore-failure";if(modified)File.WriteAllText(fixture.CurrentPath,"selected edit");
   fixture.Handler.OperationManifest=fixture.Manifest;fixture.Handler.Fail=mode=="restore-failure";
   var standalone=mode.StartsWith("standalone");var rootPath=standalone?fixture.CurrentPath:Path.Combine(Path.GetDirectoryName(fixture.CurrentPath),"ORIGINAL.SLDASM");
   if(!standalone){File.WriteAllText(rootPath,"unselected original assembly");File.SetAttributes(rootPath,FileAttributes.ReadOnly);}
   var root=standalone?fixture.Node:New("CadTreeNode");if(!standalone){Set(root,"FullPath",rootPath);Set(root,"FileName","ORIGINAL.SLDASM");Set(root,"Kind",Enum.Parse(Type("CadDocumentKind"),"Assembly"));((IList)Get(root,"Children")).Add(fixture.Node);}
   SetField(fixture.Plugin,"currentTree",root);
   var appType=fixture.Plugin.GetType().GetField("application",Flags).FieldType;var interop=appType.Assembly;var modelType=interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true);
   var configuration=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.Configuration",true),call=>call.MethodName=="get_Name"?"用户当前配置":throw new Exception(call.MethodName)).GetTransparentProxy();
   var manager=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ConfigurationManager",true),call=>call.MethodName=="get_ActiveConfiguration"?configuration:throw new Exception(call.MethodName)).GetTransparentProxy();
   var part=new Proxy(modelType,call=>{
    switch(call.MethodName){case "get_Visible":return standalone;case "GetPathName":return fixture.CurrentPath;case "GetTitle":return "PART.SLDPRT";case "GetType":return 1;case "GetSaveFlag":return false;case "IsOpenedReadOnly":return fixture.MemoryReadOnly;case "SetReadOnlyState":fixture.MemoryReadOnly=(bool)call.Args[0];return true;case "get_ConfigurationManager":return manager;default:throw new Exception("Unexpected part operation "+call.MethodName);}
   }).GetTransparentProxy();
   var component=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.Component2",true),call=>call.MethodName=="GetModelDoc2"?part:call.MethodName=="GetPathName"?fixture.CurrentPath:throw new Exception(call.MethodName)).GetTransparentProxy();
   var assembly=new DiscardAssemblyProxy(modelType,call=>{
    switch(call.MethodName){case "get_Visible":return true;case "GetPathName":return rootPath;case "GetTitle":return "ORIGINAL.SLDASM";case "GetType":return 2;case "GetSaveFlag":return false;case "IsOpenedReadOnly":return true;case "get_ConfigurationManager":return manager;case "GetComponents":return new[]{component};default:throw new Exception("Unexpected assembly operation "+call.MethodName);}
   }).GetTransparentProxy();
   var rootLoaded=!standalone;object active=standalone?part:assembly;var closes=new List<string>();var opens=new List<string>();var activations=0;
   SetField(fixture.Plugin,"application",new Proxy(appType,call=>{
    switch(call.MethodName){
     case "GetDocuments":return (rootLoaded?new[]{assembly}:new object[0]).Concat(fixture.Loaded?new[]{part}:new object[0]).ToArray();
     case "get_ActiveDoc":return active;
     case "GetDocumentDependencies2":return new string[0];
     case "CloseDoc":var title=(string)call.Args[0];closes.Add(title);if(title=="ORIGINAL.SLDASM")rootLoaded=false;else if(title=="PART.SLDPRT")fixture.Loaded=false;else throw new Exception("Closed another document");active=null;return null;
     case "OpenDoc6":var path=(string)call.Args[0];opens.Add(path);if(path!=rootPath||(string)call.Args[3]!="用户当前配置")throw new Exception("Opened selected component or lost original configuration");fixture.Loaded=true;fixture.MemoryReadOnly=true;rootLoaded=!standalone;active=standalone?part:assembly;return active;
     case "ActivateDoc3":activations++;active=part;return part;
     default:throw new Exception("Unexpected CAD operation "+call.MethodName);
    }
   }).GetTransparentProxy());
   var items=Array.CreateInstance(Type("CadTreeNode"),selected.Length);for(var i=0;i<selected.Length;i++)items.SetValue(selected[i],i);
   var failed=false;Exception failure=null;
   if(permissionOnly)
   {
    Set(fixture.Node,"CheckedOutBy","tester");Set(fixture.Node,"CheckoutSessionId",Field(fixture.Plugin,"checkoutSessionId"));
    using(var refresh=new PermissionRefreshFixture(fixture.Plugin,root,assembly,fixture.Client,(Guid)Get(fixture.Manifest,"ProjectId"),mode.EndsWith("cache-missing")))
    {
     if(mode.EndsWith("structural"))fixture.Handler.OnOperation=refresh.StructuralChange;
     DiscardCall(fixture.Plugin,"OnDiscardCheckoutRequested",null,New("CadTreeNodeEventArgs",fixture.Node));
     for(var i=0;i<300&&(int)Field(fixture.Plugin,"workspaceOperationInProgress")!=0;i++)await Task.Delay(10);
     refresh.AssertAfterHandler();
     if(fixture.Handler.LimitedVersionReads!=1)throw new Exception("Discard existence check fetched full version history");
    }
   }
   else{try{await (Task)DiscardCall(fixture.Plugin,"DiscardSelectedWorkingFilesAsync",items);}catch(InvalidOperationException error){failed=true;failure=error;}}
   if(failed!=(mode=="restore-failure"))throw new Exception("Wrong discard result "+mode,failure);
   if(active!=(standalone?part:assembly)||activations!=0)throw new Exception("Discard changed the active original window: "+mode);
   if(!modified||failed){if(opens.Count!=0||closes.Count!=0)throw new Exception("Discard unnecessarily reopened a document: "+mode);}
   else if(opens.Count!=1||opens[0]!=rootPath)throw new Exception("Discard did not restore only its original window: "+mode);
   foreach(var node in nodes.Where(node=>!selected.Contains(node)))
   {var path=(string)Get(node,"FullPath");if(File.ReadAllText(path)!=DiscardPathFixture.Canonical||(File.GetAttributes(path)&FileAttributes.ReadOnly)==0)throw new Exception("Unselected file changed");}
   if(File.ReadAllText(fixture.CurrentPath)!=(failed?"selected edit":DiscardPathFixture.Canonical))throw new Exception("Discard file restoration incorrect");
   fixture.AssertOldUntouched();Console.WriteLine("PASS: real discard task "+mode+"; original window retained/restored, no selected-component activation, 214-file context protected");
  }
 }
}
