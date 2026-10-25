using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using System.Threading;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using Environment=System.Environment;

internal static class NativeChecks
{
 const BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
 static readonly JavaScriptSerializer Json=new JavaScriptSerializer{MaxJsonLength=int.MaxValue};
 static Assembly Plugin;static ISldWorks App;static string Stage;static object Addin;static int OwnedPid;static long OwnedStart;
 [STAThread] static int Main(string[] args)
 {
  if(args.Length<4){Console.Error.WriteLine("参数：候选插件DLL、验证模式、新隔离目录、样本或上下文文件。");return 2;}
  Console.OutputEncoding=new UTF8Encoding(false);
  Stage=Path.GetFullPath(args[2]);Directory.CreateDirectory(Stage);
  var dll=Path.GetFullPath(args[0]);AppDomain.CurrentDomain.AssemblyResolve+=(_,e)=>{var path=Path.Combine(Path.GetDirectoryName(dll),new AssemblyName(e.Name).Name+".dll");return File.Exists(path)?Assembly.LoadFrom(path):null;};
  try{Plugin=Assembly.LoadFrom(dll);}catch(Exception error){Console.Error.WriteLine("无法加载候选插件："+error.Message);return 2;}var result=0;
  Environment.SetEnvironmentVariable("PDM_ADDIN_VERBOSE_SCAN","1");
  using(var monitor=new System.Threading.Timer(_=>{try{File.WriteAllText(Path.Combine(Stage,"live-diagnostics.txt"),(string)T("PluginDiagnosticLog").GetMethod("Snapshot",Flags).Invoke(null,null));}catch{}},null,1000,2000))
  using(var host=new Form{Opacity=0,ShowInTaskbar=false})
  {
   host.Shown+=async (_,__)=>{
    try
    {
     OleFilter.Register();var existing=Process.GetProcessesByName("SLDWORKS").Select(p=>p.Id).ToArray();
     App=(ISldWorks)Activator.CreateInstance(System.Type.GetTypeFromProgID("SldWorks.Application",true));
     var pid=App.GetProcessID();if(existing.Contains(pid)){App=null;throw new Exception("COM 返回了原有 SolidWorks 实例，未接管或关闭。");}
     OwnedPid=pid;OwnedStart=Process.GetProcessById(pid).StartTime.ToUniversalTime().Ticks;
     File.WriteAllText(Path.Combine(Stage,"owned-solidworks-"+args[1]+".json"),Json.Serialize(new{pid,start=OwnedStart,mode=args[1]}));
     App.Visible=false;App.UserControl=false;
     try{App.UnloadAddIn(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"UPLM","solidworks-addin","Upton.Pdm.SolidWorks.Addin.dll"));}catch(COMException){}
     T("PdmDocumentIdentityStore").GetField("bindingDirectory",Flags).SetValue(null,Path.Combine(Stage,"bindings-"+args[1]));
     T("PdmDocumentIdentityStore").GetField("workspaceRootResolver",Flags).SetValue(null,new Func<string>(()=>Stage));
     Addin=New("PdmAddin");F(Addin,"application",App);F(Addin,"scanner",New("SolidWorksReferenceTreeScanner",App));
     using(var pane=(Control)New("PdmTaskPaneControl","图档完整性隔离验证"))
     {
      pane.CreateControl();F(Addin,"taskPaneControl",pane);
      if(args[1]=="h620workspace"){H620Workspace(args[3]);return;}
      if(args[1]=="firstarchive"){FirstArchive(args[3]);return;}
      if(args[1]=="lhhworkspace"){Workspace(args[3],false,"LHH30CA2R1000Z0C-E20E20(1).SLDASM");return;}
      if(args[1]=="importprobe"){ImportProbe(args[3]);return;}
      if(args[1]=="referenceprobe")ReferenceProbe(args[3]);else if(args[1]=="prepare")Prepare(args[3]);else if(args[1]=="verify")Verify(args[3]);else if(args[1]=="dependencies")Dependencies(args[3]);else if(args[1]=="isolate")Isolate(args[3]);else if(args[1]=="workspace")Workspace(args[3]);else if(args[1]=="drawings")Workspace(args[3],true);else if(args[1]=="profile")Workspace(args[3],false,"标准机架后安全门型材框.SLDPRT");else if(args[1]=="resources")Resources(args[3]);else if(args[1]=="geometryprobe")GeometryProbe(args[3]);else if(args[1]=="safeguards")Safeguards(args[3]);else if(args[1]=="httpflow")await HttpFlow(args[3]);else throw new Exception("未知验证阶段");
     }
    }
    catch(Exception error){Console.WriteLine("FAIL: "+error);File.WriteAllText(Path.Combine(Stage,"native-"+args[1]+"-error.txt"),error.ToString());File.WriteAllText(Path.Combine(Stage,"diagnostics.txt"),(string)T("PluginDiagnosticLog").GetMethod("Snapshot",Flags).Invoke(null,null));result=1;}
    finally
    {
     if(App!=null&&OwnedPid>0)
     {
      try{var process=Process.GetProcessById(OwnedPid);if(process.StartTime.ToUniversalTime().Ticks==OwnedStart)process.Kill();}
      catch(Exception cleanup){Console.WriteLine("独立实例清理记录："+cleanup.Message);}
      App=null;
     }
     OleFilter.Revoke();host.Close();
    }
   };Application.Run(host);
  }
  return result;
 }
 static void Prepare(string fixture)
 {
  var raw=Path.Combine(Stage,"View","raw-source");Directory.CreateDirectory(raw);
  foreach(var folder in new[]{"working","historical"})foreach(var source in Directory.GetFiles(Path.Combine(fixture,folder)))
  {
   var target=Path.Combine(raw,Path.GetFileName(source));if(File.Exists(target))throw new Exception("隔离源文件重复："+target);
   File.Copy(source,target);File.SetAttributes(target,FileAttributes.Normal);
  }
  var available=Directory.GetFiles(raw).ToList();var copied=new List<object>();
  for(var i=0;i<available.Count;i++)
  {
   var owner=available[i];if(!Native(owner))continue;
   var dependencies=App.GetDocumentDependencies2(owner,false,false,false) as Array;if(dependencies==null)continue;
   for(var j=1;j<dependencies.Length;j+=2)foreach(var source in (Convert.ToString(dependencies.GetValue(j))??"").Split('|'))
   {
    var target=Path.Combine(raw,Path.GetFileName(source));if(File.Exists(target)||!File.Exists(source))continue;
    if((bool)T("NativeArchiveManifest").GetMethod("IsInterconnectCachePath",Flags).Invoke(null,new object[]{source})
     ||(bool)T("PdmAddin").GetMethod("IsSolidWorksTemporaryVirtualComponentPath",Flags).Invoke(null,new object[]{source}))continue;
    File.Copy(source,target);File.SetAttributes(target,FileAttributes.Normal);available.Add(target);copied.Add(new{source,target,sha256=Hash(target)});
   }
  }
  var project=Guid.NewGuid();var manifest=New("ControlledOpenManifestDto");Set(manifest,"ProjectId",project);Set(manifest,"Id",Guid.NewGuid());
  var list=(IList)Activator.CreateInstance(T("ControlledOpenManifestDto").GetProperty("Files").PropertyType);
  var configurations=new Dictionary<string,string>();
  foreach(var path in available)
  {
   var name=Path.GetFileName(path);var cfg=Native(path)?App.GetActiveConfigurationName(path):"";configurations[name]=cfg??"";
   var file=New("ControlledOpenFileDto");Set(file,"DocumentId",Guid.NewGuid());Set(file,"VersionId",Guid.NewGuid());Set(file,"FileName",name);Set(file,"RelativePath",name);Set(file,"Sha256",Hash(path));Set(file,"FileLength",new FileInfo(path).Length);Set(file,"Configuration",cfg??"");Set(file,"Revision","W1");list.Add(file);
   Bind(path,file,project);
  }
  Set(manifest,"Files",list);
  var root=Path.Combine(raw,"CW0030002.10.SLDASM");
  T("ControlledReferenceMaterializer").GetMethod("Prepare",Flags).Invoke(null,new object[]{App,root,manifest});
  var catalog=available.Select(path=>new{fileName=Path.GetFileName(path),path,configuration=configurations[Path.GetFileName(path)],sha256=Hash(path),length=new FileInfo(path).Length}).ToArray();
  File.WriteAllText(Path.Combine(Stage,"native-source-catalog.json"),Json.Serialize(new{raw,catalog,extraSources=copied}));
  Console.WriteLine("PASS: 原报错大装配实际文件已只读复制并重定位到隔离源目录；文件数="+catalog.Length);
 }
 static void Verify(string export)
 {
  var source=Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(export));
  var offline=Convert.ToString(source["rawSource"]);if(Directory.Exists(offline))throw new Exception("隔离源目录仍可访问，不能作为离线来源验证");
  var results=new List<object>();
  foreach(var package in ((IEnumerable)source["packages"]).Cast<Dictionary<string,object>>())
  {
   var label=(string)package["name"];var folder=Path.Combine(Stage,"View","download-"+label);Directory.CreateDirectory(folder);
   var manifest=Json.Deserialize(Json.Serialize(package["manifest"]),T("ControlledOpenManifestDto"));var project=(Guid)Get(manifest,"ProjectId");
   foreach(var file in ((IEnumerable)Get(manifest,"Files")).Cast<object>())
   {
    var mapping=((IEnumerable)package["storedFiles"]).Cast<Dictionary<string,object>>().Single(item=>item["versionId"].ToString()==Get(file,"VersionId").ToString());
    var target=Path.Combine(folder,(string)Get(file,"RelativePath"));Directory.CreateDirectory(Path.GetDirectoryName(target));File.Copy((string)mapping["serverPath"],target);File.SetAttributes(target,FileAttributes.ReadOnly);Bind(target,file,project);
   }
   var root=Path.Combine(folder,(string)Get(manifest,"RootRelativePath"));var rootFile=((IEnumerable)Get(manifest,"Files")).Cast<object>().Single(f=>(bool)Get(f,"IsRoot"));
   var timer=Stopwatch.StartNew();Console.WriteLine("BEGIN: 从服务器文件库独立打开 "+label);
   var opened=(IModelDoc2)T("PdmAddin").GetMethod("OpenControlledReadOnlyDocument",Flags).Invoke(Addin,new object[]{root,2,Get(rootFile,"Configuration"),manifest});
   if(opened==null||!string.Equals(opened.GetPathName(),root,StringComparison.OrdinalIgnoreCase))throw new Exception("未打开指定受控根");
   T("PdmAddin").GetMethod("ValidateControlledLoadedPaths",Flags).Invoke(null,new object[]{opened,root,manifest,App});
   var components=(((IAssemblyDoc)opened).GetComponents(false) as Array)?.Cast<object>().OfType<IComponent2>().ToArray()??new IComponent2[0];
   var ordinary=components.Where(c=>!c.IsVirtual).Select(c=>new{name=c.Name2,path=(c.GetModelDoc2() as IModelDoc2)?.GetPathName()??c.GetPathName(),configuration=c.ReferencedConfiguration,loaded=c.GetModelDoc2()!=null}).ToArray();
   results.Add(new{label,root,files=((IEnumerable)Get(manifest,"Files")).Cast<object>().Count(),components=components.Length,ordinary,seconds=timer.Elapsed.TotalSeconds});
   Console.WriteLine("PASS: "+label+"；组件="+components.Length+"；来源不可访问，实际受控来源核对通过；秒="+timer.Elapsed.TotalSeconds.ToString("F2"));
   App.CloseDoc(opened.GetTitle());
   foreach(var model in ((App.GetDocuments() as Array)?.Cast<object>().OfType<IModelDoc2>()??Enumerable.Empty<IModelDoc2>()).ToArray())App.CloseDoc(model.GetTitle());
  }
  File.WriteAllText(Path.Combine(Stage,"native-offline-results.json"),Json.Serialize(new{passed=true,ownedPid=OwnedPid,rawSourceUnavailable=true,results}));
 }
 static async Task<object> CallAsync(object target,string name,params object[] arguments)
 {
  var method=target.GetType().GetMethods(Flags).Single(m=>m.Name==name&&m.GetParameters().Length==arguments.Length);
  var task=(Task)method.Invoke(target,arguments);await task;return task.GetType().GetProperty("Result")?.GetValue(task);
 }
 static IEnumerable<object> Walk(object root)
 {
  var pending=new Stack<object>();var visited=new HashSet<object>();pending.Push(root);
  while(pending.Count>0){var node=pending.Pop();if(!visited.Add(node))continue;yield return node;
   foreach(var child in ((IEnumerable)Get(node,"Children")).Cast<object>().Concat(((IEnumerable)Get(node,"ReferencedModels")).Cast<object>()))pending.Push(child);}
 }
 static async Task HttpFlow(string contextPath)
 {
  var context=Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(contextPath));var url=(string)context["url"];var project=Guid.Parse((string)context["projectId"]);var rootId=Guid.Parse((string)context["rootId"]);var geometry=context.ContainsKey("geometry")&&(bool)context["geometry"];
  if(!new Uri(url).IsLoopback)throw new Exception("HTTP原生验收仅允许本机隔离服务。");
  using(var check=new System.Net.Http.HttpClient()){var status=Json.Deserialize<Dictionary<string,object>>(await check.GetStringAsync(url.TrimEnd('/')+"/health"));if(!status.ContainsKey("databaseName")||!System.Text.RegularExpressions.Regex.IsMatch((string)status["databaseName"],"^pdm_closure_20261025_[a-f0-9]{10}$"))throw new Exception("HTTP原生验收禁止访问生产数据库。");}
  var client=New("PdmApiClient",url);await CallAsync(client,"LoginAsync","admin","Closure-Own-20261025!",CancellationToken.None);
  F(Addin,"apiClient",client);F(Addin,"authenticatedUsername","admin");F(Addin,"currentProjectId",(Guid?)project);
  var manifest=await CallAsync(client,"CreateControlledOpenManifestAsync",rootId,(Guid?)null,false,false,CancellationToken.None,false);
  var folder=Path.Combine(Stage,"View","http-large");Directory.CreateDirectory(folder);
  Console.WriteLine("BEGIN: 从隔离HTTP服务完整获取大装配");
  var manifestFiles=((IEnumerable)Get(manifest,"Files")).Cast<object>().ToArray();
  for(var offset=0;offset<manifestFiles.Length;offset+=4){await Task.WhenAll(manifestFiles.Skip(offset).Take(4).Select(file=>CallAsync(client,"DownloadControlledOpenFileAsync",file,folder,CancellationToken.None)));foreach(var file in manifestFiles.Skip(offset).Take(4))Bind(Path.Combine(folder,(string)Get(file,"RelativePath")),file,project);}
  var rootPath=Path.Combine(folder,(string)Get(manifest,"RootRelativePath"));
  var opened=(IModelDoc2)T("PdmAddin").GetMethod("OpenControlledReadOnlyDocument",Flags).Invoke(Addin,new object[]{rootPath,2,Get(manifestFiles.Single(file=>(bool)Get(file,"IsRoot")),"Configuration"),manifest});
  if(opened==null)throw new Exception("大装配未能从真实HTTP获取并打开");
  Console.WriteLine("完成：原生大装配已打开，开始读取存档范围");var scanner=GetField(Addin,"scanner");object tree;
  var incremental=context.ContainsKey("incrementalManifestScope")&&(bool)context["incrementalManifestScope"];
  if(incremental)
  {
   var byDocument=manifestFiles.ToDictionary(file=>(Guid)Get(file,"DocumentId"),file=>{var node=New("CadTreeNode");var fileName=(string)Get(file,"FileName");Set(node,"DocumentId",Get(file,"DocumentId"));Set(node,"FullPath",Path.Combine(folder,(string)Get(file,"RelativePath")));Set(node,"FileName",fileName);Set(node,"InstancePath",fileName);Set(node,"Configuration",Get(file,"Configuration"));Set(node,"Kind",Enum.Parse(T("CadDocumentKind"),fileName.EndsWith(".SLDASM",StringComparison.OrdinalIgnoreCase)?"Assembly":fileName.EndsWith(".SLDDRW",StringComparison.OrdinalIgnoreCase)?"Drawing":fileName.EndsWith(".SLDPRT",StringComparison.OrdinalIgnoreCase)?"Part":"Other"));return node;});
   tree=byDocument[(Guid)Get(manifest,"RootDocumentId")];Set(tree,"StructureIndexPending",true);foreach(var node in byDocument.Values.Where(node=>!ReferenceEquals(node,tree)))((IList)Get(tree,"Children")).Add(node);
   foreach(var file in manifestFiles.Where(file=>((string)Get(file,"FileName")).EndsWith(".SLDDRW",StringComparison.OrdinalIgnoreCase))){var drawingNode=byDocument[(Guid)Get(file,"DocumentId")];var modelFile=manifestFiles.FirstOrDefault(candidate=>Path.GetFileNameWithoutExtension((string)Get(candidate,"FileName"))==Path.GetFileNameWithoutExtension((string)Get(file,"FileName"))&&!((string)Get(candidate,"FileName")).EndsWith(".SLDDRW",StringComparison.OrdinalIgnoreCase));if(modelFile!=null){var modelNode=byDocument[(Guid)Get(modelFile,"DocumentId")];((IList)Get(drawingNode,"ReferencedModels")).Add(modelNode);((IList)Get(modelNode,"Children")).Add(drawingNode);}}
   Console.WriteLine("增量范围来自已核对的受控清单；总装不提交；实际组件="+(((IAssemblyDoc)opened).GetComponents(false) as Array)?.Length);
  }
  else tree=scanner.GetType().GetMethod("ScanDocument",Flags).Invoke(scanner,new object[]{opened,true});F(Addin,"currentTree",tree);Console.WriteLine("完成：存档范围已读取");
  var filesByPath=(IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string),T("ControlledOpenFileDto")));
  foreach(var file in manifestFiles)filesByPath.Add(Path.GetFullPath(Path.Combine(folder,(string)Get(file,"RelativePath"))),file);
  T("PdmAddin").GetMethods(Flags).Single(method=>method.Name=="ApplyControlledOpenMetadata"&&method.IsStatic).Invoke(null,new object[]{tree,filesByPath,project});
  var docs=(IEnumerable)await CallAsync(client,"GetDocumentsAsync",project,CancellationToken.None);var byId=(IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(Guid),T("DocumentDto")));
  foreach(var doc in docs)byId.Add(Get(doc,"Id"),doc);T("PdmAddin").GetMethod("ApplyMetadata",Flags).Invoke(Addin,new object[]{tree,byId});
  var nodes=Walk(tree).Where(node=>Get(node,"DocumentId")!=null).GroupBy(node=>(string)Get(node,"FullPath"),StringComparer.OrdinalIgnoreCase).Select(group=>group.First()).ToArray();
  Console.WriteLine("开始：版本元数据读取，文件="+nodes.Length);var versionCounts=new Dictionary<Guid,int>();var metadataCount=0;foreach(var node in nodes){if(++metadataCount%20==0)Console.WriteLine("版本进度="+metadataCount);var history=((IEnumerable)await CallAsync(client,"GetVersionsAsync",(Guid)Get(node,"DocumentId"),CancellationToken.None,(int?)null)).Cast<object>().ToArray();versionCounts[(Guid)Get(node,"DocumentId")]=history.Length;T("PdmAddin").GetMethod("ApplyLatestVersion",Flags).Invoke(null,new[]{node,history.FirstOrDefault()});}
  var model=nodes.Single(node=>(string)Get(node,"FileName")=="7000093.101.03-02.SLDPRT");
  var drawing=nodes.FirstOrDefault(node=>(string)Get(node,"FileName")=="7000093.101.03-02.SLDDRW");
  if(drawing==null)throw new Exception("大装配范围未带出配套工程图");
  string outlineBefore=null;double[] finalMass=null;string editedDimension=null;double dimensionValue=0;
  if(geometry){int er=0,wr=0;var beforeDrawing=App.OpenDoc6((string)Get(drawing,"FullPath"),3,1,"",ref er,ref wr);if(beforeDrawing==null)throw new Exception("原工程图读取失败");outlineBefore=DrawingOutlines((IDrawingDoc)beforeDrawing);App.CloseDoc(beforeDrawing.GetTitle());}
  var session=(Guid)GetField(Addin,"checkoutSessionId");var results=new List<object>();
  for(var cycle=1;cycle<=2;cycle++)
  {
   Console.WriteLine("BEGIN: 大装配内零件与工程图存档循环 "+cycle);
   var checkout=await CallAsync(client,"CheckoutAsync",(Guid)Get(model,"DocumentId"),session,Environment.MachineName,CancellationToken.None,(Guid?)null);
   T("PdmAddin").GetMethod("ApplyCheckoutDocument",Flags).Invoke(Addin,new[]{model,checkout});
   var path=(string)Get(model,"FullPath");File.SetAttributes(path,FileAttributes.Normal);var part=(IModelDoc2)App.GetOpenDocumentByName(path);if(part==null){var er=0;var wr=0;part=App.OpenDoc6(path,1,1,"",ref er,ref wr);}part.SetReadOnlyState(false);
   var properties=part.Extension.CustomPropertyManager[""];
   if(geometry)
   {
    var activation=0;App.ActivateDoc3(part.GetTitle(),false,0,ref activation);if(activation!=0)throw new Exception("隔离零件激活失败");
    var dimension=FindDimension(part,editedDimension,false);
    if(dimension!=null){editedDimension=dimension.FullName;dimensionValue=dimension.SystemValue+0.001;if(dimension.SetSystemValue3(dimensionValue,1,null)!=0||!part.EditRebuild3()||Math.Abs(dimension.SystemValue-dimensionValue)>1e-10)throw new Exception("隔离副本的实际尺寸修改或重建失败");}
    else {AddTestHole(part,cycle);dimensionValue=0.003;}
    var massStatus=0;finalMass=(double[])part.Extension.GetMassProperties2(2,out massStatus,false);if(massStatus!=0)throw new Exception("修改后的实际几何未能读取");Console.WriteLine("实际几何修改="+editedDimension+"；新值="+dimensionValue);
   }
   properties.Add3("隔离验收循环",30,"HTTP-"+cycle,1);var saveError=0;var saveWarning=0;if(!part.Save3(1,ref saveError,ref saveWarning)||saveError!=0)throw new Exception("测试零件保存失败");
   Set(model,"IsModifiedInSolidWorks",false);Set(model,"WorkState",Enum.Parse(T("CadWorkState"),"PendingCheckIn"));Set(drawing,"RequiresModelUpdate",true);
   var list=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(T("BatchOperationItem")));list.Add(New("BatchOperationItem",model,2));list.Add(New("BatchOperationItem",drawing,3));
   var result=await CallAsync(Addin,"CheckInBatchAsync",list,project,"隔离HTTP双循环验证 "+cycle,new Action<int,int,string,string>((index,total,name,state)=>Console.WriteLine(state+" "+name)),CancellationToken.None,null,null,null,null,false,null);
   var failures=((IEnumerable)Get(result,"Failures")).Cast<object>().ToArray();if(failures.Length>0)throw new Exception(string.Join(";",failures));if((int)Get(result,"CreatedVersions")!=2)throw new Exception("存档升版范围超出零件及配套工程图");
   if(App.GetOpenDocumentByName(rootPath)==null)throw new Exception("存档过程中总装窗口消失");results.Add(new{cycle,created=Get(result,"CreatedVersions"),unchanged=Get(result,"UnchangedFiles"),successes=Get(result,"Successes")});
   Console.WriteLine("PASS: 真实HTTP大装配循环 "+cycle);
  }
  foreach(var entry in versionCounts){var after=((IEnumerable)await CallAsync(client,"GetVersionsAsync",entry.Key,CancellationToken.None,(int?)null)).Cast<object>().ToArray();var changed=entry.Key==(Guid)Get(model,"DocumentId")||entry.Key==(Guid)Get(drawing,"DocumentId");if(after.Length!=entry.Value+(changed?2:0))throw new Exception("实际版本数量异常，存在误升版或漏升版："+entry.Key);}
  if(geometry)
  {
   foreach(var document in ((App.GetDocuments() as Array)?.OfType<IModelDoc2>()??Enumerable.Empty<IModelDoc2>()).ToArray())try{App.CloseDoc(document.GetTitle());}catch(COMException){}
   var pair=await CallAsync(client,"CreateControlledOpenManifestAsync",(Guid)Get(drawing,"DocumentId"),(Guid?)null,false,false,CancellationToken.None,false);var pairFolder=Path.Combine(Stage,"View","reopened-pair");Directory.CreateDirectory(pairFolder);
   foreach(var file in ((IEnumerable)Get(pair,"Files")).Cast<object>()){await CallAsync(client,"DownloadControlledOpenFileAsync",file,pairFolder,CancellationToken.None);Bind(Path.Combine(pairFolder,(string)Get(file,"RelativePath")),file,project);}
   var reopened=(IModelDoc2)T("PdmAddin").GetMethod("OpenControlledReadOnlyDocument",Flags).Invoke(Addin,new object[]{Path.Combine(pairFolder,(string)Get(pair,"RootRelativePath")),3,"",pair});if(reopened==null)throw new Exception("存档后的配套工程图未能从服务器独立取回");
   var reopenedPart=App.GetOpenDocumentByName(Path.Combine(pairFolder,(string)Get(model,"FileName"))) as IModelDoc2;if(reopenedPart==null)throw new Exception("工程图没有载入本包的零件");if(editedDimension!=null){var reopenedDimension=FindDimension(reopenedPart,editedDimension);if(Math.Abs(reopenedDimension.SystemValue-dimensionValue)>1e-10)throw new Exception("服务器取回的零件没有实际修改");}else if(!HasFeature(reopenedPart,"UPLM隔离验证孔2"))throw new Exception("服务器取回的零件缺少实际测试孔");
   var status=0;var actualMass=(double[])reopenedPart.Extension.GetMassProperties2(2,out status,false);if(status!=0||finalMass.Where((value,index)=>Math.Abs(value-actualMass[index])>Math.Max(1e-10,Math.Abs(value)*1e-7)).Any())throw new Exception("服务器取回的实际几何与提交结果不一致");
   if(outlineBefore==DrawingOutlines((IDrawingDoc)reopened))throw new Exception("实际尺寸已修改，但工程图视图轮廓没有变化");Console.WriteLine("PASS: 服务器独立取回后，实际尺寸、几何和工程图轮廓均已更新");
  }
  File.WriteAllText(Path.Combine(Stage,"http-flow-results.json"),Json.Serialize(new{passed=true,geometry,editedDimension,dimensionValue,unrelatedVersionsUnchanged=true,rootPath,files=manifestFiles.Length,results}));
 }
 static IDimension FindDimension(IModelDoc2 model,string name,bool required=true)
 {
  if(name!=null)return model.Parameter(name) as IDimension??throw new Exception("原尺寸未找到："+name);
  var dimensions=new List<IDimension>();for(var feature=model.FirstFeature() as IFeature;feature!=null;feature=feature.GetNextFeature() as IFeature)
   for(var display=feature.GetFirstDisplayDimension() as IDisplayDimension;display!=null;display=feature.GetNextDisplayDimension(display) as IDisplayDimension){var dimension=display.GetDimension2(0) as IDimension;if(dimension!=null&&!dimension.ReadOnly&&dimension.DrivenState==2&&dimension.GetType()==0&&dimension.SystemValue>0.01&&dimension.SystemValue<1)dimensions.Add(dimension);}
  var selected=dimensions.OrderByDescending(dimension=>dimension.SystemValue).FirstOrDefault();if(selected==null&&required)throw new Exception("隔离样本没有可安全修改的驱动线性尺寸");return selected;
 }
 static bool HasFeature(IModelDoc2 model,string name){for(var feature=model.FirstFeature() as IFeature;feature!=null;feature=feature.GetNextFeature() as IFeature)if(feature.Name==name)return true;return false;}
 static string DrawingOutlines(IDrawingDoc drawing)
 {
  var result=new List<string>();foreach(var sheet in (string[])drawing.GetSheetNames()){drawing.ActivateSheet(sheet);for(var view=drawing.GetFirstView() as IView;view!=null;view=view.GetNextView() as IView){var data=view.GetPolylines7(0,out var polylines);result.Add(sheet+"|"+view.GetName2()+"|"+string.Join(",",(double[])view.GetOutline())+"|"+Json.Serialize(data)+"|"+Json.Serialize(polylines));}}return string.Join(";",result);
 }
 static void AddTestHole(IModelDoc2 model,int cycle)
 {
  var status=0;var before=(double[])model.Extension.GetMassProperties2(2,out status,false);var box=(double[])((IPartDoc)model).GetPartBox(true);
  var plane=model.FirstFeature() as IFeature;while(plane!=null&&plane.GetTypeName2()!="RefPlane")plane=plane.GetNextFeature() as IFeature;if(plane==null||!plane.Select2(false,0))throw new Exception("测试副本没有可选基准面");
  model.SketchManager.InsertSketch(true);var sketch=model.GetActiveSketch2() as ISketch;var utility=App.GetMathUtility() as IMathUtility;
  var point=(utility.CreatePoint(new[]{box[0]+(box[3]-box[0])*(cycle==1?0.37:0.63),box[1]+(box[4]-box[1])*(cycle==1?0.23:0.73),(box[2]+box[5])/2}) as IMathPoint).MultiplyTransform(sketch.ModelToSketchTransform) as IMathPoint;var xy=(double[])point.ArrayData;
  if(model.SketchManager.CreateCircleByRadius(xy[0],xy[1],0,0.003)==null)throw new Exception("测试孔草图创建失败");model.SketchManager.InsertSketch(true);
  var cut=model.FeatureManager.FeatureCut3(false,false,false,1,1,0.01,0.01,false,false,false,false,0,0,false,false,false,false,false,true,true,false,false,false,0,0,false);
  if(cut==null||!model.EditRebuild3())throw new Exception("测试孔切除失败");cut.Name="UPLM隔离验证孔"+cycle;var after=(double[])model.Extension.GetMassProperties2(2,out status,false);
  if(status!=0||!(after[3]<before[3]-1e-12))throw new Exception("测试孔没有产生真实体积变化");Console.WriteLine("实际几何修改：测试孔"+cycle+"；体积="+before[3]+" → "+after[3]);
 }
 static void GeometryProbe(string source)
 {
  var path=Path.Combine(Stage,Path.GetFileName(source));File.Copy(source,path);File.SetAttributes(path,FileAttributes.Normal);int er=0,wr=0;var model=App.OpenDoc6(path,1,1,"",ref er,ref wr);if(model==null)throw new Exception("几何副本未打开");App.ActivateDoc3(model.GetTitle(),false,0,ref er);model.SetReadOnlyState(false);
  for(var feature=model.FirstFeature() as IFeature;feature!=null;feature=feature.GetNextFeature() as IFeature){Console.WriteLine("特征="+feature.Name+"；"+feature.GetTypeName2());for(var display=feature.GetFirstDisplayDimension() as IDisplayDimension;display!=null;display=feature.GetNextDisplayDimension(display) as IDisplayDimension){var dimension=display.GetDimension2(0);Console.WriteLine("尺寸="+dimension.FullName+"；驱动="+dimension.DrivenState+"；类型="+dimension.GetType()+"；只读="+dimension.ReadOnly+"；值="+dimension.SystemValue);}}
  AddTestHole(model,1);AddTestHole(model,2);if(!model.Save3(1,ref er,ref wr)||er!=0)throw new Exception("几何修改副本未保存");Console.WriteLine("PASS: 两次原生几何切除及保存完成");
 }
 static object GetField(object target,string name)=>target.GetType().GetField(name,Flags).GetValue(target);
 static void Safeguards(string source)
 {
  var inventory=Directory.GetFiles(source);var originalHashes=inventory.ToDictionary(path=>path,Hash);var root=Path.Combine(source,"SS5Y-8位电磁阀座.SLDASM");int er=0,wr=0;var original=App.OpenDoc6(root,2,1,"",ref er,ref wr);if(original==null)throw new Exception("保护窗口未打开");App.ActivateDoc3(original.GetTitle(),false,0,ref er);var dirty=original.GetSaveFlag();var results=new List<object>();
  foreach(var name in new[]{"标准机架后安全门型材框.SLDPRT","S700130.M1.01-01.SLDDRW"})
  {
   var path=Path.Combine(source,name);Exception failure=null;
   try{using(var workspace=(IDisposable)New("ArchiveNativeCopyWorkspace",App,inventory,new Dictionary<string,string>{{path,path}},Stage,new Action<string>(text=>Console.WriteLine("处理："+text)))){}}
   catch(Exception error){failure=error is TargetInvocationException invocation?invocation.InnerException:error;}
   if(!(failure is IOException||failure is InvalidOperationException)||!failure.Message.Contains(name))throw new Exception("不安全文件未被明确阻止："+name,failure);
   if(App.GetOpenDocumentByName(root)==null||dirty!=original.GetSaveFlag()||originalHashes.Any(file=>Hash(file.Key)!=file.Value))throw new Exception("失败处理改变了原窗口、原文件或修改状态");
   var leftovers=Directory.GetDirectories(Stage).Where(folder=>Guid.TryParseExact(Path.GetFileName(folder),"N",out var value)).ToArray();if(leftovers.Length!=0)throw new Exception("失败的隔离副本未清理");
   results.Add(new{name,blockedBeforeUpload=true,originalWindowKept=true,sourceFilesUnchanged=true,temporaryCopiesRemoved=true,reason=failure.Message});Console.WriteLine("PASS: 安全拦截且保持原图档 "+name);
  }
  File.WriteAllText(Path.Combine(Stage,"safeguard-results.json"),Json.Serialize(new{passed=true,results}));
 }
 static void Resources(string source)
 {
  var folder=Path.Combine(Stage,"drawings");Directory.CreateDirectory(folder);var protectedFiles=Directory.GetFiles(source).ToDictionary(path=>path,Hash);var templateHashes=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);var rows=new List<object>();
  var preferences=Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(Plugin.Location),"SolidWorks.Interop.swconst.dll")).GetType("SolidWorks.Interop.swconst.swUserPreferenceStringValue_e",true);
  foreach(var option in new[]{"swDefaultTemplatePart","swDefaultTemplateAssembly","swDefaultTemplateDrawing"}){var path=App.GetUserPreferenceStringValue((int)Enum.Parse(preferences,option));if(File.Exists(path))templateHashes[path]=Hash(path);}
  foreach(var path in protectedFiles.Keys){var target=Path.Combine(folder,Path.GetFileName(path));File.Copy(path,target);File.SetAttributes(target,FileAttributes.Normal);}
  foreach(var path in Directory.GetFiles(folder).Where(Native))
  {
   var dependencies=App.GetDocumentDependencies2(path,false,false,false) as Array;if(dependencies==null)continue;
   for(var i=1;i<dependencies.Length;i+=2)foreach(var old in (Convert.ToString(dependencies.GetValue(i))??"").Split('|')){var target=Path.Combine(folder,Path.GetFileName(old));if(File.Exists(target)&&old!=target)App.ReplaceReferencedDocument(path,old,target);}
  }
  var resourceType=T("ArchiveResourceIsolation");var layoutMethod=resourceType.GetMethod("DrawingLayout",Flags);var detach=resourceType.GetMethod("Detach",Flags);
  foreach(var path in Directory.GetFiles(folder,"*.SLDDRW"))
  {
   Console.WriteLine("BEGIN: 工程图全部图页及模板 "+Path.GetFileName(path));int er=0,wr=0;var model=App.OpenDoc6(path,3,1,"",ref er,ref wr);if(model==null||er!=0)throw new Exception("工程图副本未能打开："+Path.GetFileName(path)+"；"+er);
   var drawing=(IDrawingDoc)model;foreach(var name in (string[])drawing.GetSheetNames()){drawing.ActivateSheet(name);var template=(drawing.GetCurrentSheet() as ISheet).GetTemplateName();if(!string.IsNullOrWhiteSpace(template)&&File.Exists(template)&&!templateHashes.ContainsKey(template))templateHashes.Add(template,Hash(template));}
   var layout=(string)layoutMethod.Invoke(null,new object[]{model});var changed=(bool)detach.Invoke(null,new object[]{model});if(layout!=(string)layoutMethod.Invoke(null,new object[]{model}))throw new Exception("固化时工程图版式变化");
   if(!model.Save3(1,ref er,ref wr)||er!=0)throw new Exception("工程图副本保存失败");var title=model.GetTitle();App.CloseDoc(title);model=App.OpenDoc6(path,3,1,"",ref er,ref wr);if(model==null||layout!=(string)layoutMethod.Invoke(null,new object[]{model}))throw new Exception("重开后工程图版式变化："+Path.GetFileName(path));
   drawing=(IDrawingDoc)model;foreach(var name in (string[])drawing.GetSheetNames()){drawing.ActivateSheet(name);if(!string.IsNullOrWhiteSpace((drawing.GetCurrentSheet() as ISheet).GetTemplateName()))throw new Exception("图框重开后仍关联原模板");}
   rows.Add(new{name=Path.GetFileName(path),changed,sheets=drawing.GetSheetCount(),reopenLayoutUnchanged=true});Console.WriteLine("PASS: "+Path.GetFileName(path));App.CloseDoc(model.GetTitle());
  }
  if(protectedFiles.Any(file=>Hash(file.Key)!=file.Value)||templateHashes.Any(file=>Hash(file.Key)!=file.Value))throw new Exception("源文件或图框模板原件发生变化");
  File.WriteAllText(Path.Combine(Stage,"resource-results.json"),Json.Serialize(new{passed=true,drawings=rows.Count,rows,templates=templateHashes,sourceFilesUnchanged=true}));
 }
 static void ImportProbe(string source)
 {
  var interconnect=(int)Enum.Parse(Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(Plugin.Location),"SolidWorks.Interop.swconst.dll")).GetType("SolidWorks.Interop.swconst.swUserPreferenceToggle_e"),"swMultiCAD_Enable3DInterconnect");
  var enabled=App.GetUserPreferenceToggle(interconnect);Console.WriteLine("3D Interconnect enabled before probe="+enabled);
  App.SetUserPreferenceToggle(interconnect,true);
  try
  {
  var name="LHH30CA2R1000Z0C-E20E20(1).SLDASM";var rows=new List<object>();
  File.Copy(Path.Combine(source,"LHH30CA2R1000Z0C-E20E20(1).STEP"),Path.Combine(Stage,"LHH30CA2R1000Z0C-E20E20(1).STEP"));
  foreach(var prefix in new[]{"uplm_probe_"})
  {
   var path=Path.Combine(Stage,prefix+name);File.Copy(Path.Combine(source,name),path);int error=0,warning=0;
   var model=App.OpenDoc6(path,2,1,"",ref error,ref warning);if(model==null||error!=0)throw new Exception("打开探针失败："+error);
   ((IAssemblyDoc)model).ResolveAllLightWeightComponents(false);model.EditRebuild3();
   var features=new List<object>();for(var feature=model.FirstFeature() as IFeature;feature!=null;feature=feature.GetNextFeature() as IFeature)features.Add(new{name=feature.Name,type=feature.GetTypeName2(),interconnect=feature.Is3DInterconnectFeature});
   var components=(((IAssemblyDoc)model).GetComponents(false) as Array)?.Cast<object>().OfType<IComponent2>().Select(c=>new{name=c.Name2,path=c.GetPathName(),loaded=c.GetModelDoc2()!=null,suppression=c.GetSuppression2(),imported=c.GetImportedPath()}).ToArray();
   rows.Add(new{prefix,error,warning,features,components});
  }
  File.WriteAllText(Path.Combine(Stage,"import-probe.json"),Json.Serialize(rows));Console.WriteLine(Json.Serialize(rows));
  }
  finally{App.SetUserPreferenceToggle(interconnect,enabled);}
 }
 static void FirstArchive(string source)
 {
  var originals=Directory.GetFiles(source).ToDictionary(path=>path,Hash,StringComparer.OrdinalIgnoreCase);
  var fixture=Path.Combine(Stage,"fixture");Directory.CreateDirectory(fixture);
  foreach(var path in originals.Keys){var target=Path.Combine(fixture,Path.GetFileName(path));File.Copy(path,target);File.SetAttributes(target,FileAttributes.Normal);}
  File.WriteAllText(Path.Combine(Stage,"original-source-hashes.json"),Json.Serialize(originals));
  var inventory=Directory.GetFiles(fixture);var requested=inventory.Where(Native).ToDictionary(path=>path,path=>path,StringComparer.OrdinalIgnoreCase);
  var timer=Stopwatch.StartNew();
  try
  {
   using(var workspace=(IDisposable)New("ArchiveNativeCopyWorkspace",App,inventory,requested,Stage,new Action<string>(name=>Console.WriteLine("处理："+name))))
   {
    var output=Path.Combine(Stage,"ready");Directory.CreateDirectory(output);int count=0;
    foreach(var copy in ((IEnumerable)Get(workspace,"Files")).Cast<object>()){File.Copy((string)copy.GetType().GetField("Path",Flags).GetValue(copy),Path.Combine(output,Path.GetFileName((string)copy.GetType().GetField("Source",Flags).GetValue(copy))));count++;}
    File.WriteAllText(Path.Combine(Stage,"first-archive-results.json"),Json.Serialize(new{passed=true,requested=requested.Count,files=count,seconds=timer.Elapsed.TotalSeconds}));
    Console.WriteLine("PASS: 全套首次存档副本准备完成，文件="+count);
   }
  }
  finally
  {
   var unchanged=originals.All(pair=>Hash(pair.Key)==pair.Value);File.WriteAllText(Path.Combine(Stage,"source-preservation.json"),Json.Serialize(new{unchanged,files=originals.Count}));
   if(!unchanged)throw new Exception("源文件改变");
  }
 }
 static void H620Workspace(string source)
 {
  var names=new[]{"H620-XX-ES2F5SRDRB-20250410043747953.SLDPRT","H620_外观.STEP"};
  var originalHashes=names.ToDictionary(name=>name,name=>Hash(Path.Combine(source,name)));
  var fixture=Path.Combine(Stage,"fixture");Directory.CreateDirectory(fixture);
  foreach(var name in names)File.Copy(Path.Combine(source,name),Path.Combine(fixture,name));
  var input=Path.Combine(fixture,names[0]);var requested=new Dictionary<string,string>{{input,input}};
  var output=Path.Combine(Stage,"ready");Directory.CreateDirectory(output);
  var timer=Stopwatch.StartNew();
  using(var workspace=(IDisposable)New("ArchiveNativeCopyWorkspace",App,Directory.GetFiles(fixture),requested,Stage,new Action<string>(Console.WriteLine)))
   foreach(var copy in ((IEnumerable)Get(workspace,"Files")).Cast<object>())
    File.Copy((string)copy.GetType().GetField("Path",Flags).GetValue(copy),Path.Combine(output,Path.GetFileName((string)copy.GetType().GetField("Source",Flags).GetValue(copy))));
  Directory.Move(fixture,fixture+"-offline");
  // Only the native file is available in the open directory; no STEP source.
  File.Move(Path.Combine(output,names[1]),Path.Combine(Stage,"saved-step-not-in-package.STEP"));
  int error=0,warning=0;var model=App.OpenDoc6(Path.Combine(output,names[0]),1,1,"",ref error,ref warning);
  if(model==null||error!=0)throw new Exception("固化文件独立打开失败："+error);
  var active=(IEnumerable)T("NativeDesignDependencies").GetMethod("Read",Flags).Invoke(null,new object[]{model});
  if(active.Cast<object>().Any())throw new Exception("固化后仍存在活动外部来源");
  for(var feature=model.FirstFeature() as IFeature;feature!=null;feature=feature.GetNextFeature() as IFeature)
   if(feature.Is3DInterconnectFeature)throw new Exception("仍有3DInterconnect链接");
  if(((IPartDoc)model).GetBodies2(0,false) is not Array bodies||bodies.Length==0)throw new Exception("独立打开后没有实体");
  if(!originalHashes.All(pair=>Hash(Path.Combine(source,pair.Key))==pair.Value))throw new Exception("原件发生变化");
  File.WriteAllText(Path.Combine(Stage,"h620-workspace-results.json"),Json.Serialize(new{passed=true,bodyCount=bodies.Length,noActiveLinks=true,noInterconnect=true,stepAbsentFromPackage=true,sourceUnchanged=true,seconds=timer.Elapsed.TotalSeconds}));
  Console.WriteLine("PASS: H620保存几何固化后无STEP依赖，独立重开实体完整，原件未变更");
 }
 static void ReferenceProbe(string source)
 {
  var names=new[]{"H620-XX-ES2F5SRDRB-20250410043747953.SLDPRT","H620_外观.STEP"};
  var hashes=names.ToDictionary(name=>name,name=>Hash(Path.Combine(source,name)));
  var folder=Path.Combine(Stage,"probe");Directory.CreateDirectory(folder);
  foreach(var name in names)File.Copy(Path.Combine(source,name),Path.Combine(folder,"uplm_probe_"+name));
  var part=Path.Combine(folder,"uplm_probe_"+names[0]);var step=Path.Combine(folder,"uplm_probe_"+names[1]);
  var deps=App.GetDocumentDependencies2(part,false,false,false) as Array;
  var replacements=new List<object>();
  if(deps!=null)for(var index=1;index<deps.Length;index+=2)
   foreach(var reference in (Convert.ToString(deps.GetValue(index))??"").Split('|'))
    replacements.Add(new{reference,target=step,replaced=App.ReplaceReferencedDocument(part,reference,step)});
  int error=0,warning=0;var model=App.OpenDoc6(part,1,1,"",ref error,ref warning);
  if(model==null||error!=0)throw new Exception("探针副本打开失败："+error);
  var features=new List<object>();
  for(var feature=model.FirstFeature() as IFeature;feature!=null;feature=feature.GetNextFeature() as IFeature)
  {
   string imported=null;int? result=null;
   if(feature.Is3DInterconnectFeature){result=feature.GetImportedFeatureParameters(out var value);if(value is IImport3DInterconnectData data)imported=data.GetReferencedFileName();}
   features.Add(new{name=feature.Name,type=feature.GetTypeName2(),interconnect=feature.Is3DInterconnectFeature,result,imported});
  }
  var output=new{dependencies=deps,replacements,features,activeReferences=T("NativeDesignDependencies").GetMethod("Read",Flags).Invoke(null,new object[]{model}),warning,sourceUnchanged=hashes.All(pair=>Hash(Path.Combine(source,pair.Key))==pair.Value)};
  File.WriteAllText(Path.Combine(Stage,"reference-probe.json"),Json.Serialize(output));Console.WriteLine(Json.Serialize(output));
  if(!hashes.All(pair=>Hash(Path.Combine(source,pair.Key))==pair.Value))throw new Exception("原件发生变化");
 }
 static void Workspace(string source,bool drawings=false,string single=null)
 {
  var inventory=Directory.GetFiles(source);var root=Path.Combine(source,"SS5Y-8位电磁阀座.SLDASM");int error=0,warning=0;
  var original=App.OpenDoc6(root,2,1,"",ref error,ref warning);if(original==null||error!=0)throw new Exception("保护样本未能打开");
  App.ActivateDoc3(original.GetTitle(),false,0,ref error);var before=Hash(root);var dirty=original.GetSaveFlag();
  var requested=new[]{"SS5Y-8位电磁阀座.SLDASM","7000093.101.03-02.SLDPRT","7000093.101.03-02.SLDDRW"}.Select(name=>Path.Combine(source,name)).ToDictionary(path=>path,path=>path,StringComparer.OrdinalIgnoreCase);
  if(single!=null)requested=new Dictionary<string,string>{{Path.Combine(source,single),Path.Combine(source,single)}};
  if(drawings)requested=inventory.Where(path=>path.EndsWith(".SLDDRW",StringComparison.OrdinalIgnoreCase)).ToDictionary(path=>path,path=>path,StringComparer.OrdinalIgnoreCase);
  var timer=Stopwatch.StartNew();Console.WriteLine("BEGIN: 原装配保持打开时准备独立副本，含工程图");
  using(var workspace=(IDisposable)New("ArchiveNativeCopyWorkspace",App,inventory,requested,Stage,new Action<string>(name=>Console.WriteLine("处理："+name))))
  {
   var result=new List<object>();var output=Path.Combine(Stage,"ready");Directory.CreateDirectory(output);
   foreach(var file in ((IEnumerable)Get(workspace,"Files")).Cast<object>())
   {
    var originalPath=(string)file.GetType().GetField("Source",Flags).GetValue(file);var path=(string)file.GetType().GetField("Path",Flags).GetValue(file);var target=Path.Combine(output,Path.GetFileName(originalPath));File.Copy(path,target);
    result.Add(new{source=originalPath,path=target,sha256=Hash(target),frozen=(bool)file.GetType().GetField("ContentFrozen",Flags).GetValue(file)});
   }
   File.WriteAllText(Path.Combine(Stage,"workspace-results.json"),Json.Serialize(new{passed=true,originalKept=App.GetOpenDocumentByName(root)!=null,originalUnchanged=before==Hash(root),originalDirtyUnchanged=dirty==original.GetSaveFlag(),seconds=timer.Elapsed.TotalSeconds,files=result}));
  }
  if(App.GetOpenDocumentByName(root)==null||before!=Hash(root)||dirty!=original.GetSaveFlag())throw new Exception("原装配或修改标志发生变化");
  Console.WriteLine("PASS: 原窗口、原文件和修改标志保持；独立副本准备完成");
 }
 static void Isolate(string catalogPath)
 {
  var prepared=Directory.Exists(catalogPath);var folder=Path.Combine(Stage,"View","isolated");
  if(!prepared)
  {
  var catalog=Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(catalogPath));var raw=(string)catalog["raw"];
  Directory.CreateDirectory(folder);
  var files=((IEnumerable)catalog["catalog"]).Cast<Dictionary<string,object>>().ToArray();
  foreach(var file in files){var name=(string)file["fileName"];var source=(string)file["path"];if(!File.Exists(source))source=Path.Combine(raw+"-offline",name);var path=Path.Combine(folder,name);File.Copy(source,path);File.SetAttributes(path,FileAttributes.Normal);}
  foreach(var path in Directory.GetFiles(folder).Where(Native))
  {
   var deps=App.GetDocumentDependencies2(path,false,false,false) as Array;if(deps==null)continue;
   for(var i=1;i<deps.Length;i+=2)foreach(var source in (Convert.ToString(deps.GetValue(i))??"").Split('|'))
   {var target=Path.Combine(folder,Path.GetFileName(source));if(File.Exists(target)&&!string.Equals(source,target,StringComparison.OrdinalIgnoreCase))App.ReplaceReferencedDocument(path,source,target);}
  }
  }

  if(prepared){Directory.CreateDirectory(folder);foreach(var source in Directory.GetFiles(catalogPath)){var target=Path.Combine(folder,Path.GetFileName(source));File.Copy(source,target);File.SetAttributes(target,FileAttributes.Normal);}}
  var rows=new List<object>();
  foreach(var name in new[]{"PSU-08OG-20250301014805833.SLDPRT","SS5Y-8位电磁阀座.SLDASM","LHH30CA2R1000Z0C-E20E20(1).SLDASM","7000093.101.03-02.SLDPRT"})
  {
   var path=Path.Combine(folder,name);if(!File.Exists(path)){Console.WriteLine("SKIP: "+name);continue;}
   var deps=App.GetDocumentDependencies2(path,false,false,false) as Array;if(deps!=null)for(var d=1;d<deps.Length;d+=2)foreach(var saved in (Convert.ToString(deps.GetValue(d))??"").Split('|')){var target=Path.Combine(folder,Path.GetFileName(saved));if(File.Exists(target)&&saved!=target&&!App.ReplaceReferencedDocument(path,saved,target))throw new Exception("隔离副本引用定位失败："+saved);}
   var error=0;var warning=0;Console.WriteLine("BEGIN: 固化外部设计来源 "+name);
   var model=App.OpenDoc6(path,name.EndsWith("SLDASM")?2:1,1,"",ref error,ref warning);
   if(model==null||model.GetPathName()!=path||error!=0)throw new Exception("隔离副本打开失败："+name+"；"+error);
   App.ActivateDoc3(model.GetTitle(),false,0,ref error);Console.WriteLine("激活结果="+error+"；活动文档="+(App.ActiveDoc as IModelDoc2)?.GetPathName());if(model is IAssemblyDoc)Console.WriteLine("隔离副本重建="+model.EditRebuild3());
   var changed=(bool)T("ArchiveReferenceIsolation").GetMethod("Detach",Flags,null,new[]{typeof(IModelDoc2)},null).Invoke(null,new object[]{model});
   changed|=(bool)T("ArchiveResourceIsolation").GetMethod("Detach",Flags).Invoke(null,new object[]{model});
   if(changed){error=warning=0;if(!model.Save3(513,ref error,ref warning)||error!=0)throw new Exception("固化副本保存失败："+name+"；"+error);}
   rows.Add(new{name,changed,dependencies=App.GetDocumentDependencies2(path,false,false,false)});
   App.CloseDoc(model.GetTitle());Console.WriteLine("PASS: "+name+"；是否固化="+changed);
  }
  File.WriteAllText(Path.Combine(Stage,"isolation-results.json"),Json.Serialize(rows));
 }
 static void Dependencies(string catalogPath)
 {
  var catalog=Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(catalogPath));var raw=(string)catalog["raw"];
  var files=((IEnumerable)catalog["catalog"]).Cast<Dictionary<string,object>>().ToArray();var names=new HashSet<string>(files.Select(file=>(string)file["fileName"]),StringComparer.OrdinalIgnoreCase);
  var rows=new Dictionary<string,string[]>(StringComparer.OrdinalIgnoreCase);
  foreach(var file in files)
  {
   var name=(string)file["fileName"];var path=(string)file["path"];if(!File.Exists(path))path=Path.Combine(raw+"-offline",name);
   var dependencies=Native(path)?App.GetDocumentDependencies2(path,false,false,false) as Array:null;var related=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
   if(dependencies!=null)for(var i=1;i<dependencies.Length;i+=2)foreach(var source in (Convert.ToString(dependencies.GetValue(i))??"").Split('|')){var leaf=Path.GetFileName(source);if(names.Contains(leaf)&&!leaf.Equals(name,StringComparison.OrdinalIgnoreCase))related.Add(leaf);}
   rows[name]=related.ToArray();
  }
  File.WriteAllText(Path.Combine(Stage,"native-dependencies.json"),Json.Serialize(rows));Console.WriteLine("PASS: 已读取实际原生逐文件依赖；SS5Y直接文件数="+rows["SS5Y-8位电磁阀座.SLDASM"].Length);
 }
 static void Bind(string path,object file,Guid project)
 {
  if(!(bool)T("PdmDocumentIdentityStore").GetMethod("TryWriteControlledVersion",Flags).Invoke(null,new object[]{path,Get(file,"DocumentId"),project,Get(file,"VersionId"),Get(file,"Revision"),Get(file,"Sha256"),Get(file,"FileLength")}))throw new Exception("隔离版本绑定未写入");
 }
 static bool Native(string p)=>new[]{".SLDASM",".SLDPRT",".SLDDRW"}.Contains(Path.GetExtension(p),StringComparer.OrdinalIgnoreCase);
 static string Hash(string path){using(var f=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))using(var h=SHA256.Create())return BitConverter.ToString(h.ComputeHash(f)).Replace("-","");}
 static System.Type T(string name)=>Plugin.GetType("Upton.Pdm.SolidWorks."+name,true);
 static object New(string name,params object[] args)=>Activator.CreateInstance(T(name),Flags&~BindingFlags.Static,null,args,null);
 static void Set(object o,string name,object value)=>o.GetType().GetProperty(name,Flags).SetValue(o,value);
 static object Get(object o,string name)=>o.GetType().GetProperty(name,Flags).GetValue(o);
 static void F(object o,string name,object value)=>o.GetType().GetField(name,Flags).SetValue(o,value);
 [ComImport,Guid("00000016-0000-0000-C000-000000000046"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IMessageFilter
 { [PreserveSig]int HandleInComingCall(int type,IntPtr task,int tick,IntPtr info);[PreserveSig]int RetryRejectedCall(IntPtr task,int tick,int reject);[PreserveSig]int MessagePending(IntPtr task,int tick,int pending); }
 sealed class OleFilter:IMessageFilter
 {
  [DllImport("ole32.dll")]static extern int CoRegisterMessageFilter(IMessageFilter filter,out IMessageFilter previous);
  public static void Register(){CoRegisterMessageFilter(new OleFilter(),out var old);}public static void Revoke(){CoRegisterMessageFilter(null,out var old);}
  public int HandleInComingCall(int t,IntPtr a,int i,IntPtr b)=>0;public int RetryRejectedCall(IntPtr a,int tick,int reject)=>reject==2&&tick<60000?250:-1;public int MessagePending(IntPtr a,int b,int c)=>2;
 }
}
