using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Diagnostics;

internal static partial class Program
{
    static void RelocatedImportMemberScenario(string output)
    {
        output=Path.GetFullPath(output);Directory.CreateDirectory(output);
        void Check(bool ok,string message){if(!ok)throw new Exception(message);Console.WriteLine("PASS: "+message);}
        var interop=Type("PdmAddin").GetField("application",Flags).FieldType.Assembly;
        object ProxyFor(string name,Func<System.Runtime.Remoting.Messaging.IMethodCallMessage,object> callback)
        {var type=interop.GetType("SolidWorks.Interop.sldworks."+name,true);return new Proxy(type,m=>m.MethodName=="GetType"?type:callback(m)).GetTransparentProxy();}
        var owner=Path.Combine(output,"OWNER.SLDASM");File.WriteAllText(owner,"saved native container");
        var original=Path.Combine(output,"old","SOURCE.stp");var current=Path.Combine(output,"new","SOURCE.stp");Directory.CreateDirectory(Path.GetDirectoryName(current));File.WriteAllText(current,"current STEP bytes");
        var cache=Path.Combine(Path.GetTempPath(),"swx-relocation","IC~~","SOURCE.stp.SLDASM");var leafPath=Path.Combine(Path.GetDirectoryName(cache),"LEAF.stp.SLDPRT");
        var root=new AssemblyProxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true),m=>m.MethodName=="GetPathName"?owner:m.MethodName=="GetType"?(object)2:throw new Exception("不应修改根图档："+m.MethodName)).GetTransparentProxy();
        bool dirty=false,proof=true,independent=false,hasMember=true,hasHost=true;int memberId=49,suppression=0;string memberPath=leafPath,memberSource=original,featureName="LEAF-1",featureType="Reference",currentSource=current;
        var importFeature=ProxyFor("Feature",m=>m.MethodName=="get_Name"?"IMPORT-1":m.MethodName=="get_Is3DInterconnectFeature"?(object)proof:throw new Exception(m.MethodName));
        var memberFeature=ProxyFor("Feature",m=>m.MethodName=="get_Name"?featureName:m.MethodName=="get_Is3DInterconnectFeature"?(object)independent:m.MethodName=="GetTypeName2"?featureType:throw new Exception(m.MethodName));
        var member=ProxyFor("Component2",m=>m.MethodName=="GetID"?(object)memberId:m.MethodName=="get_Name2"?"LEAF-1":m.MethodName=="GetPathName"?memberPath:m.MethodName=="GetImportedPath"?memberSource:throw new Exception(m.MethodName));
        var host=new AssemblyProxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true),m=>m.MethodName=="GetPathName"?cache:m.MethodName=="GetSaveFlag"?(object)dirty:m.MethodName=="GetType"?(object)2:m.MethodName=="FeatureByName"?memberFeature:m.MethodName=="GetComponentByName"?(hasMember?member:null):throw new Exception("不得解析、重建或保存父模型："+m.MethodName)).GetTransparentProxy();
        var savedParent=ProxyFor("Component2",m=>m.MethodName=="GetModelDoc2"?null:m.MethodName=="GetPathName"?owner:m.MethodName=="get_IsVirtual"?(object)false:m.MethodName=="GetParent"?null:m.MethodName=="FeatureByName"?importFeature:throw new Exception(m.MethodName));
        var importedParent=ProxyFor("Component2",m=>m.MethodName=="GetModelDoc2"?(hasHost?host:null):m.MethodName=="GetPathName"?cache:m.MethodName=="GetImportedPath"?currentSource:m.MethodName=="GetParent"?savedParent:m.MethodName=="get_Name2"?"OWNER/IMPORT-1":throw new Exception(m.MethodName));
        var leaf=ProxyFor("Component2",m=>m.MethodName=="GetModelDoc2"?null:m.MethodName=="GetPathName"?leafPath:m.MethodName=="GetImportedPath"?original:m.MethodName=="GetParent"?importedParent:m.MethodName=="get_Name2"?"OWNER/IMPORT-1/LEAF-1":m.MethodName=="GetSuppression"?(object)suppression:m.MethodName=="GetID"?(object)49:throw new Exception("不得修改压缩子件："+m.MethodName));
        var app=ProxyFor("ISldWorks",m=>throw new Exception("不得打开、激活或读打包清单："+m.MethodName));
        object Storage(){var s=New("ImportedComponentStorage",app);Set(s,"AllowInventory",false);return s;}
        object Find()=>TreeCall(Storage(),"FindOwner",leaf,root);
        var storage=Storage();
        Check((string)TreeCall(storage,"FindOwner",leaf,root)==owner,"旧来源缺失时，按父模型组件ID、路径、特征及原生导入归属确认压缩子件");
        var warning=(string)TreeCall(storage,"RelocatedSourceWarning",leaf);
        Check(warning.Contains(original)&&warning.Contains(current)&&warning.Contains("原有关联未改写"),"保留旧来源与当前来源提示，不将同名判断当作独立证据");
        memberId=50;Check(Find()==null,"同名组件ID不同不继承归属");memberId=49;
        memberPath=Path.Combine(Path.GetDirectoryName(leafPath),"OTHER.SLDPRT");Check(Find()==null,"组件记录路径不同不继承归属");memberPath=leafPath;
        memberSource=current;Check(Find()==null,"父模型中的子件来源记录不符时不继承归属");memberSource=original;
        hasMember=false;Check(Find()==null,"只有特征名称而没有父模型组件记录不放行");hasMember=true;
        independent=true;Check(Find()==null,"独立导入特征不能当成内部生成子件");independent=false;
        featureName="OTHER-1";Check(Find()==null,"不能使用其他实例的特征");featureName="LEAF-1";
        featureType="Other";Check(Find()==null,"非组件引用特征不放行");featureType="Reference";
        dirty=true;Check(Find()==null,"未保存父模型不能作为已保存归属依据");dirty=false;
        hasHost=false;Check(Find()==null,"父模型未加载且来源不一致时不猜测");hasHost=true;
        suppression=1;Check(Find()==null,"不将旧路径例外扩大到其他未加载状态");suppression=0;
        proof=false;Check(Find()==null,"缺少最终原生导入特征仍不放行");proof=true;
        Directory.CreateDirectory(Path.GetDirectoryName(original));File.WriteAllText(original,"different existing STEP");Check(Find()==null,"旧来源仍存在时不自动认定是迁移");File.Delete(original);
        File.Move(current,current+".moved");Check(Find()==null,"当前父来源也缺失时不放行");File.Move(current+".moved",current);
        currentSource=Path.Combine(output,"other.stp");File.WriteAllText(currentSource,"different name");Check(Find()==null,"来源文件名不同不猜测替代");currentSource=current;
        var scanner=New("SolidWorksReferenceTreeScanner",app);SetField(scanner,"scannedModel",root);SetField(scanner,"scannedDocumentPath",owner);SetField(scanner,"importedStorage",storage);((Stopwatch)Field(scanner,"progressClock")).Start();
        var node=New("CadTreeNode");Set(node,"FullPath",leafPath);Set(node,"FileName","LEAF.stp.SLDPRT");Set(node,"ImportedSourcePath",original);Set(node,"IsSuppressed",true);Set(node,"Kind",Enum.Parse(Type("CadDocumentKind"),"Part"));
        var pending=(IList)Field(scanner,"importedComponents");pending.Add(Activator.CreateInstance(pending.GetType().GetGenericArguments()[0],node,leaf));TreeCall(scanner,"CompleteImportedStorage");
        Check((bool)Get(node,"IsStoredImport")&&(bool)Get(node,"IsSuppressed")&&(string)Get(node,"ImportedSourcePath")==original,"扫描器保留压缩状态和旧来源，写回正确保存父级");
        var ownerNode=New("CadTreeNode");Set(ownerNode,"FullPath",owner);((IList)Get(ownerNode,"Children")).Add(node);
        Type("SolidWorksReferenceTreeScanner").GetMethod("CopyImportedSourceWarnings",Flags).Invoke(null,new[]{ownerNode});
        Type("SolidWorksReferenceTreeScanner").GetMethod("CopyImportedSourceWarnings",Flags).Invoke(null,new[]{ownerNode});
        Check(((IList)Get(ownerNode,"ReferenceWarnings")).Count==1,"旧来源提示传递到实际上传父图档，重复传播不增加重复记录");
        Check(File.ReadAllText(owner)=="saved native container"&&File.ReadAllText(current)=="current STEP bytes"&&!File.Exists(original),"验证不改写原生文件或STEP，不伪造缺失来源");
    }
}
