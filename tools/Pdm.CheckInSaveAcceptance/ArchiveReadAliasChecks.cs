using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

internal static partial class Program
{
 static async Task ArchiveReadAliasScenario(string directory)
 {
  void Check(bool ok,string text){if(!ok)throw new Exception(text);Console.WriteLine("PASS: "+text);}
  foreach(var mode in new[]{"repeat-null","hidden-repeat-null","cold-null","dirty-between","content-between","same","different","dirty","missing","other-name","assembly","drawing","missing-config","mutates","ordinary-open","unresolved-null"})
  {
   var folder=Path.Combine(directory,mode);Directory.CreateDirectory(Path.Combine(folder,"expected"));Directory.CreateDirectory(Path.Combine(folder,"loaded"));
   var kind=mode=="assembly"?"Assembly":mode=="drawing"?"Drawing":"Part";
   var name="同名接头"+(kind=="Assembly"?".SLDASM":kind=="Drawing"?".SLDDRW":".SLDPRT");
   var expected=Path.GetFullPath(Path.Combine(folder,"expected",name));var actual=Path.GetFullPath(Path.Combine(folder,"loaded",mode=="other-name"?"OTHER.SLDPRT":name));
   if(mode=="ordinary-open")actual=expected;
   File.WriteAllText(expected,"saved native fixture");if(mode!="missing")File.WriteAllText(actual,mode=="different"?"different native fixture":"saved native fixture");
   var node=New("CadTreeNode");Set(node,"Kind",Enum.Parse(Type("CadDocumentKind"),kind));Set(node,"FullPath",expected);Set(node,"FileName",name);Set(node,"Configuration","Default");Set(node,"DocumentId",Guid.NewGuid());
   var id=Get(node,"DocumentId");var appType=Type("PdmAddin").GetField("application",Flags).FieldType;var interop=appType.Assembly;var closes=0;var reads=0;var opens=0;var visibility=true;var dirtyBetween=false;
   object Mock(string type,Func<System.Runtime.Remoting.Messaging.IMethodCallMessage,object> callback)=>new Proxy(interop.GetType("SolidWorks.Interop.sldworks."+type,true),callback).GetTransparentProxy();
   var manager=Mock("CustomPropertyManager",call=>{
    switch(call.MethodName){case "GetNames":return new[]{"Description"};case "get_Count":return 1;
     case "Get6":reads++;if(mode=="mutates")File.WriteAllText(actual,"changed during property reading");var output=call.Args;output[2]="fixture description";output[3]="fixture description";output[4]=true;output[5]=false;return new System.Runtime.Remoting.Messaging.ReturnMessage(2,output,output.Length,call.LogicalCallContext,call);
     default:throw new Exception(call.MethodName);}
   });
   var extension=Mock("ModelDocExtension",call=>call.MethodName=="get_CustomPropertyManager"?manager:throw new Exception(call.MethodName));
   var configuration=Mock("Configuration",call=>call.MethodName=="get_Name"?"Default":throw new Exception(call.MethodName));
   var model=Mock("ModelDoc2",call=>{
    switch(call.MethodName){case "GetPathName":return actual;case "GetTitle":return name;case "GetSaveFlag":return mode=="dirty"||dirtyBetween;case "IsOpenedReadOnly":return true;
     case "GetConfigurationByName":return mode=="missing-config"?null:configuration;case "get_Extension":return extension;
     default:throw new Exception("不得修改或切换已加载文档："+call.MethodName);}
   });
   var app=new Proxy(appType,call=>{
    switch(call.MethodName){case "get_ActiveDoc":return null;case "GetDocuments":return mode=="ordinary-open"||mode=="hidden-repeat-null"||mode=="unresolved-null"?new object[0]:new[]{model};
     case "GetOpenDocumentByName":return null;case "GetDocumentVisible":return visibility;case "DocumentVisible":visibility=(bool)call.Args[0];return null;
     case "OpenDoc6":opens++;if(mode=="cold-null"||mode=="unresolved-null"||opens>1&&(mode.EndsWith("null")||mode.EndsWith("between"))){var output=call.Args;output[4]=65536;output[5]=2;return new System.Runtime.Remoting.Messaging.ReturnMessage(null,output,output.Length,call.LogicalCallContext,call);}return model;case "CloseDoc":closes++;return null;
     default:throw new Exception("不得关闭现有窗口或切换模型："+call.MethodName);}
   }).GetTransparentProxy();
   var addin=New("PdmAddin");SetField(addin,"application",app);SetField(addin,"currentTree",node);
   var cards=Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(Type("CadDocumentKind"),typeof(IReadOnlyList<string>)));
   Exception failure=null;object properties=null;
   try{properties=TreeCall(addin,"ReadArchivePreflightProperties",node,cards,null);}catch(System.Reflection.TargetInvocationException error){failure=error.InnerException;}
   var allowed=mode!="unresolved-null"&&(mode=="same"||mode=="ordinary-open"||mode.EndsWith("null")||mode.EndsWith("between"));
   Check((failure==null)==allowed,mode+"：仅相同内容且无未保存修改的零件别名可读取");
   Check(closes==(mode=="ordinary-open"?1:0)&&visibility,mode+"：不关闭同名用户窗口，恢复原可见设置");
   Check(File.ReadAllText(expected)=="saved native fixture"&&(string)Get(node,"FullPath")==expected&&Equals(Get(node,"DocumentId"),id),mode+"：原文件、文件名及PLM身份不被替换");
   if(!allowed){
    Check(reads==0||mode=="mutates",mode+"：冲突不能读取其他图档的属性");
    if(mode!="missing-config"&&mode!="unresolved-null")Check(failure.Message.Contains(expected)&&failure.Message.Contains(actual),mode+"：冲突提示明确列出预期和实际文件位置");
    if(mode=="unresolved-null"){
     var text=(string)Type("PdmAddin").GetMethod("ArchivePreflightError",Flags).Invoke(null,new object[]{failure});
     Check(text.Contains("同名")&&text.Contains(expected)&&!text.Contains("文件无法读取或被占用"),"65536：找不到可安全复用文档时准确说明同名冲突，不误报磁盘读写故障");
    }
    continue;
   }
   Check((string)((IDictionary)properties)["全局/Description"]=="fixture description",mode+"：完整读取已验证的属性和值");
   if(mode!="same"&&!mode.EndsWith("null")&&!mode.EndsWith("between"))continue;
   using(var handler=new ReferenceRecoveryHandler(request=>new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("[]")}))
   using(var client=(IDisposable)New("PdmApiClient","http://fixture.invalid/"))
   {
    ((HttpClient)Field(client,"httpClient")).Dispose();SetField(client,"httpClient",new HttpClient(handler){BaseAddress=new Uri("http://fixture.invalid/")});SetField(addin,"apiClient",client);
    if(mode=="dirty-between")dirtyBetween=true;
    if(mode=="content-between")File.WriteAllText(actual,"changed between phases");
    object file=null;Exception prepareFailure=null;
    try{var task=(Task)TreeCall(addin,"PrepareBatchCheckInFileAsync",node,CancellationToken.None,null,null,cards,null);await task;file=Get(task,"Result");}catch(Exception error){prepareFailure=error;}
    if(mode.EndsWith("between")){
     Check(prepareFailure!=null&&prepareFailure.ToString().Contains(actual)&&opens<=1&&closes==0,mode+"：复用前重新校验内容和未保存状态，不覆盖后来修改");continue;
    }
    if(prepareFailure!=null)throw prepareFailure;
    var copy=(string)Get(file,"UploadCopyPath");
    Check(File.ReadAllText(copy)=="saved native fixture"&&Path.GetFileName(copy)==name&&closes==0,mode+"：准备阶段沿用预期源文件，不关闭或复制另一条路径");
    Check(opens<=1,mode+"：跨阶段复用已验证的文档，不再次OpenDoc6触发65536");
    Type("PdmAddin").GetMethod("DeleteCheckInUploadCopy",Flags).Invoke(null,new object[]{copy});
   }
  }
 }
}
