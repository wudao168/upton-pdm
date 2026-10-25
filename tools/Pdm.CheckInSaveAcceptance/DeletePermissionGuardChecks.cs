using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

internal static partial class Program
{
 static void DeletePermissionGuardScenario()
 {
  var identity=Type("PdmDocumentIdentityStore");var bindings=identity.GetField("bindingDirectory",Flags);var resolver=identity.GetField("workspaceRootResolver",Flags);var isolated=identity.GetField("isolatedReadScope",Flags);
  var previousBindings=bindings.GetValue(null);var previousResolver=resolver.GetValue(null);var previousIsolated=isolated.GetValue(null);
  try
  {
   foreach(var mode in new[]{"unowned","owned","part-owned-only","foreign-user","foreign-session","stale-tracker","wrong-draft-path","closed-draft","recovery-pending","nested-unowned","nested-owned","virtual-unowned","virtual-owned","mixed-owners","unmanaged","missing-binding","face-selection","empty-selection"})
   {
    var directory=Path.Combine(Path.GetTempPath(),"pdm-delete-permission-"+Guid.NewGuid().ToString("N"));var folder=Path.Combine(directory,"View");Directory.CreateDirectory(folder);
    bindings.SetValue(null,Path.Combine(directory,"bindings"));resolver.SetValue(null,new Func<string>(()=>directory));isolated.SetValue(null,true);
    var root=LinkedNode(folder,"ROOT.SLDASM","Assembly");var sub=LinkedNode(folder,"SUB.SLDASM","Assembly");var part=LinkedNode(folder,"PART.SLDPRT","Part");((IList)Get(root,"Children")).Add(sub);((IList)Get(root,"Children")).Add(part);
    var plugin=New("PdmAddin");var session=(Guid)Field(plugin,"checkoutSessionId");var project=Guid.NewGuid();SetField(plugin,"authenticatedUsername","tester");SetField(plugin,"currentTree",root);
    foreach(var node in new[]{root,sub,part})
    {
     var path=(string)Get(node,"FullPath");identity.GetMethod("TryWriteControlledVersion",Flags).Invoke(null,new object[]{path,Get(node,"DocumentId"),project,Guid.NewGuid(),"W1",WorkingReuseHash(path),new FileInfo(path).Length});
    }
    var ownRoot=new[]{"owned","stale-tracker","wrong-draft-path","closed-draft","recovery-pending","virtual-owned","mixed-owners"}.Contains(mode);
    void Lease(object node)
    {
     var id=(Guid)Get(node,"DocumentId");Set(node,"CheckedOutBy","tester");Set(node,"CheckoutSessionId",session);
     ((ISet<Guid>)Field(plugin,"activeCheckoutDocumentIds")).Add(id);
     var edit=New("LocalEditDraft");Set(edit,"DocumentId",id);Set(edit,"ProjectId",project);Set(edit,"SessionId",session);Set(edit,"SourcePath",Get(node,"FullPath"));((IDictionary)Field(plugin,"localEdits"))[id]=edit;
    }
    if(ownRoot)Lease(root);if(mode=="part-owned-only")Lease(part);if(mode=="nested-owned")Lease(sub);
    if(mode=="foreign-user"){Set(root,"CheckedOutBy","another");Set(root,"CheckoutSessionId",Guid.NewGuid());}
    if(mode=="foreign-session"){Set(root,"CheckedOutBy","tester");Set(root,"CheckoutSessionId",Guid.NewGuid());}
    if(mode=="stale-tracker")((ISet<Guid>)Field(plugin,"activeCheckoutDocumentIds")).Clear();
    if(mode=="wrong-draft-path")Set(((IDictionary)Field(plugin,"localEdits"))[Get(root,"DocumentId")],"SourcePath",(string)Get(root,"FullPath")+".other");
    if(mode=="closed-draft")Set(((IDictionary)Field(plugin,"localEdits"))[Get(root,"DocumentId")],"Closed",true);
    if(mode=="recovery-pending")Set(((IDictionary)Field(plugin,"localEdits"))[Get(root,"DocumentId")],"RecoveryRequired",true);
    if(mode=="unmanaged"||mode=="missing-binding")
    {
     // A newly created unregistered file has no binding or server lease to obtain.
     var local=Path.Combine(folder,"NEW.SLDASM");File.WriteAllText(local,"new local assembly");Set(root,"FullPath",local);Set(root,"DocumentId",null);
     if(mode=="missing-binding")
     {
      var manifest=New("ControlledOpenManifestDto");var file=New("ControlledOpenFileDto");Set(file,"DocumentId",Guid.NewGuid());Set(file,"FileName","NEW.SLDASM");Set(file,"RelativePath","NEW.SLDASM");var files=(IList)Activator.CreateInstance(Property(manifest,"Files").PropertyType);files.Add(file);Set(manifest,"Files",files);((IDictionary)Field(plugin,"controlledOpenManifests")).Add(folder,manifest);
     }
    }
    var before=Directory.GetFiles(folder).ToDictionary(path=>path,path=>WorkingReuseHash(path));
    var appType=Type("PdmAddin").GetField("application",Flags).FieldType;var interop=appType.Assembly;var modelType=interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true);var componentType=interop.GetType("SolidWorks.Interop.sldworks.Component2",true);
    var subModel=new Proxy(modelType,call=>call.MethodName=="GetPathName"?Get(sub,"FullPath"):throw new Exception(call.MethodName)).GetTransparentProxy();
    object Parent(bool embedded)=>new Proxy(componentType,call=>call.MethodName=="GetModelDoc2"?subModel:call.MethodName=="GetPathName"?Get(sub,"FullPath"):call.MethodName=="get_IsVirtual"?embedded:call.MethodName=="GetParent"?null:throw new Exception(call.MethodName)).GetTransparentProxy();
    object Component(object parent)=>new Proxy(componentType,call=>call.MethodName=="GetParent"?parent:throw new Exception("删除前不能修改组件："+call.MethodName)).GetTransparentProxy();
    var components=new List<object>{Component(mode.StartsWith("nested")?Parent(false):mode.StartsWith("virtual")?Parent(true):null)};if(mode=="mixed-owners")components.Add(Component(Parent(false)));
    var swconst=Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(assembly.Location),"SolidWorks.Interop.swconst.dll"));var componentSelection=Convert.ToInt32(Enum.Parse(swconst.GetType("SolidWorks.Interop.swconst.swSelectType_e",true),"swSelCOMPONENTS"));
    var selection=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.SelectionMgr",true),call=>{
     switch(call.MethodName){case "GetSelectedObjectCount2":return mode=="empty-selection"?0:components.Count;case "GetSelectedObjectType3":return mode=="face-selection"?2:componentSelection;case "GetSelectedObject6":return components[(int)call.Args[0]-1];default:throw new Exception("删除前不能改变选择："+call.MethodName);}
    }).GetTransparentProxy();
    Delegate handler=null;var removals=0;var writes=new List<string>();
    var model=new AssemblyProxy(modelType,call=>{
     if(call.MethodName=="GetType"&&(call.MethodBase as MethodInfo)?.ReturnType==typeof(System.Type))return modelType;
     if(call.MethodName=="GetPathName")return Get(root,"FullPath");if(call.MethodName=="GetType")return 2;if(call.MethodName=="get_SelectionManager")return selection;
     if(call.MethodName.StartsWith("add_")){if(call.MethodName=="add_DeleteSelectionPreNotify")handler=(Delegate)call.Args[0];return null;}
     if(call.MethodName.StartsWith("remove_")){if(call.MethodName=="remove_DeleteSelectionPreNotify")removals++;return null;}
     writes.Add(call.MethodName);throw new Exception("删除拦截不能保存、开关窗口或获取权限："+call.MethodName);
    }).GetTransparentProxy();
    SetField(plugin,"application",new Proxy(appType,call=>call.MethodName=="get_ActiveDoc"?model:throw new Exception("删除回调不允许网络或原生写入："+call.MethodName)).GetTransparentProxy());
    // Exercise the production event registration and callback, not just the permission predicate.
    TreeCall(plugin,"BindAssemblyEvents");if(handler==null)throw new Exception("未订阅删除前可取消事件");
    var result=(int)handler.DynamicInvoke();var allow=new[]{"owned","nested-owned","virtual-owned","unmanaged","face-selection","empty-selection"}.Contains(mode);
    if(result!=(allow?0:1))throw new Exception("删除前拦截结果不符："+mode+" result="+result);
    TreeCall(plugin,"UnwireAssemblyEvents");if(removals!=1||Field(plugin,"assemblyEventDocument")!=null)throw new Exception("切换窗口后未正确解除删除回调");
    if(writes.Count!=0||before.Any(pair=>WorkingReuseHash(pair.Key)!=pair.Value))throw new Exception("删除回调改写了图档："+mode);
    Console.WriteLine("PASS: delete permission guard "+mode+"; production event wired/unwired; S_FALSE blocks before deletion, no saves/lease requests/file writes");
   }
  }
  finally{bindings.SetValue(null,previousBindings);resolver.SetValue(null,previousResolver);isolated.SetValue(null,previousIsolated);}
 }
}
