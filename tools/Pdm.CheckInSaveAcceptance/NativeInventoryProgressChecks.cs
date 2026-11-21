using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Remoting.Messaging;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static partial class Program
{
    static async Task NativeInventoryProgressScenario(Form host,string output)
    {
        Directory.CreateDirectory(output);output=Path.GetFullPath(output);
        var path=Path.Combine(output,"原生清单样件.SLDASM");File.WriteAllText(path,"isolated native inventory fixture");
        var part=Path.ChangeExtension(path,".SLDPRT");File.WriteAllText(part,"isolated part");
        void Check(bool value,string text){TreeCheck(value,text);Console.WriteLine("PASS: "+text);}
        VerifyNativeInventoryActivation(path,part,Check);
        foreach(var cancel in new[]{false,true})
        {
            var session=Type("ArchiveSession").GetMethod("Start",Flags).Invoke(null,new object[]{host.Handle,CancellationToken.None});
            await (Task)Get(session,"Ready");
            try
            {
                var caller=Thread.CurrentThread.ManagedThreadId;var calls=new List<string>();var threads=new List<int>();
                var appType=Type("PdmAddin").GetField("application",Flags).FieldType;var interop=appType.Assembly;
                object owner=null;
                using(var entered=new ManualResetEventSlim())
                {
                    var pack=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.PackAndGo",true),call=>
                    {
                        calls.Add(call.MethodName);threads.Add(Thread.CurrentThread.ManagedThreadId);
                        if(call.MethodName=="set_IncludeDrawings")
                        {
                            entered.Set();Thread.Sleep(1300);if(cancel)TreeCall(session,"RequestCancellation");return null;
                        }
                        if(call.MethodName=="set_IncludeSuppressed")return null;
                        if(call.MethodName=="GetDocumentNames")return new ReturnMessage(true,new object[]{new[]{path,part}},1,call.LogicalCallContext,call);
                        throw new Exception("未预期的原生清单调用："+call.MethodName);
                    }).GetTransparentProxy();
                    var extension=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDocExtension",true),call=>
                    {
                        if(call.MethodName!="GetPackAndGo")throw new Exception(call.MethodName);
                        calls.Add(call.MethodName);threads.Add(Thread.CurrentThread.ManagedThreadId);return pack;
                    }).GetTransparentProxy();
                    var modelType=interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true);
                    owner=new Proxy(modelType,call=>call.MethodName=="GetType"?modelType:call.MethodName=="GetPathName"?path
                        :call.MethodName=="GetTitle"?Path.GetFileName(path):call.MethodName=="get_Extension"?extension
                        :throw new Exception("不允许修改图档："+call.MethodName)).GetTransparentProxy();
                    var app=new Proxy(appType,call=>
                    {
                        if(call.MethodName=="GetType")return appType;
                        if(call.MethodName=="get_ActiveDoc")return owner;
                        if(call.MethodName=="ActivateDoc3"){calls.Add(call.MethodName);threads.Add(Thread.CurrentThread.ManagedThreadId);return owner;}
                        throw new Exception("不允许额外打开或保存："+call.MethodName);
                    }).GetTransparentProxy();
                    var storage=New("ImportedComponentStorage",app);
                    var probe=Task.Run(async()=>
                    {
                        if(!entered.Wait(4000))throw new TimeoutException("未进入关联工程图原生等待");
                        await Task.Delay(700).ConfigureAwait(false);
                        return ArchiveUi(session,()=>new[]{((Label)Field(ArchiveWindow(session),"stage")).Text,
                            ((Label)Field(ArchiveWindow(session),"file")).Text,((Control)Field(ArchiveWindow(session),"progress")).Text,
                            Convert.ToString(Get(ArchiveWindow(session),"RefreshTicks"))});
                    });
                    string[] files=null;var canceled=false;
                    try{files=(string[])TreeCall(storage,"ReadNativeInventory",owner,true);}
                    catch(System.Reflection.TargetInvocationException error)when(error.InnerException is OperationCanceledException){canceled=true;}
                    var snapshot=await probe;
                    if(cancel)
                    {
                        Check(canceled&&!calls.Contains("set_IncludeSuppressed")&&!calls.Contains("GetDocumentNames"),
                            "取消在当前原生接口返回后停止，不继续下一步、不强行中断COM调用");
                        Check(!(bool)Type("ImportedComponentStorage").GetProperty("IsReadingInventory",Flags).GetValue(null),"取消后释放本轮原生读取标记");
                    }
                    else
                    {
                        Check(calls.SequenceEqual(new[]{"GetPackAndGo","set_IncludeDrawings","set_IncludeSuppressed","GetDocumentNames"}),
                            "原生清单保持工程图和压缩组件范围，目标已活动时不重复激活");
                        Check(threads.All(thread=>thread==caller),"所有SolidWorks调用继续使用原调用STA，未移到界面或线程池");
                        Check(snapshot[0].Contains("检索关联工程图")&&snapshot[1]==Path.GetFileName(path),"长等待时准确显示原生子步骤及所属图档");
                        Check(!snapshot[2].Contains("%")&&int.Parse(snapshot[3])>=2,"无进度回调时不编造百分比，独立窗口持续刷新");
                        Check(ArchiveHistory(session).Contains("获取SolidWorks原生打包清单")&&ArchiveHistory(session).Contains("枚举原生清单中的实际文件")
                            &&files.SequenceEqual(new[]{path,part}),"完整保留各原生步骤过程及返回文件清单");
                        Check(File.ReadAllText(path)=="isolated native inventory fixture", "细分提示和计时不修改原生图档");
                    }
                }
            }
            finally{ArchiveClose(session);await (Task)Get(session,"Closed");((IDisposable)session).Dispose();}
        }
    }

    private static void VerifyNativeInventoryActivation(string path,string part,Action<bool,string> check)
    {
        var appType=Type("PdmAddin").GetField("application",Flags).FieldType;
        var interop=appType.Assembly;
        var modelType=interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true);
        var constants=System.Reflection.Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(assembly.Location),"SolidWorks.Interop.swconst.dll"));
        var rebuildWarning=Convert.ToInt32(Enum.Parse(constants.GetType("SolidWorks.Interop.swconst.swActivateDocError_e",true),"swDocNeedsRebuildWarning"));
        var noRebuild=Convert.ToInt32(Enum.Parse(constants.GetType("SolidWorks.Interop.swconst.swRebuildOnActivation_e",true),"swDontRebuildActiveDoc"));
        foreach(var mode in new[]{"活动总装","切换同名异类型","返回图档错误","实际活动图档错误","清单根图档错误","同名异目录"})
        {
            var activations=new List<string>();
            var packCalls=0;
            object owner=null, active=null;
            var pack=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.PackAndGo",true),call=>
            {
                if(call.MethodName=="set_IncludeDrawings"||call.MethodName=="set_IncludeSuppressed")return null;
                if(call.MethodName=="GetDocumentNames")return new ReturnMessage(true,new object[]{mode=="清单根图档错误"?new[]{part}:new[]{path,part}},1,call.LogicalCallContext,call);
                throw new Exception("读取清单不得保存或修改："+call.MethodName);
            }).GetTransparentProxy();
            var extension=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDocExtension",true),call=>
            {
                if(call.MethodName!="GetPackAndGo")throw new Exception(call.MethodName);
                check(ReferenceEquals(owner,active),"读取原生清单时实际活动窗口与目标一致");
                packCalls++;
                return pack;
            }).GetTransparentProxy();
            object Model(string file)=>new Proxy(modelType,call=>call.MethodName=="GetType"?modelType:call.MethodName=="GetPathName"?file
                :call.MethodName=="GetTitle"?Path.GetFileNameWithoutExtension(file):call.MethodName=="get_Extension"?extension
                :throw new Exception("读取清单不得修改模型："+call.MethodName)).GetTransparentProxy();
            owner=Model(path);
            var other=Model(part);
            var foreign=Model(Path.Combine(Path.GetDirectoryName(path),"其他目录",Path.GetFileName(path)));
            var previous=mode=="活动总装"?owner:other;
            active=previous;
            var app=new Proxy(appType,call=>
            {
                if(call.MethodName=="GetType")return appType;
                if(call.MethodName=="get_ActiveDoc")return active;
                if(call.MethodName=="GetDocuments")return mode=="同名异目录"?new[]{owner,other,foreign}:new[]{owner,other};
                if(call.MethodName!="ActivateDoc3")throw new Exception("读取清单不得打开或保存图档："+call.MethodName);
                var name=(string)call.Args[0];
                activations.Add(name);
                check(Equals(call.Args[1],false)&&Equals(call.Args[2],noRebuild),"激活原生清单图档不执行重建");
                var selectOwner=name==Path.GetFileName(path);
                active=selectOwner&&mode!="实际活动图档错误"?owner:other;
                var result=selectOwner&&mode!="返回图档错误"?owner:other;
                var args=call.Args;
                args[3]=mode=="切换同名异类型"&&selectOwner?rebuildWarning:0;
                return new ReturnMessage(result,args,args.Length,call.LogicalCallContext,call);
            }).GetTransparentProxy();
            var storage=New("ImportedComponentStorage",app);
            string[] files=null;
            Exception failure=null;
            try{files=(string[])TreeCall(storage,"ReadNativeInventory",owner,true);}
            catch(System.Reflection.TargetInvocationException error){failure=error.InnerException;}
            var succeeds=mode=="活动总装"||mode=="切换同名异类型"||mode=="返回图档错误";
            check(succeeds?failure==null&&files.SequenceEqual(new[]{path,part}):failure is InvalidOperationException,"核对原生清单和文档身份："+mode);
            check(ReferenceEquals(active,previous),"清单完成后恢复原活动窗口："+mode);
            if(mode=="活动总装"||mode=="同名异目录")check(activations.Count==0,"已活动或存在同名异目录时不激活："+mode);
            else check(activations[0]==Path.GetFileName(path),"切换时传入含扩展名的文件名："+mode);
            if(mode=="返回图档错误")check(packCalls==1,"收到错误返回对象后仅在重新确认目标为活动窗口时读取一次");
            if(mode=="实际活动图档错误"||mode=="同名异目录")
                check(packCalls==0,"图档身份有冲突时不读取清单："+mode);
        }
    }
}
