using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;

internal static partial class Program
{
 static void HistoricalReferenceScopeScenario()
 {
  var folder=Path.Combine(Path.GetTempPath(),"UPLM-HistoricalScope-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
  object Make(string name,string kind,bool registered)
  {
   var node=New("CadTreeNode");var path=Path.Combine(folder,name);File.WriteAllText(path,"isolated scope fixture");
   Set(node,"FullPath",path);Set(node,"FileName",name);Set(node,"Kind",Enum.Parse(Type("CadDocumentKind"),kind));
   if(registered)Set(node,"DocumentId",Guid.NewGuid());return node;
  }
  var root=Make("ROOT.SLDASM","Assembly",true);var owner=Make("OLD-VALVE.SLDASM","Assembly",true);
  var part=Make("CHANGED.SLDPRT","Part",true);var drawing=Make("CHANGED.SLDDRW","Drawing",true);
  ((IList)Get(root,"Children")).Add(owner);((IList)Get(root,"Children")).Add(part);((IList)Get(root,"Children")).Add(drawing);
  ((IList)Get(drawing,"ReferencedModels")).Add(part);
  var snapshot=New("DocumentReferenceNodeDto");Set(snapshot,"DocumentId",Get(owner,"DocumentId"));
  var version=New("DocumentVersionDto");Set(version,"ReferenceSnapshot",snapshot);Set(owner,"SuppressedArchiveVersion",version);
  Set(owner,"SuppressedArchiveSha256","verified-parent");Set(owner,"SuppressedLocalStructureSha256","verified-parent");
  var missing=Enumerable.Range(1,8).Select(i=>Make("OLD-"+i+".SLDPRT","Part",false)).ToArray();
  foreach(var node in missing)((IList)Get(owner,"Children")).Add(node);
  void Check(params object[] nodes)
  {
   var selected=Array.CreateInstance(Type("BatchOperationItem"),nodes.Length);
   for(var i=0;i<nodes.Length;i++)selected.SetValue(New("BatchOperationItem",nodes[i],0),i);
   Type("PdmAddin").GetMethod("ValidateHistoricalReferenceScope",Flags).Invoke(null,new object[]{root,selected});
  }
  Check(part,drawing);
  Console.WriteLine("PASS: changed part and its drawing proceed despite an unrelated historical valve gap");
  foreach(var target in new[]{root,owner})
  {
   var rejected=false;
   try{Check(target);}catch(TargetInvocationException error)when(error.InnerException is InvalidOperationException)
   {
    rejected=missing.All(node=>error.InnerException.Message.Contains((string)Get(node,"FileName")))
     && error.InnerException.Message.Contains("历史遗漏引用");
   }
   if(!rejected)throw new Exception("An actual upload requiring eight unconfirmed old references was not blocked with its full list");
  }
  Console.WriteLine("PASS: actual root or valve upload cannot silently register historical omissions");
  foreach(var node in missing)Set(node,"DocumentId",Guid.NewGuid());Check(root);
  Console.WriteLine("PASS: independently registered references no longer block the archive scope");
  Set(missing[0],"DocumentId",null);Set(owner,"SuppressedLocalStructureSha256","new-parent-content");Check(owner);
  Console.WriteLine("PASS: a changed parent is not falsely treated as proof of a historical omission");
 }
}
