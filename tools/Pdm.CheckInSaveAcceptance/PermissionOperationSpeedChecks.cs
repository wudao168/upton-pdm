using System;
using System.Collections;
using System.Threading;

internal static partial class Program
{
 sealed class PermissionRefreshFixture:IDisposable
 {
  readonly object plugin,root,client;readonly Guid project;readonly string key;readonly bool missing;
  readonly CancellationTokenSource previousMetadata=new CancellationTokenSource();readonly CancellationToken oldToken;
  bool structural;
  public PermissionRefreshFixture(object plugin,object root,object model,object client,Guid project,bool missing)
  {
   this.plugin=plugin;this.root=root;this.client=client;this.project=project;this.missing=missing;
   key=(string)Type("PdmAddin").GetMethod("BuildActiveTreeCacheKey",Flags).Invoke(null,new[]{model});
   SetField(plugin,"currentProjectId",project);SetField(plugin,"currentTreeCacheKey",key);SetField(plugin,"currentDocumentIdentity",Get(root,"FullPath"));
   if(!missing)Type("PdmAddin").GetMethod("CacheActiveTree",Flags).Invoke(plugin,new object[]{key,root,project});
   var staleDocuments=Array.CreateInstance(Type("DocumentDto"),0);
   ((IDictionary)Field(plugin,"projectDocumentsCache"))[project]=New("PdmAddin+ProjectDocumentsCacheEntry",staleDocuments,DateTime.UtcNow.AddMinutes(10));
   oldToken=previousMetadata.Token;SetField(plugin,"metadataRefreshCancellation",previousMetadata);
   SetField(plugin,"pendingTreeRefresh",0);SetField(plugin,"pendingTreeRefreshForceScan",0);SetField(plugin,"pendingTreeRefreshScheduledAt",0L);
  }
  public void StructuralChange()
  {
   if(structural)return;structural=true;
   Type("PdmAddin").GetMethod("OnAssemblyTreeChanged",Flags).Invoke(plugin,null);
  }
  public void AssertAfterHandler()
  {
   if((int)Field(plugin,"pendingTreeRefresh")!=1||(int)Field(plugin,"pendingTreeRefreshForceScan")!=(structural?1:0))
    throw new Exception("Permission-only handler forced a full scan or erased a real structural scan");
   if(!oldToken.IsCancellationRequested||((IDictionary)Field(plugin,"projectDocumentsCache")).Contains(project))
    throw new Exception("Permission refresh retained stale/in-flight checkout metadata");
   // Inspect the production cache restore without starting another metadata task.
   ((System.Net.Http.HttpClient)Field(client,"httpClient")).DefaultRequestHeaders.Authorization=null;
   var restored=(bool)Type("PdmAddin").GetMethod("TryRestoreActiveTreeFromCache",Flags).Invoke(plugin,new object[]{key});
   if(restored!=( !missing&&!structural )||restored&&!ReferenceEquals(Field(plugin,"currentTree"),root))
    throw new Exception("Permission refresh lost the existing tree or reused an invalidated/missing tree");
  }
  public void Dispose()=>previousMetadata.Dispose();
 }
}
