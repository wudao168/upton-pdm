using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Remoting.Messaging;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static partial class Program
{
    static async Task ProjectStateRefreshScenario()
    {
        void Check(bool ok,string message){if(!ok)throw new Exception(message);Console.WriteLine("PASS: "+message);}
        var id=Guid.NewGuid();var handler=new ProjectStateHandler(id);
        using(var client=(IDisposable)New("PdmApiClient","http://project-state.invalid/"))
        using(var http=new HttpClient(handler){BaseAddress=new Uri("http://project-state.invalid/")})
        using(var selector=(Control)New("ProjectBrowserControl",false,true))
        {
            SetField(client,"httpClient",http);Set(client,"EditJournalScope","fixture");var plugin=New("PdmAddin");SetField(plugin,"apiClient",client);
            async Task Refresh(CancellationToken token=default(CancellationToken))=>await (Task)Type("PdmAddin").GetMethod("RefreshArchiveProjectsAsync",Flags).Invoke(plugin,new object[]{token});
            object Project()=>((IEnumerable)Field(plugin,"availableProjects")).Cast<object>().Single();
            ListViewItem Row()=> (ListViewItem)Type("ProjectBrowserDialog").GetMethod("CreateProjectItem",Flags).Invoke(null,new[]{Project(),(object)false});
            await Refresh();TreeCall(selector,"SetProjects",Field(plugin,"availableProjects"));TreeCall(selector,"SelectProject",(Guid?)id);
            Check(Row().SubItems[1].Text=="暂无图档","初始无图档使用服务器0计数");
            handler.Count=60;await Refresh();TreeCall(selector,"SetProjects",Field(plugin,"availableProjects"));
            Check(handler.Calls==2&&Row().SubItems[1].Text=="有图档（60）"&&Row().BackColor==Color.FromArgb(232,245,233),"完成存档后再次进入读取60份图档并显示绿色");
            Check((Guid?)Get(selector,"SelectedProjectId")==id,"更新项目元数据保留项目选择");
            handler.Count=0;await Refresh();Check(Row().SubItems[1].Text=="暂无图档"&&Row().BackColor!=Color.FromArgb(232,245,233),"服务器图档被移除时不使用本地累计数量冒充状态");
            handler.Allowed=false;await Refresh();TreeCall(selector,"SetProjects",Field(plugin,"availableProjects"));
            Check((Guid?)Get(selector,"SelectedProjectId")==null&&Row().SubItems[3].Text=="无存档权限","权限变化同次刷新生效");
            var previous=Field(plugin,"availableProjects");handler.Fail=true;var failed=false;
            try{await Refresh();}catch(InvalidOperationException){failed=true;}
            Check(failed&&ReferenceEquals(previous,Field(plugin,"availableProjects")),"刷新失败不捏造状态且停止建立使用旧状态的新存档窗口");
            handler.Fail=false;using(var cancel=new CancellationTokenSource()){handler.BeforeReply=()=>cancel.Cancel();var cancelled=false;try{await Refresh(cancel.Token);}catch(OperationCanceledException){cancelled=true;}Check(cancelled&&ReferenceEquals(previous,Field(plugin,"availableProjects")),"取消刷新不覆盖当前列表");}
            handler.BeforeReply=()=>Set(client,"EditJournalScope","other-company");var changed=false;try{await Refresh();}catch(OperationCanceledException){changed=true;}
            Check(changed&&ReferenceEquals(previous,Field(plugin,"availableProjects")),"账号公司切换后不接受旧请求的项目状态");
        }
    }
    sealed class ProjectStateHandler:HttpMessageHandler
    {
        readonly Guid id;internal int Count,Calls;internal bool Allowed=true,Fail;internal Action BeforeReply;
        internal ProjectStateHandler(Guid id){this.id=id;}
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            if(request.Method!=HttpMethod.Get||request.RequestUri.AbsolutePath!="/api/projects")throw new Exception("Unexpected project request");
            Calls++;BeforeReply?.Invoke();return Task.FromResult(new HttpResponseMessage(Fail?HttpStatusCode.BadRequest:HttpStatusCode.OK){Content=new StringContent(Fail?"{\"message\":\"项目状态读取失败\"}":"[{\"id\":\""+id+"\",\"code\":\"P700016-11\",\"name\":\"8888\",\"documentCount\":"+Count+",\"canReadContent\":true,\"canSubmitArchive\":"+(Allowed?"true":"false")+"}]")});
        }
    }

    static void MirrorAuxiliaryReferenceScenario()
    {
        void Check(bool ok,string message){if(!ok)throw new Exception(message);Console.WriteLine("PASS: "+message);}
        var interop=Type("PdmAddin").GetField("application",Flags).FieldType.Assembly;
        const string parent=@"C:\fixture\PARENT.SLDASM",source=@"C:\fixture\SOURCE.SLDPRT";
        foreach(var mode in new[]{"mirror-plane","ordinary-overlap","assembly-source","not-mirror","known-context"})
        {
            object ProxyFor(string name,Func<IMethodCallMessage,object> callback)=>new Proxy(interop.GetType("SolidWorks.Interop.sldworks."+name,true),callback).GetTransparentProxy();
            var mirror=ProxyFor("MirrorPartFeatureData",call=>call.MethodName=="get_PathName"?(mode=="assembly-source"?parent:source):throw new Exception("Unexpected mirror write/read: "+call.MethodName));
            var feature=ProxyFor("Feature",call=>call.MethodName=="get_Name"?"MirrorA":call.MethodName=="GetTypeName2"?(mode=="not-mirror"?"Stock":"MirrorStock"):call.MethodName=="get_Is3DInterconnectFeature"?(object)false:call.MethodName=="GetDefinition"?mirror:throw new Exception(call.MethodName));
            var manager=ProxyFor("FeatureManager",call=>call.MethodName=="GetFeatures"?new[]{feature}:call.MethodName=="GetFeatureCount"?(object)1:throw new Exception(call.MethodName));
            var sketches=ProxyFor("SketchManager",call=>call.MethodName=="GetSketchBlockDefinitions"?new object[0]:throw new Exception(call.MethodName));
            var extension=ProxyFor("ModelDocExtension",call=>{
                if(call.MethodName!="ListExternalFileReferences2")throw new Exception("Unexpected model mutation");
                var overlap=mode=="ordinary-overlap";
                return new ReturnMessage(null,new object[]{overlap?new[]{source,parent,parent}:new[]{source,parent},new string[overlap?3:2],overlap?new[]{"MirrorA","MirrorA","DerivedB"}:new[]{"MirrorA","MirrorA"},new string[overlap?3:2],overlap?new[]{3,3,3}:new[]{3,3},new string[overlap?3:2],overlap?new[]{"","",""}:new[]{"",mode=="known-context"?"PART<1>":""},new int[overlap?3:2],new string[overlap?3:2]},9,call.LogicalCallContext,call);
            });
            var model=new AssemblyProxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true),call=>call.MethodName=="GetType"?(object)1:call.MethodName=="get_Extension"?extension:call.MethodName=="get_FeatureManager"?manager:call.MethodName=="get_SketchManager"?sketches:call.MethodName=="GetEquationMgr"||call.MethodName=="GetDesignTable"?null:throw new Exception("Unexpected model write: "+call.MethodName)).GetTransparentProxy();
            var evidence=Type("NativeDesignDependencies").GetMethod("ReadCheckIn",Flags).Invoke(null,new[]{model});
            var unknown=((IEnumerable)Get(evidence,"UnverifiedSources")).Cast<string>().ToArray();
            Check(unknown.Contains(parent)==(mode=="mirror-plane"),mode+"：辅助引用与明确文件来源分别处理");
            Check(((IEnumerable)Get(evidence,"Sources")).Cast<string>().Contains(parent)&&((IEnumerable)Get(evidence,"Sources")).Cast<string>().Contains(source),mode+"：保留全部原引用路径");
            var owner=New("CadTreeNode");var back=New("CadTreeNode");Set(owner,"FullPath",@"C:\fixture\MIRROR.SLDPRT");Set(back,"FullPath",parent);Set(back,"SourceReferencePath",parent);Set(back,"Kind",Enum.Parse(Type("CadDocumentKind"),"Assembly"));Set(back,"Role",Enum.Parse(Type("CadReferenceRole"),"ExternalFileDependency"));((IList)Get(owner,"Children")).Add(back);
            Type("PdmAddin").GetMethod("ClassifyReferenceLinks",Flags).Invoke(null,new[]{owner,evidence});
            Check((bool)Get(back,"ReferenceTypeVerified")!=(mode=="mirror-plane")&&(bool)Get(back,"IsContextReference")==(mode=="known-context"),mode+"：不将缺少归属证据的镜向辅助引用误标为已确认循环");
            var root=New("CadTreeNode");Set(root,"FullPath",parent);Set(root,"FileName","PARENT.SLDASM");Set(root,"Kind",Enum.Parse(Type("CadDocumentKind"),"Assembly"));Set(owner,"FileName","MIRROR.SLDPRT");Set(owner,"Kind",Enum.Parse(Type("CadDocumentKind"),"Part"));Set(back,"FileName","PARENT.SLDASM");((IList)Get(root,"Children")).Add(owner);
            var items=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(Type("BatchOperationItem")));items.Add(New("BatchOperationItem",root,0));items.Add(New("BatchOperationItem",owner,1));
            var policy=Activator.CreateInstance(assembly.GetType("Upton.Pdm.Domain.ArchiveWarningPolicy",true));
            void Configure(bool strict){Set(policy,"BlockAll",strict);Type("ArchiveWarningGate").GetMethod("Configure",Flags).Invoke(null,new[]{policy});}
            var validate=Type("ArchiveReferenceCycles").GetMethod("Validate",Flags);Configure(false);
            if(mode=="mirror-plane")
            {
                validate.Invoke(null,new object[]{items});Check((bool)Get(back,"ReferenceTypeUnverified")&&!(bool)Get(back,"IsContextReference"),"镜向辅助回边默认按待核对提示存档，不断链或冒充上下文");
                Configure(true);try{validate.Invoke(null,new object[]{items});throw new Exception("严格策略未生效");}catch(TargetInvocationException error){Check(error.InnerException.Message.Contains("管理员存档警告策略"),"镜向辅助回边服从管理员严格策略");}finally{Configure(false);}
            }
            else if(mode=="known-context") {validate.Invoke(null,new object[]{items});Check(true,"已确认上下文不构成普通文件循环");}
            else {try{validate.Invoke(null,new object[]{items});throw new Exception("真实循环未阻止");}catch(TargetInvocationException error){Check(error.InnerException.Message.Contains("已确认文件依赖存在循环"),mode+"：已确认普通循环仍被拦截");}}
        }
    }
}
