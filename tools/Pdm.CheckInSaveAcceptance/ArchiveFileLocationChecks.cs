using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

internal static partial class Program
{
 static void ArchiveFileLocationsScenario(string output)
 {
  var names=new[]{"BC1-带灯按钮绿色-20211230095433766.SLDPRT","BP1-内六角圆柱头螺钉-20220317094720347.SLDPRT","4mm.SLDPRT",
   "SS5Y-8位电磁阀座.SLDASM","LHH30CA2R1000Z0C-E20E20(1).SLDASM","GHH30CA2R1000Z0C-20_20滑块.SLDPRT",
   "_GHR30R1000C+RC-20_20滑轨.SLDPRT","禁止开门警示贴.SLDPRT"};
  void Check(bool value,string text){if(!value)throw new Exception(text);Console.WriteLine("PASS: "+text);}
  foreach(var mode in new[]{"native","saved-only","native-null","existing-old","missing","ambiguous","invalid-list","late-invalid-list"})
  {
   var folder=Path.Combine(Path.GetFullPath(output),mode);var current=Path.Combine(folder,"1");var old=Path.Combine(folder,"CopyDesignTemp");
   Directory.CreateDirectory(current);
   var rootPath=Path.Combine(current,"CW0030002.10.SLDASM");File.WriteAllText(rootPath,"fixture assembly; no CAD session opened");
   foreach(var name in names.Where(name=>mode!="missing"||name!=names[0]))File.WriteAllText(Path.Combine(current,name),"fixture "+name);
   if(mode=="existing-old"){Directory.CreateDirectory(old);File.WriteAllText(Path.Combine(old,names[0]),"existing distinct original; must not replace");}
   var duplicate=Path.Combine(folder,"another",names[0]);if(mode=="ambiguous"){Directory.CreateDirectory(Path.GetDirectoryName(duplicate));File.WriteAllText(duplicate,"different candidate");}
   object Node(string path,string kind){var node=New("CadTreeNode");Set(node,"FullPath",path);Set(node,"SourceReferencePath",path);Set(node,"FileName",Path.GetFileName(path));Set(node,"Kind",Enum.Parse(Type("CadDocumentKind"),kind));Set(node,"Configuration","默认");return node;}
   var root=Node(rootPath,"Assembly");var nodes=names.Select(name=>Node(Path.Combine(old,name),name.EndsWith(".SLDASM")?"Assembly":"Part")).ToArray();
   foreach(var node in nodes){Set(node,"Status",Enum.Parse(Type("CadReferenceStatus"),"Missing"));Set(node,"InstancePath","ROOT/"+Get(node,"FileName"));}
   Set(nodes[3],"IsSuppressed",true);Set(nodes[4],"IsSuppressed",true);
   foreach(var index in new[]{0,1,2,3,4,7})((IList)Get(root,"Children")).Add(nodes[index]);
   ((IList)Get(nodes[4],"Children")).Add(nodes[5]);((IList)Get(nodes[4],"Children")).Add(nodes[6]);
   var directory="original-working-directory";var reads=new Dictionary<string,int>();
   var appType=Type("PdmAddin").GetField("application",Flags).FieldType;
   var app=new Proxy(appType,call=>{
    switch(call.MethodName){
     case "GetRunningCommandInfo":call.Args[0]=0;call.Args[1]="";call.Args[2]=false;return true;
     case "GetCurrentWorkingDirectory":return directory;
     case "SetCurrentWorkingDirectory":directory=(string)call.Args[0];return true;
     case "GetDocumentDependencies2":
      var owner=(string)call.Args[0];reads[owner]=reads.TryGetValue(owner,out var count)?count+1:1;
      if(!File.Exists(owner)||directory!=Path.GetDirectoryName(owner)||(bool)call.Args[1]||(bool)call.Args[3])throw new Exception("核对必须使用实际所属文件和目录");
      if(mode=="invalid-list"||mode=="late-invalid-list"&&owner!=rootPath)return new[]{"incomplete"};
      if(mode=="native-null")return null;
      var indexes=owner==rootPath?new[]{0,1,2,3,4,7}:new[]{5,6};var values=new List<string>();
      foreach(var index in indexes){values.Add(names[index]);values.Add(Path.Combine((bool)call.Args[2]&&mode!="saved-only"?current:old,names[index]));}
      if(mode=="ambiguous"&&(bool)call.Args[2]&&owner==rootPath){values.Add(names[0]);values.Add(duplicate);}return values.ToArray();
     default:throw new Exception("定位阶段不能打开、关闭、保存或切换文档："+call.MethodName);
    }
   }).GetTransparentProxy();
   var addin=New("PdmAddin");SetField(addin,"application",app);
   var before=Directory.GetFiles(folder,"*",SearchOption.AllDirectories).ToDictionary(path=>path,File.ReadAllText);
   Exception failure=null;IDictionary aliases=null;
   try{aliases=(IDictionary)TreeCall(addin,"ResolveArchiveFileLocations",root);}catch(TargetInvocationException error){failure=error.InnerException;}
   if(mode=="ambiguous"||mode=="invalid-list"||mode=="late-invalid-list"){
    Check(failure is InvalidOperationException,mode+"：多目标或原生清单不完整时拒绝猜测");
    Check(nodes.All(node=>(string)Get(node,"FullPath")==Path.Combine(old,(string)Get(node,"FileName"))),mode+"：后续检查失败时还原本次树路径变更");
   }
   else
   {
    Check(failure==null,mode+"：定位阶段完成");
    foreach(var index in Enumerable.Range(0,8).Where(index=>index!=0||mode!="missing"&&mode!="existing-old"))
     Check((string)Get(nodes[index],"FullPath")==Path.Combine(current,names[index]),mode+"：找到实际文件 "+names[index]);
    Check(nodes.All(node=>(string)Get(node,"SourceReferencePath")==Path.Combine(old,(string)Get(node,"FileName"))),mode+"：保留原始引用地址及全部文件名");
    Check(Get(nodes[3],"Status").ToString()=="Suppressed"&&Get(nodes[4],"Status").ToString()=="Suppressed",mode+"：定位不解除压缩或修改配置");
    Check(reads.Values.All(count=>count==2)&&reads.Count==2,mode+"：每个所属装配只批量读取一次保存与解析清单");
    Exception validation=null;try{Type("NativeArchiveManifest").GetMethod("Validate",Flags).Invoke(null,new object[]{root,before.Keys.ToArray()});}catch(TargetInvocationException error){validation=error.InnerException;}
    Check((validation!=null)==(mode=="missing"),mode+"：结构校验接受已定位文件，真正缺件仍拦截");
    if(mode=="existing-old")Check((string)Get(nodes[0],"FullPath")==Path.Combine(old,names[0])&&!aliases.Contains(Path.Combine(old,names[0])),"原地址仍有文件时不擅自改用同名副本");
   }
   Check(directory=="original-working-directory",mode+"：完成或异常均恢复原工作目录");
   Check(before.All(pair=>File.ReadAllText(pair.Key)==pair.Value)&&Directory.GetFiles(folder,"*",SearchOption.AllDirectories).Length==before.Count,mode+"：无文件保存、改名、复制或内容修改");
  }
 }
}
