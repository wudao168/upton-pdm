using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Remoting.Messaging;

internal static partial class Program
{
    static void DrawingAssociationSafetyScenario(string output)
    {
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        var drawing = Path.Combine(output, "2110038-4.SLDDRW");
        var model = Path.Combine(output, "2110038-4.SLDASM");
        var part = Path.Combine(output, "AB02.SLDPRT");
        foreach (var path in new[] { drawing, model, part }) File.WriteAllText(path, "unchanged");
        var plugin = New("PdmAddin");
        var appType = Type("PdmAddin").GetField("application", Flags).FieldType;
        var reads = 0; var broken = false;
        var app = new Proxy(appType, call => {
            switch (call.MethodName) {
                case "GetType": return appType;
                case "GetOpenDocumentByName": return null;
                case "GetDocumentDependencies2": reads++; if(broken) throw new IOException("fixture unreadable");return new[] { "2110038-4", model };
                case "GetCurrentWorkingDirectory": return output;
                case "SetCurrentWorkingDirectory": return true;
                default: throw new Exception("关联核对不得改变图档：" + call.MethodName);
            }
        }).GetTransparentProxy();
        SetField(plugin,"scanner",New("SolidWorksReferenceTreeScanner",app));
        bool Disproved(string target,bool read) => (bool)TreeCall(plugin,"DrawingAssociationDisproved",drawing,new[]{target},read);
        void Check(bool ok,string text){if(!ok)throw new Exception(text);Console.WriteLine("PASS: "+text);}
        Check(!Disproved(part,false),"无证据时不删除历史关系");
        Check(Disproved(part,true),"总装工程图不能作为 AB02 的配套工程图");
        var count=reads;
        Check(Disproved(part,false)&&reads==count,"后台设计树复用证据，不调用原生依赖扫描");
        Check(!Disproved(model,false),"保留真正直接引用的装配体关系");
        var manifest=New("ControlledOpenManifestDto");
        var files=(System.Collections.IList)Activator.CreateInstance(Property(manifest,"Files").PropertyType);
        var ids=new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var paths=new[] { drawing, model, part };
        for(var i=0;i<paths.Length;i++)
        {
            var file=New("ControlledOpenFileDto");
            Set(file,"DocumentId",ids[i]);
            Set(file,"FileName",Path.GetFileName(paths[i]));
            files.Add(file);
        }
        Set(manifest,"Files",files);
        ((System.Collections.IDictionary)Field(plugin,"controlledOpenManifests")).Add(output+Path.DirectorySeparatorChar,manifest);
        var relations=Array.CreateInstance(Type("DocumentModelDrawingRelationDto"),2);
        for(var i=0;i<2;i++)
        {
            var relation=New("DocumentModelDrawingRelationDto");
            Set(relation,"DrawingDocumentId",ids[0]);
            Set(relation,"ModelDocumentId",ids[i+1]);
            relations.SetValue(relation,i);
        }
        var filtered=((System.Collections.IEnumerable)TreeCall(plugin,"FilterDrawingRelations",relations,output,true,null)).Cast<object>().ToArray();
        Check(filtered.Length==1&&Equals(Get(filtered[0],"ModelDocumentId"),ids[1]),"工作目录尾部分隔符差异不影响直接模型证据核对");
        TreeCall(plugin,"OnDrawingAssociationChanged");
        Check(!Disproved(part,false),"工程图编辑后旧证据失效");
        Disproved(part,true);File.AppendAllText(drawing,"updated");
        Check(!Disproved(part,false),"磁盘变化后旧证据失效");
        broken=true;
        Check(!Disproved(part,true)&&!Disproved(part,false),"读取失败保留待核对状态，不猜测归属");

        var nativeType=assembly.GetReferencedAssemblies().Select(Assembly.Load).Select(a=>a.GetType("SolidWorks.Interop.sldworks.IModelDoc2")).First(t=>t!=null);
        foreach(var mode in new[]{"ok","readonly","wrong-kind","wrong-path","save-failed","warning"})
        {
            var saves=0;
            var native=new Proxy(nativeType,call=>{
                switch(call.MethodName){
                    case "GetType":return call.MethodBase.DeclaringType==typeof(object)?(object)nativeType:mode=="wrong-kind"?1:3;
                    case "GetPathName":return mode=="wrong-path"?part:drawing;
                    case "IsOpenedReadOnly":return mode=="readonly";
                    case "Save3":
                        saves++;Check((int)call.Args[0]==1,"仅保存工程图，不设置 SaveReferenced："+mode);
                        return new ReturnMessage(mode!="save-failed",new object[]{call.Args[0],mode=="save-failed"?1:0,mode=="warning"?2:0},3,call.LogicalCallContext,call);
                    default:throw new Exception("仅保存工程图不得清除模型修改、关闭或另存："+call.MethodName);
                }
            }).GetTransparentProxy();
            var failed=false;
            try{TreeCallStatic("PdmAddin","SaveManagedDocumentOnly",native,drawing);}catch(Exception){failed=true;}
            Check(failed==(mode!="ok"&&mode!="warning")&&saves==(mode=="ok"||mode=="warning"||mode=="save-failed"?1:0),"保存边界与失败结果："+mode);
        }
        Check(File.ReadAllText(model)=="unchanged"&&File.ReadAllText(part)=="unchanged","所有关联核对及保存测试均未改写 3D 文件");
        var parentNode=New("CadTreeNode");Set(parentNode,"FullPath",part);Set(parentNode,"DocumentId",Guid.NewGuid());
        Set(parentNode,"Kind",Enum.Parse(Type("CadDocumentKind"),"Part"));
        var drawingNode=New("CadTreeNode");Set(drawingNode,"FullPath",drawing);Set(drawingNode,"DocumentId",Guid.NewGuid());
        Set(drawingNode,"Kind",Enum.Parse(Type("CadDocumentKind"),"Drawing"));Set(drawingNode,"DrawingViewsRead",true);
        var actualModel=New("CadTreeNode");Set(actualModel,"FullPath",model);Set(actualModel,"DocumentId",Guid.NewGuid());
        ((System.Collections.IList)Get(parentNode,"Children")).Add(drawingNode);
        var actualReferences=(System.Collections.IList)Get(drawingNode,"ReferencedModels");actualReferences.Add(actualModel);
        TreeCallStatic("PdmAddin","BindDesignDrawingFiles",parentNode);
        Check(actualReferences.Count==1&&ReferenceEquals(actualReferences[0],actualModel),"设计树旧父节点不能改写工程图已核实的实际模型引用");
        DrawingSaveCommandScope(output, Check);
        ManagedAssemblySaveScope(output, Check);
    }

    static void DrawingSaveCommandScope(string output, Action<bool,string> check)
    {
        var identity=Type("PdmDocumentIdentityStore");
        var bindings=identity.GetField("bindingDirectory",Flags);var resolver=identity.GetField("workspaceRootResolver",Flags);
        var oldBindings=bindings.GetValue(null);var oldRoot=resolver.GetValue(null);
        bindings.SetValue(null,Path.Combine(output,"bindings"));resolver.SetValue(null,new Func<string>(()=>output));
        try
        {
            var folder=Path.Combine(output,"View","save");Directory.CreateDirectory(folder);
            var path=Path.Combine(folder,"drawing.SLDDRW");File.WriteAllText(path,"managed drawing");
            var id=Guid.NewGuid();check((bool)TreeCallStatic("PdmDocumentIdentityStore","TryWrite",path,id,(Guid?)Guid.NewGuid()),"建立隔离图档身份");
            foreach(var mode in new[]{"owned","unowned","other-user","other-session","lost","save-all","save-as","assembly","unmanaged","busy"})
            {
                var addin=New("PdmAddin");var tree=New("CadTreeNode");
                Set(tree,"Kind",Enum.Parse(Type("CadDocumentKind"),mode=="assembly"?"Assembly":"Drawing"));
                Set(tree,"FullPath",path);Set(tree,"DocumentId",mode=="unmanaged"?(object)null:id);
                Set(tree,"CheckedOutBy",mode=="unowned"?null:mode=="other-user"?"other":"tester");
                Set(tree,"CheckoutSessionId",mode=="other-session"?Guid.NewGuid():Field(addin,"checkoutSessionId"));
                Set(tree,"CheckoutSessionLost",mode=="lost");
                SetField(addin,"currentTree",tree);SetField(addin,"authenticatedUsername","tester");
                if(mode=="busy")SetField(addin,"workspaceOperationInProgress",1);
                var appType=Type("PdmAddin").GetField("application",Flags).FieldType;
                var docType=appType.Assembly.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true);
                var document=new AssemblyProxy(docType,call=>call.MethodName=="GetType"?3:call.MethodName=="GetPathName"?(object)path:throw new Exception(call.MethodName)).GetTransparentProxy();
                var calls=0;
                var app=new Proxy(appType,call=>{calls++;return call.MethodName=="get_ActiveDoc"?document:throw new Exception(call.MethodName);}).GetTransparentProxy();
                SetField(addin,"application",app);
                var command=mode=="save-all"?19:mode=="save-as"?620:2;
                var handled=(bool)TreeCall(addin,"TryQueueManagedSave",command);
                var eligible=mode=="owned"||mode=="assembly"||mode=="unowned"||mode=="other-user"||mode=="other-session"||mode=="lost";
                check(handled==eligible&&(Field(addin,"pendingManagedSave")!=null)==(mode=="owned"||mode=="assembly")
                    &&(eligible||calls==0),"保存入口权限、会话及命令范围："+mode);
            }
        }
        finally{bindings.SetValue(null,oldBindings);resolver.SetValue(null,oldRoot);}
    }

    static void ManagedAssemblySaveScope(string output, Action<bool,string> check)
    {
        var identity=Type("PdmDocumentIdentityStore");var bindings=identity.GetField("bindingDirectory",Flags);var resolver=identity.GetField("workspaceRootResolver",Flags);
        var oldBindings=bindings.GetValue(null);var oldRoot=resolver.GetValue(null);
        bindings.SetValue(null,Path.Combine(output,"scope-bindings"));resolver.SetValue(null,new Func<string>(()=>output));
        try
        {
            foreach(var mode in new[]{"readonly-child","owned-child","other-user","other-session","lost","unmanaged-child","failed-child","wrong-binding","disk-readonly","memory-readonly"})
            {
                var folder=Path.Combine(output,"View",mode);Directory.CreateDirectory(folder);
                var rootPath=Path.Combine(folder,"ROOT.SLDASM");var childPath=Path.Combine(folder,"CHILD.SLDASM");
                File.WriteAllText(rootPath,"root");File.WriteAllText(childPath,"child");
                var addin=New("PdmAddin");SetField(addin,"authenticatedUsername","tester");var project=Guid.NewGuid();
                object Node(string path,bool child)
                {
                    var node=New("CadTreeNode");var id=Guid.NewGuid();Set(node,"FullPath",path);Set(node,"DocumentId",child&&mode=="unmanaged-child"?(object)null:id);
                    Set(node,"Kind",Enum.Parse(Type("CadDocumentKind"),"Assembly"));
                    Set(node,"CheckedOutBy",child&&mode=="readonly-child"?null:child&&mode=="other-user"?"other":"tester");
                    Set(node,"CheckoutSessionId",child&&mode=="other-session"?Guid.NewGuid():Field(addin,"checkoutSessionId"));
                    Set(node,"CheckoutSessionLost",child&&mode=="lost");
                    TreeCallStatic("PdmDocumentIdentityStore","TryWrite",path,child&&mode=="wrong-binding"?Guid.NewGuid():id,(Guid?)project);return node;
                }
                var root=Node(rootPath,false);var child=Node(childPath,true);((System.Collections.IList)Get(root,"Children")).Add(child);SetField(addin,"currentTree",root);
                var nativeType=Type("PdmAddin").GetField("application",Flags).FieldType.Assembly.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true);
                var order=new System.Collections.Generic.List<string>();var dirty=true;object component=null;
                object Model(string path,bool isChild)=>new DiscardAssemblyProxy(nativeType,call=>{
                    switch(call.MethodName){
                        case "GetType":return 2;case "GetPathName":return path;case "GetSaveFlag":return isChild?dirty:true;
                        case "IsOpenedReadOnly":return isChild&&(mode=="readonly-child"||mode=="memory-readonly");
                        case "GetComponents":if(isChild)throw new Exception("Unexpected child scan");return new[]{component};
                        case "Save3":
                            if((int)call.Args[0]!=1)throw new Exception("SaveReferenced would trigger unauthorized reference writes");
                            order.Add(isChild?"child":"root");var ok=!(isChild&&mode=="failed-child");
                            if(ok){File.AppendAllText(path,"|saved");if(isChild)dirty=false;}
                            return new ReturnMessage(ok,new object[]{call.Args[0],ok?0:1,0},3,call.LogicalCallContext,call);
                        default:throw new Exception("Save must not clear flags, change references, close or SaveAs: "+call.MethodName);
                    }
                }).GetTransparentProxy();
                var childDoc=Model(childPath,true);var rootDoc=Model(rootPath,false);
                component=new Proxy(nativeType.Assembly.GetType("SolidWorks.Interop.sldworks.Component2",true),call=>call.MethodName=="GetModelDoc2"?childDoc:throw new Exception(call.MethodName)).GetTransparentProxy();
                if(mode=="disk-readonly"||mode=="readonly-child")File.SetAttributes(childPath,FileAttributes.ReadOnly);
                var failed=false;try{TreeCall(addin,"SaveManagedDocumentScope",rootDoc,root);}catch(Exception error)when(error is InvalidOperationException||error.InnerException is InvalidOperationException){failed=true;}
                var blocked=mode=="wrong-binding"||mode=="disk-readonly"||mode=="memory-readonly";
                var skip=new[]{"readonly-child","other-user","other-session","lost","unmanaged-child"}.Contains(mode);
                check(failed==(blocked||mode=="failed-child")&&string.Join(",",order)==(blocked?"":mode=="failed-child"?"child":skip?"root":"child,root"),"总装保存权限、预检查及子件先保存："+mode);
                check(!skip||dirty&&File.ReadAllText(childPath)=="child","未授权子装配字节和内存修改标记保留："+mode);
                File.SetAttributes(childPath,FileAttributes.Normal);
            }
        }
        finally{bindings.SetValue(null,oldBindings);resolver.SetValue(null,oldRoot);}
    }
}
