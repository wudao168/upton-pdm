using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

internal static partial class Program
{
    static void SavedDrawingPreflightCacheScenario(string folder)
    {
        var node=LinkedNode(folder,"SAVED-PROPERTIES.SLDDRW","Drawing");
        var path=(string)Get(node,"FullPath");
        var plugin=New("PdmAddin");
        var appType=Type("PdmAddin").GetField("application",Flags).FieldType;
        var nativeWrites=new List<string>();
        var drawing=new Model(appType.Assembly.GetType("SolidWorks.Interop.sldworks.IModelDoc2",true),path,false,nativeWrites);
        var opens=0;var closes=0;
        SetField(plugin,"application",new Proxy(appType,call=>{
            switch(call.MethodName)
            {
                case "GetDocuments":return new object[0];
                case "GetOpenDocumentByName":return null;
                case "GetDocumentVisible":return true;
                case "DocumentVisible":return null;
                case "OpenDoc6":
                    TreeCheck((string)call.Args[0]==path&&((int)call.Args[2]&1027)==1027,"属性读取只打开指定工程图的只读出详图副本");
                    opens++;return drawing.GetTransparentProxy();
                case "CloseDoc":
                    TreeCheck((string)call.Args[0]==Path.GetFileName(path),"属性读取只关闭自己打开的工程图");
                    closes++;return null;
                default:throw new Exception("属性核对不应调用："+call.MethodName);
            }
        }).GetTransparentProxy());
        IDictionary ReadProperties(string hash)=>(IDictionary)TreeCall(plugin,"ReadArchivePreflightPropertiesCore",node,null,null,null,hash);
        var initialHash=WorkingReuseHash(path);
        drawing.MemoryProperties["物料名称"]="首次完整读取";
        var initial=ReadProperties(initialHash);
        Set(node,"ArchiveInspectedProperties",initial);
        Set(node,"ArchiveInspectedPropertiesSha256",initialHash);
        drawing.MemoryProperties["物料名称"]="文件变化后重新读取";
        var reused=ReadProperties(initialHash);
        TreeCheck(opens==1&&closes==1&&(string)reused["全局/物料名称"]=="首次完整读取","源指纹相同的重试不重复打开工程图读取属性");
        File.AppendAllText(path,"new saved drawing contents");
        var changedHash=WorkingReuseHash(path);
        var refreshed=ReadProperties(changedHash);
        TreeCheck(opens==2&&closes==2&&(string)refreshed["全局/物料名称"]=="文件变化后重新读取"
            &&WorkingReuseHash(path)==changedHash&&nativeWrites.Count==0,"源指纹变化时重新读取属性并保留工程图字节");
        Console.WriteLine("PASS: saved drawing preflight reuses complete properties only for matching source bytes");
    }

    static async Task SavedDrawingBytesScenario(string output)
    {
        output=Path.GetFullPath(output); Directory.CreateDirectory(output);
        void Check(bool ok,string message){if(!ok)throw new Exception(message);Console.WriteLine("PASS: "+message);}
        foreach(var mode in new[]{"closed","loaded-clean","readonly","cache-changed","dirty","needs-model-update"})
        {
            var folder=Path.Combine(output,mode);Directory.CreateDirectory(folder);
            var path=Path.Combine(folder,"CW0040004.01.01.01-01.SLDDRW");
            File.WriteAllBytes(path,Enumerable.Range(0,3073).Select(i=>(byte)(i%251)).ToArray());
            var original=File.ReadAllBytes(path);var originalHash=WorkingReuseHash(path);
            var node=New("CadTreeNode"); Set(node,"Kind",Enum.Parse(Type("CadDocumentKind"),"Drawing"));
            Set(node,"FullPath",path);Set(node,"FileName",Path.GetFileName(path));Set(node,"InstancePath","DRAWING");
            Set(node,"DocumentId",(Guid?)Guid.NewGuid());Set(node,"Revision","W1");Set(node,"CurrentRevision","W1");
            Set(node,"RequiresModelUpdate",mode=="needs-model-update");Set(node,"IsModifiedInSolidWorks",mode=="dirty");
            var plugin=New("PdmAddin");var appType=Type("PdmAddin").GetField("application",Flags).FieldType;var interop=appType.Assembly;
            var forbidden=0;var loaded=mode=="loaded-clean"||mode=="dirty";
            var model=new AssemblyProxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true),call=>
                call.MethodName=="GetPathName"?path:call.MethodName=="GetSaveFlag"?(object)(mode=="dirty")
                :call.MethodName=="IsOpenedReadOnly"?(object)false:throw new Exception("无需读取或改写模型："+call.MethodName)).GetTransparentProxy();
            var app=new Proxy(appType,call=>{
                if(call.MethodName=="GetDocuments")return loaded?new[]{model}:new object[0];
                if(call.MethodName=="GetOpenDocumentByName")return loaded?model:null;
                if(call.MethodName=="GetDocumentVisible")return true;
                if(call.MethodName=="DocumentVisible")return null;
                forbidden++;throw new Exception("检测到完整打开、重建或重存："+call.MethodName);
            }).GetTransparentProxy();
            SetField(plugin,"application",app);SetField(plugin,"currentTree",node);
            var cards=Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(Type("CadDocumentKind"),typeof(IReadOnlyList<string>)));
            var snapshot=new Dictionary<string,string>{{"全局/名称","已核对的工程图名称"},
                {"UPLM.CustomPropertiesRead","1"},{"UPLM.CustomPropertyConfigurations","[]"}};
            Set(node,"ArchiveInspectedProperties",snapshot);Set(node,"ArchiveInspectedPropertiesSha256",originalHash);
            if(mode=="cache-changed")
            {
                File.AppendAllText(path,"saved update");original=File.ReadAllBytes(path);
                Exception staleFailure=null;
                try { TreeCall(plugin,"ReadArchivePreflightPropertiesCore",node,cards,null,null,WorkingReuseHash(path)); }
                catch(Exception error) { staleFailure=error; }
                Check(staleFailure!=null&&staleFailure.ToString().Contains("未能完整读取图档自定义属性")&&forbidden>0,
                    "源字节改变后必须重新读取属性，无法读取时阻止使用旧索引");
                forbidden=0;
                snapshot=new Dictionary<string,string>(snapshot);
                snapshot["全局/名称"]="文件变化后核对的名称";
                Set(node,"ArchiveInspectedProperties",snapshot);Set(node,"ArchiveInspectedPropertiesSha256",WorkingReuseHash(path));
            }
            if(mode=="readonly")File.SetAttributes(path,FileAttributes.ReadOnly);
            if(!loaded)
            {
                var props=(IDictionary)TreeCall(plugin,"ReadArchivePreflightPropertiesCore",node,cards,null,null,WorkingReuseHash(path));
                Check(forbidden==0,mode+"：预检不调用OpenDoc6/OpenDoc7、不改变图纸窗口");
                Check((string)props["全局/名称"]==snapshot["全局/名称"],mode+"：使用同一份已保存字节的完整属性索引");
            }
            using(var client=(IDisposable)New("PdmApiClient","http://fixture.invalid/"))
            using(var handler=new ReferenceRecoveryHandler(request=>new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("[]")}))
            using(var http=new HttpClient(handler){BaseAddress=new Uri("http://fixture.invalid/")})
            {
                SetField(client,"httpClient",http);SetField(plugin,"apiClient",client);
                object prepared=null;Exception failure=null;
                try{var task=(Task)TreeCall(plugin,"PrepareBatchCheckInFileAsync",node,CancellationToken.None,null,null,cards,null);await task;prepared=Get(task,"Result");}
                catch(Exception error){failure=error;}
                if(mode=="dirty")
                {
                    Check(failure?.GetBaseException().GetType().Name=="ArchivePendingSaveException",
                        "工程图真实未保存修改仍在上传前阻止，不用磁盘旧内容覆盖修改");
                    Check(forbidden==0,"拒绝未保存修改时不打开、保存或关闭其他窗口");
                }
                else if(mode=="needs-model-update")
                {
                    Check(failure!=null&&forbidden>0,"需同步3D变化的工程图仍进入既有更新流程，不被原字节路径绕过");
                }
                else
                {
                    if(failure!=null)throw failure;
                    var copy=(string)Get(prepared,"UploadCopyPath");
                    Check(forbidden==0,mode+"：副本准备同样不完整打开、不激活、不重建或SaveAs");
                    Check(Path.GetFileName(copy)==Path.GetFileName(path)&&File.ReadAllBytes(copy).SequenceEqual(original),mode+"：提交副本保留原文件名及每一个字节");
                    Check(File.ReadAllBytes(path).SequenceEqual(original),mode+"：源文件内容不变");
                    var props=(IDictionary)Get(prepared,"Properties");
                    if(mode=="loaded-clean")Check((string)props["全局/名称"]=="已核对的工程图名称","已核对且内容哈希相同的属性快照被复用");
                    if(mode=="cache-changed")Check((string)props["全局/名称"]=="文件变化后核对的名称","源文件变化后仅提交重新核对的属性");
                    if(mode=="readonly")Check((File.GetAttributes(path)&FileAttributes.ReadOnly)!=0,"共享只读工程图保持只读属性");
                    Type("PdmAddin").GetMethod("DeleteCheckInUploadCopy",Flags).Invoke(null,new object[]{copy});
                }
            }
            Check(File.ReadAllBytes(path).SequenceEqual(original),mode+"：所有分支均未改写原工程图");
            File.SetAttributes(path,FileAttributes.Normal);
        }
        var cachedPath=Path.Combine(output,"已有索引.SLDDRW");File.WriteAllText(cachedPath,"saved drawing bytes");
        var cachedNode=New("CadTreeNode");Set(cachedNode,"Kind",Enum.Parse(Type("CadDocumentKind"),"Drawing"));
        Set(cachedNode,"FullPath",cachedPath);Set(cachedNode,"FileName",Path.GetFileName(cachedPath));
        var cachedPlugin=New("PdmAddin");var cachedVersion=New("DocumentVersionDto");
        Set(cachedVersion,"Id",Guid.NewGuid());Set(cachedVersion,"Sha256",WorkingReuseHash(cachedPath));
        Set(cachedVersion,"FileLength",new FileInfo(cachedPath).Length);
        Set(cachedVersion,"PropertySnapshot",new Dictionary<string,string>{{"全局/名称","已存档且已核对的名称"},
            {"UPLM.CustomPropertiesRead","1"},{"UPLM.CustomPropertyConfigurations","[]"}});
        var cachedProperties=(IDictionary)TreeCall(cachedPlugin,"ReadSavedDrawingProperties",cachedNode,WorkingReuseHash(cachedPath),cachedVersion);
        Check((string)cachedProperties["全局/名称"]=="已存档且已核对的名称","磁盘内容匹配已有版本时直接复用已核对属性索引");
        Check(!((IEnumerable)Get(cachedNode,"ReferenceWarnings")).Cast<string>().Any(),"已有完整索引的相同文件不误报属性待补齐");
        var cachedRoot=New("DocumentReferenceNodeDto");Set(cachedRoot,"ReferenceWarnings",new List<string>{"属性索引待补齐：先前未提取"});Set(cachedVersion,"ReferenceSnapshot",cachedRoot);
        Exception incomplete=null;
        try { TreeCall(cachedPlugin,"ReadSavedDrawingProperties",cachedNode,WorkingReuseHash(cachedPath),cachedVersion); }
        catch(Exception error) { incomplete=error; }
        Check(incomplete!=null&&incomplete.ToString().Contains("未能完整读取图档自定义属性"),
            "原版本带有未完整读取的提醒时，再次读取失败会阻止提交");
    }
}
