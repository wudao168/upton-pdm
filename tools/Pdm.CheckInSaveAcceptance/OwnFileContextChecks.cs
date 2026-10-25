using System;
using System.Collections;
using System.IO;
using System.Linq;

internal static partial class Program
{
 static void OwnFileContextScenario()
 {
  var directory=Path.Combine(Path.GetTempPath(),"pdm-own-file-context-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
  try
  {
   var matches=Type("PdmAddin").GetMethods(Flags).Single(method=>method.Name=="ReferenceSnapshotMatchesTree"&&method.GetParameters().Length==2);
   foreach(var mode in new[]{"suppressed","resolved","hidden","lightweight","configuration","quantity","missing","identity","filename","child-suppressed","child-quantity","child-configuration"})
   {
    var isParent=mode.StartsWith("child-",StringComparison.Ordinal);
    var node=LinkedNode(directory,isParent?"ROOT.SLDASM":"PART.SLDPRT",isParent?"Assembly":"Part");
    var child=LinkedNode(directory,"CHILD.SLDPRT","Part");
    if(isParent)((IList)Get(node,"Children")).Add(child);
    if(mode=="resolved")Set(node,"Status",Enum.Parse(Type("CadReferenceStatus"),"Suppressed"));
    var snapshot=LinkedSnapshot(node);
    var changed=isParent?child:node;
    if(mode.EndsWith("suppressed",StringComparison.Ordinal))Set(changed,"Status",Enum.Parse(Type("CadReferenceStatus"),"Suppressed"));
    if(mode=="resolved")Set(node,"Status",Enum.Parse(Type("CadReferenceStatus"),"Normal"));
    if(mode=="hidden")Set(node,"Status",Enum.Parse(Type("CadReferenceStatus"),"Hidden"));
    if(mode=="lightweight")Set(node,"Status",Enum.Parse(Type("CadReferenceStatus"),"Lightweight"));
    if(mode.EndsWith("configuration",StringComparison.Ordinal))Set(changed,"Configuration","另一已保存配置");
    if(mode.EndsWith("quantity",StringComparison.Ordinal))Set(changed,"Quantity",3);
    if(mode=="missing")Set(node,"Status",Enum.Parse(Type("CadReferenceStatus"),"Missing"));
    if(mode=="identity")Set(node,"DocumentId",Guid.NewGuid());
    if(mode=="filename")Set(node,"FileName","OTHER.SLDPRT");
    var expected=mode=="suppressed"||mode=="resolved"||mode=="hidden"||mode=="lightweight"||mode=="configuration"||mode=="quantity";
    if((bool)matches.Invoke(null,new[]{snapshot,node})!=expected)throw new Exception("File occurrence comparison incorrect: "+mode);
    Console.WriteLine("PASS: file context comparison "+mode);
   }
  }
  finally {Directory.Delete(directory,true);}
 }
}
