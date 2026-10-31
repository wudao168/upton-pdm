using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Remoting.Messaging;
using System.Web.Script.Serialization;

internal static partial class Program
{
    static void ContextComponentSourcesScenario(string output,string fixture)
    {
        output=Path.GetFullPath(output);Directory.CreateDirectory(output);
        void Check(bool ok,string message){if(!ok)throw new Exception(message);Console.WriteLine("PASS: "+message);}
        var rows=((object[])new JavaScriptSerializer{MaxJsonLength=int.MaxValue}.DeserializeObject(File.ReadAllText(fixture))).Cast<Dictionary<string,object>>().ToArray();
        var mapped=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach(var path in rows.Select(row=>(string)row["path"]).Concat(rows.SelectMany(row=>(object[])row["externalModels"]).Cast<string>()).Distinct(StringComparer.OrdinalIgnoreCase))
        {mapped[path]=Path.Combine(output,Path.GetFileName(path));File.WriteAllText(mapped[path],"隔离引用测试数据");}
        var interop=Type("PdmAddin").GetField("application",Flags).FieldType.Assembly;
        var manager=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.FeatureManager",true),call=>call.MethodName=="GetFeatures"?new object[0]:call.MethodName=="GetFeatureCount"?(object)0:throw new Exception(call.MethodName)).GetTransparentProxy();
        var sketches=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.SketchManager",true),call=>call.MethodName=="GetSketchBlockDefinitions"?new object[0]:throw new Exception(call.MethodName)).GetTransparentProxy();
        object Read(Dictionary<string,object> row,bool resolved,bool direct=false)
        {
            var other=rows.Single(value=>!ReferenceEquals(value,row));var otherPath=mapped[(string)other["path"]];
            var otherName=Path.GetFileNameWithoutExtension((string)other["path"]);
            var models=((object[])row["externalModels"]).Cast<string>().Select(path=>mapped[path]).ToList();
            var components=((object[])row["externalComponents"]).Select(Convert.ToString).ToList();
            var features=((object[])row["externalFeatures"]).ToList();var types=((object[])row["externalTypes"]).ToList();
            var statuses=((object[])row["externalStatuses"]).ToList();var owners=((object[])row["externalOwners"]).ToList();
            var entities=((object[])row["externalEntities"]).ToList();
            // Simulate resolution of the same captured in-context geometry: only
            // ComponentPathName and the in/out-of-context status become available.
            if(resolved)for(var i=0;i<models.Count;i++)
                if(Convert.ToString(entities[i]).IndexOf(otherName,StringComparison.OrdinalIgnoreCase)>=0)
                {components[i]=otherPath;if(Convert.ToInt32(statuses[i])==4)statuses[i]=3;}
            if(direct)
            {models.Add(otherPath);components.Add("");features.Add("独立派生来源");types.Add("实体");statuses.Add(3);owners.Add("");entities.Add("");}
            var extension=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDocExtension",true),call=>
            {
                if(call.MethodName!="ListExternalFileReferences2")throw new Exception("禁止额外原生操作："+call.MethodName);
                return new ReturnMessage(null,new object[]{models.ToArray(),components.ToArray(),features.ToArray(),types.ToArray(),statuses.ToArray(),entities.ToArray(),owners.ToArray(),new int[models.Count],new string[models.Count]},9,call.LogicalCallContext,call);
            }).GetTransparentProxy();
            var model=new AssemblyProxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true),call=>call.MethodName=="GetType"?(object)1:call.MethodName=="get_Extension"?extension:call.MethodName=="get_FeatureManager"?manager:call.MethodName=="get_SketchManager"?sketches:call.MethodName=="GetEquationMgr"||call.MethodName=="GetDesignTable"?null:throw new Exception("禁止打开、保存、重建或关闭图档："+call.MethodName)).GetTransparentProxy();
            return Type("NativeDesignDependencies").GetMethod("ReadCheckIn",Flags).Invoke(null,new[]{model});
        }
        string[] Sources(object value)=>((IEnumerable)Get(value,"Sources")).Cast<string>().ToArray();
        var isolated=rows.Select(row=>Read(row,false)).ToArray();var resolvedReads=rows.Select(row=>Read(row,true)).ToArray();
        for(var i=0;i<rows.Length;i++)
        {
            var other=rows[1-i];var file=Path.GetFileName((string)rows[i]["path"]);
            Check(new HashSet<string>(Sources(isolated[i]),StringComparer.OrdinalIgnoreCase).SetEquals(Sources(resolvedReads[i])),file+"：单独打开与解析上下文组件时的文件来源一致");
            Check(!Sources(resolvedReads[i]).Contains(mapped[(string)other["path"]]),file+"：几何组件路径不被额外转成普通文件依赖");
            var owners=((IEnumerable)Get(resolvedReads[i],"Owners")).Cast<object>();
            Check(owners.Any(owner=>(string)owner.GetType().GetField("Component",Flags).GetValue(owner)==mapped[(string)other["path"]]),file+"：原生组件来源仍保留在上下文证据中");
            Check(new HashSet<string>(((IEnumerable)Get(isolated[i],"Contexts")).Cast<string>()).SetEquals(((IEnumerable)Get(resolvedReads[i],"Contexts")).Cast<string>()),file+"：原有装配上下文及锁定关联保留");
        }
        var ids=new Dictionary<string,Guid>(StringComparer.OrdinalIgnoreCase);
        object Node(string path,string kind)
        {
            var node=New("CadTreeNode");Set(node,"FullPath",path);Set(node,"FileName",Path.GetFileName(path));Set(node,"DisplayName",Path.GetFileNameWithoutExtension(path));
            if(!ids.TryGetValue(path,out var id))ids[path]=id=Guid.NewGuid();
            Set(node,"DocumentId",(Guid?)id);Set(node,"Revision","W1");Set(node,"CurrentRevision","W1");
            Set(node,"InstancePath",Path.GetFileName(path));Set(node,"Kind",Enum.Parse(Type("CadDocumentKind"),kind));return node;
        }
        var environment=((IEnumerable)Get(resolvedReads[1],"Contexts")).Cast<string>().Single();
        var root=Node(environment,"Assembly");var parts=rows.Select(row=>Node(mapped[(string)row["path"]],"Part")).ToArray();
        foreach(var part in parts)((IList)Get(root,"Children")).Add(part);
        var items=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(Type("BatchOperationItem")));
        foreach(var node in new[]{root}.Concat(parts))items.Add(New("BatchOperationItem",node,1));
        void LinkSources(object part,object read)
        {
            var contexts=new HashSet<string>(((IEnumerable)Get(read,"Contexts")).Cast<string>(),StringComparer.OrdinalIgnoreCase);
            foreach(var source in Sources(read))
            {
                var link=Node(source,source.EndsWith(".SLDASM",StringComparison.OrdinalIgnoreCase)?"Assembly":"Part");
                Set(link,"Role",Enum.Parse(Type("CadReferenceRole"),"ExternalFileDependency"));Set(link,"IsContextReference",contexts.Contains(source));
                ((IList)Get(part,"Children")).Add(link);
            }
            // Match the current production reader: source paths become verified
            // file dependencies only after applying the native reference evidence.
            Type("PdmAddin").GetMethod("ClassifyReferenceLinks",Flags).Invoke(null,new[]{part,read});
        }
        for(var i=0;i<parts.Length;i++)LinkSources(parts[i],resolvedReads[i]);
        var validate=Type("ArchiveReferenceCycles").GetMethod("Validate",Flags);
        validate.Invoke(null,new object[]{items});
        Check(true,"真实样本的上下文组件解析不再凭空生成零件到零件的提交闭环");
        var snapshot=Type("PdmApiClient").GetMethod("CaptureArchiveSnapshot",Flags).Invoke(null,new object[]{parts[0],new HashSet<string>(mapped.Values,StringComparer.OrdinalIgnoreCase)});
        var payload=(Dictionary<string,object>)TreeCall(snapshot,"CreateRoot");
        Check(((object[])payload["children"]).Cast<Dictionary<string,object>>().Count(child=>Convert.ToBoolean(child["isContextReference"]))==2,"提交快照仍保留两个原有装配环境，不删除原生上下文关系");
        Check(items.Count==3&&((IList)Get(root,"Children")).Count==2,"修复不增加提交图档，也不改变总装成员范围");
        for(var i=0;i<parts.Length;i++)
        {
            var direct=Read(rows[i],true,true);
            Check(Sources(direct).Contains(mapped[(string)rows[1-i]["path"]]),Path.GetFileName((string)rows[i]["path"])+"：同一零件另有普通派生用途时仍是实际文件来源");
            ((IList)Get(parts[i],"Children")).Clear();LinkSources(parts[i],direct);
        }
        try{validate.Invoke(null,new object[]{items});throw new Exception("普通派生循环未拦截");}
        catch(TargetInvocationException error){Check(error.InnerException.Message.Contains("70004524.M01.01-01")&&error.InnerException.Message.Contains("70004524.M01.01-02"),"普通派生来源的真实双向文件循环仍被拦截");}
        foreach(var part in parts)((IList)Get(part,"Children")).Clear();
        var assembly=Node(Path.Combine(output,"真实装配循环.SLDASM"),"Assembly");((IList)Get(root,"Children")).Add(assembly);((IList)Get(assembly,"Children")).Add(root);
        try{validate.Invoke(null,new object[]{items});throw new Exception("实际装配循环未拦截");}
        catch(TargetInvocationException error){Check(error.InnerException.Message.Contains("真实装配循环"),"实际装配成员循环继续拦截，不受上下文来源修复影响");}
    }
}
