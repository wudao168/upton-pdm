using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;

internal static partial class Program
{
    static void ImportUpdateBoundaryScenario(string output)
    {
        Directory.CreateDirectory(output);
        void Check(bool ok,string message){if(!ok)throw new Exception(message);Console.WriteLine("PASS: "+message);}
        var appType=Type("PdmAddin").GetField("application",Flags).FieldType;
        var interop=appType.Assembly;
        foreach(var mode in new[]{"saved","native-update","part-update","drawing-update"})
        {
            var cleanPath=Path.GetFullPath(Path.Combine(output,mode+"-原装配.SLDASM"));
            var changedPath=Path.GetFullPath(Path.Combine(output,mode+"-更新目标.SLDASM"));
            File.WriteAllText(cleanPath,"原文件内容，不允许被读取流程修改");File.WriteAllText(changedPath,"已保存目标内容");
            var cleanHash=WorkingReuseHash(cleanPath);var changedHash=WorkingReuseHash(changedPath);
            object Node(string path,string kind,bool update){var n=New("CadTreeNode");Set(n,"FullPath",path);Set(n,"FileName",Path.GetFileName(path));Set(n,"Kind",Enum.Parse(Type("CadDocumentKind"),kind));Set(n,"RequiresModelUpdate",update);return n;}
            var clean=Node(cleanPath,"Assembly",false);
            var target=Node(changedPath,mode=="part-update"?"Part":mode=="drawing-update"?"Drawing":"Assembly",mode!="saved");
            var requested=new List<string>();var enumerations=0;var cleanDirty=false;
            var model=new AssemblyProxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true),call=>{
                if(call.MethodName=="GetComponents"){enumerations++;return new object[0];}
                throw new Exception("禁止其他原生读写："+call.MethodName);
            }).GetTransparentProxy();
            var app=new Proxy(appType,call=>{
                if(call.MethodName!="GetOpenDocumentByName")throw new Exception("禁止打开、保存或重建："+call.MethodName);
                var path=(string)call.Args[0];requested.Add(path);
                if(path==cleanPath){cleanDirty=true;throw new Exception("已保存源装配被不必要的预检扰动");}
                return model;
            }).GetTransparentProxy();
            Type("ArchiveNativeCopyWorkspace").GetMethod("ValidateLoadedImports",Flags).Invoke(null,new object[]{app,LinkedItems(new[]{clean,target})});
            Check(!cleanDirty&&!requested.Contains(cleanPath),mode+"：已保存原装配不进入递归导入预检");
            Check(requested.Count==(mode=="native-update"?1:0)&&enumerations==requested.Count,mode+"：只检查确实需要原生更新的装配目标");
            Check(WorkingReuseHash(cleanPath)==cleanHash&&WorkingReuseHash(changedPath)==changedHash,mode+"：原文件字节不变，无保存、重建或改名");
        }
    }
}
