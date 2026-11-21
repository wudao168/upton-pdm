using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Remoting.Messaging;
using System.Threading;
using System.Threading.Tasks;

internal static partial class Program
{
    private sealed class DrawingSheetFixture
    {
        internal readonly object Node, Document;
        internal readonly Dictionary<string,int> Rebuilds = new Dictionary<string,int>();
        internal int DisplayRebuilds;
        internal int LastDocumentStatusAt;
        internal int WalksAfterDuplicate;
        internal int Copies;
        internal string CurrentSheet = "Model";
        internal bool Modified;
        private readonly string mode;
        private readonly bool readOnly;
        private readonly string[] sheets = { "Model", "图纸2" };
        private readonly object sheet, sheetView, view, secondView, extension, referenced, foreignReference;
        private readonly string rootPath;
        private readonly DrawingModelDiagnostics diagnostics;

        internal DrawingSheetFixture(Assembly interop, object node, DrawingModelDiagnostics diagnostics, string rootPath, string mode,bool readOnly)
        {
            this.mode = mode;
            this.readOnly=readOnly;
            Node = node;
            this.diagnostics=diagnostics;
            this.referenced = diagnostics.Model;
            this.rootPath=rootPath;
            var type = interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2", true);
            var foreignPath=Path.Combine(Path.GetDirectoryName(rootPath),"另一个目录",Path.GetFileName(rootPath));
            foreignReference=new Proxy(type,call=>call.MethodName=="GetPathName"?foreignPath:throw new Exception(call.MethodName)).GetTransparentProxy();
            view = new AssemblyProxy(interop.GetType("SolidWorks.Interop.sldworks.View", true), ReadView).GetTransparentProxy();
            if(mode=="model-diagnostic-same-feature-repeated")
            {
                secondView=new AssemblyProxy(interop.GetType("SolidWorks.Interop.sldworks.View", true),call=>
                {
                    if(call.MethodName=="GetHashCode")return 3;
                    if(call.MethodName=="Equals")return ReferenceEquals(secondView,call.Args[0]);
                    if(call.MethodName=="GetNextView")return null;
                    return ReadView(call);
                }).GetTransparentProxy();
            }
            sheetView = new AssemblyProxy(interop.GetType("SolidWorks.Interop.sldworks.View", true), call =>
            {
                if(call.MethodName=="get_Type")return 1;
                if(call.MethodName=="GetHashCode")return 1;
                if(call.MethodName=="Equals")return ReferenceEquals(sheetView,call.Args[0]);
                if(call.MethodName=="GetNextView")return mode=="no-view-proof"&&RecoverySheet?null:view;
                throw new Exception("意外的图纸容器视图调用 "+call.MethodName);
            }).GetTransparentProxy();
            sheet = new Proxy(interop.GetType("SolidWorks.Interop.sldworks.Sheet", true), ReadSheet).GetTransparentProxy();
            extension = new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDocExtension", true), ExtensionCall).GetTransparentProxy();
            Document = new SheetReferenceModelProxy(type, true, ReadDrawing, true).GetTransparentProxy();
        }

        private object ReadSheet(IMethodCallMessage call)
        {
            switch (call.MethodName)
            {
                case "GetName": return CurrentSheet;
                case "GetViews": return mode=="no-view-proof"&&RecoverySheet?new object[0]:new[] { view };
                default: throw new Exception("意外的图纸页调用 "+call.MethodName);
            }
        }
        private object ReadView(IMethodCallMessage call)
        {
            switch (call.MethodName)
            {
                case "get_Type":return 4;
                case "GetHashCode":return 2;
                case "Equals":return ReferenceEquals(view,call.Args[0]);
                case "GetNextView":
                    if(secondView!=null)return secondView;
                    if(mode!="cyclic-views"||!RecoverySheet)return null;
                    if(++WalksAfterDuplicate>1)throw new Exception("重复视图的链表被遍历了第二次");
                    return view;
                case "get_ReferencedDocument":
                    if(RecoverySheet&&mode=="unloaded-view")return null;
                    if(RecoverySheet&&mode=="external-view")return foreignReference;
                    return referenced;
                case "GetReferencedModelName":return rootPath;
                case "get_ReferencedConfiguration": return RecoverySheet&&mode=="other-view-configuration"?"其他配置":"Default";
                case "get_Name":case "GetName2": return "模型视图";
                case "IsModelOutOfDate": return mode=="still-outdated" && RecoverySheet;
                default: throw new Exception("意外的视图调用 "+call.MethodName);
            }
        }
        private object ExtensionCall(IMethodCallMessage call)
        {
            if(diagnostics.IsScenario&&(call.MethodName=="GetWhatsWrong"||call.MethodName=="GetWhatsWrongCount"))
                return diagnostics.ReadIssues(call,true);
            if(call.MethodName=="Rebuild")
            {
                TreeCheck(Convert.ToInt32(call.Args[0])==8,"须限定重建当前工程图纸页");
                DisplayRebuilds++;
                return mode!="both-false-current"&&mode!="native-error"&&mode!="no-view-proof";
            }
            if(call.MethodName=="GetWhatsWrongCount")
                return mode is "native-error" or "display-true-native-error" or "warnings-only" or "warnings-incomplete" or "error-read-failed"?2:0;
            if(call.MethodName=="GetWhatsWrong")
            {
                var warnings=mode=="warnings-incomplete"?new[]{true}:new[]{true,mode is "warnings-only" or "error-read-failed"};
                return new ReturnMessage(mode!="error-read-failed",new object[]{new object[2],new[]{2,3},warnings},
                    3,call.LogicalCallContext,call);
            }
            if(call.MethodName=="get_NeedsRebuild2")
            {
                LastDocumentStatusAt=Rebuilds.Count;
                return mode=="needs-rebuild"||mode=="pending-until-last-sheet"&&!Rebuilds.ContainsKey("图纸2")?1:0;
            }
            if (call.MethodName!="SaveAs3") throw new Exception("意外的源文件调用 "+call.MethodName);
            Copies++;call.Args[5]=0;call.Args[6]=0;
            File.WriteAllText((string)call.Args[0],"updated drawing views "+Get(Node,"FileName"));
            Modified=false;
            return true;
        }
        private bool RecoverySheet=>CurrentSheet=="图纸2"&&Rebuilds.ContainsKey(CurrentSheet);
        private object ReadDrawing(IMethodCallMessage call)
        {
            switch (call.MethodName)
            {
                case "GetType": return 3;
                case "GetPathName": return Get(Node,"FullPath");
                case "GetTitle": return Get(Node,"FileName");
                case "GetUpdateStamp": return 1;
                case "GetSaveFlag": return Modified;
                case "SetSaveFlag": Modified=true;return null;
                case "IsOpenedReadOnly": return readOnly;
                case "GetViews": return sheets.Select(name=>secondView==null?new object[]{null,view}:new[]{null,view,secondView}).ToArray();
                case "GetCurrentSheet": return sheet;
                case "GetFirstView": return sheetView;
                case "GetSheetNames": return sheets;
                case "ActivateSheet":
                    var target=(string)call.Args[0];
                    if (target==CurrentSheet || (mode=="cannot-activate" && target=="图纸2")) return false;
                    if(mode=="wrong-active-sheet"&&target=="图纸2")return true;
                    CurrentSheet=target;
                    return mode!="switch-reported-false";
                case "ForceRebuild3":
                    diagnostics.RebuildStarted=true;
                    Rebuilds[CurrentSheet]=Rebuilds.TryGetValue(CurrentSheet,out var calls)?calls+1:1;
                    Modified=true;
                    if(mode=="pending-until-last-sheet")return CurrentSheet=="图纸2";
                    return CurrentSheet!="图纸2"||mode=="current-sheet"||mode=="switch-reported-false";
                case "get_Extension": return extension;
                case "get_SelectionManager": return null;
                case "add_ModifyNotify":case "remove_ModifyNotify":case "add_DestroyNotify2":case "remove_DestroyNotify2":
                case "add_FileReloadNotify":case "remove_FileReloadNotify": return null;
                default: throw new Exception("意外的原工程图操作 "+call.MethodName);
            }
        }
    }

    static async Task DrawingSheetBatchScenario(string output)
    {
        output=Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        var lockedFile=Path.Combine(output,"锁定工程图.SLDDRW");
        using(var lockOwner=new FileStream(lockedFile,FileMode.Create,FileAccess.ReadWrite,FileShare.None))
        {
            IOException failure=null;
            try{File.ReadAllBytes(lockedFile);}
            catch(IOException error){failure=error;}
            TreeCheck(failure!=null&&((string)TreeCallStatic("PdmAddin","ArchivePreflightError",failure)).Contains("占用"),"真实共享冲突仍应提示文件占用");
        }
        Console.WriteLine("PASS: 独占文件的真实共享冲突仍可识别");
        var identity=Type("PdmDocumentIdentityStore");var resolver=identity.GetField("workspaceRootResolver",Flags);
        var bindings=identity.GetField("bindingDirectory",Flags);var isolated=identity.GetField("isolatedReadScope",Flags);
        var oldResolver=resolver.GetValue(null);var oldBindings=bindings.GetValue(null);var oldIsolated=isolated.GetValue(null);
        resolver.SetValue(null,new Func<string>(()=>output));bindings.SetValue(null,Path.Combine(output,"bindings"));isolated.SetValue(null,true);
        try{await RunDrawingSheetBatchAsync(output);}
        finally{resolver.SetValue(null,oldResolver);bindings.SetValue(null,oldBindings);isolated.SetValue(null,oldIsolated);}
    }

    static async Task RunDrawingSheetBatchAsync(string output)
    {
        var names=new[]{"70002727.00-专利01.SLDDRW","70002727.00-专利02.SLDDRW","P701242-6-时序图2.SLDDRW","P701242-6-时序图3.SLDDRW"};
        foreach(var mode in new[]{"current-sheet","switch-reported-false","display-rebuilt","both-false-current",
            "cannot-activate","wrong-active-sheet","still-outdated","needs-rebuild","unloaded-view","external-view",
            "native-error","no-view-proof","other-view-configuration","wrong-active-drawing","one-drawing-failed",
            "display-true-native-error","warnings-only","warnings-incomplete","error-read-failed","pending-until-last-sheet","cyclic-views"}.Concat(DrawingModelDiagnostics.Cases))
        {
            var folder=Path.Combine(Path.GetFullPath(output),mode);
            Directory.CreateDirectory(folder);
            var root=LinkedNode(folder,"70002727.00.SLDASM","Assembly");
            var plugin=New("PdmAddin");var project=Guid.NewGuid();var session=(Guid)Field(plugin,"checkoutSessionId");
            SetField(plugin,"currentTree",root);SetField(plugin,"currentProjectId",project);SetField(plugin,"authenticatedUsername","tester");
            var drawings=names.Select(name=>LinkedNode(folder,name,"Drawing")).ToArray();
            foreach(var node in new[]{root}.Concat(drawings))
            {
                Set(node,"CheckedOutBy","tester");Set(node,"CheckoutSessionId",session);
                if(node==root)continue;
                ((IList)Get(root,"Children")).Add(node);
                ((IList)Get(node,"ReferencedModels")).Add(root);Set(node,"RequiresModelUpdate",true);
            }
            var nodes=new[]{root}.Concat(drawings).ToArray();
            var versions=nodes.ToDictionary(node=>(Guid)Get(node,"DocumentId"),node=>LinkedVersion(node,LinkedSnapshot(node)));
            var inserted=LinkedNode(folder,"新增零件.SLDPRT","Part");
            ((IList)Get(root,"Children")).Add(inserted);
            File.AppendAllText((string)Get(root,"FullPath")," saved assembly after inserting a part");
            var originals=nodes.Concat(new[]{inserted}).ToDictionary(node=>(string)Get(node,"FullPath"),node=>WorkingReuseHash((string)Get(node,"FullPath")));
            var appType=Type("PdmAddin").GetField("application",Flags).FieldType;
            var interop=appType.Assembly;
            var diagnostics=new DrawingModelDiagnostics(interop,(string)Get(root,"FullPath"),mode);
            var nativeRoot=diagnostics.Model;
            var sheets=drawings.Select((drawing,index)=>new DrawingSheetFixture(interop,drawing,diagnostics,(string)Get(root,"FullPath"),
                mode=="one-drawing-failed"?(index==1?"still-outdated":"current-sheet"):mode,mode=="current-sheet"||diagnostics.IsScenario)).ToArray();
            object active=nativeRoot;
            var app=new Proxy(appType,call=>{
                switch(call.MethodName)
                {
                    case "GetType":return appType;
                    case "IsSame":return diagnostics.SameNativeObject(call.Args[0],call.Args[1])?1:0;
                    case "GetDocuments":return new[]{diagnostics.ForeignModel,nativeRoot}.Where(model=>model!=null).Concat(sheets.Select(drawing=>drawing.Document)).ToArray();
                    case "get_ActiveDoc":return active;
                    case "GetCurrentWorkingDirectory":return folder;
                    case "SetCurrentWorkingDirectory":return true;
                    case "ActivateDoc3":
                        call.Args[3]=0;
                        var target=sheets.SingleOrDefault(drawing=>string.Equals((string)Get(drawing.Node,"FileName"),Path.GetFileName((string)call.Args[0]),StringComparison.OrdinalIgnoreCase))?.Document??nativeRoot;
                        diagnostics.RebuildStarted=false;
                        if(mode!="wrong-active-drawing")active=target;
                        return target;
                    default:throw new Exception("意外的总装或窗口调用 "+call.MethodName);
                }
            }).GetTransparentProxy();
            SetField(plugin,"application",app);SetField(plugin,"scanner",New("SolidWorksReferenceTreeScanner",app));
            using(var handler=new LinkedArchiveHandler(nodes,versions,project,session,new List<string>(),false))
            using(var client=(IDisposable)New("PdmApiClient","http://drawing-sheets.invalid/"))
            {
                LinkedClient(plugin,client,handler);
                var plan=New("PdmAddin+BatchCheckInPlan",LinkedItems(nodes),0,null);
                Exception failure=null;
                try{await (Task)TreeCall(plugin,"UpdateLinkedDrawingsAsync",plan,project,CancellationToken.None);}
                catch(InvalidOperationException error){failure=error;}
                var succeeds=mode is "current-sheet" or "switch-reported-false" or "display-rebuilt" or "both-false-current" or "warnings-only" or "pending-until-last-sheet"
                    or "model-diagnostic-inherited" or "model-diagnostic-same-feature-repeated" or "model-diagnostic-equivalent-wrappers";
                if(succeeds&&failure!=null)throw new Exception("四张工程图应能在已活动的 Model 页更新",failure);
                if(!succeeds)
                {
                    var failedNames=mode=="one-drawing-failed"?new[]{names[1]}:names;
                    var documentIssue=diagnostics.IsScenario||mode is "needs-rebuild" or "native-error" or "display-true-native-error" or "warnings-incomplete" or "error-read-failed";
                    TreeCheck(failure!=null&&failure.Message.Contains("尚未上传或升版")&&!failure.Message.Contains("被占用")
                        &&names.All(name=>failure.Message.Contains(name)==failedNames.Contains(name))&&(mode=="wrong-active-drawing"||documentIssue&&failure.Message.Contains("重建")||failure.Message.Contains("图纸2")),
                        "未保留实际失败的工程图名称及图纸页："+failure?.Message);
                    if(mode is "native-error" or "display-true-native-error")TreeCheck(failure.Message.Contains("含有重建错误"),"真实特征错误未与不可读取的诊断区分");
                    if(mode is "warnings-incomplete" or "error-read-failed")TreeCheck(failure.Message.Contains("未能确认"),"缺失的原生诊断未被识别");
                    if(diagnostics.IsScenario)TreeCheck(failure.Message.Contains("70002727.M1-1")&&failure.Message.Contains(mode=="model-diagnostic-code-changed"?"52":"51"),"新错误未指出真实特征与代码："+failure.Message);
                }
                foreach(var drawing in sheets)
                {
                    var copyReady=succeeds||mode=="one-drawing-failed"&&Get(drawing.Node,"FileName").ToString()!=names[1];
                    TreeCheck(drawing.CurrentSheet=="Model"&&drawing.Copies==(copyReady?1:0),"图纸页恢复或提交副本次数错误 "+mode);
                    if(succeeds)TreeCheck(drawing.Rebuilds.Count==2&&TreeCall(plugin,"MemoryCopy",drawing.Node)!=null,"漏更新工程图图纸页或缺少提交副本");
                    if(succeeds)TreeCheck(drawing.DisplayRebuilds==(mode is "current-sheet" or "switch-reported-false"?0:1),"再次重建的页数与实际失败页不符");
                    if(mode=="pending-until-last-sheet")TreeCheck(drawing.LastDocumentStatusAt==2,"未在后续纸页更新完成后核对全图状态");
                    if(mode=="cyclic-views")TreeCheck(drawing.WalksAfterDuplicate==1&&failure.Message.Contains("视图列表"),"遇到重复视图未停止或未指出视图列表异常");
                }
                TreeCheck(ReferenceEquals(active,nativeRoot)&&originals.All(pair=>WorkingReuseHash(pair.Key)==pair.Value),"工程图更新影响原总装窗口或源图档");
                TreeCall(plugin,"PruneArchiveMemoryCopies",true);
                Console.WriteLine("PASS: 变更总装的四张配套工程图 "+mode);
            }
        }
    }
}
