using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Web.Script.Serialization;

internal static partial class Program
{
    static void ArchiveSubmissionGraphScenario(string output,string statePath,bool audit)
    {
        Directory.CreateDirectory(output);output=Path.GetFullPath(output);
        var json=new JavaScriptSerializer{MaxJsonLength=int.MaxValue,RecursionLimit=512};
        var snapshotType=Type("PdmApiClient+ArchiveReferenceSnapshot");
        var capture=Type("PdmApiClient").GetMethod("CaptureArchiveSnapshot",Flags);
        var validator=assembly.GetType("Upton.Pdm.SolidWorks.FrozenArchiveSubmissionGraph");
        var find=assembly.GetType("Upton.Pdm.Domain.ReferenceCycleGroups",true).GetMethod("Find").MakeGenericMethod(typeof(Guid));
        void Check(bool ok,string text){if(!ok)throw new Exception(text);Console.WriteLine("PASS: "+text);}
        Guid? Id(Dictionary<string,object> node)=>Guid.TryParse(Convert.ToString(node["documentId"]),out var id)?id:(Guid?)null;
        IEnumerable<Dictionary<string,object>> Refs(Dictionary<string,object> node)
        {foreach(var child in ((object[])node["children"]).Cast<Dictionary<string,object>>()){if(Convert.ToInt32(child["kind"])==2)continue;yield return child;foreach(var nested in Refs(child))yield return nested;}}
        bool Flag(Dictionary<string,object> node,string name)=>node.TryGetValue(name,out var value)&&Convert.ToBoolean(value);
        IList Groups(Dictionary<Guid,Dictionary<string,object>> roots)
        {
            var edges=roots.SelectMany(owner=>Refs(owner.Value).Where(node=>!Flag(node,"isContextReference")&&!Flag(node,"referenceTypeUnverified"))
                .Select(Id).Where(id=>id.HasValue&&roots.ContainsKey(id.Value)).Select(id=>new KeyValuePair<Guid,Guid>(owner.Key,id.Value))).ToArray();
            return (IList)find.Invoke(null,new object[]{edges,EqualityComparer<Guid>.Default});
        }
        object Node(string path,int kind,Guid id)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));File.WriteAllText(path,"isolated graph fixture");
            var node=New("CadTreeNode");Set(node,"FullPath",path);Set(node,"FileName",Path.GetFileName(path));Set(node,"DisplayName",Path.GetFileName(path));Set(node,"InstancePath",path);Set(node,"DocumentId",(Guid?)id);Set(node,"Kind",Enum.ToObject(Type("CadDocumentKind"),kind));Set(node,"Revision","W1");Set(node,"CurrentRevision","W1");Set(node,"Configuration","Default");Set(node,"NativeFileScopeVerified",true);return node;
        }
        void Add(object parent,object child)=>((IList)Get(parent,"Children")).Add(child);
        (IDictionary Snapshots,Dictionary<Guid,Dictionary<string,object>> Roots) Capture(object[] selected,HashSet<string> paths,string name)
        {
            var snapshots=(IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(Guid),snapshotType));
            var roots=new Dictionary<Guid,Dictionary<string,object>>();
            foreach(var node in selected){var id=(Guid)Get(node,"DocumentId");var snap=capture.Invoke(null,new object[]{node,paths});snapshots.Add(id,snap);roots.Add(id,(Dictionary<string,object>)TreeCall(snap,"CreateRoot"));}
            File.WriteAllText(Path.Combine(output,name+".json"),json.Serialize(roots.Select(pair=>new{documentId=pair.Key,root=pair.Value}).ToArray()));
            Console.WriteLine("GRAPH "+name+": "+Groups(roots).Count+" cycle groups");return (snapshots,roots);
        }
        void Validate(IDictionary snapshots)=>validator.GetMethod("Validate",Flags).Invoke(null,new object[]{snapshots});
        var a=Node(Path.Combine(output,"PARENT.SLDASM"),0,Guid.NewGuid());var b=Node(Path.Combine(output,"CHILD-B.SLDASM"),0,Guid.NewGuid());var c=Node(Path.Combine(output,"CHILD-C.SLDASM"),0,Guid.NewGuid());var drawing=Node(Path.Combine(output,"MULTI.SLDDRW"),2,Guid.NewGuid());
        Add(a,b);Add(a,c);Add(a,drawing);Add(b,drawing);Add(c,drawing);
        foreach(var node in new[]{a,b,c})((IList)Get(drawing,"ReferencedModels")).Add(node);
        var all=new[]{a,b,c,drawing};var paths=new HashSet<string>(all.Select(node=>(string)Get(node,"FullPath")),StringComparer.OrdinalIgnoreCase);
        foreach(var node in all)Set(node,"NativeFileScopePaths",paths);
        var synthetic=Capture(all,paths,"multi-model-drawing");
        if(!audit)
        {
            Check(Groups(synthetic.Roots).Count==0,"配套工程图的多模型导航不制造3D父子或兄弟循环");Validate(synthetic.Snapshots);
            Check(!Refs(synthetic.Roots[(Guid)Get(b,"DocumentId")]).Any()&&!Refs(synthetic.Roots[(Guid)Get(c,"DocumentId")]).Any(),"子装配体不因共用工程图而增加父装配或兄弟依赖");
            var modelIds=Refs(synthetic.Roots[(Guid)Get(drawing,"DocumentId")]).Select(Id).Where(id=>id.HasValue).Select(id=>id.Value).Distinct().ToArray();
            Check(modelIds.Length==3&&all.Take(3).All(node=>modelIds.Contains((Guid)Get(node,"DocumentId"))),"工程图自身仍完整保留三个模型关联");
            var back=json.Deserialize<Dictionary<string,object>>(json.Serialize(synthetic.Roots[(Guid)Get(a,"DocumentId")]));back["children"]=new object[0];back["role"]=1;
            var root=synthetic.Roots[(Guid)Get(b,"DocumentId")];root["children"]=new object[]{back};
            object Frozen(Dictionary<string,object> value)=>Activator.CreateInstance(snapshotType,Flags,null,new object[]{json.Serialize(value),Convert.ToString(value["fileName"]),all.Select(node=>(Guid)Get(node,"DocumentId")).ToArray(),null},null);
            synthetic.Snapshots[(Guid)Get(b,"DocumentId")]=Frozen(root);
            try{Validate(synthetic.Snapshots);throw new Exception("最终快照新增的循环未在上传前拦截");}catch(TargetInvocationException error){Check(error.InnerException.Message.Contains("尚未上传任何文件"),"初检后最终快照新增的真实循环在上传前整批拦截");}
            back["isContextReference"]=true;synthetic.Snapshots[(Guid)Get(b,"DocumentId")]=Frozen(root);Validate(synthetic.Snapshots);Check(true,"最终快照已确认上下文按服务器规则保留且不作为普通依赖");
            back["isContextReference"]=false;back["referenceTypeUnverified"]=true;synthetic.Snapshots[(Guid)Get(b,"DocumentId")]=Frozen(root);Validate(synthetic.Snapshots);Check(true,"最终快照未知类型标记保留，不擅自转成普通循环");
            back["role"]=0;synthetic.Snapshots[(Guid)Get(b,"DocumentId")]=Frozen(root);try{Validate(synthetic.Snapshots);throw new Exception("无效类型标记未阻止");}catch(TargetInvocationException error){Check(error.InnerException.Message.Contains("引用类型记录不完整"),"未知类型标记不能覆盖真实装配成员");}
        }
        if(string.IsNullOrWhiteSpace(statePath))return;
        var state=(Dictionary<string,object>)json.DeserializeObject(File.ReadAllText(statePath));var input=(Dictionary<string,object>)((object[])state["Roots"])[0];var rows=((object[])input["Nodes"]).Cast<Dictionary<string,object>>().ToArray();
        var mapped=new Dictionary<string,object>();var files=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);var ids=new Dictionary<string,Guid>(StringComparer.OrdinalIgnoreCase);
        foreach(var row in rows)
        {
            var original=(string)row["Path"];var name=Path.GetFileName(original);if(string.IsNullOrWhiteSpace(name))name="VIRTUAL-"+row["Address"]+".SLDPRT";
            if(!files.TryGetValue(original,out var path)){path=Path.Combine(output,"replay",files.Count.ToString(),name);files[original]=path;}
            if(!ids.TryGetValue(name,out var id)){id=Guid.NewGuid();ids[name]=id;}
            var node=Node(path,Convert.ToInt32(row["Kind"]),id);Set(node,"SourceReferencePath",path);Set(node,"InstancePath",row["Instance"]);
            Set(node,"Role",Enum.ToObject(Type("CadReferenceRole"),row["Role"]));Set(node,"Status",Enum.ToObject(Type("CadReferenceStatus"),row["Status"]));Set(node,"IsContextReference",row["Context"]);Set(node,"ReferenceTypeVerified",row["Verified"]);Set(node,"ReferenceTypeUnverified",row["Unverified"]);Set(node,"IsOutsideNativeFileScope",row["Outside"]);mapped[(string)row["Address"]]=node;
        }
        var scope=new HashSet<string>(files.Values,StringComparer.OrdinalIgnoreCase);
        foreach(var row in rows){var node=mapped[(string)row["Address"]];Set(node,"NativeFileScopePaths",scope);foreach(var id in (object[])row["Children"])Add(node,mapped[(string)id]);foreach(var id in (object[])row["Models"])((IList)Get(node,"ReferencedModels")).Add(mapped[(string)id]);}
        var names=new[]{"70004158.F02.02.02.SLDASM","70004158.F02.SLDASM","70004158.F02.02.SLDASM","70004158.Z02.03.SLDASM","70004158.Z02.SLDASM","70004158.Z02.04.SLDASM"};
        var selected=names.Select(name=>mapped.Values.Where(node=>(string)Get(node,"FileName")==name&&!(bool)Get(node,"IsContextReference")&&Convert.ToInt32(Get(node,"Role"))==0).OrderByDescending(node=>((IList)Get(node,"Children")).Count).First()).ToArray();
        var replay=Capture(selected,scope,"actual-six-file-replay");
        if(!audit){Check(Groups(replay.Roots).Count==0,"真实关系树六份报错图档重放不再制造两组循环");Validate(replay.Snapshots);Check(selected.Length==6,"重放仅核对原六份图档，不增加提交文件");}
    }
}
