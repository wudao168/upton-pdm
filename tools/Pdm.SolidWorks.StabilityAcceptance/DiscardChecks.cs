using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using SolidWorks.Interop.sldworks;

internal static partial class Program
{
    private static void DiscardNativeEdit(SldWorks app, Assembly plugin, string copies, string root, string qa)
    {
        ControlledIsolation(app,plugin,copies,root,qa);
        var source=Directory.GetFiles(Path.Combine(qa,"downloaded"),"*.SLDPRT").First();
        var bytes=File.ReadAllBytes(source);var originalHash=Hash(source);
        int errors=0,warnings=0;
        foreach(var path in Directory.GetFiles(Path.Combine(qa,"downloaded"))) File.SetAttributes(path,FileAttributes.ReadOnly);
        var owner=app.OpenDoc6(Path.Combine(qa,"downloaded",Path.GetFileName(root)),2,3,"",ref errors,ref warnings);
        if(owner==null||errors!=0)throw new IOException("真实只读总装未打开："+errors);
        File.SetAttributes(source,FileAttributes.Normal);
        var model=app.OpenDoc6(source,1,1,"",ref errors,ref warnings);
        if(model==null||errors!=0)throw new IOException("真实零件未打开："+errors);
        if(model.IsOpenedReadOnly()&&!model.SetReadOnlyState(false))throw new IOException("隔离零件未进入编辑状态");
        app.ActivateDoc3(model.GetTitle(),false,0,ref errors);
        model.SketchManager.Insert3DSketch(true);
        if(model.SketchManager.CreateLine(0,0,0,0.0123,0.0234,0.0345)==null)throw new IOException("真实草图未创建");
        model.SketchManager.Insert3DSketch(true);
        if(!model.Save3(1,ref errors,ref warnings)||errors!=0||Hash(source)==originalHash)throw new IOException("隔离零件修改未保存");
        model.SetSaveFlag(); // Cover a still-open editor with pending in-memory changes as well.
        var otherPath=Directory.GetFiles(Path.Combine(qa,"downloaded"),"*.SLDPRT").First(path=>path!=source);
        File.SetAttributes(otherPath,FileAttributes.Normal);
        var other=app.OpenDoc6(otherPath,1,1,"",ref errors,ref warnings);
        if(other==null||errors!=0)throw new IOException("其他零件未打开");
        if(other.IsOpenedReadOnly())other.SetReadOnlyState(false);
        other.Extension.CustomPropertyManager[""].Add3("UPLM_QA_PreserveOtherEdit",30,"其他零件未提交的修改",1);
        other.SetSaveFlag();
        object New(string name,params object[] args)=>Activator.CreateInstance(plugin.GetType("Upton.Pdm.SolidWorks."+name,true),
            Members&~BindingFlags.Static,null,args,null);
        void Set(object value,string name,object data)=>value.GetType().GetProperty(name,Members).SetValue(value,data);
        void Field(object value,string name,object data)=>value.GetType().GetField(name,Members).SetValue(value,data);
        var manifest=New("ControlledOpenManifestDto");var file=New("ControlledOpenFileDto");
        var id=Guid.NewGuid();var project=Guid.NewGuid();var version=Guid.NewGuid();
        Set(manifest,"Id",Guid.NewGuid());Set(manifest,"ProjectId",project);Set(manifest,"ProjectCode","DISCARD-NATIVE");
        Set(manifest,"RootDocumentId",id);Set(manifest,"RootVersionId",version);Set(manifest,"RootRelativePath",Path.GetFileName(source));
        Set(file,"DocumentId",id);Set(file,"VersionId",version);Set(file,"FileName",Path.GetFileName(source));
        Set(file,"RelativePath",Path.GetFileName(source));Set(file,"Sha256",originalHash);Set(file,"FileLength",(long)bytes.Length);Set(file,"Revision","W1");
        var files=(IList)Activator.CreateInstance(manifest.GetType().GetProperty("Files").PropertyType);files.Add(file);Set(manifest,"Files",files);
        plugin.GetType("Upton.Pdm.SolidWorks.PdmDocumentIdentityStore",true).GetMethod("TryWriteControlledVersion",Members)
            .Invoke(null,new object[]{source,id,project,version,"W1",originalHash,(long)bytes.Length});
        using(var client=(IDisposable)New("PdmApiClient","http://native-discard.invalid/"))
        using(var host=new Form{ShowInTaskbar=false,Opacity=0})
        {
            var http=new HttpClient(new NativeDiscardDownload(bytes)){BaseAddress=new Uri("http://native-discard.invalid/")};
            http.DefaultRequestHeaders.Authorization=new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer","isolated");
            ((HttpClient)client.GetType().GetField("httpClient",Members).GetValue(client)).Dispose();
            Field(client,"httpClient",http);
            var addin=New("PdmAddin");
            Field(addin,"application",app);Field(addin,"apiClient",client);Field(addin,"lifetime",new CancellationTokenSource());
            Field(addin,"controlledWorkspace",New("ControlledWorkspaceManager",client,qa));
            Field(addin,"authenticatedUsername","tester");
            Field(addin,"editJournal",New("EditDraftJournal",Path.Combine(qa,"journal"),"native"));
            Field(addin,"editJournalScope",(string)Get(client,"EditJournalScope")+"|tester");
            var node=New("CadTreeNode");Set(node,"DocumentId",id);Set(node,"FullPath",source);Set(node,"FileName",Path.GetFileName(source));
            addin.GetType().GetMethod("MarkDiscardedLocalEdit",Members).Invoke(addin,new[]{node});
            var previous=SynchronizationContext.Current;
            var handle=host.Handle;
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            string otherHash;
            try
            {
                var blocked=false;
                try { Pump((Task)addin.GetType().GetMethod("RestoreDiscardedEditsAsync",Members).Invoke(addin,new[]{manifest})); }
                catch(InvalidOperationException e){blocked=e.Message.Contains("其他未保存修改");}
                if(!blocked||app.GetOpenDocumentByName(source)==null||!other.GetSaveFlag())
                    throw new IOException("放弃子件时没有保护其他零件的未保存修改");
                if(!other.Save3(1,ref errors,ref warnings)||errors!=0)throw new IOException("其他零件的修改未保存");
                otherHash=Hash(otherPath);
                Pump((Task)addin.GetType().GetMethod("RestoreDiscardedEditsAsync",Members).Invoke(addin,new[]{manifest}));
                if(Hash(otherPath)!=otherHash)throw new IOException("恢复子件时覆盖了其他零件的修改");
            }
            finally{SynchronizationContext.SetSynchronizationContext(previous);}
            if(app.GetOpenDocumentByName(source)!=null||Hash(source)!=originalHash)
                throw new IOException("放弃后真实CAD未关闭或工作文件仍为修改版");
            model=app.OpenDoc6(source,1,3,"",ref errors,ref warnings);
            if(model==null||errors!=0||!model.IsOpenedReadOnly())throw new IOException("恢复后的真实零件无法只读打开");
            Report["nativeDiscard"]=new { passed=true, file=Path.GetFileName(source), restoredSha256=Hash(source),
                baselineSha256=originalHash, realSavedSketchDiscarded=true, closedDirtyEditor=true, reopenedReadOnly=true,
                readOnlyParentContext=true, unrelatedUnsavedEditProtected=true, unrelatedSavedFilePreserved=true };
            Console.WriteLine("PASS native explicit discard: saved CAD edit restored to original bytes and reopened");
            app.CloseAllDocuments(true);
        }
    }

    private sealed class NativeDiscardDownload:HttpMessageHandler
    {
        private readonly byte[] bytes;
        public NativeDiscardDownload(byte[] bytes){this.bytes=bytes;}
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            if(request.Method!=HttpMethod.Get||!request.RequestUri.AbsolutePath.EndsWith("/file"))throw new IOException("隔离测试拒绝其他请求");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)});
        }
    }
}
