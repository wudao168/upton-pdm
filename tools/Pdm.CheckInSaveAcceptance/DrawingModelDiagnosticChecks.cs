using System;
using System.IO;
using System.Reflection;
using System.Runtime.Remoting.Messaging;

internal static partial class Program
{
    private sealed class DrawingModelDiagnostics
    {
        internal static readonly string[] Cases = {
            "model-diagnostic-inherited", "model-diagnostic-own-drawing", "model-diagnostic-other-path",
            "model-diagnostic-other-feature", "model-diagnostic-added", "model-diagnostic-code-changed",
            "model-diagnostic-severity-changed","model-diagnostic-same-feature-repeated","model-diagnostic-other-active-configuration",
            "model-diagnostic-equivalent-wrappers","model-diagnostic-extra-feature"
        };

        private readonly string mode;
        private readonly object warning, error, drawingFeature, sameNameOtherFile, sameNameOtherFeature;
        internal object Model { get; }
        internal object ForeignModel { get; }
        internal bool RebuildStarted;
        internal bool IsScenario => mode.StartsWith("model-diagnostic-", StringComparison.Ordinal);
        internal bool SameNativeObject(object a,object b)=>ReferenceEquals(a,b)
            || mode=="model-diagnostic-equivalent-wrappers"&&(ReferenceEquals(a,error)&&ReferenceEquals(b,drawingFeature)||ReferenceEquals(b,error)&&ReferenceEquals(a,drawingFeature));

        internal DrawingModelDiagnostics(Assembly interop, string rootPath, string mode)
        {
            this.mode=mode;
            var nativeFeature=interop.GetType("SolidWorks.Interop.sldworks.Feature",true);
            object Feature(string name,int id,int identity)
            {
                object self=null;
                self=new AssemblyProxy(nativeFeature,call=>{
                    switch(call.MethodName)
                    {
                        case "get_Name":return name;
                        case "GetID":return id;
                        case "GetTypeName":case "GetTypeName2":return "Reference";
                        case "GetHashCode":return identity;
                        case "Equals":return ReferenceEquals(self,call.Args[0]);
                        default:throw new InvalidOperationException("不支持的特征查询 "+call.MethodName);
                    }
                }).GetTransparentProxy();
                return self;
            }
            warning=Feature("70002727.M2-1",88,1);error=Feature("70002727.M1-1",27,2);
            drawingFeature=Feature("70002727.M1-1",27,3);
            sameNameOtherFile=Feature("70002727.M1-1",27,4);
            sameNameOtherFeature=Feature("70002727.M1-1",28,5);
            var extension=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDocExtension",true),call=>ReadIssues(call,false)).GetTransparentProxy();
            var configuration=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.Configuration",true),call=>
                call.MethodName=="get_Name"?(mode=="model-diagnostic-other-active-configuration"?"其他配置":"Default"):throw new Exception(call.MethodName)).GetTransparentProxy();
            var manager=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ConfigurationManager",true),call=>
                call.MethodName=="get_ActiveConfiguration"?configuration:throw new Exception(call.MethodName)).GetTransparentProxy();
            if(mode=="model-diagnostic-other-path")
            {
                var path=Path.Combine(Path.GetDirectoryName(rootPath),"同名旧装配",Path.GetFileName(rootPath));
                Directory.CreateDirectory(Path.GetDirectoryName(path));File.Copy(rootPath,path,true);
                ForeignModel=new AssemblyProxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true),call=>
                    call.MethodName=="GetPathName"?path:call.MethodName=="GetTitle"?Path.GetFileName(path):
                    throw new InvalidOperationException("不得向其他目录的同名模型借用特征诊断 "+call.MethodName)).GetTransparentProxy();
            }
            Model=new AssemblyProxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true),call=>{
                switch(call.MethodName)
                {
                    case "GetPathName":return rootPath;
                    case "GetTitle":return Path.GetFileName(rootPath);
                    case "GetType":return 2;
                    case "GetSaveFlag":case "IsOpenedReadOnly":return false;
                    case "get_Extension":return extension;
                    case "get_ConfigurationManager":return manager;
                    case "FeatureByName":return (string)call.Args[0]=="70002727.M1-1"?error:warning;
                    default:throw new InvalidOperationException("未执行模型保存或重建 "+call.MethodName);
                }
            }).GetTransparentProxy();
        }

        internal object ReadIssues(IMethodCallMessage call,bool drawing)
        {
            if(!IsScenario)
            {
                if(call.MethodName=="GetWhatsWrongCount")return 0;
                throw new InvalidOperationException("无诊断时不应读取模型问题列表 "+call.MethodName);
            }
            var features=new[]{warning,error};
            var codes=new[]{51,51};var warnings=new[]{true,false};
            if(!drawing&&mode=="model-diagnostic-added"&&!RebuildStarted)
            { features=new[]{warning};codes=new[]{51};warnings=new[]{true}; }
            if(drawing)
            {
                if(mode is "model-diagnostic-own-drawing" or "model-diagnostic-equivalent-wrappers")features[1]=drawingFeature;
                if(mode=="model-diagnostic-other-path")features[1]=sameNameOtherFile;
                if(mode=="model-diagnostic-other-feature")features[1]=sameNameOtherFeature;
                if(mode=="model-diagnostic-code-changed")codes[1]=52;
                if(mode is "model-diagnostic-same-feature-repeated" or "model-diagnostic-extra-feature")
                { features=new[]{warning,error,mode=="model-diagnostic-extra-feature"?sameNameOtherFeature:error};codes=new[]{51,51,51};warnings=new[]{true,false,false}; }
            }
            else if(mode=="model-diagnostic-severity-changed")warnings[1]=true;
            if(call.MethodName=="GetWhatsWrongCount")return features.Length;
            if(call.MethodName=="GetWhatsWrong")
                return new ReturnMessage(true,new object[]{features,codes,warnings},3,call.LogicalCallContext,call);
            throw new InvalidOperationException("未知模型诊断操作 "+call.MethodName);
        }
    }
}
