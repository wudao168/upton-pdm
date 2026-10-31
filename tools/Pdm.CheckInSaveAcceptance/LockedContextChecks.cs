using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Remoting.Messaging;
using System.Web.Script.Serialization;

internal static partial class Program
{
    static void LockedContextScenario(string output,string fixture)
    {
        output=Path.GetFullPath(output);Directory.CreateDirectory(output);
        void Check(bool ok,string text){if(!ok)throw new Exception(text);Console.WriteLine("PASS: "+text);}
        var classifier=Type("NativeDesignDependencies").GetMethod("IsAssemblyContext",Flags);
        bool Context(string owner,int status)=> (bool)classifier.Invoke(null,new object[]{@"C:\模型\总装.SLDASM",owner,"M6 螺纹孔2",status});
        Check(Context("子装<1>/机架<1>",1),"已锁定且具有特征所属实例的装配环境仍识别为上下文");
        Check(!Context("",1),"已锁定的普通派生来源不能仅凭锁定状态绕过真实循环检查");
        Check(!Context("子装<1>/机架<1>",0),"已断开引用不被重新激活");
        var rows=((object[])new JavaScriptSerializer{MaxJsonLength=int.MaxValue}.DeserializeObject(File.ReadAllText(fixture))).Cast<Dictionary<string,object>>().ToArray();
        var interop=Type("PdmAddin").GetField("application",Flags).FieldType.Assembly;
        var manager=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.FeatureManager",true),call=>call.MethodName=="GetFeatures"?new object[0]:call.MethodName=="GetFeatureCount"?(object)0:throw new Exception(call.MethodName)).GetTransparentProxy();
        var sketches=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.SketchManager",true),call=>call.MethodName=="GetSketchBlockDefinitions"?new object[0]:throw new Exception(call.MethodName)).GetTransparentProxy();
        object Evidence(Dictionary<string,object> row)
        {
            var extension=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDocExtension",true),call=>
            {
                if(call.MethodName!="ListExternalFileReferences2")throw new Exception("禁止其他原生操作："+call.MethodName);
                return new ReturnMessage(null,new object[]{row["externalModels"],row["externalComponents"],row["externalFeatures"],row["externalTypes"],row["externalStatuses"],new string[((object[])row["externalModels"]).Length],row["externalOwners"],row["externalOptions"],row["externalConfigurations"]},9,call.LogicalCallContext,call);
            }).GetTransparentProxy();
            var model=new AssemblyProxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true),call=>call.MethodName=="GetType"?(object)1:call.MethodName=="get_Extension"?extension:call.MethodName=="get_FeatureManager"?manager:call.MethodName=="get_SketchManager"?sketches:call.MethodName=="GetEquationMgr"||call.MethodName=="GetDesignTable"?null:throw new Exception("不得打开、重建或修改模型："+call.MethodName)).GetTransparentProxy();
            return Type("NativeDesignDependencies").GetMethod("ReadCheckIn",Flags).Invoke(null,new[]{model});
        }
        var frameRow=rows.Single(row=>Path.GetFileName((string)row["path"])=="70004524.M01.01-01.SLDPRT");
        var frameEvidence=Evidence(frameRow);
        var contexts=((IEnumerable)Get(frameEvidence,"Contexts")).Cast<string>().ToArray();
        Check(contexts.Any(path=>Path.GetFileName(path)=="70004524.M01（SHCM93检漏机）.SLDASM"),"真实样本4条锁定与46条脱离上下文混合时，不再冲掉当前总装上下文标记");
        Check(contexts.Any(path=>Path.GetFileName(path)=="30002674.M1 下机架.SLDASM"),"旧装配的锁定上下文仍保留关联，不解除锁定或断链");
        var deepRow=rows.Single(row=>Path.GetFileName((string)row["path"])=="70004524.M01.01.01.01-01.SLDPRT");
        var deepEvidence=Evidence(deepRow);
        Check(((IEnumerable)Get(deepEvidence,"Contexts")).Cast<string>().Count()==3,"深层零件三个原生上下文全部识别，包含旧装配及当前两级装配");
        object Node(string file,string kind,string selection="")
        {
            var node=New("CadTreeNode");Set(node,"FullPath",Path.Combine(output,file));Set(node,"FileName",file);Set(node,"Kind",Enum.Parse(Type("CadDocumentKind"),kind));Set(node,"InstancePath",file);Set(node,"ComponentSelectionName",selection);return node;
        }
        var root=Node("ROOT.SLDASM","Assembly");
        var deep=Node("70004524.M01.01.01.01-01.SLDPRT","Part","70004524.M01.01.01.01-01-2@70004524.M01.01.01.01-1@70004524.M01.01.01-2@70004524.M01.01-1@ROOT");
        ((IList)Get(root,"Children")).Add(deep);
        var target=rows.SelectMany(row=>(object[])row["externalModels"]).Cast<string>().First(path=>Path.GetFileName(path)=="70004524.M01（SHCM93检漏机）.SLDASM");
        var link=Node(Path.GetFileName(target),"Assembly");Set(link,"FullPath",target);Set(link,"SourceReferencePath",target);Set(link,"Role",Enum.Parse(Type("CadReferenceRole"),"ExternalFileDependency"));((IList)Get(deep,"Children")).Add(link);
        var proven=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var direct=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var apply=Type("ArchiveContextOwnership").GetMethod("Apply",Flags);
        apply.Invoke(null,new object[]{root,deepEvidence,proven,direct});
        Check(proven.Contains((string)Get(deep,"FullPath"))&&(bool)Get(link,"IsContextReference"),"未加载零件可从父装配原生特征归属取得上下文类型，已有回指同步修正");
        Check(((IEnumerable)Get(deep,"DesignContextPaths")).Cast<string>().Contains(target),"仅使用匹配原生实例和真实成员路径的证据");
        Set(link,"IsContextReference",false);direct.Add((string)Get(deep,"FullPath"));
        apply.Invoke(null,new object[]{root,deepEvidence,new HashSet<string>(),direct});
        Check(!(bool)Get(link,"IsContextReference"),"零件自身已读取的普通来源结论优先，父装配证据不能覆盖它");
        direct.Clear();var other=Node("70004524.M01.01.01.01-01.SLDPRT","Part",(string)Get(deep,"ComponentSelectionName"));Set(other,"FullPath",Path.Combine(output,"other","70004524.M01.01.01.01-01.SLDPRT"));((IList)Get(root,"Children")).Add(other);
        var ambiguous=new HashSet<string>();apply.Invoke(null,new object[]{root,deepEvidence,ambiguous,direct});
        Check(ambiguous.Count==0&&!(bool)Get(link,"IsContextReference"),"不同路径的同名实例无法唯一对应时不猜测归属、不绕过校验");
        Check(((IList)Get(deep,"Children")).Count==1,"分类不会新增上传文件或改变原装配成员结构");
    }
}
