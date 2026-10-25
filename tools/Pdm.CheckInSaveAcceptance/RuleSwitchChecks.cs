using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;

internal static partial class Program
{
 static void RuleSwitchScenario()
 {
  var folder=Path.Combine(Path.GetTempPath(),"pdm-rule-switch-"+Guid.NewGuid().ToString("N"));
  Directory.CreateDirectory(folder);
  var identity=Type("PdmDocumentIdentityStore");
  var binding=identity.GetField("bindingDirectory",Flags);
  var oldBinding=binding.GetValue(null);
  var configure=Type("DocumentRuleGate").GetMethod("Configure",Flags);
  void Rules(params string[] disabled)=>configure.Invoke(null,new object[]{disabled});
  void MustFail(Action action,string text)
  {
   try{action();}catch(TargetInvocationException error) when(error.InnerException.Message.Contains(text)){return;}
   throw new Exception("Expected rule rejection: "+text);
  }
  try
  {
   binding.SetValue(null,Path.Combine(folder,"bindings"));
   Rules();
   var source=Path.Combine(folder,"ROOT.SLDASM");File.WriteAllText(source,"server base");
   var hash=(string)Type("PdmAddin").GetMethod("ComputeFileHash",Flags).Invoke(null,new object[]{source});
   var document=Guid.NewGuid();var project=Guid.NewGuid();var version=Guid.NewGuid();
   var manifest=New("ControlledOpenManifestDto");Set(manifest,"ProjectId",project);
   var file=New("ControlledOpenFileDto");Set(file,"DocumentId",document);Set(file,"VersionId",version);
   Set(file,"FileName","ROOT.SLDASM");Set(file,"RelativePath","ROOT.SLDASM");Set(file,"Sha256",hash);
   Set(file,"FileLength",new FileInfo(source).Length);Set(file,"IsRoot",true);
   var files=(IList)Activator.CreateInstance(Property(manifest,"Files").PropertyType);files.Add(file);Set(manifest,"Files",files);
   var writeBinding=identity.GetMethod("TryWriteControlledVersion",Flags);
   writeBinding.Invoke(null,new object[]{source,document,project,version,"A",hash,new FileInfo(source).Length});
   var appType=Type("PdmAddin").GetField("application",Flags).FieldType;
   var app=new Proxy(appType,call=>call.MethodName=="GetDocuments"?Array.Empty<object>():throw new Exception("Unexpected native rewrite: "+call.MethodName)).GetTransparentProxy();
   var materializer=Type("ControlledReferenceMaterializer");
   var prepare=materializer.GetMethod("Prepare",Flags);var editable=materializer.GetMethod("PrepareEditable",Flags);
   File.WriteAllText(source,"unsubmitted local change");
   MustFail(()=>prepare.Invoke(null,new[]{app,source,manifest}),"未核对的本地修改");
   editable.Invoke(null,new[]{app,source,manifest});
   if(File.ReadAllText(source)!="unsubmitted local change")throw new Exception("Editable transition overwrote local content");
   Rules("F11");prepare.Invoke(null,new[]{app,source,manifest});
   if(File.ReadAllText(source)!="unsubmitted local change")throw new Exception("Disabled F11 overwrote local content");
   Rules();
   Set(manifest,"ProjectId",Guid.NewGuid());
   MustFail(()=>editable.Invoke(null,new[]{app,source,manifest}),"未核对的本地修改");
   Set(manifest,"ProjectId",project);
   Console.WriteLine("PASS: F11 editable re-entry preserves identified local changes; unrelated identity and readonly preview remain strict; disabling only skips rewrite");

   var readVerified=materializer.GetMethod("ReadVerifiedCopyHash",Flags);
   MustFail(()=>readVerified.Invoke(null,new[]{source,file}),"服务器版本副本校验失败");
   var readArgs=new object[]{source,null};identity.GetMethod("TryReadLocalMetadata",Flags).Invoke(null,readArgs);
   var relocatedHash=(string)Type("PdmAddin").GetMethod("ComputeFileHash",Flags).Invoke(null,new object[]{source});
   identity.GetMethod("TryRecordResolvedReferences",Flags).Invoke(null,new[]{(object)source,readArgs[1],relocatedHash});
   if((string)readVerified.Invoke(null,new[]{source,file})!=relocatedHash)throw new Exception("Verified relocation rejected");
   File.AppendAllText(source,"tampered after relocation");
   MustFail(()=>readVerified.Invoke(null,new[]{source,file}),"服务器版本副本校验失败");
   Console.WriteLine("PASS: I07 accepts verified relocated bytes and rejects later corruption before discard restore");

   object Node(string path)
   {
    var node=New("CadTreeNode");Set(node,"FullPath",path);Set(node,"FileName",Path.GetFileName(path));
    Set(node,"Kind",Enum.Parse(Type("CadDocumentKind"),"Assembly"));Set(node,"InstancePath",Path.GetFileName(path));return node;
   }
   var root=Node(source);var missing=Node(Path.Combine(folder,"MISSING.SLDPRT"));
   var emptyPath=Path.Combine(folder,"EMPTY.SLDPRT");File.WriteAllText(emptyPath,"");var empty=Node(emptyPath);
   var children=(IList)Get(root,"Children");children.Add(missing);children.Add(empty);
   var validate=Type("NativeArchiveManifest").GetMethod("Validate",Flags);
   Rules("B09");
   MustFail(()=>validate.Invoke(null,new object[]{root,new[]{source,emptyPath}}),"树内图档为空");
   children.Remove(empty);validate.Invoke(null,new object[]{root,new[]{source}});
   if(!(bool)Get(root,"StructureIndexPending"))throw new Exception("Disabled missing-file precheck lost pending structure flag");
   Rules();MustFail(()=>validate.Invoke(null,new object[]{root,new[]{source}}),"树内图档缺失");
   Console.WriteLine("PASS: B09 disabled still evaluates enabled B10; skipped indexing check stays pending; re-enable restores blocking");
   Rules("F20");ControlledReferenceScenario(true);Rules();
  }
  finally{Rules();binding.SetValue(null,oldBinding);Directory.Delete(folder,true);}
 }
}
