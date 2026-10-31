using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

internal static partial class Program
{
 static void SelectedScopeScenario(string output)
 {
  output=Path.GetFullPath(output);Directory.CreateDirectory(output);
  void Check(bool ok,string text){if(!ok)throw new Exception(text);Console.WriteLine("PASS: "+text);}
  var root=LinkedNode(output,"ROOT.SLDASM","Assembly");var part=LinkedNode(output,"PART.SLDPRT","Part");
  var drawing=LinkedNode(output,"PART.SLDDRW","Drawing");((IList)Get(root,"Children")).Add(part);((IList)Get(part,"Children")).Add(drawing);
  for(var i=0;i<6;i++)((IList)Get(root,"Children")).Add(LinkedNode(output,"UNSELECTED-"+i+".SLDASM","Assembly"));
  var items=LinkedItems(part,drawing);var rootAncestors=Array.CreateInstance(Type("CadTreeNode"),1);rootAncestors.SetValue(root,0);
  foreach(var item in items)TreeCall(item,"AddAncestors",rootAncestors);
  var inspect=Type("PdmAddin").GetMethod("ArchiveInspectionRoots",Flags);
  var actual=((IEnumerable)inspect.Invoke(null,new[]{root,(object)items})).Cast<object>().ToArray();
  Check(actual.Length==2&&actual.Contains(part)&&actual.Contains(drawing),"只检查勾选零件及配套工程图，不检查另外6个子装配");
  var whole=((IEnumerable)inspect.Invoke(null,new[]{root,(object)LinkedItems(root,part,drawing)})).Cast<object>().ToArray();
  Check(whole.Length==1&&whole[0]==root,"选择总装时继续检查完整根结构，不重复检查子项");
  var sub=LinkedNode(output,"SELECTED.SLDASM","Assembly");var embedded=New("CadTreeNode");Set(embedded,"Status",Enum.Parse(Type("CadReferenceStatus"),"Virtual"));((IList)Get(sub,"Children")).Add(embedded);
  var subItems=LinkedItems(sub,part);var ancestors=Array.CreateInstance(Type("CadTreeNode"),2);ancestors.SetValue(root,0);ancestors.SetValue(sub,1);TreeCall(subItems.GetValue(1),"AddAncestors",ancestors);
  actual=((IEnumerable)inspect.Invoke(null,new[]{root,(object)subItems})).Cast<object>().ToArray();
  Check(actual.Length==1&&actual[0]==sub&&((IList)Get(sub,"Children")).Contains(embedded),"选中子装配完整保留嵌入层级，已包含的子项不重复检查");
  var plan=New("PdmAddin+BatchCheckInPlan",LinkedItems(part,drawing,drawing),0,null);
  Check(((IEnumerable)Get(plan,"Items")).Cast<object>().Count()==2,"提交计划按文件去重，显示数量与实际提交数量一致");
  Set(root,"IsModifiedInSolidWorks",true);
  using(var dialog=(IDisposable)New("BatchOperationDialog",root,LinkedItems(root,part,drawing),Array.CreateInstance(Type("ProjectDto"),0),null,"tester",Enum.Parse(Type("BatchOperationKind"),"CheckIn"),null,null,null,false,false,0))
  {
   var message=(string)TreeCall(dialog,"ArchiveSelectionText",root);
   Check(message.Contains("未获取权限")&&!message.Contains("请先保存"),"未获取总装权限时不再提示先保存只读总装");
   Set(root,"CheckedOutBy","tester");message=(string)TreeCall(dialog,"ArchiveSelectionText",root);
   Check(message.Contains("已获取权限"),"已获取权限与未获取权限使用不同的待核对提示");Set(root,"CheckedOutBy",null);
  }

  var plugin=New("PdmAddin");SetField(plugin,"authenticatedUsername","tester");SetField(plugin,"currentTree",root);
  SetField(plugin,"taskPaneControl",New("PdmTaskPaneControl","范围回归"));
  var project=Guid.NewGuid();var session=(Guid)Field(plugin,"checkoutSessionId");
  var cached=LinkedNode(output,"CACHED.SLDASM","Assembly");var cachedPart=New("CadTreeNode");Set(cachedPart,"DocumentId",Get(part,"DocumentId"));Set(cachedPart,"FullPath",Get(part,"FullPath"));((IList)Get(cached,"Children")).Add(cachedPart);
  var historical=New("CadTreeNode");Set(historical,"DocumentId",Get(part,"DocumentId"));Set(historical,"FullPath",Path.Combine(output,"history","PART.SLDPRT"));Set(historical,"IsHistoricalPreview",true);Set(historical,"CurrentRevision","W0");
  var trees=(IDictionary)Field(plugin,"activeTreeCache");trees.Add("cached",New("PdmAddin+ActiveTreeCacheEntry",cached,project,DateTime.UtcNow));trees.Add("history",New("PdmAddin+ActiveTreeCacheEntry",historical,project,DateTime.UtcNow));
  var doc=New("DocumentDto");Set(doc,"Id",Get(part,"DocumentId"));Set(doc,"ProjectId",project);Set(doc,"CheckedOutBy","tester");Set(doc,"CheckoutSessionId",session);Set(doc,"Revision",LinkedRevision("W2"));Set(doc,"DrawingVersion","V2");Set(doc,"StoredVersionCount",2);
  TreeCall(plugin,"ApplyCheckoutDocumentToMatchingInstances",Array.CreateInstance(Type("CadTreeNode"),0),doc);
  // Explicit sources are required: another cached directory may not inherit this checkout.
  Check(Get(cachedPart,"CheckedOutBy")==null,"没有明确工作路径时不改动其他缓存树的权限");
  var sourceNodes=Array.CreateInstance(Type("CadTreeNode"),1);sourceNodes.SetValue(part,0);TreeCall(plugin,"ApplyCheckoutDocumentToMatchingInstances",sourceNodes,doc);
  Check((string)Get(cachedPart,"CheckedOutBy")=="tester"&&Get(cachedPart,"CheckoutSessionId").Equals(session),"获取回执立即更新同路径的缓存树实例");
  Check((string)Get(historical,"CurrentRevision")=="W0"&&Get(historical,"CheckedOutBy")==null,"状态回执不改写历史预览或其他目录副本");
  var version=LinkedVersion(part,LinkedSnapshot(part));Set(version,"Revision",LinkedRevision("W2"));Set(doc,"CheckedOutBy",null);Set(doc,"CheckoutSessionId",null);
  TreeCall(plugin,"ApplyCheckedInDocumentToMatchingInstances",part,doc,version);
  Check((string)Get(cachedPart,"CurrentRevision")=="W2"&&string.IsNullOrEmpty((string)Get(cachedPart,"CheckedOutBy")),"存档回执立即更新版本并释放对应缓存实例权限");
  Set(part,"WorkState",Enum.Parse(Type("CadWorkState"),"ModifiedUnsaved"));
  var state=(string)TreeCall(Field(plugin,"taskPaneControl"),"WorkStateText",part);
  Check(state.Contains("未获取权限"),"设计树同时说明未获取权限和内存待核对状态");
  Set(part,"VersionStatusPending",true);state=(string)TreeCall(Field(plugin,"taskPaneControl"),"WorkStateText",part);
  Check(state.Contains("版本待核对"),"版本读取未完成时不会把旧显示当作已核对结果");
  var stamp=Type("PdmAddin").GetMethod("NavigationVersionStamp",Flags);var before=(string)stamp.Invoke(null,new[]{doc});Set(doc,"UpdatedAt",DateTime.UtcNow.AddHours(1));
  Check(before==(string)stamp.Invoke(null,new[]{doc}),"权限或更新时间变化不清空未变更文件的版本缓存");Set(doc,"DrawingVersion","V3");
  Check(before!=(string)stamp.Invoke(null,new[]{doc}),"新图档版本仍使缓存失效");
  Set(doc,"DrawingVersion","V2");Set(doc,"UpdatedAt",DateTime.UtcNow);TreeCall(plugin,"RememberNavigationReceipt",doc,null);
  var docs=Array.CreateInstance(Type("DocumentDto"),1);docs.SetValue(doc,0);TreeCall(plugin,"RememberNavigationVersions",docs);
  Check(((IDictionary)Field(plugin,"navigationVersions")).Contains(Get(doc,"Id")),"确认的获取或释放回执更新时间不清除同一版本缓存");
  Set(doc,"UpdatedAt",DateTime.UtcNow.AddHours(2));TreeCall(plugin,"RememberNavigationVersions",docs);
  Check(!((IDictionary)Field(plugin,"navigationVersions")).Contains(Get(doc,"Id")),"其他服务端元数据变化仍使缓存失效，结构索引更新不会被忽略");
  ReferencePreparationReuseChecks(Path.Combine(output,"projection"),Check);
 }

 static void ReferencePreparationReuseChecks(string output,Action<bool,string> check)
 {
  Directory.CreateDirectory(output);var store=Type("PdmDocumentIdentityStore");var bindings=store.GetField("bindingDirectory",Flags);var resolver=store.GetField("workspaceRootResolver",Flags);
  var oldBindings=bindings.GetValue(null);var oldResolver=resolver.GetValue(null);bindings.SetValue(null,Path.Combine(output,"bindings"));resolver.SetValue(null,new Func<string>(()=>output));
  try
  {
   var manifest=New("ControlledOpenManifestDto");Set(manifest,"ProjectId",Guid.NewGuid());var files=(IList)Activator.CreateInstance(Property(manifest,"Files").PropertyType);
   for(var i=0;i<387;i++)
   {
    var name=i==0?"ROOT.SLDASM":"PART-"+i+".SLDPRT";var path=Path.Combine(output,name);File.WriteAllText(path,"unchanged archived "+name);
    var file=New("ControlledOpenFileDto");Set(file,"DocumentId",Guid.NewGuid());Set(file,"VersionId",Guid.NewGuid());Set(file,"FileName",name);Set(file,"RelativePath",name);Set(file,"Sha256",WorkingReuseHash(path));Set(file,"FileLength",new FileInfo(path).Length);Set(file,"Revision","W1");files.Add(file);
    store.GetMethod("TryWriteControlledVersion",Flags).Invoke(null,new[]{(object)path,Get(file,"DocumentId"),Get(manifest,"ProjectId"),Get(file,"VersionId"),"W1",Get(file,"Sha256"),Get(file,"FileLength")});
   }
   Set(manifest,"Files",files);var root=Path.Combine(output,"ROOT.SLDASM");var materializer=Type("ControlledReferenceMaterializer");var calls=0;
   var appType=Type("PdmAddin").GetField("application",Flags).FieldType;
   object App()=>new Proxy(appType,call=>call.MethodName=="GetDocuments"?new object[0]:call.MethodName=="GetDocumentDependencies2"?Count():throw new Exception("不得打开、保存或重建图档："+call.MethodName)).GetTransparentProxy();
   object Count(){calls++;return new string[0];}
   materializer.GetMethod("Prepare",Flags).Invoke(null,new[]{App(),root,manifest});check(calls==387,"首次引用预处理仍逐项核对受控范围");
   materializer.GetMethod("RememberVerifiedOpen",Flags).Invoke(null,new[]{(object)root,manifest});calls=0;
   materializer.GetMethod("Prepare",Flags).Invoke(null,new[]{App(),root,manifest});check(calls==0,"387项已成功核对的副本再次打开时不重复调用原生引用预处理");
   var key=(string)materializer.GetMethod("ReferencePreparationKey",Flags).Invoke(null,new[]{(object)output,manifest});
   bool Prepared(object file)=> (bool)materializer.GetMethod("HasPreparedReferences",Flags).Invoke(null,new[]{(object)Path.Combine(output,(string)Get(file,"FileName")),file,Get(manifest,"ProjectId"),key});
   var originalVersion=Get(files[1],"VersionId");Set(files[1],"VersionId",Guid.NewGuid());check(!Prepared(files[1]),"基准版本改变不能复用旧引用准备记录");Set(files[1],"VersionId",originalVersion);
   // A byte change, even with unchanged size and write time, must invalidate reuse.
   var changed=Path.Combine(output,(string)Get(files[2],"FileName"));var bytes=File.ReadAllBytes(changed);var time=File.GetLastWriteTimeUtc(changed);bytes[0]^=1;File.WriteAllBytes(changed,bytes);File.SetLastWriteTimeUtc(changed,time);
   check(!Prepared(files[2]),"文件字节变化即失效，不仅依赖时间和大小");
   Set(files[3],"RelativePath","other/PART-3.SLDPRT");var moved=(string)materializer.GetMethod("ReferencePreparationKey",Flags).Invoke(null,new[]{(object)output,manifest});check(moved!=key,"受控路径映射改变必须重新检查");
   var other=(string)materializer.GetMethod("ReferencePreparationKey",Flags).Invoke(null,new[]{(object)Path.Combine(output,"other"),manifest});check(other!=moved,"移动工作目录后不会复用旧目录的引用记录");
   Set(files[3],"RelativePath","PART-3.SLDPRT");File.WriteAllText(changed,"unchanged archived PART-2.SLDPRT");
   var unknown=files[1];Set(unknown,"VersionId",Guid.NewGuid());var unknownPath=Path.Combine(output,(string)Get(unknown,"FileName"));
   store.GetMethod("TryWriteControlledVersion",Flags).Invoke(null,new[]{(object)unknownPath,Get(unknown,"DocumentId"),Get(manifest,"ProjectId"),Get(unknown,"VersionId"),"W1",Get(unknown,"Sha256"),Get(unknown,"FileLength")});
   var externalMode=false;var source=Path.Combine(output,"missing.STEP");
   object UncertainApp()=>new Proxy(appType,call=>call.MethodName=="GetDocuments"?new object[0]:call.MethodName=="GetDocumentDependencies2"
    ?((string)call.Args[0]==unknownPath?(externalMode?new[]{"source",source}:null):new string[0]):throw new Exception("禁止的模型操作")).GetTransparentProxy();
   materializer.GetMethod("Prepare",Flags).Invoke(null,new[]{UncertainApp(),root,manifest});materializer.GetMethod("RememberVerifiedOpen",Flags).Invoke(null,new[]{(object)root,manifest});
   check(!Prepared(unknown),"依赖清单未完整读取时不生成可跳过检查的复用记录");externalMode=true;
   materializer.GetMethod("Prepare",Flags).Invoke(null,new[]{UncertainApp(),root,manifest});materializer.GetMethod("RememberVerifiedOpen",Flags).Invoke(null,new[]{(object)root,manifest});
   check(!Prepared(unknown),"旧外部来源缺失记录保留，每次仍核对，不冒充引用已完整受控");
   File.WriteAllText(source,"external source appeared later");var blocked=false;
   try{materializer.GetMethod("Prepare",Flags).Invoke(null,new[]{UncertainApp(),root,manifest});}
   catch(System.Reflection.TargetInvocationException error){blocked=error.InnerException is IOException;}
   check(blocked,"原目录外部来源后来重新出现时仍拦截，不被复用记录绕过");
  }
  finally{bindings.SetValue(null,oldBindings);resolver.SetValue(null,oldResolver);}
 }
}
