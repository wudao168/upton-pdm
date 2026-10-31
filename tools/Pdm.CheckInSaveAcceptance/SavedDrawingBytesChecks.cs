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
            var snapshot=new Dictionary<string,string>{{"全局/名称","已核对的工程图名称"}};
            if(loaded||mode=="cache-changed")
            {
                Set(node,"ArchiveInspectedProperties",snapshot);Set(node,"ArchiveInspectedPropertiesSha256",originalHash);
                if(mode=="cache-changed"){File.AppendAllText(path,"saved update");original=File.ReadAllBytes(path);}
            }
            if(mode=="readonly")File.SetAttributes(path,FileAttributes.ReadOnly);
            if(!loaded)
            {
                var props=(IDictionary)TreeCall(plugin,"ReadArchivePreflightProperties",node,cards,null,null);
                Check(forbidden==0,mode+"：预检不调用OpenDoc6/OpenDoc7、不改变图纸窗口");
                Check(((IEnumerable)Get(node,"ReferenceWarnings")).Cast<string>().Any(value=>value.StartsWith("属性索引待补齐：")),mode+"：属性未完整提取时明确记录待补齐提示");
                var frozen=Type("PdmApiClient").GetMethod("CaptureArchiveSnapshot",Flags).Invoke(null,new object[]{node,new HashSet<string>{path}});
                var root=(Dictionary<string,object>)TreeCall(frozen,"CreateRoot");
                Check(((object[])root["referenceWarnings"]).Cast<string>().Any(value=>value.Contains("属性索引待补齐")),mode+"：待补齐记录随存档快照持久保留");
                Check(!props.Contains("全局/名称"),mode+"：不把未经核对的旧属性声明为当前值");
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
                    Check(failure!=null&&failure.ToString().Contains("未保存"),"工程图真实未保存修改仍在上传前阻止，不用磁盘旧内容覆盖修改");
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
                    if(mode=="cache-changed")Check(!props.Contains("全局/名称"),"源文件变化后不复用旧属性快照");
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
        Set(cachedVersion,"PropertySnapshot",new Dictionary<string,string>{{"全局/名称","已存档且已核对的名称"}});
        var cachedProperties=(IDictionary)TreeCall(cachedPlugin,"ReadSavedDrawingProperties",cachedNode,WorkingReuseHash(cachedPath),cachedVersion);
        Check((string)cachedProperties["全局/名称"]=="已存档且已核对的名称","磁盘内容匹配已有版本时直接复用已核对属性索引");
        Check(!((IEnumerable)Get(cachedNode,"ReferenceWarnings")).Cast<string>().Any(),"已有完整索引的相同文件不误报属性待补齐");
        var cachedRoot=New("DocumentReferenceNodeDto");Set(cachedRoot,"ReferenceWarnings",new List<string>{"属性索引待补齐：先前未提取"});Set(cachedVersion,"ReferenceSnapshot",cachedRoot);
        TreeCall(cachedPlugin,"ReadSavedDrawingProperties",cachedNode,WorkingReuseHash(cachedPath),cachedVersion);
        Check(((IEnumerable)Get(cachedNode,"ReferenceWarnings")).Cast<string>().Any(value=>value.StartsWith("属性索引待补齐：")),"相同字节不能把先前尚未提取的属性索引冒充完整");
    }
}
