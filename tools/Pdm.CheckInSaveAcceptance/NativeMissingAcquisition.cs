using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.Remoting.Messaging;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

internal static partial class Program
{
 sealed class NativeMissingAcquisition
 {
  internal static readonly string[] Cases={"stored-missing-unchanged-pair","stored-missing-added","stored-missing-removed",
   "stored-missing-removed-all","stored-missing-other-path","stored-missing-configuration","stored-missing-resolved",
   "stored-missing-unavailable","stored-missing-corrupt","stored-missing-renamed","stored-missing-checkout-race"};
  public sealed class Row
  {
   public string Path {get;set;}
   public string Instance {get;set;}
   public string Configuration {get;set;}
   public int Suppression {get;set;}
  }
  public sealed class CapturedComponent
  {
   public string Name {get;set;}
   public string Path {get;set;}
   public string Config {get;set;}
   public string Owner {get;set;}
   public int Suppression {get;set;}
  }
  readonly Row[] original;
  readonly string nativeOwner,solidWorksExecutable;
  int readerCalls;
  internal string OwnerFileName=>nativeOwner==null?"OLD-FIXTURE.SLDASM":Path.GetFileName(nativeOwner);
  internal string ChildPath=>original[0].Path;
  NativeMissingAcquisition(Row[] original,string nativeOwner=null,string solidWorksExecutable=null)
  {this.original=original;this.nativeOwner=nativeOwner;this.solidWorksExecutable=solidWorksExecutable;}

  internal static NativeMissingAcquisition Synthetic(string directory)
  {
   var path=Path.Combine(directory,"旧目录","历史缺件.SLDPRT");
   return new NativeMissingAcquisition(new[]{1,2}.Select(id=>new Row{Path=path,Instance="旧夹具-"+id,Configuration="默认",Suppression=0}).ToArray());
  }
  internal static NativeMissingAcquisition FromCapturedFile(string source,string sha,string captured,string child,string executable)
  {
   if(!File.Exists(source)||!string.Equals(WorkingReuseHash(source),sha,StringComparison.OrdinalIgnoreCase)
    ||!File.Exists(executable)||!Path.GetFileName(executable).Equals("SLDWORKS.exe",StringComparison.OrdinalIgnoreCase))
    throw new InvalidDataException("原件或 SolidWorks 可执行文件无法核对");
   var evidence=new JavaScriptSerializer{MaxJsonLength=int.MaxValue}.Deserialize<CapturedComponent[]>(File.ReadAllText(captured));
   var rows=evidence.Where(row=>string.Equals(Path.GetFileName(row.Owner),Path.GetFileName(source),StringComparison.OrdinalIgnoreCase)
    &&string.Equals(Path.GetFileName(row.Path),child,StringComparison.OrdinalIgnoreCase))
    .Select(row=>new Row{Path=row.Path,Instance=row.Name.Split('/').Last(),Configuration=row.Config,Suppression=row.Suppression})
    .GroupBy(row=>new{row.Path,row.Instance,row.Configuration,row.Suppression}).Select(group=>group.First()).ToArray();
   if(rows.Length!=2||rows.Any(row=>row.Suppression!=0||File.Exists(row.Path))||rows.Select(row=>row.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=1)
    throw new InvalidDataException("采集数据未找到对应的两个原始被抑制缺件实例");
   return new NativeMissingAcquisition(rows,source,executable);
  }
  internal void PrepareWorkingFile(string target)
  {if(nativeOwner!=null)File.Copy(nativeOwner,target,true);}
  internal IEnumerable<Row> Current(string mode,string directory)
  {
   if(mode=="stored-missing-removed-all")yield break;
   var count=mode=="stored-missing-added"?3:mode=="stored-missing-removed"?1:2;
   for(var index=0;index<count;index++)
   {
    var row=original[index%original.Length];
    yield return new Row{Path=mode=="stored-missing-other-path"?Path.Combine(directory,"替换来源",Path.GetFileName(row.Path)):row.Path,
     Configuration=mode=="stored-missing-configuration"&&index==1?"另一配置":row.Configuration,
     Suppression=mode=="stored-missing-resolved"?2:row.Suppression,
     Instance=index==2?row.Instance+"-extra":mode=="stored-missing-renamed"&&index==1?row.Instance+"-renamed":row.Instance};
   }
  }
  internal bool TryCaller(IMethodCallMessage call,string rootPath,out object value)
  {
   value=null;
   if(call.MethodName=="GetExecutablePath"){value=solidWorksExecutable;return true;}
   if(call.MethodName=="GetProcessID"){using(var caller=Process.GetCurrentProcess())value=caller.Id;return true;}
   if(call.MethodName!="GetDocumentDependencies2")return false;
   var source=(string)call.Args[0];
   if(call.Args.Skip(1).Cast<bool>().Any(flag=>flag)||string.Equals(source,rootPath,StringComparison.OrdinalIgnoreCase)
    ||!string.Equals(WorkingReuseHash(source),WorkingReuseHash(rootPath),StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("必须读取独立下载并核实的原件及其直接引用");
   value=new[]{Path.GetFileName(ChildPath),ChildPath};return true;
  }
  internal void InstallSyntheticReader(object plugin,string rootPath)
  {readerCalls=0;if(nativeOwner==null)InstallReader(plugin,rootPath,original,()=>readerCalls++);}

  internal static void InstallReader(object plugin,string rootPath,Row[] stored,Action onRead=null)
  {
   var field=plugin.GetType().GetField("readArchivedOccurrences",Flags);
   if(field==null)return; // Earlier plugin binaries can replay the same acquisition fixture.
   var parameters=field.FieldType.GetMethod("Invoke").GetParameters().Select(p=>Expression.Parameter(p.ParameterType,p.Name)).ToArray();
   var invoke=field.FieldType.GetMethod("Invoke");
   var resultType=invoke.ReturnType.GetGenericArguments().Single();
   var read=new Func<string,string,string,object>((source,configuration,sha)=>
   {
    if(string.Equals(source,rootPath,StringComparison.OrdinalIgnoreCase)||configuration!="Default"
     ||!string.Equals(WorkingReuseHash(source),sha,StringComparison.OrdinalIgnoreCase)
     ||!string.Equals(WorkingReuseHash(rootPath),sha,StringComparison.OrdinalIgnoreCase))
     throw new InvalidOperationException("原生读取未使用指定版本的独立原件和配置");
    onRead?.Invoke();
    var rows=Array.CreateInstance(resultType.GetElementType(),stored.Length);
    for(var index=0;index<stored.Length;index++)
    {
     var target=Activator.CreateInstance(resultType.GetElementType());var row=stored[index];
     Set(target,"Path",row.Path);Set(target,"Configuration",row.Configuration);Set(target,"Instance",row.Instance);Set(target,"Suppression",row.Suppression);rows.SetValue(target,index);
    }
    return typeof(Task).GetMethod("FromResult").MakeGenericMethod(resultType).Invoke(null,new object[]{rows});
   });
   var call=Expression.Invoke(Expression.Constant(read),parameters[1],parameters[2],parameters[3]);
   field.SetValue(plugin,Expression.Lambda(field.FieldType,Expression.Convert(call,invoke.ReturnType),parameters).Compile());
  }
  internal void Verify(string mode,int downloads,int checkouts)
  {
   if(mode=="stored-missing-unchanged-pair"&&(downloads!=1||checkouts!=1||nativeOwner==null&&readerCalls!=1))
    throw new InvalidOperationException("未读取父版本原件或未完成所选装配获取");
   if(mode!="stored-missing-unchanged-pair"&&checkouts!=(mode=="stored-missing-checkout-race"?1:0))
    throw new InvalidOperationException("证据不一致时已经变更权限");
   if((mode=="stored-missing-unavailable"||mode=="stored-missing-corrupt")&&downloads==0)
    throw new InvalidOperationException("未尝试读取对应原件");
   Console.WriteLine("PASS: immutable owner acquisition "+mode+"; original downloads="+downloads+", checkouts="+checkouts+", native="+(nativeOwner!=null));
  }
 }
}
