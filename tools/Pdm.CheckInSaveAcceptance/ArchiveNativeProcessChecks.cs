using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Remoting.Messaging;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

internal static partial class Program
{
 static void ArchiveNativeProcessScenario(string fixture)
 {
  Directory.CreateDirectory(fixture);var source=Path.Combine(fixture,"ENGINE.SLDPRT");File.WriteAllText(source,"immutable source");
  foreach(var mode in new[]{"sheet","solid","wire","unknown-error","missing-geometry","non-finite","empty-topology","zero-area"})
  {
   var shape=new double[21];shape[9]=0.01;shape[18]=1;shape[19]=82;shape[20]=222;
   if(mode=="non-finite")shape[0]=double.NaN;if(mode=="empty-topology")shape[19]=0;if(mode=="zero-area")shape[9]=0;if(mode=="missing-geometry")Array.Resize(ref shape,20);
   var verified=(bool)Type("ArchiveReferenceIsolation").GetMethod("CanVerifySheetBodies",Flags).Invoke(null,new object[]{mode=="unknown-error"?1:2,null,new[]{mode=="solid"?0:mode=="wire"?2:1},new[]{shape}});
   if(verified!=(mode=="sheet"))throw new Exception("曲面几何核对规则不正确："+mode);Console.WriteLine("PASS: surface-only native geometry validates "+mode+" without bypassing solid or unknown failures");
  }
  foreach(var mode in new[]{"unchanged","changed-area","changed-topology"})
  {
   var before=new double[21];before[9]=0.01;before[18]=1;before[19]=82;before[20]=222;var after=(double[])before.Clone();if(mode=="changed-area")after[9]+=0.001;if(mode=="changed-topology")after[19]++;
   object State(double[] values){var value=New("ArchiveReferenceIsolation+State");var type=value.GetType();type.GetField("Bodies",Flags).SetValue(value,1);type.GetField("Components",Flags).SetValue(value,new string[0]);type.GetField("PartShapes",Flags).SetValue(value,new[]{values});return value;}
   var original=State(before);var failed=false;try{original.GetType().GetMethod("Verify",Flags).Invoke(original,new[]{State(after),(object)"surface configuration"});}catch(TargetInvocationException error)when(error.InnerException is IOException){failed=true;}
   if(failed!=(mode!="unchanged"))throw new Exception("曲面前后完整性比对不正确："+mode);Console.WriteLine("PASS: stored surface geometry and topology comparison validates "+mode);
  }
  var packageType=Type("ArchiveEmbeddedPackageInspection");
  foreach(var mode in new[]{"same-components","same-components-reversed","native-integration-drift","changed-aggregate","changed-component-body","moved-component","moved-body","changed-body-topology","missing-component","non-finite-component"})
  {
   var matrix=new double[16];matrix[0]=matrix[4]=matrix[8]=matrix[12]=1;var body=new double[15];body[3]=0.01;body[12]=0;body[13]=6;body[14]=12;
   var changedMatrix=(double[])matrix.Clone();var changedBody=(double[])body.Clone();
   if(mode=="changed-component-body")changedBody[3]+=0.001;if(mode=="moved-component")changedMatrix[9]=0.02;if(mode=="non-finite-component")changedBody[3]=double.NaN;
   if(mode=="native-integration-drift"){changedBody[0]=2.4e-9;changedBody[3]*=1.0000005;}
   if(mode=="moved-body")changedBody[0]=1e-4;if(mode=="changed-body-topology")changedBody[13]++;
   object State(bool after)
   {
    var value=New("ArchiveReferenceIsolation+State");var type=value.GetType();type.GetField("Bodies",Flags).SetValue(value,-1);type.GetField("Components",Flags).SetValue(value,new[]{"part-1|Default|False"});type.GetField("PartShapes",Flags).SetValue(value,new double[0][]);
    var rows=new Dictionary<string,double[][]>();if(!after||mode!="missing-component")rows.Add("part-1|Default",after?new[]{changedMatrix,changedBody}:new[]{matrix,body});type.GetField("ComponentGeometry",Flags).SetValue(value,rows);
    if(after&&mode!="same-components-reversed"||!after&&(mode=="same-components-reversed"||mode=="changed-aggregate")){var mass=new double[13];if(after&&mode=="changed-aggregate")mass[3]=0.02;type.GetField("Mass",Flags).SetValue(value,mass);}return value;
   }
   var original=State(false);var failed=false;try{original.GetType().GetMethod("Verify",Flags).Invoke(original,new[]{State(true),(object)"assembly configuration"});}catch(TargetInvocationException error)when(error.InnerException is IOException){failed=true;}
   if(failed!=(mode!="same-components"&&mode!="same-components-reversed"&&mode!="native-integration-drift"))throw new Exception("装配逐组件实体和位置核对规则不正确："+mode);
   Console.WriteLine("PASS: independently verified assembly component geometry validates "+mode);
  }
  byte[] PackageData(string name,string text)
  {
   using(var memory=new MemoryStream())using(var writer=new BinaryWriter(memory))
   {
    writer.Write(0);writer.Write((ushort)2);
    foreach(var value in new[]{name,@"Z:\missing\"+name}){writer.Write(System.Text.Encoding.ASCII.GetBytes(value));writer.Write((byte)0);}
    writer.Write(0);var temporary=@"Z:\missing\temp\"+name;writer.Write(temporary.Length+1);writer.Write(System.Text.Encoding.ASCII.GetBytes(temporary));writer.Write((byte)0);
    var bytes=System.Text.Encoding.ASCII.GetBytes(text);writer.Write(bytes.Length);writer.Write(bytes);writer.Flush();var data=memory.ToArray();Array.Copy(BitConverter.GetBytes(data.Length-4),data,4);return data;
   }
  }
  foreach(var mode in new[]{"solidworks-note","version-note","unknown-text","workbook-name","bad-length","truncated"})
  {
   var data=PackageData(mode=="workbook-name"?"table.xls":"Save as Part.txt",mode=="unknown-text"?"external data source":mode=="version-note"?"SOLIDWORKS 2022":"SOLIDWORKS ");
   if(mode=="bad-length")data[0]^=1;if(mode=="truncated")Array.Resize(ref data,data.Length-3);
   var accepted=(bool)packageType.GetMethod("IsExportNoteData",Flags).Invoke(null,new object[]{data});
   if(accepted!=(mode=="solidworks-note"||mode=="version-note"))throw new Exception("嵌入说明内容核对不正确："+mode);
   Console.WriteLine("PASS: embedded native payload validates "+mode+" without consulting historical paths");
  }
  var oleType=Type("PdmAddin").GetField("application",Flags).FieldType.Assembly.GetType("SolidWorks.Interop.sldworks.SwOLEObject",true);
  foreach(var mode in new[]{"rgb","bitfields","bad-offset","truncated","wrong-size","profile-header","unsupported-compression","invalid-dimension"})
  {
   var mask=mode=="bitfields"?12:0;var data=new byte[54+mask+16];data[0]=(byte)'B';data[1]=(byte)'M';
   void Put(int offset,uint value)=>Array.Copy(BitConverter.GetBytes(value),0,data,offset,4);
   Put(2,(uint)data.Length);Put(10,(uint)(54+mask));Put(14,40);Put(18,2);Put(22,2);data[26]=1;data[28]=(byte)(mask==0?24:32);Put(30,(uint)(mask==0?0:3));Put(34,16);
   if(mode=="bad-offset")Put(10,1000);if(mode=="truncated")Array.Resize(ref data,data.Length-1);if(mode=="wrong-size")Put(34,99);
   if(mode=="profile-header")Put(14,124);if(mode=="unsupported-compression")Put(30,5);if(mode=="invalid-dimension")Put(18,0);
   var valid=(bool)packageType.GetMethod("IsBitmapData",Flags).Invoke(null,new object[]{data});
   if(valid!=(mode=="rgb"||mode=="bitfields"))throw new Exception("内嵌静态位图完整性规则不正确："+mode);
   Console.WriteLine("PASS: embedded static bitmap validates "+mode+" without changing stored pixels");
  }
  foreach(var mode in new[]{"internal-formulas","slicer-style","unknown-extension","external-relationship","missing-target","absolute-target","connections","macro","external-formula","dde-formula","unknown-function","malformed-xml"})
  {
   using(var memory=new MemoryStream())
   {
    using(var zip=new System.IO.Compression.ZipArchive(memory,System.IO.Compression.ZipArchiveMode.Create,true))
    {
     void Entry(string name,string text){using(var writer=new StreamWriter(zip.CreateEntry(name).Open()))writer.Write(text);}
     Entry("[Content_Types].xml","<Types/>");
     Entry("xl/workbook.xml",mode=="malformed-xml"?"<workbook>":"<workbook><definedName>Sheet1!$A$2</definedName></workbook>");
     var target=mode=="missing-target"?"worksheets/missing.xml":mode=="absolute-target"?"https://example.invalid/file.xlsx":"worksheets/sheet1.xml";
     Entry("xl/_rels/workbook.xml.rels","<Relationships><Relationship Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet' Target='"+target+"'"+(mode=="external-relationship"?" TargetMode='External'":"")+"/></Relationships>");
     var formula=mode=="external-formula"?"[other.xlsx]Sheet1!A1":mode=="dde-formula"?"server|topic!A1":mode=="unknown-function"?"WEBSERVICE(A1)":"MID(A3,8,1)";
     Entry("xl/worksheets/sheet1.xml","<worksheet><f>"+formula+"</f></worksheet>");
     if(mode=="connections")Entry("xl/connections.xml","<connections/>");
     if(mode=="macro")Entry("xl/vbaProject.bin","macro");
     if(mode=="slicer-style"||mode=="unknown-extension")Entry("xl/styles.xml","<styleSheet><extLst><ext uri='"+(mode=="slicer-style"?"{EB79DEF2-80B8-43e5-95BD-54CBDDF9020C}":"{UNKNOWN}")+"'><slicerStyles xmlns='http://schemas.microsoft.com/office/spreadsheetml/2009/9/main' defaultSlicerStyle='SlicerStyleLight1'/></ext></extLst></styleSheet>");
    }
    var accepted=(bool)packageType.GetMethod("IsWorkbookData",Flags).Invoke(null,new object[]{memory.ToArray()});
    if(accepted!=(mode=="internal-formulas"||mode=="slicer-style"))throw new Exception("内嵌设计表依赖检查不正确："+mode);
    Console.WriteLine("PASS: embedded design table validates "+mode+" without starting Excel or changing parameters");
   }
  }
  foreach(var mode in new[]{"cached-value","empty-string","shared-formula","missing-cache","error-cache","external-name","query-source","macro-source","hyperlink","network-source","live-source","missing-local-source"})
  {
   var sourcePath=Path.Combine(fixture,"source-"+mode+".xlsx");if(mode=="live-source")File.WriteAllText(sourcePath,"existing design source");
   var target=mode=="network-source"?"https://example.invalid/source.xlsx":new Uri(Path.GetFullPath(sourcePath)).AbsoluteUri;
   byte[] data;using(var memory=new MemoryStream())
   {
    using(var zip=new System.IO.Compression.ZipArchive(memory,System.IO.Compression.ZipArchiveMode.Create,true))
    {
     void Entry(string name,string text){using(var writer=new StreamWriter(zip.CreateEntry(name).Open()))writer.Write(text);}
     Entry("[Content_Types].xml","<Types><Override PartName='/xl/externalLinks/externalLink1.xml'/></Types>");
     Entry("xl/workbook.xml","<workbook><externalReferences><externalReference/></externalReferences>"+(mode=="external-name"?"<definedName>[1]Sheet0!A1</definedName>":"")+"</workbook>");
     Entry("xl/_rels/workbook.xml.rels","<Relationships><Relationship Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/externalLink' Target='externalLinks/externalLink1.xml'/></Relationships>");
     Entry("xl/externalLinks/externalLink1.xml","<externalLink/>");
     Entry("xl/externalLinks/_rels/externalLink1.xml.rels","<Relationships><Relationship Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/externalLinkPath' Target='"+target+"' TargetMode='External'/></Relationships>");
     var cached=mode=="missing-cache"?"":mode=="empty-string"?"<v/>":mode=="error-cache"?"<v>#REF!</v>":"<v>1.25</v>";
     Entry("xl/worksheets/sheet1.xml","<worksheet><c r='A1'"+(mode=="empty-string"?" t='str'":mode=="error-cache"?" t='e'":"")+"><f"+(mode=="shared-formula"?" t='shared' si='4'":"")+">[1]Sheet0!A1</f>"+cached+"</c>"+(mode=="shared-formula"?"<c r='A2'><f t='shared' si='4'/><v>2.5</v></c>":"")+"<c r='B1'><f>SUM(A1)</f><v>1.25</v></c>"+(mode=="hyperlink"?"<hyperlinks><hyperlink/></hyperlinks>":"")+"</worksheet>");
     if(mode=="query-source")Entry("xl/connections.xml","<connections/>");if(mode=="macro-source")Entry("xl/vbaProject.bin","macro");
    }
    data=memory.ToArray();
   }
   var rejected=mode=="missing-cache"||mode=="error-cache"||mode=="external-name"||mode=="query-source"||mode=="macro-source";
   byte[] clean=null;Exception failure=null;try{clean=(byte[])packageType.GetMethod("RemoveWorkbookLinks",Flags).Invoke(null,new object[]{data});}catch(TargetInvocationException error){failure=error.InnerException;}
   if((failure!=null)!=rejected||failure!=null&&failure is not IOException)throw new Exception("设计表保存结果断链规则不正确："+mode,failure);
   if(!rejected)
   {
    using(var input=new MemoryStream(clean))using(var zip=new System.IO.Compression.ZipArchive(input,System.IO.Compression.ZipArchiveMode.Read))
    {
     using var reader=new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml").Open());var xml=reader.ReadToEnd();
     if(zip.Entries.Any(entry=>entry.FullName.StartsWith("xl/externalLinks/"))||xml.Contains("[1]")||!xml.Contains("SUM(A1)")||mode=="shared-formula"&&xml.Contains("si="))throw new Exception("设计表仍有外链或丢失内部公式："+mode);
    }
    failure=null;try{packageType.GetMethod("RequireUnavailableLocalWorkbookSources",Flags).Invoke(null,new object[]{data});}catch(TargetInvocationException error){failure=error.InnerException;}
    if((failure!=null)!=(mode=="network-source"||mode=="live-source"))throw new Exception("设计表禁止意外刷新来源的规则不正确："+mode,failure);
   }
   Console.WriteLine("PASS: external design table content preserves safe cached data and validates "+mode);
  }
  using(var child=Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),@"WindowsPowerShell\v1.0\powershell.exe"),"-NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 30\""){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden}))
  {
   try{using(var owned=(IDisposable)Activator.CreateInstance(Type("ArchiveOwnedProcess"),Flags,null,new object[]{child},null)){}if(!child.WaitForExit(5000))throw new Exception("专用辅助进程没有随归档所有者清理");}
   finally{if(!child.HasExited)child.Kill();}
  }
  Console.WriteLine("PASS: dedicated helper exits with its Windows job and unrelated processes remain untouched");
  foreach(var mode in new[]{"linked","unknown-class","missing-buffer","not-storage"})
  {
   var item=new Proxy(oleType,call=>call.MethodName=="get_IsLinked"?mode=="linked":call.MethodName=="get_Clsid"?(mode=="unknown-class"?Guid.NewGuid().ToString("B"):"{0003000C-0000-0000-C000-000000000046}"):call.MethodName=="get_Buffer"?(mode=="missing-buffer"?null:new sbyte[]{1,2,3}):call.MethodName=="get_BufferSize"?3:throw new Exception(call.MethodName)).GetTransparentProxy();
   if((bool)packageType.GetMethod("IsSolidWorksExportNote",Flags).Invoke(null,new[]{item}))throw new Exception("未知嵌入对象被放行："+mode);
   Console.WriteLine("PASS: embedded object rejects "+mode+" without activation or changing its contents");
  }
  var jobs=Path.Combine(Path.GetTempPath(),"UPTON-PDM","native-isolation","jobs");Directory.CreateDirectory(jobs);
  var smartInterop=Type("PdmAddin").GetField("application",Flags).FieldType.Assembly;
  foreach(var mode in new[]{"broken-history","controlled-source","non-smart-missing","mixed-links","incomplete-status"})
  {
   var missing=Path.Combine(fixture,"MISSING-SMART.SLDPRT");var available=Path.Combine(fixture,"AVAILABLE-SOURCE.SLDPRT");File.WriteAllText(available,"existing source");var breaks=0;
   var paths=mode=="mixed-links"?new[]{missing,available}:new[]{mode=="controlled-source"?available:missing};
   var labels=mode=="mixed-links"?new[]{"Smart Link","Extrusion"}:new[]{mode=="non-smart-missing"?"Extrusion":"Smart Link"};
   var statuses=mode=="incomplete-status"?new int[0]:mode=="mixed-links"?new[]{3,3}:new[]{mode=="broken-history"?0:3};
   object ProxyFor(string name,Func<IMethodCallMessage,object> call)=>new Proxy(smartInterop.GetType("SolidWorks.Interop.sldworks."+name,true),call).GetTransparentProxy();
   var smart=ProxyFor("SmartComponentFeatureData",call=>throw new Exception("守卫不应打开智能训练装配"));
   var feature=ProxyFor("Feature",call=>call.MethodName=="GetDefinition"?smart:call.MethodName=="get_Name"?"Smart Link":call.MethodName=="GetTypeName2"?"SmartFeature":call.MethodName=="GetFirstSubFeature"||call.MethodName=="GetNextFeature"?null:throw new Exception(call.MethodName));
   var featureManager=ProxyFor("FeatureManager",call=>call.MethodName=="GetFeatures"?new[]{feature}:call.MethodName=="GetFeatureCount"?(object)1:throw new Exception(call.MethodName));
   var extension=ProxyFor("ModelDocExtension",call=>{
    if(call.MethodName=="ListExternalFileReferences2"){var args=(object[])call.Args.Clone();args[0]=paths;args[2]=labels;args[4]=statuses;return new ReturnMessage(null,args,args.Length,call.LogicalCallContext,call);}
    if(call.MethodName=="BreakAllExternalFileReferences2"){breaks++;return null;}throw new Exception(call.MethodName);
   });
   var model=ProxyFor("ModelDoc2",call=>call.MethodName=="get_Extension"?extension:call.MethodName=="get_FeatureManager"?featureManager:call.MethodName=="FirstFeature"?feature:call.MethodName=="GetPathName"?source:throw new Exception("守卫不应读写配置或几何："+call.MethodName));
   var controlled=new Dictionary<string,string>{{Path.GetFileName(available),available}};var arguments=new object[]{model,controlled,false};Exception failure=null;
   try{Type("ArchiveSmartComponentLinks").GetMethod("Prepare",Flags).Invoke(null,arguments);}catch(TargetInvocationException error){failure=error.InnerException;}
   var expected=mode=="non-smart-missing"||mode=="mixed-links"||mode=="incomplete-status";
   if((failure!=null)!=expected||failure!=null&&failure is not IOException||breaks!=0)throw new Exception("智能关联安全边界不正确："+mode,failure);
   Console.WriteLine("PASS: smart association guard validates "+mode+" without changing genuine design links");
  }
  var processType=Type("ArchiveNativeProcessWorkspace");var validate=processType.GetMethod("ValidateResult",Flags);
  foreach(var mode in new[]{"valid","wrong-job","unknown-source","unknown-dependency","missing-requested","duplicate-source","corrupt-output","outside-job","wrong-name","empty-source-hash","failed-response"})
  {
   var job=Path.Combine(jobs,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(job);
   try
   {
    var path=Path.Combine(job,"ENGINE.SLDPRT");File.WriteAllText(path,"independent output");
    var request=New("ArchiveNativeProcessWorkspace+Request");Set(request,"JobId",Path.GetFileName(job));Set(request,"Inventory",new[]{source});Set(request,"Requested",new Dictionary<string,string>{{source,source}});
    var response=New("ArchiveNativeProcessWorkspace+Response");Set(response,"Success",mode!="failed-response");Set(response,"JobId",mode=="wrong-job"?Guid.NewGuid().ToString("N"):Path.GetFileName(job));
    var file=New("ArchiveNativeProcessWorkspace+CompletedFile");Set(file,"Source",mode=="unknown-source"?Path.Combine(fixture,"FOREIGN.SLDPRT"):source);
    Set(file,"Path",mode=="outside-job"?source:mode=="wrong-name"?Path.Combine(job,"RENAMED.SLDPRT"):path);Set(file,"SourceSha256",mode=="empty-source-hash"?"":new string('A',64));
    Set(file,"Sha256",mode=="corrupt-output"?new string('0',64):FileSha256(path));Set(file,"ContentFrozen",true);
    if(mode=="unknown-dependency")Set(file,"Dependencies",new[]{Path.Combine(fixture,"FOREIGN.SLDPRT")});
    var files=(IList)Get(response,"Files");if(mode!="missing-requested")files.Add(file);if(mode=="duplicate-source")files.Add(file);
    Exception failure=null;try{validate.Invoke(null,new[]{job,request,response});}catch(TargetInvocationException error){failure=error.InnerException;}
    if((failure==null)!=(mode=="valid")||failure!=null&&failure is not InvalidOperationException)throw new Exception("原名独立结果校验范围不正确："+mode,failure);
    if(File.ReadAllText(source)!="immutable source")throw new Exception("原件被修改");
    Console.WriteLine("PASS: native process result validates "+mode+" without touching source files");
   }
   finally{Directory.Delete(job,true);}
  }
  if((bool)processType.GetMethod("IsJob",Flags).Invoke(null,new object[]{fixture}))throw new Exception("普通目录被当作独立作业");Console.WriteLine("PASS: ordinary folders cannot invoke the private native job protocol");
  var ordinary=Path.Combine(fixture,"work");Directory.CreateDirectory(ordinary);var ordinaryArgs=new object[]{null,ordinary,null};
  if((bool)processType.GetMethod("TryRun",Flags).Invoke(null,ordinaryArgs))throw new Exception("普通网页解析被独立作业接管");Console.WriteLine("PASS: ordinary browser archives retain their existing reader path");
  var progressJob=Path.Combine(jobs,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(progressJob);
  var previousJob=Environment.GetEnvironmentVariable("UPLM_NATIVE_ARCHIVE_JOB");
  try
  {
   var write=processType.GetMethod("WriteProgress",Flags);var progress=Path.Combine(progressJob,"isolation-progress.json");
   using(var locked=new FileStream(progress,FileMode.Create,FileAccess.ReadWrite,FileShare.None))write.Invoke(null,new object[]{progressJob,"ENGINE.SLDPRT"});
   Console.WriteLine("PASS: concurrent progress reads cannot abort native processing");
   write.Invoke(null,new object[]{progressJob,"ENGINE.SLDPRT"});
   if(!File.ReadAllText(progress).Contains("ENGINE.SLDPRT"))throw new Exception("进度未恢复");Console.WriteLine("PASS: progress updates resume after a transient file lock");
   File.WriteAllText(Path.Combine(progressJob,"cancel"),"cancel");var canceled=false;
   try{write.Invoke(null,new object[]{progressJob,"ENGINE.SLDPRT"});}catch(TargetInvocationException error)when(error.InnerException is OperationCanceledException){canceled=true;}
   if(!canceled)throw new Exception("进度锁定保护吞掉了取消");Console.WriteLine("PASS: progress reporting still honors explicit cancellation");
   Environment.SetEnvironmentVariable("UPLM_NATIVE_ARCHIVE_JOB",fixture);
   if((bool)processType.GetMethod("IsReaderStartup",Flags).Invoke(null,null))throw new Exception("普通目录禁止了交互插件");Console.WriteLine("PASS: ordinary sessions retain interactive add-in startup");
   File.WriteAllText(Path.Combine(progressJob,"native-isolation-request.json"),"{}");Environment.SetEnvironmentVariable("UPLM_NATIVE_ARCHIVE_JOB",progressJob);
   var addin=New("PdmAddin");
   if(!(bool)Type("PdmAddin").GetMethod("ConnectToSW",Flags).Invoke(addin,new object[]{null,0})||Field(addin,"taskPaneControl")!=null)throw new Exception("后台作业加载了交互界面");
   using(var current=Process.GetCurrentProcess())if(!(bool)processType.GetMethod("IsReaderAddin",Flags).Invoke(null,new object[]{progressJob,current.Id}))throw new Exception("后台插件身份未确认");
   if(!(bool)Type("PdmAddin").GetMethod("DisconnectFromSW",Flags).Invoke(addin,null))throw new Exception("后台插件未正确退出");
   Console.WriteLine("PASS: private child add-in connects successfully without interactive startup or registry changes");
   using(var current=Process.GetCurrentProcess())
   {
    var unloadCalls=0;var applicationType=Type("PdmAddin").GetField("application",Flags).FieldType;
    var app=new Proxy(applicationType,call=>{if(call.MethodName=="GetProcessID")return current.Id;if(call.MethodName=="get_StartupProcessCompleted")return true;if(call.MethodName=="UnloadAddIn"){unloadCalls++;throw new Exception("禁止后台卸载插件");}throw new Exception(call.MethodName);}).GetTransparentProxy();
    var prepare=processType.GetMethod("PrepareOwnedReader",Flags);
    prepare.Invoke(null,new object[]{app,current,0,progressJob});
    Console.WriteLine("PASS: matching private reader starts without unloading or changing add-in startup preferences");
    var marker=Path.Combine(progressJob,"reader-addin.json");var json=new JavaScriptSerializer();var stale=json.Deserialize<Dictionary<string,object>>(File.ReadAllText(marker));stale["AssemblyId"]=Guid.Empty.ToString("N");File.WriteAllText(marker,json.Serialize(stale));
    var rejected=false;try{prepare.Invoke(null,new object[]{app,current,0,progressJob});}catch(TargetInvocationException error)when(error.InnerException is InvalidOperationException&&error.InnerException.Message.Contains("版本不一致")){rejected=true;}
    if(!rejected||unloadCalls!=0)throw new Exception("旧版后台组件未在卸载交互插件前被拒绝");
    Console.WriteLine("PASS: stale private reader is rejected before any interactive add-in unload or CAD processing");
   }
  }
  finally{Environment.SetEnvironmentVariable("UPLM_NATIVE_ARCHIVE_JOB",previousJob);Directory.Delete(progressJob,true);}
  var protectedJob=Path.Combine(jobs,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(protectedJob);
  try
  {
   using(var self=Process.GetCurrentProcess())
   {
    var start=self.StartTime.ToUniversalTime().Ticks;
    File.WriteAllText(Path.Combine(protectedJob,"native-owner.json"),new JavaScriptSerializer().Serialize(new{Id=self.Id,Started=start,WorkerId=self.Id,WorkerStarted=start}));
    var rejected=false;try{((Task)processType.GetMethod("StopOwnedReaderAsync",Flags).Invoke(null,new object[]{self,start,self.Id,protectedJob})).GetAwaiter().GetResult();}catch(InvalidOperationException){rejected=true;}
    if(!rejected||self.HasExited)throw new Exception("取消保护未拒绝调用者会话");Console.WriteLine("PASS: cancellation rejects the caller's process before any termination");
   }
  }
  finally{Directory.Delete(protectedJob,true);}
 }
 static string FileSha256(string path)
 {
  using(var stream=File.OpenRead(path))using(var sha=System.Security.Cryptography.SHA256.Create())return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","");
 }
}
