using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Web.Script.Serialization;

internal static class Program
{
    const BindingFlags All = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    static Assembly addin;
    static string root;
    static Type Type(Assembly assembly, string name) => assembly.GetType("Upton.Pdm.SolidWorks." + name, true);
    static object New(string name) => Activator.CreateInstance(Type(addin,name), true);
    static void Set(object value,string name,object data) => value.GetType().GetProperty(name,All).SetValue(value,data);
    static object Get(object value,string name) => value.GetType().GetProperty(name,All).GetValue(value);
    static object Call(Assembly assembly,string type,string method,params object[] args) => Type(assembly,type).GetMethod(method,All).Invoke(null,args);
    static void Check(bool condition,string detail) { if(!condition)throw new Exception(detail);Console.WriteLine("PASS "+detail); }
    static string Hash(string path) { using(var h=SHA256.Create())using(var s=File.OpenRead(path))return BitConverter.ToString(h.ComputeHash(s)).Replace("-",""); }
    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            root=Path.GetFullPath(args[2]); if(Directory.Exists(root))throw new Exception("隔离目录已存在");Directory.CreateDirectory(root);
            var addinPath=Path.GetFullPath(args[0]);
            AppDomain.CurrentDomain.AssemblyResolve+=(_,e)=>{var p=Path.Combine(Path.GetDirectoryName(addinPath),new AssemblyName(e.Name).Name+".dll");return File.Exists(p)?Assembly.LoadFrom(p):null;};
            addin=Assembly.LoadFrom(addinPath);var desktop=Assembly.LoadFrom(Path.GetFullPath(args[1]));
            foreach(var assembly in new[]{addin,desktop})
            {
                Type(assembly,"PdmDocumentIdentityStore").GetField("bindingDirectory",All).SetValue(null,Path.Combine(root,"bindings"));
                Type(assembly,"PdmDocumentIdentityStore").GetField("workspaceRootResolver",All).SetValue(null,(Func<string>)(()=>Path.Combine(root,"workspace")));
            }
            var project=Guid.NewGuid();var id=Guid.NewGuid();var versionId=Guid.NewGuid();
            var source=Path.Combine(root,"source.SLDPRT");File.WriteAllBytes(source,Enumerable.Range(0,4096).Select(i=>(byte)i).ToArray());
            var version=New("DocumentVersionDto");Set(version,"Id",versionId);Set(version,"Sha256",Hash(source));
            var revision=Activator.CreateInstance(version.GetType().GetProperty("Revision").PropertyType,true);Set(revision,"Display","W1");Set(version,"Revision",revision);
            var cache=(string)Call(addin,"PdmAddin","CacheCheckedInVersionInProjectView",Path.Combine(root,"workspace"),"TEST",source,id,project,version);
            Check(!string.IsNullOrEmpty(cache)&&Hash(source)==Hash(cache),"插件缓存与源文件字节一致");
            var unmatched=New("DocumentVersionDto");Set(unmatched,"Sha256",new string('F',64));
            Set(unmatched,"PropertySnapshot",new Dictionary<string,string>{{"FileName",Path.GetFileName(source)},{"LastWriteTimeUtc",File.GetLastWriteTimeUtc(source).ToString("O")}});
            Check(!(bool)Call(addin,"PdmAddin","VersionMatchesLocalFile",unmatched,source,Hash(source)),
                "同名且时间戳相同也不能冒充已存档内容");
            foreach(var assembly in new[]{addin,desktop})
            {
                var request=Activator.CreateInstance(Type(assembly,"WorkspaceDocumentStateRequest"),true);
                Set(request,"DocumentId",id);Set(request,"FileName","source.SLDPRT");Set(request,"LatestRevision","W1");
                var requests=Array.CreateInstance(request.GetType(),1);requests.SetValue(request,0);
                var snapshot=Call(assembly,"WorkspaceLocalStateReader","Read",Path.Combine(root,"workspace"),project,"TEST","QA",requests);
                var state=((IEnumerable)Get(snapshot,"Items")).Cast<object>().Single();
                Console.WriteLine(assembly.GetName().Name+" state="+Get(state,"LocalState"));
                Check((string)Get(state,"LocalState")=="ReadOnlyCache","桌面与插件均认可相同缓存绑定："+assembly.GetName().Name);
            }
            if(args.Length>3&&args[3]=="--cache-only")return 0;
            if(args.Length>4&&args[3]=="--catalog") VerifyCatalog(desktop,args[4],project);
            HeartbeatSafety(source,id,project);
            return 0;
        }
        catch(Exception e){Console.WriteLine(e);return 1;}
    }
    static void VerifyCatalog(Assembly desktop,string catalogPath,Guid project)
    {
        var json=new JavaScriptSerializer { MaxJsonLength=int.MaxValue };
        var catalog=json.Deserialize<Dictionary<string,object>>(File.ReadAllText(catalogPath));
        var vault=Path.GetFullPath((string)catalog["Vault"]).TrimEnd('\\')+"\\";
        var verified=0;
        foreach(Dictionary<string,object> file in (IEnumerable)catalog["Versions"])
        {
            var document=Guid.Parse((string)file["id"]);var fileName=(string)file["file_name"];
            Check(Path.GetFileName(fileName)==fileName,"图档文件名无路径穿越："+fileName);
            var original=Path.GetFullPath(Path.Combine(vault,(string)file["storage_relative_path"]));
            if(!original.StartsWith(vault,StringComparison.OrdinalIgnoreCase))throw new Exception("原件路径越界");
            var source=Path.Combine(root,"catalog-source",document.ToString("N"),fileName);Directory.CreateDirectory(Path.GetDirectoryName(source));File.Copy(original,source);
            var actual=Hash(source);Check(actual==(string)file["sha256"],"服务器原件副本校验："+fileName);
            var target=Path.Combine(root,"workspace","View","TEST","图档缓存",document.ToString("N"),fileName);
            Directory.CreateDirectory(Path.GetDirectoryName(target));File.WriteAllText(target,"earlier local copy");
            var oldVersion=Guid.NewGuid();
            Call(desktop,"PdmDocumentIdentityStore","TryWriteControlledVersion",target,document,(Guid?)project,oldVersion,"W0",Hash(target),new FileInfo(target).Length);
            var read=Type(desktop,"PdmDocumentIdentityStore").GetMethod("TryReadControlledMetadata",All);var readArgs=new object[]{target,null};read.Invoke(null,readArgs);var stale=readArgs[1];
            File.Copy(source,target,true);
            var version=New("DocumentVersionDto");Set(version,"Id",Guid.Parse((string)file["version_id"]));Set(version,"Sha256",actual);
            var revision=Activator.CreateInstance(version.GetType().GetProperty("Revision").PropertyType,true);Set(revision,"Display",(string)file["revision_label"]);Set(version,"Revision",revision);
            Call(addin,"PdmAddin","CacheCheckedInVersionInProjectView",Path.Combine(root,"workspace"),"TEST",source,document,project,version);
            var request=Activator.CreateInstance(Type(desktop,"WorkspaceDocumentStateRequest"),true);Set(request,"DocumentId",document);Set(request,"FileName",fileName);Set(request,"LatestRevision",(string)file["revision_label"]);
            var state=Type(desktop,"WorkspaceLocalStateReader").GetMethod("Evaluate",All).Invoke(null,new[]{request,(object)target,stale,"QA"});
            Check((string)Get(state,"LocalState")=="ReadOnlyCache"&&Hash(target)==actual,"实际CAD副本不受扫描旧快照影响："+fileName);
            verified++;
        }
        Console.WriteLine("PASS 实际服务器CAD副本批量缓存验收 Files="+verified);
    }
    static void HeartbeatSafety(string source,Guid id,Guid project)
    {
        var plugin=New("PdmAddin");var pluginType=plugin.GetType();var session=(Guid)pluginType.GetField("checkoutSessionId",All).GetValue(plugin);
        pluginType.GetField("authenticatedUsername",All).SetValue(plugin,"QA");
        var node=New("CadTreeNode");Set(node,"FullPath",source);Set(node,"DocumentId",(Guid?)id);
        Set(node,"CheckedOutBy","QA");Set(node,"CheckoutSessionId",(Guid?)session);Set(node,"CheckedOutAt",(DateTime?)DateTime.UtcNow.AddMinutes(-1));
        var document=New("DocumentDto");Set(document,"Id",id);Set(document,"ProjectId",project);Set(document,"CheckedOutBy","QA");Set(document,"CheckoutSessionId",(Guid?)session);
        pluginType.GetMethod("ApplyPreparedDocumentToMatchingInstances",All).Invoke(plugin,new[]{node,document,(object)false});
        var before=Hash(source);
        bool Lost(DateTime time)=>(bool)pluginType.GetMethod("ApplyLostCheckoutSession",All).Invoke(plugin,new[]{node,(object)time});
        Check(!Lost(DateTime.UtcNow)&&(File.GetAttributes(source)&FileAttributes.ReadOnly)==0,"临时登记不会被旧心跳锁成只读");
        ((ISet<Guid>)pluginType.GetField("pendingArchiveDocumentIds",All).GetValue(plugin)).Clear();
        Set(node,"CheckedOutAt",(DateTime?)DateTime.UtcNow.AddMinutes(1));
        Check(!Lost(DateTime.UtcNow),"旧心跳不能覆盖后取得的编辑权限");
        Set(node,"CheckedOutAt",(DateTime?)DateTime.UtcNow.AddMinutes(-1));Set(node,"CheckoutSessionId",null);
        Check(!Lost(DateTime.UtcNow),"已结束编辑不被旧响应改成他人编辑");
        Set(node,"CheckoutSessionId",(Guid?)session);
        Check(Lost(DateTime.UtcNow)&&(File.GetAttributes(source)&FileAttributes.ReadOnly)!=0,"正式图档真实权限丢失仍受只读保护");
        Check(Hash(source)==before,"权限变化不修改文件内容");
    }
}
