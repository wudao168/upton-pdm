using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Remoting.Messaging;

internal static partial class Program
{
    static void SavedOwnerRemovalScenario(string output)
    {
        var folder=Path.GetFullPath(output);
        var ownerPath=Path.Combine(folder,"70002724.00.SLDASM");
        var target=Path.Combine(folder,"70002724.F2.SLDASM");
        var interop=Type("PdmAddin").GetField("application",Flags).FieldType.Assembly;
        foreach (var mode in new[]{"removed-then-save","save-without-removal","other-project","unlisted","external-same-name",
            "still-lightweight","suppressed","deleted-reference","not-resident","wrong-resident","dirty","owner-blocked","reference-blocked",
            "background-drawing","background-unknown","mate-active","api-busy","source-open","newer-command","later-delete","no-save","wrong-save",
            "newer-epoch","review-only","one-idle","active-switch","fallback-direct","later-wake"})
        {
            var plugin=New("PdmAddin");
            var project=Guid.NewGuid();
            var root=LinkedNode(folder,Path.GetFileName(ownerPath),"Assembly");
            var node=LinkedNode(folder,Path.GetFileName(target),"Assembly");
            Set(node,"Role",Enum.Parse(Type("CadReferenceRole"),"Component"));
            Set(node,"ComponentSelectionName",mode=="fallback-direct"?"":"70002724.F2-1");
            ((IList)Get(root,"Children")).Add(node);
            SetField(plugin,"currentTree",root);SetField(plugin,"currentProjectId",project);SetField(plugin,"currentDocumentIdentity",ownerPath);
            var manifest=New("ControlledOpenManifestDto");
            Set(manifest,"RootDocumentId",Get(root,"DocumentId"));Set(manifest,"ProjectId",mode=="other-project"?Guid.NewGuid():project);
            var files=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(Type("ControlledOpenFileDto")));
            foreach(var path in new[]{ownerPath,target})
            {
                if(mode=="unlisted"&&path==target)continue;
                var file=New("ControlledOpenFileDto");Set(file,"FileName",Path.GetFileName(path));Set(file,"IsRoot",path==ownerPath);files.Add(file);
            }
            Set(manifest,"Files",files);((IDictionary)Field(plugin,"controlledOpenManifests")).Add(folder,manifest);
            var modelType=interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true);
            object ComponentModel(string path)=>new AssemblyProxy(modelType,call=>
            {
                if(call.MethodName=="GetPathName")return path;
                if(call.MethodName=="GetBlockingState")return mode=="reference-blocked"?1:0;
                throw new Exception("不应修改或激活引用："+call.MethodName);
            }).GetTransparentProxy();
            var reference=ComponentModel(target);
            var wrong=ComponentModel(Path.Combine(folder,"other",Path.GetFileName(target)));
            var drawing=ComponentModel(Path.Combine(folder,"占用的工程图.SLDDRW"));
            var resident=mode!="later-wake";
            var component=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.Component2",true),call=>
            {
                switch(call.MethodName)
                {
                    case "GetPathName":return mode=="external-same-name"?Path.Combine(folder,"other",Path.GetFileName(target)):target;
                    case "GetModelDoc2":return mode=="not-resident"||!resident?null:reference;
                    case "GetSuppression2":return mode=="still-lightweight"?1:mode=="suppressed"?0:2;
                    default:throw new Exception("不应解析或修改实例："+call.MethodName);
                }
            }).GetTransparentProxy();
            var owner=new AssemblyProxy(modelType,call=>
            {
                switch(call.MethodName)
                {
                    case "GetPathName":return ownerPath;
                    case "GetSaveFlag":return mode=="dirty";
                    case "GetBlockingState":return mode=="owner-blocked"?1:0;
                    case "GetUpdateStamp":return 30;
                    case "GetComponentByName":return mode=="deleted-reference"?null:component;
                    case "GetComponents":if(!(bool)call.Args[0])throw new Exception("不得遍历整棵装配");return mode=="deleted-reference"?Array.Empty<object>():new[]{component};
                    default:throw new Exception("不应操纵总装："+call.MethodName);
                }
            },mode=="review-only").GetTransparentProxy();
            var active=owner;
            var app=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.SldWorks",true),call=>
            {
                switch(call.MethodName)
                {
                    case "get_StartupProcessCompleted":return true;
                    case "get_CommandInProgress":return mode=="api-busy";
                    case "GetRunningCommandInfo":return new ReturnMessage(null,new object[]{-3,null,mode=="mate-active"},3,call.LogicalCallContext,call);
                    case "get_ActiveDoc":return active;
                    case "get_EnableBackgroundProcessing":return mode.StartsWith("background-");
                    case "GetDocuments":return mode=="background-unknown"?null:new[]{owner,reference,drawing};
                    case "IsBackgroundProcessingCompleted":return false;
                    case "GetOpenDocumentByName":return Equals(call.Args[0],target)?mode=="wrong-resident"?wrong:reference:null;
                    case "GetDocumentCount":return 8;
                    default:throw new Exception("不应操作CAD进程："+call.MethodName);
                }
            }).GetTransparentProxy();
            SetField(plugin,"application",app);SetField(plugin,"assemblyEventDocument",owner);SetField(plugin,"assemblyEventDocumentPath",ownerPath);
            if(mode=="source-open")SetField(plugin,"nativeUiCommandId",2);
            TreeCall(plugin,"OnFileOpening",target);
            if(mode!="save-without-removal")TreeCall(plugin,"OnAssemblyComponentDeleted",2,"QA-R1-1");
            if(mode!="no-save")TreeCall(plugin,"OnAssemblyFileSaved",0,mode=="wrong-save"?target:ownerPath);
            if(mode=="later-delete")TreeCall(plugin,"OnAssemblyComponentDeleted",2,"另一个实例");
            if(mode=="newer-command")SetField(plugin,"nativeCommandSequence",1);
            if(mode=="newer-epoch")TreeCall(plugin,"OnFileOpening",Path.Combine(folder,"extra.SLDDRW"));
            if(mode=="active-switch")active=wrong;
            var epoch=(int)Field(plugin,"nativeLoadEpoch");SetField(plugin,"pendingOpenIdleEpoch",epoch);SetField(plugin,"nativeIdleSequence",1);
            TreeCall(plugin,"ReconcileNativeCommandBeforeUserOperation");
            if((int)Field(plugin,"nativeFileOpenInProgress")==0)throw new Exception("同一次空闲误清状态："+mode);
            if(mode!="one-idle")SetField(plugin,"nativeIdleSequence",2);
            if(mode=="later-wake"){TreeCall(plugin,"TryCompleteFinishedNativeOpen");resident=true;}
            TreeCall(plugin,"ReconcileNativeCommandBeforeUserOperation");
            var expect=mode=="removed-then-save"||mode=="save-without-removal"||mode=="fallback-direct"||mode=="later-wake";
            OwnershipCheck(((int)Field(plugin,"nativeFileOpenInProgress")==0)==expect && (TreeCall(plugin,"ArchiveRequestBusyReason")==null)==expect,
                "删除并保存后的总装存档 "+mode);
        }
    }
}
