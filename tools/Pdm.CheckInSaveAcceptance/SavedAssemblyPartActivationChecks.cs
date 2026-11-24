using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.Remoting.Messaging;

internal static partial class Program
{
    static void SavedAssemblyPartActivationScenario(string output)
    {
        var folder = Path.GetFullPath(output);
        var rootPath = Path.Combine(folder, "总装.SLDASM");
        var partPath = Path.Combine(folder, "目标零件.SLDPRT");
        var references = Enumerable.Range(1, 7).Select(index => Path.Combine(folder, "子装" + index + ".SLDASM")).ToArray();
        var interop = Type("PdmAddin").GetField("application", Flags).FieldType.Assembly;
        foreach (var mode in new[]{"seven-lightweight","no-save","other-save","no-activation","wrong-project","unknown-member",
            "foreign-pre","target-is-loading","dirty-again","busy","api-busy","host-blocked","part-blocked","new-epoch",
            "stamp-change","command-change","removed","unrelated-post","no-resident-part"})
        {
            var plugin = New("PdmAddin"); var project = Guid.NewGuid();
            var root = LinkedNode(folder, Path.GetFileName(rootPath), "Assembly");
            SetField(plugin, "currentTree", root);
            SetField(plugin, "currentProjectId", project);
            SetField(plugin, "currentDocumentIdentity", rootPath);
            var manifest = New("ControlledOpenManifestDto");
            Set(manifest, "RootDocumentId", Get(root,"DocumentId"));
            Set(manifest, "ProjectId", mode == "wrong-project" ? Guid.NewGuid() : project);
            var files = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(Type("ControlledOpenFileDto")));
            foreach (var path in new[]{rootPath,partPath}.Concat(references))
            {
                if (mode == "unknown-member" && path == partPath) continue;
                var file = New("ControlledOpenFileDto"); Set(file, "FileName", Path.GetFileName(path));
                Set(file, "IsRoot", path == rootPath); files.Add(file);
            }
            Set(manifest,"Files",files); ((IDictionary)Field(plugin,"controlledOpenManifests")).Add(folder,manifest);
            var partStamp = 10;
            object Model(string path) => new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true),call =>
            {
                switch(call.MethodName)
                {
                    case "GetPathName": return path;
                    case "GetSaveFlag": return mode == "dirty-again";
                    case "GetUpdateStamp": return path == rootPath ? 42 : partStamp;
                    case "GetBlockingState": return mode == (path == rootPath ? "host-blocked" : "part-blocked") ? 2 : 0;
                    default: throw new Exception("加载收尾不应保存、遍历或解析图档：" + call.MethodName);
                }
            }).GetTransparentProxy();
            var owner = Model(rootPath); var part = Model(partPath); var active = owner;
            SetField(plugin,"assemblyEventDocument",owner); SetField(plugin,"assemblyEventDocumentPath",rootPath);
            var pendingRequest = New("SolidWorksOpenRequest"); SetField(plugin,"pendingControlledOpenRequest",pendingRequest);
            var app = new Proxy(interop.GetType("SolidWorks.Interop.sldworks.SldWorks",true),call =>
            {
                switch(call.MethodName)
                {
                    case "get_StartupProcessCompleted":return true;
                    case "get_CommandInProgress":return mode == "api-busy";
                    case "GetRunningCommandInfo":return new ReturnMessage(null,new object[]{0,"",mode=="busy"},3,call.LogicalCallContext,call);
                    case "get_ActiveDoc":return active;
                    case "GetDocumentCount":return 2;
                    case "GetOpenDocumentByName":return mode != "no-resident-part" && (string)call.Args[0] == partPath ? part : null;
                    default:throw new Exception("收尾不应更改用户窗口：" + call.MethodName);
                }
            }).GetTransparentProxy();
            SetField(plugin,"application",app);
            void Opening(string path)
            {
                if ((int)TreeCall(plugin,"OnFileOpening",path) != 0) throw new Exception("测试事件已被原生文件入口拒绝："+path);
            }
            foreach(var path in references) Opening(path);
            if(mode=="foreign-pre") Opening(Path.Combine(folder,"外部","清单以外的部件.SLDASM"));
            if(mode=="target-is-loading") Opening(partPath);
            if(mode!="no-save") TreeCall(plugin,"OnAssemblyFileSaved",0,mode=="other-save"?partPath:rootPath);
            active=part;
            if(mode!="no-activation") TreeCall(plugin,"OnActiveModelDocumentChanged");
            if(mode=="removed") TreeCall(plugin,"OnAssemblyComponentDeleted",2,"子装1-1");
            var epoch=(int)Field(plugin,"nativeLoadEpoch"); SetField(plugin,"pendingOpenIdleEpoch",epoch);
            SetField(plugin,"nativeIdleSequence",1);
            bool Complete()=>(bool)TreeCall(plugin,"TryCompleteFinishedNativeOpen");
            if(Complete()||Complete())throw new Exception("同一次空闲回调误结束了批量加载："+mode);
            if(mode=="new-epoch") Opening(Path.Combine(folder,"另一次打开.SLDDRW"));
            if(mode=="stamp-change") partStamp++;
            if(mode=="command-change") SetField(plugin,"nativeCommandSequence",4);
            if(mode=="unrelated-post")
            {
                Opening(Path.Combine(folder,"后来打开的工程图.SLDDRW"));
                TreeCall(plugin,"OnFileOpened",Path.Combine(folder,"后来打开的工程图.SLDDRW"));
            }
            SetField(plugin,"nativeIdleSequence",2);
            var recovered=Complete();
            var expect=mode=="seven-lightweight";
            if(recovered!=expect||((int)Field(plugin,"nativeFileOpenInProgress")==0)!=(expect||mode=="unrelated-post"))
                throw new Exception("保存后零件激活的收尾状态有误："+mode);
            if(expect && ((bool)TreeCall(plugin,"IsSolidWorksCommandActive") || (int)Field(plugin,"pendingTreeRefresh")==0))
                throw new Exception("未恢复设计树及排队操作的调度");
            if(!ReferenceEquals(pendingRequest,Field(plugin,"pendingControlledOpenRequest")))throw new Exception("丢失已排队的打开请求");
            Console.WriteLine("PASS: saved assembly to resident part "+mode);
        }
    }
}
