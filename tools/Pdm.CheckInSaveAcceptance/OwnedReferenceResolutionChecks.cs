using System;
using System.IO;
using System.Reflection;

internal static partial class Program
{
 static void OwnedReferenceResolutionScenario(string output)
 {
  void Check(bool value,string text){if(!value)throw new Exception(text);Console.WriteLine("PASS: "+text);}
  foreach(var mode in new[]{"complete","dirty","preexisting","active","later-load","wrong-model","native-failed","already-resolved"})
  {
   var f=new SavedInsertionFixture(Path.Combine(output,mode));
   var interop=Type("PdmAddin").GetField("application",Flags).FieldType.Assembly;
   var calls=0;var initial=mode=="already-resolved"?2:1;
   var component=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.Component2",true),call=>
   {
    if(call.MethodName=="GetSuppression")return initial;
    if(call.MethodName=="GetModelDoc2")return mode=="wrong-model"?f.Other:f.Owner;
    if(call.MethodName=="SetSuppression2"){
     calls++;if(Convert.ToInt32(call.Args[0])!=3)throw new Exception("禁止递归解析全装配");
     f.Begin();initial=2;
     if(mode=="native-failed")throw new InvalidOperationException("模拟原生解析失败");
     if(mode=="later-load")f.OnCommandRead=()=>f.Begin("另一次加载.SLDPRT");
     return 2;
    }
    throw new Exception("禁止其他原生操作："+call.MethodName);
   }).GetTransparentProxy();
   if(mode=="preexisting")f.Begin();if(mode=="active")f.UiActive=true;if(mode=="dirty")f.Dirty=true;
   var owner=New("CadTreeNode");Set(owner,"FullPath",f.OwnerPath);Set(owner,"FileName","总装.SLDASM");
   Exception failure=null;try{Type("PdmAddin").GetMethod("ResolveReferenceComponent",Flags).Invoke(f.Plugin,new[]{component,owner});}catch(TargetInvocationException error){failure=error.InnerException;}
   var success=mode=="complete"||mode=="dirty"||mode=="already-resolved";
   Check((failure==null)==success,mode+"：解析结果与保护条件一致");
   Check((int)Field(f.Plugin,"ownedReferenceResolutionDepth")==0&&(int)Field(f.Plugin,"refreshSuppressionDepth")==0,mode+"：操作计数始终释放");
   if(success)Check((int)Field(f.Plugin,"nativeFileOpenInProgress")==0,mode+"：仅在本次解析完成证据充分时结束加载标记");
   else if(mode!="active")Check((int)Field(f.Plugin,"nativeFileOpenInProgress")==1,mode+"：异常或新加载仍保留保护");
   if(mode=="preexisting"||mode=="active"||mode=="already-resolved")Check(calls==0,mode+"：没有不必要的解析调用");
   if(mode=="dirty")Check(f.Dirty,"原有未保存状态不被清除");
  }
 }
}
