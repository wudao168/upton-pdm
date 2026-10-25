using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Remoting;
using System.Runtime.Remoting.Messaging;
using System.Runtime.Remoting.Proxies;

internal static partial class Program
{
 static void DrawingSheetReferenceScenario()
 {
  var directory=Path.GetFullPath(Path.Combine(Path.GetTempPath(),"pdm-drawing-sheet-"+Guid.NewGuid().ToString("N")));
  Directory.CreateDirectory(directory);
  Type("DocumentRuleGate").GetMethod("Configure",Flags).Invoke(null,new object[]{Array.Empty<string>()});
  try
  {
   foreach(var mode in new[]{"multiple-sheets","empty-sheets","real-outside-model","real-missing-model"})
   {
    var folder=Path.Combine(directory,mode);Directory.CreateDirectory(folder);Directory.CreateDirectory(Path.Combine(folder,"old"));
    var root=LinkedNode(folder,"DRAWING.SLDDRW","Drawing");var a=LinkedNode(folder,"A.SLDPRT","Part");var b=LinkedNode(folder,"B.SLDPRT","Part");
    var unrelated=LinkedNode(Path.Combine(folder,"old"),"UNRELATED.SLDPRT","Part");
    foreach(var node in new[]{root,a,b,unrelated}){Set(node,"Revision","W1");Set(node,"CurrentRevision","W1");}
    var drawingPath=(string)Get(root,"FullPath");var aPath=(string)Get(a,"FullPath");var bPath=(string)Get(b,"FullPath");var unrelatedPath=(string)Get(unrelated,"FullPath");
    var oldPropertySource=Path.Combine(folder,"old","SHEET-PROPERTY-HISTORY.SLDASM");var actualMissing=Path.Combine(folder,"ACTUAL-MISSING.SLDPRT");
    var before=Directory.GetFiles(folder,"*",SearchOption.AllDirectories).ToDictionary(path=>path,File.ReadAllBytes);
    var appType=Type("PdmAddin").GetField("application",Flags).FieldType;var interop=appType.Assembly;
    var modelType=interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true);var viewType=interop.GetType("SolidWorks.Interop.sldworks.View",true);
    var extension=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDocExtension",true),call=>
     call.MethodName=="ListExternalFileReferences2"?null:throw new Exception("Unexpected model-extension mutation: "+call.MethodName)).GetTransparentProxy();
    object Part(string path)=>new SheetReferenceModelProxy(modelType,false,call=>
     call.MethodName=="GetType"?1:call.MethodName=="GetPathName"?path:call.MethodName=="GetTitle"?Path.GetFileName(path)
     :call.MethodName=="GetSaveFlag"?false:call.MethodName=="IsOpenedReadOnly"?true:call.MethodName=="get_Extension"?extension
     :call.MethodName=="GetEquationMgr"||call.MethodName=="GetDesignTable"?null:throw new Exception("Unexpected part mutation: "+call.MethodName)).GetTransparentProxy();
    var partA=Part(aPath);var partB=Part(bPath);var other=Part(unrelatedPath);var headerReads=0;
    object View(object model,string path,bool header)=>new AssemblyProxy(viewType,call=>
    {
     if(call.MethodName=="GetType"&&(call.MethodBase as MethodInfo)?.ReturnType==typeof(System.Type))return viewType;
     if(header)headerReads++;
     if(call.MethodName=="get_ReferencedDocument")return model;
     if(call.MethodName=="GetReferencedModelName")return path;
     if(call.MethodName=="get_ReferencedConfiguration")return header?"":"Default";
     throw new Exception("Unexpected drawing-view mutation: "+call.MethodName);
    }).GetTransparentProxy();
    var firstHeader=View(null,oldPropertySource,true);var secondHeader=View(other,unrelatedPath,true);var emptyHeader=View(other,unrelatedPath,true);
    var secondView=mode=="real-outside-model"?View(other,unrelatedPath,false):mode=="real-missing-model"?View(null,actualMissing,false):View(partB,bPath,false);
    var sheets=mode=="empty-sheets"?new object[]{new object[]{firstHeader},new object[]{secondHeader},Array.Empty<object>()}
     :new object[]{new object[]{firstHeader,View(partA,aPath,false)},new object[]{secondHeader,secondView},new object[]{emptyHeader}};
    var drawing=new SheetReferenceModelProxy(modelType,true,call=>
     call.MethodName=="GetType"?3:call.MethodName=="GetViews"?sheets:call.MethodName=="GetPathName"?drawingPath
     :call.MethodName=="GetTitle"?"DRAWING.SLDDRW":call.MethodName=="GetSaveFlag"?false:call.MethodName=="IsOpenedReadOnly"?true
     :throw new Exception("Unexpected drawing mutation: "+call.MethodName)).GetTransparentProxy();
    var app=new Proxy(appType,call=>call.MethodName=="GetType"&&(call.MethodBase as MethodInfo)?.ReturnType==typeof(System.Type)?appType
     :call.MethodName=="GetDocuments"?new[]{drawing,partA,partB,other}:throw new Exception("Sheet reference validation must not operate native windows: "+call.MethodName)).GetTransparentProxy();
    ((IList)Get(root,"ReferencedModels")).Add(a);((IList)Get(root,"ReferencedModels")).Add(b);
    var inventory=mode=="real-missing-model"?new[]{drawingPath,aPath,bPath,actualMissing}:new[]{drawingPath,aPath,bPath};
    Type("NativeArchiveManifest").GetMethod("ConfirmFileScope",Flags).Invoke(null,new object[]{root,inventory});
    var scanner=Type("SolidWorksReferenceTreeScanner").GetConstructor(Flags,null,new[]{appType},null).Invoke(new[]{app});
    Type("SolidWorksReferenceTreeScanner").GetMethod("UpdateDrawingReferences",Flags).Invoke(scanner,new[]{drawing,root});
    if(!(bool)Get(root,"DrawingViewsRead"))throw new Exception("Completed native view read was not recorded: "+mode);
    if(mode=="real-missing-model"&&!(bool)Get(root,"StructureIndexPending"))throw new Exception("A missing real view model was mistaken for a complete drawing index");
    var expected=mode=="empty-sheets"?Array.Empty<string>():new[]{aPath,mode=="real-outside-model"?unrelatedPath:mode=="real-missing-model"?actualMissing:bPath};
    var references=((IEnumerable)Get(root,"ReferencedModels")).Cast<object>().Select(node=>(string)Get(node,"FullPath")).ToArray();
    if(!new HashSet<string>(references,StringComparer.OrdinalIgnoreCase).SetEquals(expected)||references.Length!=expected.Length||headerReads!=0)
     throw new Exception("Sheet header became a model reference or a real view on another sheet was omitted: "+mode);
    foreach(var method in new[]{"LoadedReferencePaths","LoadedModelReferencePaths"})
    {
     var paths=((IEnumerable)Type("PdmAddin").GetMethod(method,Flags).Invoke(null,new[]{drawing})).Cast<string>().ToArray();
     if(!new HashSet<string>(paths,StringComparer.OrdinalIgnoreCase).SetEquals(expected)||paths.Length!=expected.Length)
      throw new Exception("Editing/discard dependency graph includes sheet metadata or misses a real view: "+method+" / "+mode);
    }
    Console.WriteLine("PASS: all-sheet model reference/permission/discard paths exclude sheet headers: "+mode);
    var manifest=New("ControlledOpenManifestDto");var files=(IList)Activator.CreateInstance(Property(manifest,"Files").PropertyType);
    foreach(var path in new[]{drawingPath,aPath,bPath}){var file=New("ControlledOpenFileDto");Set(file,"FileName",Path.GetFileName(path));Set(file,"RelativePath",Path.GetFileName(path));files.Add(file);}Set(manifest,"Files",files);
    var rejected=false;
    try{Type("PdmAddin").GetMethod("ValidateControlledLoadedPaths",Flags).Invoke(null,new[]{drawing,(object)drawingPath,manifest,app});}
    catch(TargetInvocationException error)when(error.InnerException is InvalidOperationException)
    {if(!error.InnerException.Message.Contains(mode=="real-missing-model"?"ACTUAL-MISSING.SLDPRT":"UNRELATED.SLDPRT"))throw;rejected=true;}
    if(rejected!=(mode=="real-outside-model"||mode=="real-missing-model"))throw new Exception("Actual loaded-view isolation was weakened or a sheet-only property owner caused rejection");
    Console.WriteLine("PASS: controlled open ignores sheet metadata but still rejects a real outside model view: "+mode);
    if(mode=="real-missing-model")
    {
     rejected=false;try{Type("PdmApiClient").GetMethod("CaptureArchiveSnapshot",Flags).Invoke(null,new object[]{root,inventory});}
     catch(TargetInvocationException error)when(error.InnerException is InvalidOperationException)
     {rejected=error.InnerException.Message.Contains("ACTUAL-MISSING.SLDPRT")&&(error.InnerException.Message.Contains("缺失")||error.InnerException.Message.Contains("尚未定位"));}
     if(!rejected)throw new Exception("A real missing drawing view in native scope no longer fails B09");
     Console.WriteLine("PASS: first real model view is retained and native-scope missing view still fails B09");
    }
    else if(mode!="real-outside-model")
    {
     var snapshot=Type("PdmApiClient").GetMethod("CaptureArchiveSnapshot",Flags).Invoke(null,new object[]{root,inventory});
     var captured=(Dictionary<string,object>)snapshot.GetType().GetMethod("CreateRoot",Flags).Invoke(snapshot,null);
     if(Equals(captured["structureIndexPending"],true)||((IEnumerable)captured["children"]).Cast<object>().Count()!=expected.Length)
      throw new Exception("Valid model views or empty sheets produced an incomplete index");
     Console.WriteLine("PASS: complete model views and empty sheets capture without false pending index: "+mode);
    }
    if(mode=="multiple-sheets")
    {
     var plugin=New("PdmAddin");SetField(plugin,"application",app);
     Type("PdmAddin").GetMethod("EnsureNoUnselectedReferenceOwners",Flags).Invoke(plugin,new object[]{new[]{unrelatedPath}});
     rejected=false;try{Type("PdmAddin").GetMethod("EnsureNoUnselectedReferenceOwners",Flags).Invoke(plugin,new object[]{new[]{aPath}});}
     catch(TargetInvocationException error)when(error.InnerException is InvalidOperationException){rejected=error.InnerException.Message.Contains("DRAWING.SLDDRW");}
     if(!rejected)throw new Exception("A real model owner stopped protecting editing switches");
     var discard=Type("PdmAddin").GetMethod("DiscardAffectedDocuments",Flags);
     var unrelatedClosing=((IEnumerable)discard.Invoke(plugin,new object[]{new HashSet<string>(new[]{unrelatedPath},StringComparer.OrdinalIgnoreCase)})).Cast<object>().Select(item=>(string)Get(item,"Path")).ToArray();
     var actualClosing=((IEnumerable)discard.Invoke(plugin,new object[]{new HashSet<string>(new[]{aPath},StringComparer.OrdinalIgnoreCase)})).Cast<object>().Select(item=>(string)Get(item,"Path")).ToArray();
     if(!new HashSet<string>(unrelatedClosing).SetEquals(new[]{unrelatedPath})||!new HashSet<string>(actualClosing).SetEquals(new[]{drawingPath,aPath}))
      throw new Exception("Discard closure included sheet property sources or omitted actual model owners");
     Console.WriteLine("PASS: edit/discard closure protects actual owners and leaves unrelated sheet property sources alone");
    }
    if(headerReads!=0||before.Any(pair=>!File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)))throw new Exception("Sheet header was inspected as a model or source bytes changed");
   }
  }
  finally
  {
   if(!directory.StartsWith(Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\')+"\\pdm-drawing-sheet-",StringComparison.OrdinalIgnoreCase))throw new IOException("Refusing non-QA cleanup");
   Directory.Delete(directory,true);
  }
 }
 sealed class SheetReferenceModelProxy:RealProxy,IRemotingTypeInfo
 {
  readonly System.Type modelType;readonly bool drawing;readonly Func<IMethodCallMessage,object> callback;
  public SheetReferenceModelProxy(System.Type modelType,bool drawing,Func<IMethodCallMessage,object> callback):base(modelType){this.modelType=modelType;this.drawing=drawing;this.callback=callback;}
  public string TypeName{get;set;}
  public bool CanCastTo(System.Type type,object value)=>type==typeof(object)||type.Namespace=="SolidWorks.Interop.sldworks"
   &&(type.Name=="ModelDoc2"||type.Name=="IModelDoc2"||drawing&&(type.Name=="DrawingDoc"||type.Name=="IDrawingDoc"));
  public override IMessage Invoke(IMessage message)
  {
   var call=(IMethodCallMessage)message;
   try{var result=call.MethodName=="GetType"&&(call.MethodBase as MethodInfo)?.ReturnType==typeof(System.Type)?modelType:callback(call);
    return result as IMessage??new ReturnMessage(result,call.Args,call.ArgCount,call.LogicalCallContext,call);}
   catch(Exception error){return new ReturnMessage(error,call);}
  }
 }
}
