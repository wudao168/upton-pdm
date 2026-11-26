using System;
using System.Collections;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static partial class Program
{
    static void LightweightImportOwnerScenario(string output)
    {
        void Check(bool ok,string text){if(!ok)throw new Exception(text);Console.WriteLine("PASS: "+text);}
        var interop=Type("PdmAddin").GetField("application",Flags).FieldType.Assembly;
        object ProxyFor(string name,Func<System.Runtime.Remoting.Messaging.IMethodCallMessage,object> call)
        {var type=interop.GetType("SolidWorks.Interop.sldworks."+name,true);return new Proxy(type,message=>message.MethodName=="GetType"?type:call(message)).GetTransparentProxy();}
        var rootPath=Path.GetFullPath(Path.Combine(output,"ROOT.SLDASM"));var ownerPath=Path.GetFullPath(Path.Combine(output,"OWNER.SLDASM"));File.WriteAllText(rootPath,"root");File.WriteAllText(ownerPath,"owner");
        var root=new AssemblyProxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true),call=>call.MethodName=="GetPathName"?rootPath:call.MethodName=="GetType"?(object)2:throw new Exception("不应向上越过真实存储文件："+call.MethodName)).GetTransparentProxy();
        var cache=Path.Combine(Path.GetTempPath(),"swx-fixture","IC~~","IMPORT.SLDASM");var cacheModel=ProxyFor("ModelDoc2",call=>call.MethodName=="GetPathName"?cache:throw new Exception(call.MethodName));
        var proven=true;var queries=0;
        var feature=ProxyFor("Feature",call=>call.MethodName=="get_Name"?"IMPORT-1":call.MethodName=="get_Is3DInterconnectFeature"?(object)proven:throw new Exception(call.MethodName));
        var nativeParent=ProxyFor("Component2",call=>call.MethodName=="GetModelDoc2"?null:call.MethodName=="GetPathName"?ownerPath:call.MethodName=="get_IsVirtual"?(object)false:call.MethodName=="GetParent"?null:call.MethodName=="FeatureByName"?ReadFeature():throw new Exception("禁止解析或保存："+call.MethodName));
        object ReadFeature(){queries++;return feature;}
        object Component(string name,object parent,string path=null)=>ProxyFor("Component2",call=>call.MethodName=="GetModelDoc2"?cacheModel:call.MethodName=="GetPathName"?(path??cache):call.MethodName=="GetImportedPath"?@"Z:\history\source.stp":call.MethodName=="GetParent"?parent:call.MethodName=="get_Name2"?name:call.MethodName=="get_IsVirtual"?(object)false:call.MethodName=="get_ReferencedConfiguration"?"Default":throw new Exception("禁止修改组件："+call.MethodName));
        var imported=Component("OWNER-1/IMPORT-1",nativeParent);var child=Component("OWNER-1/IMPORT-1/PART-1",imported);
        var app=ProxyFor("ISldWorks",call=>throw new Exception("不应读取打包清单或激活图档："+call.MethodName));
        object Storage(){var s=New("ImportedComponentStorage",app);Set(s,"AllowInventory",false);return s;}
        var storage=Storage();Check((string)TreeCall(storage,"FindOwner",imported,root)==ownerPath,"轻化父模型为空仍按真实持久路径确认导入归属");
        Check((string)TreeCall(storage,"FindOwner",child,root)==ownerPath&&queries==1,"内部子件继承同一已确认归属，缓存一次特征证明");
        proven=false;Check(TreeCall(Storage(),"FindOwner",child,root)==null,"缺少原生证明时仍保持待核对，不盲目归到总装");
        Check(File.ReadAllText(ownerPath)=="owner"&&File.ReadAllText(rootPath)=="root","归属核对不解析、重建、改名或写入原生文件");

        var configuration=ProxyFor("Configuration",call=>call.MethodName=="get_Name"?"Default":throw new Exception(call.MethodName));
        var manager=ProxyFor("ConfigurationManager",call=>call.MethodName=="get_ActiveConfiguration"?configuration:throw new Exception(call.MethodName));
        bool MatchCheckout(string path,object occurrence)
        {
            var model=new AssemblyProxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true),call=>
            {
                switch(call.MethodName)
                {
                    case "GetPathName":return path;
                    case "get_ConfigurationManager":return manager;
                    case "GetComponents":return new[]{occurrence};
                    default:throw new Exception("获取校验不得修改装配："+call.MethodName);
                }
            }).GetTransparentProxy();
            var id=Guid.NewGuid();var json=new System.Web.Script.Serialization.JavaScriptSerializer();
            var manifest=json.Deserialize(json.Serialize(new { rootDocumentId=id,files=new[]{new{documentId=id,
                fileName=Path.GetFileName(path),relativePath=Path.GetFileName(path),configuration="Default"}} }),Type("ControlledOpenManifestDto"));
            var snapshot=json.Deserialize(json.Serialize(new { fileName=Path.GetFileName(path),configuration="Default",
                children=new[]{new { fileName=Path.GetFileName(cache),configuration="Default",kind=0,
                    status=(int)Enum.Parse(Type("CadReferenceStatus"),"Virtual") }} }),Type("DocumentReferenceNodeDto"));
            return (bool)TreeCallStatic("PdmAddin","WorkingReuseLoadedContextForEditMatches",model,Path.GetDirectoryName(path),
                manifest,snapshot,null,null,null,null,null,null,false,null,null,app,null,null);
        }
        proven=true;
        Check(MatchCheckout(ownerPath,imported),"获取时按原生特征核实归属，即使 SolidWorks IsVirtual 为 false，仍与存档内部实例一致");
        Check(!MatchCheckout(rootPath,imported),"导入归属指向其他持久装配时不能按当前装配的内部实例通过");
        Check(!MatchCheckout(ownerPath,Component("OWNER-1/IMPORT-1",nativeParent,Path.Combine(output,"IMPORT.SLDASM"))),
            "真实外部同名模型不能借用导入归属");
        proven=false;
        Check(!MatchCheckout(ownerPath,imported),"仅有导入缓存名和存档记录时，获取仍需原生归属证明");
    }
}
