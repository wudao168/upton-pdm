using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Remoting.Messaging;

internal static partial class Program
{
 // These tests cover scope, permissions, receipts and rollback. Real CAD geometry
 // is tested separately. The fake accepts native writes only to owned copies.
 static object ArchiveNativeMock(Type appType, Func<IMethodCallMessage,object> original,
  Func<IMethodCallMessage,object> references=null)
 {
  var documents=new Dictionary<string,object>(StringComparer.OrdinalIgnoreCase);object active=null;
  var interop=appType.Assembly;
  object ProxyFor(string name,Func<IMethodCallMessage,object> callback)=>new Proxy(interop.GetType("SolidWorks.Interop.sldworks."+name,true),callback).GetTransparentProxy();
  bool Owned(string path)=>!string.IsNullOrWhiteSpace(path)&&Path.GetFullPath(path).StartsWith(Path.Combine(Path.GetTempPath(),"UPTON-PDM","native-isolation")+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)&&Path.GetFileName(path).StartsWith("uplm_",StringComparison.Ordinal);
  var configuration=ProxyFor("Configuration",call=>call.MethodName=="get_Name"?"Default":call.MethodName=="get_Type"?(object)0:call.MethodName=="GetParent"?null:throw new Exception(call.MethodName));
  var manager=ProxyFor("ConfigurationManager",call=>call.MethodName=="get_ActiveConfiguration"?configuration:throw new Exception(call.MethodName));
  var sketch=ProxyFor("SketchManager",call=>call.MethodName=="GetSketchBlockDefinitions"?new object[0]:throw new Exception(call.MethodName));
  var extension=ProxyFor("ModelDocExtension",call=>{
   if(call.MethodName=="ListExternalFileReferences2")return null;
   if(call.MethodName=="GetMassProperties2"){var output=(object[])call.Args.Clone();output[1]=0;return new ReturnMessage(null,output,output.Length,call.LogicalCallContext,call);}
   if(call.MethodName=="GetOLEObjects"||call.MethodName=="GetRenderMaterials")return new object[0];
   if(call.MethodName=="GetOLEObjectCount"||call.MethodName=="GetDecalsCount")return 0;
   if(call.MethodName=="GetUserPreferenceToggle")return true;
   throw new Exception("未模拟的副本扩展调用："+call.MethodName);
  });
  object Model(string path)=>ProxyFor("ModelDoc2",call=>{
   if(!Owned(path))throw new Exception("测试禁止写入原图档："+path);
   switch(call.MethodName){case "GetPathName":return path;case "GetTitle":return Path.GetFileName(path);case "GetType":return path.EndsWith(".SLDASM",StringComparison.OrdinalIgnoreCase)?2:path.EndsWith(".SLDDRW",StringComparison.OrdinalIgnoreCase)?3:1;
    case "get_ConfigurationManager":return manager;case "GetConfigurationNames":return new[]{"Default"};case "GetConfigurationByName":return configuration;
    case "get_Extension":return extension;case "get_SketchManager":return sketch;case "ShowConfiguration2":return true;case "GetSaveFlag":return false;
    case "GetEquationMgr":case "GetDesignTable":case "FirstFeature":case "GetFirstAnnotation2":return null;
    case "Save3":var output=(object[])call.Args.Clone();output[1]=0;output[2]=0;return new ReturnMessage(true,output,output.Length,call.LogicalCallContext,call);
    default:throw new Exception("未模拟的副本调用："+call.MethodName);}
  });
  return new Proxy(appType,call=>{
   var path=call.Args.Length>0?call.Args[0] as string:null;
   if(call.MethodName=="GetDocuments")return ((original(call) as object[])??new object[0]).Concat(documents.Values).ToArray();
   if(call.MethodName=="get_ActiveDoc"&&active!=null)return active;
   if(call.MethodName=="GetDocumentDependencies2")return references==null?new string[0]:references(call);
   if(call.MethodName=="GetDocumentVisible")return true;if(call.MethodName=="DocumentVisible")return null;
   if(call.MethodName=="GetOpenDocumentByName"&&Owned(path))return documents.TryGetValue(path,out var loaded)?loaded:null;
   if(call.MethodName=="OpenDoc6"&&Owned(path)){var output=(object[])call.Args.Clone();output[4]=0;output[5]=0;var loaded=Model(path);documents[path]=loaded;active=loaded;return new ReturnMessage(loaded,output,output.Length,call.LogicalCallContext,call);}
   if(call.MethodName=="ActivateDoc3"&&documents.Keys.Any(key=>Path.GetFileName(key)==path)){active=documents.Single(pair=>Path.GetFileName(pair.Key)==path).Value;var output=(object[])call.Args.Clone();output[3]=0;return new ReturnMessage(active,output,output.Length,call.LogicalCallContext,call);}
   if(call.MethodName=="CloseDoc"&&documents.Keys.Any(key=>Path.GetFileName(key)==path)){documents.Remove(documents.Keys.Single(key=>Path.GetFileName(key)==path));active=null;return null;}
   if(call.MethodName=="ReplaceReferencedDocument"&&Owned(path))return references==null?true:references(call);
   return original(call);
  }).GetTransparentProxy();
 }
}
