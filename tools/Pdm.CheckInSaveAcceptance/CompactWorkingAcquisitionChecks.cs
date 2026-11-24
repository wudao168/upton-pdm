using System;
using System.IO;
using System.Threading.Tasks;

internal static partial class Program
{
 static async Task CompactWorkingAcquisition()
 {
  foreach(var scenario in new[]{"cold-occurrence-open-latest-edit","cold-occurrence-verified","cold-occurrence-local-disk-edit",
     "cold-occurrence-wrong-loaded-path","cold-occurrence-configuration-change","root-version-conflict","foreign-lease"})
   await AcquireAssemblyEditScenario(scenario,compactWorkspace:true);
  await SelectedPartCheckoutScenario(Path.Combine(Path.GetTempPath(),"pdm-compact-part-"+Guid.NewGuid().ToString("N")),true);
  var resolver=Type("PdmDocumentIdentityStore").GetField("workspaceRootResolver",Flags);
  var saved=resolver.GetValue(null);
  var root=Path.Combine(Path.GetTempPath(),"pdm-compact-scope-"+Guid.NewGuid().ToString("N"));
  resolver.SetValue(null,new Func<string>(()=>root));
  try
  {
   const string code="P700014-1";
   var identity=Guid.NewGuid()+"|"+Guid.NewGuid();
   string Compact(string kind,string project,string location)=>(string)TreeCallStatic("ManagedCadPaths","CompactDirectory",location,kind,identity,project);
   void Check(string name,string folder,bool expected)
   {
    var actual=(bool)TreeCallStatic("PdmAddin","IsCurrentProjectWorkingPath",Path.Combine(folder,"sample.SLDASM"),code);
    if(actual!=expected)throw new Exception("工作路径分类不符："+name);
    Console.WriteLine("PASS: "+name);
   }
   Check("project View workspace",Path.Combine(root,"View",code),true);
   Check("managed compact working",Compact("w",code,root),true);
   Check("managed compact cached drawing",Compact("b",code,root),true);
   Check("managed compact historical",Compact("r",code,root),false);
   Check("managed compact read-only preview",Compact("l",code,root),false);
   Check("managed compact derived",Compact("d",code,root),false);
   Check("managed compact foreign project",Compact("w","P700014-2",root),false);
   Check("unmanaged View folder",Path.Combine(root,"unrelated","View",code),false);
   var machine=Type("ManagedCadPaths").GetProperty("CompactWorkspaceRoot",Flags).GetValue(null).ToString();
   Check("machine compact working",Compact("w",code,machine),true);
   Check("machine compact other project",Compact("w","P700014-2",machine),false);
  }
  finally{resolver.SetValue(null,saved);}
 }
}
