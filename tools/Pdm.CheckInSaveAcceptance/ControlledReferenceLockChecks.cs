using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;

internal static partial class Program
{
 [System.Runtime.InteropServices.DllImport("kernel32.dll",CharSet=System.Runtime.InteropServices.CharSet.Unicode,SetLastError=true)]
 static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string path,uint access,uint sharing,IntPtr security,uint mode,uint flags,IntPtr template);
 static void ControlledReferenceLockScenario(string directory)
 {
  directory=Path.GetFullPath(directory);Directory.CreateDirectory(directory);
  var store=Type("PdmDocumentIdentityStore");var binding=store.GetField("bindingDirectory",Flags);var resolver=store.GetField("workspaceRootResolver",Flags);
  var oldBinding=binding.GetValue(null);var oldResolver=resolver.GetValue(null);
  binding.SetValue(null,Path.Combine(directory,"bindings"));resolver.SetValue(null,new Func<string>(()=>directory));
  void Check(bool ok,string text){if(!ok)throw new Exception(text);Console.WriteLine("PASS: "+text);}
  try
  {
   foreach(var mode in new[]{"shared-read","shared-writer","identity-lock-on-replace","locked-identity-stream","exclusive","partial-failure","rollback-lock","changed-during-discovery","loaded"})
   {
    var folder=Path.Combine(directory,mode);Directory.CreateDirectory(folder);
    var manifest=New("ControlledOpenManifestDto");Set(manifest,"ProjectId",Guid.NewGuid());
    var files=(IList)Activator.CreateInstance(Property(manifest,"Files").PropertyType);
    var count=mode=="shared-writer"?387:3;
    for(var i=0;i<count;i++)
    {
     var name=i==0?"ROOT.SLDASM":i==1?"SUB.SLDASM":"PART-"+i+".SLDPRT";var path=Path.Combine(folder,name);File.WriteAllText(path,"saved "+name);
     var file=New("ControlledOpenFileDto");Set(file,"DocumentId",Guid.NewGuid());Set(file,"VersionId",Guid.NewGuid());Set(file,"FileName",name);Set(file,"RelativePath",name);
     Set(file,"Sha256",WorkingReuseHash(path));Set(file,"FileLength",new FileInfo(path).Length);Set(file,"Revision","W1");files.Add(file);
     if(!(bool)store.GetMethod("TryWriteControlledVersion",Flags).Invoke(null,new[]{(object)path,Get(file,"DocumentId"),Get(manifest,"ProjectId"),Get(file,"VersionId"),"W1",Get(file,"Sha256"),Get(file,"FileLength")}))throw new Exception("Fixture receipt failed");
     File.SetAttributes(path,FileAttributes.ReadOnly);
    }
    Set(manifest,"Files",files);var root=Path.Combine(folder,"ROOT.SLDASM");var original=File.ReadAllBytes(root);
    var appType=Type("PdmAddin").GetField("application",Flags).FieldType;var locks=new Dictionary<string,FileStream>(StringComparer.OrdinalIgnoreCase);var calls=0;
    var loaded=new Proxy(appType.Assembly.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true),call=>call.MethodName=="GetPathName"?root:throw new Exception("Loaded window must stay untouched")).GetTransparentProxy();
    var app=new Proxy(appType,call=>{
     switch(call.MethodName)
     {
      case "GetDocuments":return mode=="loaded"?new[]{loaded}:new object[0];
      case "GetDocumentDependencies2":
       var owner=(string)call.Args[0];if(!owner.EndsWith(".SLDASM",StringComparison.OrdinalIgnoreCase))return new string[0];
       if(mode=="shared-read"||mode=="shared-writer"||mode=="exclusive")
       {
        if(mode!="shared-read")File.SetAttributes(owner,FileAttributes.Normal);
        locks[owner]=new FileStream(owner,FileMode.Open,mode=="shared-read"?FileAccess.Read:FileAccess.ReadWrite,
         mode=="exclusive"?FileShare.None:FileShare.ReadWrite|FileShare.Delete);
        File.SetAttributes(owner,FileAttributes.ReadOnly);
       }
       if(mode=="locked-identity-stream")
       {
        var stream=CreateFile(owner+":UptonPdm.DocumentId",0x80000000,0,IntPtr.Zero,3,0,IntPtr.Zero);
        if(stream.IsInvalid)throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
        locks[owner+":identity"]=new FileStream(stream,FileAccess.Read);
       }
       if(mode=="changed-during-discovery"){File.SetAttributes(owner,FileAttributes.Normal);File.AppendAllText(owner,"|unverified edit");}
       return new[]{"PART-2.SLDPRT",Path.Combine(directory,"external","PART-2.SLDPRT"),"SUB.SLDASM",Path.Combine(directory,"external","SUB.SLDASM")};
      case "ReplaceReferencedDocument":
       calls++;var target=(string)call.Args[0];
       if(mode=="identity-lock-on-replace")
       {
        File.AppendAllText(target,"|partial");
        var stream=CreateFile(target+":UptonPdm.DocumentId",0x80000000,0,IntPtr.Zero,3,0,IntPtr.Zero);
        if(stream.IsInvalid)throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
        locks[target+":identity"]=new FileStream(stream,FileAccess.Read);return false;
       }
       if(mode=="rollback-lock") {File.AppendAllText(target,"|partial");locks[target]=new FileStream(target,FileMode.Open,FileAccess.ReadWrite,FileShare.None);return false;}
       if(mode=="partial-failure"){File.AppendAllText(target,"|partial");return false;}
       if(locks.TryGetValue(target,out var held)&&held.CanWrite){held.Position=held.Length;var bytes=System.Text.Encoding.UTF8.GetBytes("|resolved");held.Write(bytes,0,bytes.Length);held.Flush();}
       else File.AppendAllText(target,"|resolved");return true;
      default:throw new Exception("Unexpected native window mutation: "+call.MethodName);
     }
    }).GetTransparentProxy();
    Exception failure=null;
    try{Type("ControlledReferenceMaterializer").GetMethod("Prepare",Flags).Invoke(null,new[]{app,(object)root,manifest});}
    catch(System.Reflection.TargetInvocationException error){failure=error.InnerException;}
    finally{foreach(var handle in locks.Values)handle.Dispose();}
    if(failure!=null)Console.WriteLine("DIAGNOSTIC: "+mode+" "+failure);
    if(mode=="shared-read"||mode=="shared-writer"||mode=="locked-identity-stream")
    {
     Check(failure==null,mode+"：共享读取及原生写句柄不再导致备份复制失败，文件数="+count);
     Check(calls==4,mode+"：总装与子装配的引用均已定位，不跳过子装配");
     foreach(var file in files.Cast<object>().Take(2))
     {
      var path=Path.Combine(folder,(string)Get(file,"FileName"));
      Check((bool)Type("ControlledReferenceMaterializer").GetMethod("Matches",Flags).Invoke(null,new[]{(object)path,file}),mode+"：定位后内容与原服务器版本绑定可核验："+Path.GetFileName(path));
      Check((File.GetAttributes(path)&FileAttributes.ReadOnly)!=0,mode+"：恢复只读状态："+Path.GetFileName(path));
     }
    }
    else if(mode=="partial-failure"||mode=="identity-lock-on-replace")Check(failure==null&&File.ReadAllBytes(root).SequenceEqual(original),mode+"：原生替换失败时完整回滚后再允许原生打开验证");
    else if(mode=="rollback-lock")
    {
     var backups=Directory.GetFiles(folder,".ref-*.bak");
     Check(failure is IOException&&backups.Length==1,"回滚被锁定时停止打开，保留完整恢复副本");
     Check(File.ReadAllBytes(backups[0]).SequenceEqual(original)&&failure.Message.Contains(backups[0]),"恢复副本保持原始内容，并在中文错误中给出位置");
    }
    else if(mode=="exclusive")Check(failure is IOException&&calls==0&&File.ReadAllBytes(root).SequenceEqual(original),"独占写锁仍安全拦截，不绕过或改写原文档");
    else if(mode=="changed-during-discovery")Check(failure is IOException&&calls==0&&File.ReadAllText(root).EndsWith("|unverified edit"),"读取引用期间的未知修改被保留并拦截");
    else Check(failure==null&&File.ReadAllBytes(root).SequenceEqual(original),"已打开的总装窗口保持原样，不备份或重写");
    if(mode!="rollback-lock")Check(Directory.GetFiles(folder,".ref-*.bak").Length==0,mode+"：成功或安全拦截后不遗留无效备份");
   }
  }
  finally{binding.SetValue(null,oldBinding);resolver.SetValue(null,oldResolver);}
 }
}
