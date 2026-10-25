using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

internal static partial class Program
{
 static async Task WorkingCopyReuseScenario(string evidenceRoot)
 {
  var json=new JavaScriptSerializer{MaxJsonLength=int.MaxValue,RecursionLimit=256};
  var match=Type("PdmAddin").GetMethod("WorkingReuseManifestsMatch",Flags);
  object Manifest(int count)
  {
   var value=New("ControlledOpenManifestDto");Set(value,"ForEdit",true);Set(value,"ProjectId",Guid.NewGuid());Set(value,"RootDocumentId",Guid.NewGuid());Set(value,"RootVersionId",Guid.NewGuid());Set(value,"RootRelativePath","ROOT.SLDASM");
   var files=(IList)Activator.CreateInstance(Property(value,"Files").PropertyType);
   for(var i=0;i<count;i++){var file=New("ControlledOpenFileDto");Set(file,"DocumentId",i==0?Get(value,"RootDocumentId"):Guid.NewGuid());Set(file,"VersionId",i==0?Get(value,"RootVersionId"):Guid.NewGuid());Set(file,"FileName",i==0?"ROOT.SLDASM":"PART-"+i+".SLDPRT");Set(file,"RelativePath",Get(file,"FileName"));Set(file,"Sha256",new string('A',64));Set(file,"FileLength",100L+i);Set(file,"Configuration","Default");files.Add(file);}Set(value,"Files",files);return value;
  }
  object Copy(object value)=>json.Deserialize(json.Serialize(value),value.GetType());
  var baseline=Manifest(214);
  foreach(var mode in new[]{"same","project","root-version","child-version","source-hash","length","configuration","relative-path","document-id","duplicate","missing","readonly-base","readonly-result"})
  {
   var next=Copy(baseline);var files=(IList)Get(next,"Files");var file=files[13];
   switch(mode){case "project":Set(next,"ProjectId",Guid.NewGuid());break;case "root-version":Set(next,"RootVersionId",Guid.NewGuid());break;case "child-version":Set(file,"VersionId",Guid.NewGuid());break;case "source-hash":Set(file,"Sha256",new string('B',64));break;case "length":Set(file,"FileLength",999L);break;case "configuration":Set(file,"Configuration","Changed");break;case "relative-path":Set(file,"RelativePath","other/"+Get(file,"FileName"));break;case "document-id":Set(file,"DocumentId",Guid.NewGuid());break;case "duplicate":files[13]=files[12];break;case "missing":files.RemoveAt(13);break;case "readonly-base":case "readonly-result":Set(next,"ForEdit",false);break;}
   var before=mode=="readonly-base"?next:baseline;var after=mode=="readonly-base"?baseline:next;
   if((bool)match.Invoke(null,new[]{before,after})!=(mode=="same"||mode=="readonly-base"))throw new Exception("Incorrect 214-file working baseline decision: "+mode);
   Console.WriteLine("PASS: working baseline "+mode);
  }
  if(!string.IsNullOrWhiteSpace(evidenceRoot))
  {
   var audit=(Dictionary<string,object>)json.DeserializeObject(File.ReadAllText(Path.Combine(evidenceRoot,"reopen-baseline-audit.json")));
   var runs=(object[])audit["runs"];
   var real=new List<object>();
   foreach(Dictionary<string,object> run in runs)
   {
    var value=Manifest(1);var files=(IList)Activator.CreateInstance(Property(value,"Files").PropertyType);
    foreach(Dictionary<string,object> binding in (object[])run["bindings"]){var file=New("ControlledOpenFileDto");Set(file,"DocumentId",Guid.Parse((string)binding["documentId"]));Set(file,"VersionId",Guid.Parse((string)binding["versionId"]));Set(file,"FileName",binding["fileName"]);Set(file,"RelativePath",binding["fileName"]);Set(file,"Sha256",binding["sourceSha256"]);Set(file,"FileLength",Convert.ToInt64(binding["fileLength"]));files.Add(file);if((string)binding["fileName"]=="CW0030002.10.SLDASM"){Set(value,"ProjectId",Guid.Parse((string)binding["projectId"]));Set(value,"RootDocumentId",Get(file,"DocumentId"));Set(value,"RootVersionId",Get(file,"VersionId"));Set(value,"RootRelativePath",binding["fileName"]);}}Set(value,"Files",files);real.Add(value);
   }
   if(real.Count!=3||real.Skip(1).Any(next=>!(bool)match.Invoke(null,new[]{real[0],next})))throw new Exception("Real 214-file repeated acquisitions did not match");
   Console.WriteLine("PASS: all three recorded 214-file acquisitions share the same working baseline");
   var histories=(Dictionary<string,object>)json.DeserializeObject(File.ReadAllText(Path.Combine(evidenceRoot,"server-identity-evidence-v2","valve-and-root-version-snapshots.json")));
   var cad=(Dictionary<string,object>)json.DeserializeObject(File.ReadAllText(Path.Combine(evidenceRoot,"current-cad-evidence.json")));
   foreach(Dictionary<string,object> version in (object[])histories["versions"])
   {
    var name=(string)version["FileName"];
    var snapshot=json.Deserialize((string)version["SnapshotJson"],Type("DocumentReferenceNodeDto"));
    if(name=="SS5Y-8位电磁阀座.SLDASM")
    {
     var unresolved=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(Type("CadTreeNode")));
     for(var i=0;i<8;i++){var pending=New("CadTreeNode");Set(pending,"FileName","original-unindexed-"+i+".SLDPRT");unresolved.Add(pending);}
     var originalChildren=Get(snapshot,"Children");var originalId=Get(snapshot,"DocumentId");var originalPending=Get(snapshot,"StructureIndexPending");var args=new object[]{snapshot,unresolved,false};
     var accepted=(bool)Type("PdmAddin").GetMethod("WorkingReuseChildrenMatch",Flags).Invoke(null,args);
     if(!accepted||!(bool)args[2]||Get(snapshot,"Children")!=originalChildren||((IList)Get(snapshot,"Children")).Count!=0
       ||!Equals(Get(snapshot,"DocumentId"),originalId)||!Equals(Get(snapshot,"StructureIndexPending"),originalPending))throw new Exception("Legacy missing structure was changed or declared complete");
     Console.WriteLine("PASS: legacy empty SS5Y index stays explicitly pending without adopting its eight external children");continue;
    }
    var prefix=name=="CW0030002.10.SLDASM"?"":"CW0030002.101-1/";
    var nodes=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(Type("CadTreeNode")));
    foreach(Dictionary<string,object> component in (object[])cad["Components"]){var instance=(string)component["Name"];if(!instance.StartsWith(prefix)||instance.Substring(prefix.Length).Contains("/"))continue;var node=New("CadTreeNode");Set(node,"FileName",Path.GetFileName((string)component["Path"]));Set(node,"Configuration",component["Configuration"]);Set(node,"Status",Enum.Parse(Type("CadReferenceStatus"),(bool)component["Virtual"]?"Virtual":"Normal"));nodes.Add(node);}
    var invoke=new object[]{snapshot,nodes,false};var childrenMatch=Type("PdmAddin").GetMethod("WorkingReuseChildrenMatch",Flags);
    if(!(bool)childrenMatch.Invoke(null,invoke)||(bool)invoke[2])throw new Exception("Recorded native reference/configuration differs for "+name);
    Set(nodes[0],"Configuration","changed-for-test");if((bool)childrenMatch.Invoke(null,new object[]{snapshot,nodes,false}))throw new Exception("Changed occurrence configuration accepted");
    Console.WriteLine("PASS: actual "+nodes.Count+" children match "+name+" and an altered occurrence configuration is rejected");
   }
   var recordedRoot=((object[])histories["versions"]).Cast<Dictionary<string,object>>().Single(v=>(string)v["FileName"]=="CW0030002.10.SLDASM");
   var rootSnapshot=json.Deserialize((string)recordedRoot["SnapshotJson"],Type("DocumentReferenceNodeDto"));
   var runDirectory=(string)((Dictionary<string,object>)runs[2])["directory"];
   var realRootPath=Path.Combine(runDirectory,"CW0030002.10.SLDASM");
   var interop=Type("PdmAddin").GetField("application",Flags).FieldType.Assembly;
   var activeConfiguration="Default";var changedOccurrence=false;
   var configurationProxy=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.Configuration",true),call=>call.MethodName=="get_Name"?activeConfiguration:throw new Exception(call.MethodName)).GetTransparentProxy();
   var configurationManager=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ConfigurationManager",true),call=>call.MethodName=="get_ActiveConfiguration"?configurationProxy:throw new Exception(call.MethodName)).GetTransparentProxy();
   var componentRows=((object[])cad["Components"]).Cast<Dictionary<string,object>>().ToArray();
   var loadedRows=((object[])cad["DocumentsBefore"]).Cast<Dictionary<string,object>>().ToDictionary(row=>(string)row["Path"],StringComparer.OrdinalIgnoreCase);
   var firstRootInstance=(string)componentRows.First(row=>!((string)row["Name"]).Contains("/"))["Name"];
   object RecordedComponent(Dictionary<string,object> row)
   {
    var name=(string)row["Name"];var prefix=name+"/";var path=(string)row["Path"];
    object loaded=null;
    if(loadedRows.TryGetValue(path,out var state))loaded=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true),call=>{switch(call.MethodName){case "GetPathName":return path;case "GetSaveFlag":return state["Dirty"];case "IsOpenedReadOnly":return state["ReadOnly"];default:throw new Exception("Recorded loaded model must not be activated or read in another configuration: "+call.MethodName);}}).GetTransparentProxy();
    return new Proxy(interop.GetType("SolidWorks.Interop.sldworks.Component2",true),call=>{switch(call.MethodName){case "GetPathName":return path;case "get_ReferencedConfiguration":return changedOccurrence&&name==firstRootInstance?"incorrect-occurrence":row["Configuration"];case "get_IsVirtual":return row["Virtual"];case "GetModelDoc2":return loaded;case "GetSuppression":return Convert.ToInt32(row["Suppression"]);case "GetChildren":return componentRows.Where(child=>((string)child["Name"]).StartsWith(prefix)&&!((string)child["Name"]).Substring(prefix.Length).Contains("/")).Select(RecordedComponent).ToArray();default:throw new Exception("Recorded component mutation "+call.MethodName);}}).GetTransparentProxy();
   }
   var rootComponents=componentRows.Where(row=>!((string)row["Name"]).Contains("/")).Select(RecordedComponent).ToArray();
   var rootProxy=new AssemblyProxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true),call=>{switch(call.MethodName){case "GetPathName":return realRootPath;case "GetType":return 2;case "get_ConfigurationManager":return configurationManager;case "GetComponents":if(!(bool)call.Args[0])throw new Exception("Scope must remain direct children");return rootComponents;default:throw new Exception("Unexpected assembly mutation "+call.MethodName);}}).GetTransparentProxy();
   var exactEvidence=(Dictionary<string,object>)json.DeserializeObject(File.ReadAllText(Path.Combine(Path.GetDirectoryName(evidenceRoot),"false-new-change-fix-20261023","domain-fix","cw-evidence","real-current-snapshots.json")));
   var exactSnapshots=(IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(Guid),Type("DocumentReferenceNodeDto")));
   var manifestFiles=((IEnumerable)Get(real[2],"Files")).Cast<object>().ToDictionary(file=>(Guid)Get(file,"DocumentId"));
   foreach(Dictionary<string,object> row in (object[])exactEvidence["rows"])
   {
    var id=Guid.Parse((string)row["Id"]);if(!manifestFiles.TryGetValue(id,out var file))continue;
    if((Guid)Get(file,"VersionId")!=Guid.Parse((string)row["VersionId"])||!string.Equals((string)Get(file,"Sha256"),(string)row["Sha256"],StringComparison.OrdinalIgnoreCase))throw new Exception("Exact snapshot evidence does not match recorded manifest");
    exactSnapshots.Add(id,json.Deserialize((string)row["SnapshotJson"],Type("DocumentReferenceNodeDto")));
   }
   var loadedMatch=Type("PdmAddin").GetMethod("WorkingReuseLoadedContextMatchesCore",Flags);
   bool MatchRecordedRoot()=>(bool)loadedMatch.Invoke(null,new[]{rootProxy,(object)runDirectory,real[2],rootSnapshot,exactSnapshots,null});
   if(!string.IsNullOrEmpty((string)Get(rootSnapshot,"Configuration")))throw new Exception("Expected the real legacy root's missing configuration");
   if(!MatchRecordedRoot())throw new Exception("Real 214-file legacy empty-root configuration was rejected");
   var lazySnapshots=(IDictionary)Activator.CreateInstance(exactSnapshots.GetType());lazySnapshots.Add((Guid)Get(real[2],"RootDocumentId"),rootSnapshot);
   var requested=new HashSet<Guid>();var completed=false;
   for(var attempt=0;attempt<214;attempt++)
   {
    var missing=new HashSet<Guid>();
    if((bool)loadedMatch.Invoke(null,new[]{rootProxy,(object)runDirectory,real[2],rootSnapshot,lazySnapshots,missing})){completed=true;break;}
    if(missing.Count==0)throw new Exception("Recorded occurrence context rejected without a precise missing version");
    foreach(var id in missing){if(!requested.Add(id)||!exactSnapshots.Contains(id))throw new Exception("Snapshot requests repeated or escaped the verified manifest");lazySnapshots.Add(id,exactSnapshots[id]);}
   }
   if(!completed||requested.Count==0||requested.Count>=214)throw new Exception("Lazy exact snapshot validation did not converge within the missing scope");
   Console.WriteLine("PASS: recorded "+componentRows.Length+" occurrences validate with "+requested.Count+" exact missing snapshots instead of 214 requests");
   changedOccurrence=true;if(MatchRecordedRoot())throw new Exception("Changed native occurrence configuration was accepted under empty root configuration");
   changedOccurrence=false;Set(rootSnapshot,"Configuration","recorded-root");if(MatchRecordedRoot())throw new Exception("Known mismatched root configuration was accepted");
   activeConfiguration="recorded-root";if(!MatchRecordedRoot())throw new Exception("Known matching root configuration was rejected");
   Set(rootSnapshot,"Configuration",string.Empty);var rootFile=((IEnumerable)Get(real[2],"Files")).Cast<object>().Single(file=>(Guid)Get(file,"DocumentId")== (Guid)Get(real[2],"RootDocumentId"));
   Set(rootFile,"Configuration","manifest-root");if(MatchRecordedRoot())throw new Exception("Known manifest root configuration was ignored under an empty legacy snapshot");
   activeConfiguration="manifest-root";if(!MatchRecordedRoot())throw new Exception("Matching manifest root configuration was rejected");
   Console.WriteLine("PASS: actual 214-file root with empty historical configuration reuses only matching native occurrences; known root and changed occurrence configurations remain strict");
  }
  WorkingReuseInstanceConfigurationScenario();
  WorkingReuseSnapshotVersionScenario();
  await WorkingReusePermissionScenario();
 }

 static void WorkingReuseSnapshotVersionScenario()
 {
  foreach(var mode in new[]{"exact","version","document","file-name","stored-hash","stored-length","missing-tree","empty-hash"})
  {
   var document=Guid.NewGuid();var id=Guid.NewGuid();var file=New("ControlledOpenFileDto");Set(file,"DocumentId",document);Set(file,"VersionId",id);Set(file,"FileName","SUB.SLDASM");Set(file,"Sha256",new string('A',64));Set(file,"FileLength",100L);
   var exact=New("DocumentVersionDto");Set(exact,"Id",id);Set(exact,"Sha256",new string('A',64));Set(exact,"FileLength",100L);var snapshot=New("DocumentReferenceNodeDto");Set(snapshot,"DocumentId",document);Set(snapshot,"FileName","SUB.SLDASM");Set(exact,"ReferenceSnapshot",snapshot);
   switch(mode){case "version":Set(exact,"Id",Guid.NewGuid());break;case "document":Set(snapshot,"DocumentId",Guid.NewGuid());break;case "file-name":Set(snapshot,"FileName","OTHER.SLDASM");break;case "stored-hash":Set(exact,"Sha256",new string('B',64));break;case "stored-length":Set(exact,"FileLength",101L);break;case "missing-tree":Set(exact,"ReferenceSnapshot",null);break;case "empty-hash":Set(exact,"Sha256",string.Empty);break;}
   if((bool)Type("PdmAddin").GetMethod("WorkingReuseSnapshotVersionMatches",Flags).Invoke(null,new[]{file,exact})!=(mode=="exact"))throw new Exception("Root/nested canonical snapshot guard "+mode);
   Console.WriteLine("PASS: root/nested exact snapshot identity and canonical bytes "+mode);
  }
 }

 static void WorkingReuseInstanceConfigurationScenario()
 {
  var folder=Path.Combine(Path.GetTempPath(),"pdm-reuse-instance-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
  try
  {
   var names=new[]{"ROOT.SLDASM","SUB.SLDASM","A.SLDPRT","B.SLDPRT"};var paths=names.ToDictionary(name=>name,name=>Path.Combine(folder,name));
   foreach(var pair in paths)File.WriteAllText(pair.Value,pair.Key+" verified bytes");
   var ids=names.ToDictionary(name=>name,name=>Guid.NewGuid());var interop=Type("PdmAddin").GetField("application",Flags).FieldType.Assembly;
   var rootManager=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ConfigurationManager",true),call=>call.MethodName=="get_ActiveConfiguration"?new Proxy(interop.GetType("SolidWorks.Interop.sldworks.Configuration",true),cfg=>cfg.MethodName=="get_Name"?"Default":throw new Exception(cfg.MethodName)).GetTransparentProxy():throw new Exception(call.MethodName)).GetTransparentProxy();
   var json=new JavaScriptSerializer();
   foreach(var mode in new[]{"separate-instance-trees","swapped-children","wrong-instance-configuration","wrong-loaded-path","suppressed-instance","shallow-parent-exact-complete","shallow-parent-wrong-membership","legacy-exact-empty","legacy-exact-empty-readonly-dirty"})
   {
    var wrongModelScopeReads=0;var dirty=mode=="legacy-exact-empty-readonly-dirty";
    var sub=new AssemblyProxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true),call=>{switch(call.MethodName){case "GetPathName":return paths["SUB.SLDASM"];case "GetSaveFlag":return dirty;case "IsOpenedReadOnly":return true;default:wrongModelScopeReads++;throw new Exception("Must not read or change shared ModelDoc active configuration/subtree: "+call.MethodName);}}).GetTransparentProxy();
    object Leaf(string name,string configuration)
    {
     var filePath=paths.ContainsKey(name)?paths[name]:Path.Combine(folder,"external",name);
     var model=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true),call=>call.MethodName=="GetPathName"?(mode=="wrong-loaded-path"&&name=="A.SLDPRT"?Path.Combine(folder,"old",name):filePath):throw new Exception("Leaf mutation "+call.MethodName)).GetTransparentProxy();
     return new Proxy(interop.GetType("SolidWorks.Interop.sldworks.Component2",true),call=>{switch(call.MethodName){case "GetPathName":return filePath;case "get_ReferencedConfiguration":return configuration;case "get_IsVirtual":return false;case "GetModelDoc2":return model;default:throw new Exception("Leaf component mutation "+call.MethodName);}}).GetTransparentProxy();
    }
    var a=Leaf("A.SLDPRT","A");var b=Leaf("B.SLDPRT","B");var external=Leaf("OLD-EXTERNAL.SLDPRT","Default");
    var components=new[]{"C1","C2"}.Select(config=>new Proxy(interop.GetType("SolidWorks.Interop.sldworks.Component2",true),call=>{switch(call.MethodName){case "GetPathName":return paths["SUB.SLDASM"];case "get_ReferencedConfiguration":return mode=="wrong-instance-configuration"&&config=="C1"?"C3":config;case "get_IsVirtual":return false;case "GetModelDoc2":return sub;case "GetSuppression":return mode=="suppressed-instance"&&config=="C1"?0:2;case "GetChildren":if(config=="C2")return new[]{b};if(mode=="suppressed-instance")return new object[0];if(mode.StartsWith("legacy-"))return new[]{external};return new[]{mode=="swapped-children"?b:a};default:throw new Exception("Instance mutation "+call.MethodName);}}).GetTransparentProxy()).ToArray();
    var root=new AssemblyProxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true),call=>{switch(call.MethodName){case "GetPathName":return paths["ROOT.SLDASM"];case "get_ConfigurationManager":return rootManager;case "GetComponents":return components;case "GetType":return 2;default:throw new Exception("Root mutation "+call.MethodName);}}).GetTransparentProxy();
    object Node(string name,string configuration,object[] children)=>new{documentId=ids[name],fileName=name,kind=name.EndsWith("SLDASM")?0:1,configuration,children};
    var aSnapshot=Node("A.SLDPRT","A",new object[0]);var bSnapshot=Node("B.SLDPRT","B",new object[0]);
    var shallow=mode.StartsWith("shallow-")||mode.StartsWith("legacy-");
    var snapshot=json.Deserialize(json.Serialize(Node("ROOT.SLDASM","Default",new[]{Node("SUB.SLDASM","C1",shallow?new object[0]:new[]{aSnapshot}),Node("SUB.SLDASM","C2",new[]{bSnapshot})})),Type("DocumentReferenceNodeDto"));
    var manifest=New("ControlledOpenManifestDto");Set(manifest,"RootDocumentId",ids["ROOT.SLDASM"]);var files=(IList)Activator.CreateInstance(Property(manifest,"Files").PropertyType);
    foreach(var name in names){var file=New("ControlledOpenFileDto");Set(file,"DocumentId",ids[name]);Set(file,"VersionId",Guid.NewGuid());Set(file,"FileName",name);Set(file,"RelativePath",name);Set(file,"Sha256",WorkingReuseHash(paths[name]));Set(file,"FileLength",new FileInfo(paths[name]).Length);files.Add(file);}Set(manifest,"Files",files);
    var dictionary=(IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(Guid),Type("DocumentReferenceNodeDto")));dictionary.Add(ids["ROOT.SLDASM"],snapshot);
    if(shallow){var ownChildren=mode.StartsWith("legacy-")?new object[0]:new[]{mode=="shallow-parent-wrong-membership"?bSnapshot:aSnapshot};dictionary.Add(ids["SUB.SLDASM"],json.Deserialize(json.Serialize(Node("SUB.SLDASM","C1",ownChildren)),Type("DocumentReferenceNodeDto")));}
    var accepted=(bool)Type("PdmAddin").GetMethod("WorkingReuseLoadedContextMatchesCore",Flags).Invoke(null,new[]{root,(object)folder,manifest,snapshot,dictionary,null});
    var expected=mode=="separate-instance-trees"||mode=="suppressed-instance"||mode=="shallow-parent-exact-complete"||mode=="legacy-exact-empty";
    if(accepted!=expected||wrongModelScopeReads!=0)throw new Exception("Incorrect actual occurrence verification: "+mode);
    Console.WriteLine("PASS: occurrence-specific configuration/paths "+mode+" without reading shared model active subtree");
   }
  }
  finally{if(!Path.GetFullPath(folder).StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase))throw new IOException("Unexpected QA folder");Directory.Delete(folder,true);}
 }
 static string WorkingReuseHash(string path){using(var hash=System.Security.Cryptography.SHA256.Create())return BitConverter.ToString(hash.ComputeHash(File.ReadAllBytes(path))).Replace("-","");}

 static async Task WorkingReusePermissionScenario()
 {
  var directory=Path.Combine(Path.GetTempPath(),"pdm-reuse-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
  var identity=Type("PdmDocumentIdentityStore");var bindings=identity.GetField("bindingDirectory",Flags);var rootResolver=identity.GetField("workspaceRootResolver",Flags);var oldBindings=bindings.GetValue(null);var oldRoot=rootResolver.GetValue(null);
  bindings.SetValue(null,Path.Combine(directory,"bindings"));rootResolver.SetValue(null,new Func<string>(()=>directory));
  try
  {
   foreach(var mode in new[]{"same","latest-readonly","latest-readonly-214","latest-readonly-stale","snapshot-preview","historical-preview","same-readonly-child","bounded-loaded-enumeration","resolved-copy","unproven-edit","owned-edit","stale-with-edit","stale-readonly-child","foreign-journal","unchanged-foreign-path","unchanged-foreign-base","unchanged-foreign-session","stale-clean","stale-after-checkout","postarchive-source","postarchive-wrong-source","postarchive-wrong-stored","postarchive-wrong-length","postarchive-wrong-version","postarchive-new-version","refused-unlock"})
   {
    var folder=mode=="snapshot-preview"||mode=="historical-preview"?Path.Combine(directory,".uplm","Snapshots","QA",Guid.NewGuid().ToString("N"),mode=="snapshot-preview"?"Latest":"ReadOnly","fixture"):Path.Combine(directory,"View",mode);Directory.CreateDirectory(folder);var path=Path.Combine(folder,"PART.SLDPRT");File.WriteAllText(path,"stored baseline");var hash=WorkingReuseHash(path);
    var plugin=New("PdmAddin");SetField(plugin,"lifetime",new CancellationTokenSource());var project=Guid.NewGuid();var id=Guid.NewGuid();var version=Guid.NewGuid();var session=(Guid)Field(plugin,"checkoutSessionId");var checkedAt=new DateTime(DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond * TimeSpan.TicksPerMillisecond,DateTimeKind.Utc);
    var file=New("ControlledOpenFileDto");Set(file,"DocumentId",id);Set(file,"VersionId",version);Set(file,"FileName","PART.SLDPRT");Set(file,"RelativePath","PART.SLDPRT");Set(file,"FileLength",new FileInfo(path).Length);Set(file,"Sha256",hash);Set(file,"Configuration","Default");
    identity.GetMethod("TryWriteControlledVersion",Flags).Invoke(null,new object[]{path,id,project,version,"W1",hash,new FileInfo(path).Length});
    if(mode=="resolved-copy")
    {
     var metadataArgs=new object[]{path,null};identity.GetMethod("TryReadLocalMetadata",Flags).Invoke(null,metadataArgs);File.WriteAllText(path,"same geometry with resolved local references");
     if(!(bool)identity.GetMethod("TryRecordResolvedReferences",Flags).Invoke(null,new[]{(object)path,metadataArgs[1],WorkingReuseHash(path)}))throw new Exception("Resolved fixture binding failed");
    }
    var modified=mode=="unproven-edit"||mode=="owned-edit"||mode=="stale-with-edit"||mode=="foreign-journal";
    if(modified)File.WriteAllText(path,"actual user modification");
    var manifest=New("ControlledOpenManifestDto");Set(manifest,"ForEdit",!mode.StartsWith("latest-readonly")&&mode!="snapshot-preview"&&mode!="historical-preview");Set(manifest,"ProjectId",project);Set(manifest,"RootDocumentId",id);Set(manifest,"RootVersionId",version);Set(manifest,"RootRelativePath","PART.SLDPRT");var files=(IList)Activator.CreateInstance(Property(manifest,"Files").PropertyType);files.Add(file);Set(manifest,"Files",files);
    var node=New("CadTreeNode");Set(node,"FullPath",path);Set(node,"FileName","PART.SLDPRT");Set(node,"DocumentId",id);Set(node,"Kind",Enum.Parse(Type("CadDocumentKind"),"Part"));Set(node,"Configuration","Default");SetField(plugin,"currentTree",node);SetField(plugin,"authenticatedUsername","tester");
    ((IDictionary)Field(plugin,"controlledOpenManifests")).Add(folder,manifest);
    var doc=New("DocumentDto");Set(doc,"Id",id);Set(doc,"ProjectId",project);Set(doc,"FileName","PART.SLDPRT");Set(doc,"CheckedOutAt",checkedAt);
    var owned=mode=="owned-edit"||mode=="stale-with-edit"||mode=="foreign-journal"||mode.StartsWith("unchanged-foreign-");
    if(owned){Set(doc,"CheckedOutBy","tester");Set(doc,"CheckoutSessionId",session);}
    var draft=New("LocalEditDraft");Set(draft,"DocumentId",id);Set(draft,"ProjectId",project);Set(draft,"SessionId",mode=="foreign-journal"||mode=="unchanged-foreign-session"?Guid.NewGuid():session);Set(draft,"CheckedOutAt",checkedAt);Set(draft,"SourcePath",mode=="unchanged-foreign-path"?path+".old":path);Set(draft,"BaseVersionId",mode=="unchanged-foreign-base"?Guid.NewGuid():version);Set(draft,"BaseSha256",hash);
    if(owned)((IDictionary)Field(plugin,"localEdits")).Add(id,draft);
    var appType=Type("PdmAddin").GetField("application",Flags).FieldType;var interop=appType.Assembly;var readOnly=true;var unlocks=0;var nativeCalls=new List<string>();var documentEnumerations=0;var loadedPathReads=0;
    var configuration=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.Configuration",true),call=>call.MethodName=="get_Name"?"Default":throw new Exception(call.MethodName)).GetTransparentProxy();
    var manager=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ConfigurationManager",true),call=>call.MethodName=="get_ActiveConfiguration"?configuration:throw new Exception(call.MethodName)).GetTransparentProxy();
    var model=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true),call=>{nativeCalls.Add(call.MethodName);switch(call.MethodName){case "GetPathName":loadedPathReads++;return path;case "GetSaveFlag":return false;case "IsOpenedReadOnly":return readOnly;case "get_ConfigurationManager":return manager;case "GetType":return 1;case "SetReadOnlyState":unlocks++;if(mode=="refused-unlock")return false;readOnly=(bool)call.Args[0];return true;default:throw new Exception("Unexpected native mutation "+call.MethodName);}}).GetTransparentProxy();
    var loadedModels=new List<object>{model};string readonlyChildPath=null;string readonlyChildHash=null;
    if(mode.EndsWith("readonly-child"))
    {
     readonlyChildPath=Path.Combine(folder,"UNSAVED-CHILD.SLDPRT");File.WriteAllText(readonlyChildPath,"unchanged child disk bytes");readonlyChildHash=WorkingReuseHash(readonlyChildPath);
     var childFile=New("ControlledOpenFileDto");var childId=Guid.NewGuid();var childVersion=Guid.NewGuid();Set(childFile,"DocumentId",childId);Set(childFile,"VersionId",childVersion);Set(childFile,"FileName","UNSAVED-CHILD.SLDPRT");Set(childFile,"RelativePath","UNSAVED-CHILD.SLDPRT");Set(childFile,"FileLength",new FileInfo(readonlyChildPath).Length);Set(childFile,"Sha256",readonlyChildHash);Set(childFile,"Configuration","Default");files.Add(childFile);
     identity.GetMethod("TryWriteControlledVersion",Flags).Invoke(null,new object[]{readonlyChildPath,childId,project,childVersion,"W1",readonlyChildHash,new FileInfo(readonlyChildPath).Length});
     var child=new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true),call=>{switch(call.MethodName){case "GetPathName":return readonlyChildPath;case "GetSaveFlag":return true;case "IsOpenedReadOnly":return true;default:throw new Exception("Unselected readonly child's pending memory must not change: "+call.MethodName);}}).GetTransparentProxy();loadedModels.Add(child);
    }
    if(mode=="bounded-loaded-enumeration"||mode=="latest-readonly-214")
    {
     loadedModels.Clear();
     for(var i=1;i<214;i++)
     {
      var extraPath=Path.Combine(folder,"LOADED-"+i+".SLDPRT");File.WriteAllText(extraPath,"unselected "+i);var extraHash=WorkingReuseHash(extraPath);var extraId=Guid.NewGuid();var extraVersion=Guid.NewGuid();
      var extra=New("ControlledOpenFileDto");Set(extra,"DocumentId",extraId);Set(extra,"VersionId",extraVersion);Set(extra,"FileName",Path.GetFileName(extraPath));Set(extra,"RelativePath",Path.GetFileName(extraPath));Set(extra,"FileLength",new FileInfo(extraPath).Length);Set(extra,"Sha256",extraHash);Set(extra,"Configuration","Default");files.Add(extra);
      identity.GetMethod("TryWriteControlledVersion",Flags).Invoke(null,new object[]{extraPath,extraId,project,extraVersion,"W1",extraHash,new FileInfo(extraPath).Length});File.SetAttributes(extraPath,FileAttributes.ReadOnly);
      loadedModels.Add(new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2",true),call=>{switch(call.MethodName){case "GetPathName":loadedPathReads++;return extraPath;case "GetSaveFlag":return false;case "IsOpenedReadOnly":return true;default:throw new Exception("Unselected loaded model mutated "+call.MethodName);}}).GetTransparentProxy());
     }
     loadedModels.Add(model); // Root last exposes repeated full-document scans.
    }
    SetField(plugin,"application",new Proxy(appType,call=>{if(call.MethodName=="GetDocuments"){documentEnumerations++;return loadedModels.ToArray();}throw new Exception("Must not open, close or activate CAD: "+call.MethodName);}).GetTransparentProxy());
    var snapshot=new{documentId=id,fileName="PART.SLDPRT",kind=1,configuration="Default",children=new object[0]};
    var canonicalHash=new string('B',64);var archivedVersion=new{id=version,sha256=canonicalHash,fileLength=1234L,propertySnapshot=new Dictionary<string,string>{{"SourceFileSha256",hash}},referenceSnapshot=snapshot};
    if(mode.StartsWith("postarchive-"))
    {
     var oldVersion=Guid.NewGuid();Set(file,"VersionId",oldVersion);Set(manifest,"RootVersionId",oldVersion);
     identity.GetMethod("TryWriteControlledVersion",Flags).Invoke(null,new object[]{path,id,project,oldVersion,"W1",hash,new FileInfo(path).Length});
     var serializer=new JavaScriptSerializer();var committed=serializer.Deserialize(serializer.Serialize(archivedVersion),Type("DocumentVersionDto"));
     var warning=(string)Type("PdmAddin").GetMethod("RememberControlledVersionIdentityAndManifest",Flags).Invoke(plugin,new object[]{path,id,project,committed});
     if(!string.IsNullOrEmpty(warning)||(Guid)Get(file,"VersionId")!=version||(string)Get(file,"Sha256")!=hash||(long)Get(file,"FileLength")!=new FileInfo(path).Length)
      throw new Exception("Actual check-in path did not record the new version's local source representation: "+warning);
    }
    var handler=new WorkingReuseHandler(manifest,doc,mode.StartsWith("postarchive-")?(object)archivedVersion:new{id=version,sha256=hash,fileLength=(long)Get(file,"FileLength"),referenceSnapshot=snapshot},session,mode);
    using(var client=(IDisposable)New("PdmApiClient","http://reuse.invalid/"))using(var http=new HttpClient(handler){BaseAddress=new Uri("http://reuse.invalid/")})
    {
     http.DefaultRequestHeaders.Authorization=new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer","fixture");SetField(client,"httpClient",http);SetField(client,"controlledOpenHttpClient",http);SetField(plugin,"apiClient",client);
     SetField(plugin,"editJournalScope",(string)Get(client,"EditJournalScope")+"|tester");SetField(plugin,"editJournal",New("EditDraftJournal",Path.Combine(directory,"journal-"+mode),"fixture"));
     var item=New("BatchOperationItem",node,0);var selected=Array.CreateInstance(Type("BatchOperationItem"),1);selected.SetValue(item,0);var before=WorkingReuseHash(path);var blocked=false;var result=false;
     try{var task=(Task)Type("PdmAddin").GetMethod("TryAcquireReusableWorkingCopyAsync",Flags).Invoke(plugin,new object[]{selected,project,id});await task;result=(bool)Get(task,"Result");}catch(InvalidOperationException){blocked=true;}catch(IOException){blocked=true;}
     var expectedFallback=mode=="stale-clean"||mode=="latest-readonly-stale"||mode=="snapshot-preview"||mode=="historical-preview"||mode.StartsWith("postarchive-wrong-")||mode=="postarchive-new-version";
     var shouldBlock=mode=="unproven-edit"||mode=="stale-with-edit"||mode=="stale-readonly-child"||mode=="foreign-journal"||mode.StartsWith("unchanged-foreign-")||mode=="stale-after-checkout"||mode=="refused-unlock";
     if(blocked!=shouldBlock||!shouldBlock&&result!=!expectedFallback||WorkingReuseHash(path)!=before)throw new Exception("Incorrect working reuse permission behavior "+mode);
     if(owned&&((IDictionary)Field(plugin,"localEdits"))[id]!=draft)throw new Exception("Existing edit journal was replaced: "+mode);
     if(shouldBlock&&mode!="refused-unlock"&&mode!="stale-after-checkout"&&(handler.Checkouts!=0||unlocks!=0))throw new Exception("Unsafe permission change before validation "+mode);
     if(!shouldBlock&&!expectedFallback&&(unlocks!=1||handler.Checkouts!=1))throw new Exception("Expected only selected permission in place "+mode);
     if(mode=="stale-after-checkout"&&(handler.Checkouts!=1||unlocks!=0))throw new Exception("Race unlocked a stale file");
     if(mode=="stale-after-checkout")
     {
      var retained=((IDictionary)Field(plugin,"localEdits"))[id];
      if(retained==null||(string)Get(retained,"SourcePath")!=path||(Guid?)Get(retained,"BaseVersionId")!=version)throw new Exception("New lease lost its recovery journal after a manifest race");
     }
     if(expectedFallback&&(handler.Checkouts!=0||unlocks!=0))throw new Exception("Clean stale copy was not left for update");
     if(mode=="same"&&handler.VersionReads!=1||mode=="postarchive-source"&&handler.VersionReads!=2||mode=="postarchive-new-version"&&handler.VersionReads!=0)throw new Exception("Unexpected history requests for "+mode+": "+handler.VersionReads);
     if(readonlyChildPath!=null&&WorkingReuseHash(readonlyChildPath)!=readonlyChildHash)throw new Exception("Unselected readonly child disk changed");
     if(mode=="bounded-loaded-enumeration"||mode=="latest-readonly-214")
     {
      if(documentEnumerations>3||loadedPathReads>3*loadedModels.Count+2)throw new Exception("Unbounded loaded-document scan: GetDocuments="+documentEnumerations+" GetPathName="+loadedPathReads+" loaded="+loadedModels.Count);
      foreach(var extra in files.Cast<object>().Skip(1)){var extraPath=Path.Combine(folder,(string)Get(extra,"FileName"));if(WorkingReuseHash(extraPath)!=(string)Get(extra,"Sha256")||(File.GetAttributes(extraPath)&FileAttributes.ReadOnly)==0)throw new Exception("Unselected loaded file changed");}
     }
     Console.WriteLine("PASS: working-copy permission "+mode+"; zero native open/close/activate/save and unchanged file bytes"+(mode=="bounded-loaded-enumeration"?"; GetDocuments="+documentEnumerations+" GetPathName="+loadedPathReads:""));
    }
    ((CancellationTokenSource)Field(plugin,"lifetime")).Dispose();
   }
  }
  finally{bindings.SetValue(null,oldBindings);rootResolver.SetValue(null,oldRoot);foreach(var file in Directory.GetFiles(directory,"*",SearchOption.AllDirectories))File.SetAttributes(file,FileAttributes.Normal);if(!Path.GetFullPath(directory).StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase))throw new Exception("Unexpected QA cleanup directory");Directory.Delete(directory,true);}
 }

 sealed class WorkingReuseHandler:HttpMessageHandler
 {
  readonly object manifest,document,version;readonly Guid session;readonly string mode;public int Checkouts;public int VersionReads;int manifests;
  public WorkingReuseHandler(object manifest,object document,object version,Guid session,string mode){this.manifest=manifest;this.document=document;this.version=version;this.session=session;this.mode=mode;}
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
  {
   var path=request.RequestUri.AbsolutePath;object value;
   if(path.EndsWith("/open-manifest"))
   {
    value=manifest;manifests++;
    if(mode.StartsWith("latest-readonly"))
    {var serializer=new JavaScriptSerializer();value=serializer.Deserialize(serializer.Serialize(manifest),manifest.GetType());Set(value,"ForEdit",true);}
    if(mode=="stale-with-edit"||mode=="stale-readonly-child"||mode=="stale-clean"||mode=="latest-readonly-stale"||mode=="stale-after-checkout"&&manifests>1)
    {var serializer=new JavaScriptSerializer();value=serializer.Deserialize(serializer.Serialize(manifest),manifest.GetType());Set(value,"RootVersionId",Guid.NewGuid());}
    if(mode.StartsWith("postarchive-"))
    {
     var serializer=new JavaScriptSerializer();value=serializer.Deserialize(serializer.Serialize(manifest),manifest.GetType());
     var storedFile=((IList)Get(value,"Files"))[0];Set(storedFile,"Sha256",new string('B',64));Set(storedFile,"FileLength",1234L);
     if(mode=="postarchive-new-version"){var next=Guid.NewGuid();Set(value,"RootVersionId",next);Set(storedFile,"VersionId",next);}
    }
   }
   else if(path.EndsWith("/documents"))value=new[]{document};
   else if(path.EndsWith("/versions"))
   {
    VersionReads++;var serialized=new JavaScriptSerializer().Serialize(version);
    var response=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(serialized);
    if(mode=="postarchive-wrong-source")response["propertySnapshot"]=new Dictionary<string,string>{{"SourceFileSha256",new string('C',64)}};
    if(mode=="postarchive-wrong-stored")response["sha256"]=new string('C',64);
    if(mode=="postarchive-wrong-length")response["fileLength"]=4321L;
    if(mode=="postarchive-wrong-version")response["id"]=Guid.NewGuid();
    value=new[]{response};
   }
   else if(path.EndsWith("/document-relations"))value=new object[0];else if(path.EndsWith("/document-operation-rules"))value=new{disabledRuleIds=new string[0]};
   else if(path.EndsWith("/checkout")){Checkouts++;Set(document,"CheckedOutBy","tester");Set(document,"CheckoutSessionId",session);value=document;}
   else throw new Exception("Unexpected API call "+path);
   return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(new JavaScriptSerializer().Serialize(value))});
  }
 }
}
