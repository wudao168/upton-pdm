using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

internal static partial class Program
{
 static void AssemblyPermissionPromptScenario(Form host,string output)
 {
  output=Path.GetFullPath(output);Directory.CreateDirectory(output);
  var identity=Type("PdmDocumentIdentityStore");var binding=identity.GetField("bindingDirectory",Flags);var resolver=identity.GetField("workspaceRootResolver",Flags);var isolated=identity.GetField("isolatedReadScope",Flags);
  var oldBinding=binding.GetValue(null);var oldResolver=resolver.GetValue(null);var oldIsolated=isolated.GetValue(null);
  binding.SetValue(null,Path.Combine(output,"bindings"));resolver.SetValue(null,new Func<string>(()=>output));isolated.SetValue(null,true);
  void Check(bool ok,string message){if(!ok)throw new Exception(message);Console.WriteLine("PASS: "+message);}
  try
  {
   foreach(var mode in new[]{"insert-unowned","insert-owned","insert-unmanaged","insert-history","file-new","new-part","new-assembly","new-subassembly","paste","file-drop","context-change","selection-change","configuration-change","edit-target-change"})
   {
    binding.SetValue(null,Path.Combine(output,"bindings"));
    var folder=Path.Combine(output,mode,"View");Directory.CreateDirectory(folder);var root=LinkedNode(folder,"ROOT.SLDASM","Assembly");var project=Guid.NewGuid();
    identity.GetMethod("TryWriteControlledVersion",Flags).Invoke(null,new[]{Get(root,"FullPath"),Get(root,"DocumentId"),(object)project,Guid.NewGuid(),"W1",WorkingReuseHash((string)Get(root,"FullPath")),(object)new FileInfo((string)Get(root,"FullPath")).Length});
    var plugin=New("PdmAddin");SetField(plugin,"authenticatedUsername","tester");SetField(plugin,"currentTree",root);
    using(var pane=(Control)New("PdmTaskPaneControl","权限事前检查"))
    {
     host.Controls.Add(pane);pane.CreateControl();SetField(plugin,"taskPaneControl",pane);
     if(mode=="insert-owned")
     {
      var session=Field(plugin,"checkoutSessionId");Set(root,"CheckedOutBy","tester");Set(root,"CheckoutSessionId",session);
      Field(plugin,"activeCheckoutDocumentIds").GetType().GetMethod("Add").Invoke(Field(plugin,"activeCheckoutDocumentIds"),new[]{Get(root,"DocumentId")});
      var edit=New("LocalEditDraft");Set(edit,"DocumentId",Get(root,"DocumentId"));Set(edit,"SessionId",session);Set(edit,"SourcePath",Get(root,"FullPath"));((IDictionary)Field(plugin,"localEdits")).Add(Get(root,"DocumentId"),edit);
     }
     if(mode=="insert-unmanaged"){Set(root,"DocumentId",null);binding.SetValue(null,Path.Combine(output,"unmanaged-bindings"));}
     if(mode=="insert-history")Set(root,"IsHistoricalPreview",true);
     var interop=Type("PdmAddin").GetField("application",Flags).FieldType.Assembly;var calls=0;var cfg="Default";var selectionObjects=new object[0];object active=null,target=null,model=null;
     var configuration=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.Configuration",true),call=>call.MethodName=="get_Name"?cfg:throw new Exception(call.MethodName)).GetTransparentProxy();
     var configurationManager=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ConfigurationManager",true),call=>call.MethodName=="get_ActiveConfiguration"?configuration:throw new Exception(call.MethodName)).GetTransparentProxy();
     var selection=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.SelectionMgr",true),call=>call.MethodName=="GetSelectedObjectCount2"?selectionObjects.Length:call.MethodName=="GetSelectedObject6"?selectionObjects[(int)call.Args[0]-1]:throw new Exception(call.MethodName)).GetTransparentProxy();
     var modelType=interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true);
     model=new AssemblyProxy(modelType,call=>{
      switch(call.MethodName){case "GetPathName":return Get(root,"FullPath");case "GetEditTarget":return target??model;case "get_SelectionManager":return selection;case "get_ConfigurationManager":return configurationManager;default:calls++;throw new Exception("预事件不能保存、开关文档或直接获取权限："+call.MethodName);}
     }).GetTransparentProxy();active=model;
     var appType=Type("PdmAddin").GetField("application",Flags).FieldType;SetField(plugin,"application",new Proxy(appType,call=>call.MethodName=="get_ActiveDoc"?active:throw new Exception("预事件不得修改原生窗口："+call.MethodName)).GetTransparentProxy());SetField(plugin,"assemblyEventDocument",model);
     var command=mode=="file-new"?1:mode=="new-part"?91:mode=="new-assembly"?295:mode=="new-subassembly"?1258:mode=="paste"?585:13;
     var result=(int)TreeCall(plugin,mode=="file-drop"?"OnAssemblyFileDropPreNotify":"OnAssemblyCommandOpening",mode=="file-drop"?new object[]{"SOURCE.SLDPRT"}:new object[]{command,0});
     var allowed=new[]{"insert-owned","insert-unmanaged","file-new"}.Contains(mode);
     Check(result==(allowed?0:1),mode+"：先核对所属装配体权限，再决定是否启动原生操作");
     var pending=Field(plugin,"pendingAssemblyOperation");Check((pending==null)==(allowed||mode=="insert-history"),mode+"：无权限时只排队提示，历史预览不自动获取或修改");
     if(pending!=null)
     {
      Check((bool)TreeCall(plugin,"PendingAssemblyContextMatches",pending),mode+"：原窗口和选择一致时才可继续");
      if(mode=="configuration-change")cfg="Other";
      if(mode=="selection-change")selectionObjects=new[]{new object()};
      if(mode=="context-change")active=null;
      if(mode=="edit-target-change")target=new AssemblyProxy(modelType,call=>call.MethodName=="GetPathName"?Path.Combine(folder,"OTHER.SLDASM"):throw new Exception(call.MethodName)).GetTransparentProxy();
      if(mode.EndsWith("change"))Check(!(bool)TreeCall(plugin,"PendingAssemblyContextMatches",pending),mode+"：上下文变化后不重放旧插入或删除操作");
     }
     Check(calls==0,mode+"：拦截阶段不保存、不关闭、不重命名图档");
    }
   }
  }
  finally{binding.SetValue(null,oldBinding);resolver.SetValue(null,oldResolver);isolated.SetValue(null,oldIsolated);}
 }
}
