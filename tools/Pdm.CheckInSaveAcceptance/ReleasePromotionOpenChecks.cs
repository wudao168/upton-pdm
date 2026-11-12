using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

internal static partial class Program
{
 static void ReleasePromotionOpenScenario(string evidencePath)
 {
  var json=new JavaScriptSerializer{MaxJsonLength=int.MaxValue};
  var check=Type("PdmAddin").GetMethod("WorkingReuseReleasePromotionMatches",Flags);
  if(check==null)throw new InvalidOperationException("当前插件仍使用版本ID直接冲突判定，尚无审批发布来源等价校验。");
  bool Matches(object source,object requested,object before,object after)=>(bool)check.Invoke(null,new[]{source,requested,before,after});
  object Clone(object value)=>json.Deserialize(json.Serialize(value),value.GetType());
  foreach(var mode in new[]{"promotion","property-order","missing-source","wrong-source","source-document","released-document","requested-document","content","length","source-record-content","released-record-length","missing-snapshot","snapshot-document","snapshot-configuration","snapshot-role","snapshot-suppression","snapshot-children","snapshot-scope","snapshot-dependencies","snapshot-native-inventory","snapshot-native-isolation","snapshot-isolation-verified","properties","source-status","released-status","released-date","requested-configuration"})
  {
   var documentId=Guid.NewGuid();var sourceId=Guid.NewGuid();var releasedId=Guid.NewGuid();var hash=new string('A',64);
   var source=json.Deserialize(json.Serialize(new{documentId,versionId=sourceId,fileName="PART.SLDPRT",relativePath="PART.SLDPRT",configuration="Default",revision="W1",sha256=hash,fileLength=100L}),Type("ControlledOpenFileDto"));
   var requested=Clone(source);Set(requested,"VersionId",releasedId);Set(requested,"Revision","A");
   var before=json.Deserialize(json.Serialize(new{id=sourceId,documentId,status=0,revision=new{display="W1"},createdAt=new DateTime(2026,11,11),sha256=hash,fileLength=100L,
    propertySnapshot=new Dictionary<string,string>{{"DrawingNumber","PART"}},referenceSnapshot=new{documentId,fileName="PART.SLDPRT",configuration="Default",kind=1,children=new object[0]}}),Type("DocumentVersionDto"));
   var after=Clone(before);Set(after,"Id",releasedId);Set(after,"SourceVersionId",sourceId);Set(after,"Status",1);Set(Get(after,"Revision"),"Display","A");Set(after,"CreatedAt",new DateTime(2026,11,12));
   var snapshot=Get(after,"ReferenceSnapshot");
   switch(mode)
   {
    case "property-order":Set(after,"ReferenceSnapshot",json.Deserialize(json.Serialize(new{children=new object[0],kind=1,configuration="Default",fileName="PART.SLDPRT",documentId}),Type("DocumentReferenceNodeDto")));break;
    case "missing-source":Set(after,"SourceVersionId",null);break;
    case "wrong-source":Set(after,"SourceVersionId",Guid.NewGuid());break;
    case "source-document":Set(before,"DocumentId",Guid.NewGuid());break;
    case "released-document":Set(after,"DocumentId",Guid.NewGuid());break;
    case "requested-document":Set(requested,"DocumentId",Guid.NewGuid());break;
    case "content":Set(requested,"Sha256",new string('B',64));break;
    case "length":Set(requested,"FileLength",101L);break;
    case "source-record-content":Set(before,"Sha256",new string('B',64));break;
    case "released-record-length":Set(after,"FileLength",101L);break;
    case "missing-snapshot":Set(after,"ReferenceSnapshot",null);break;
    case "snapshot-document":Set(snapshot,"DocumentId",Guid.NewGuid());break;
    case "snapshot-configuration":Set(snapshot,"Configuration","Other");break;
    case "snapshot-role":Set(snapshot,"Role",1);break;
    case "snapshot-suppression":Set(snapshot,"IsSuppressed",true);break;
    case "snapshot-children":((IList)Get(snapshot,"Children")).Add(Clone(snapshot));break;
    case "snapshot-scope":Set(snapshot,"TreeScopeOnly",true);break;
    case "snapshot-dependencies":Set(snapshot,"IncludesDesignDependencies",true);break;
    case "snapshot-native-inventory":Set(snapshot,"UsesNativeFileInventory",true);break;
    case "snapshot-native-isolation":Set(snapshot,"NativeIsolation",json.Deserialize(json.Serialize(new{sourceVersionId=sourceId,sourceSha256=hash,relativePath="isolated/PART.SLDPRT",length=100L,sha256=hash,geometryEvidenceSha256=hash,policyVersion=1,referenceSnapshotId=Guid.NewGuid(),referenceScopeSha256=hash}),Type("NativeFileIsolationDto")));break;
    case "snapshot-isolation-verified":Set(snapshot,"NativeIsolationVerified",true);break;
    case "properties":((IDictionary)Get(after,"PropertySnapshot"))["DrawingNumber"]="CHANGED";break;
    case "source-status":Set(before,"Status",1);break;
    case "released-status":Set(after,"Status",0);break;
    case "released-date":Set(after,"CreatedAt",new DateTime(2026,11,10));break;
    case "requested-configuration":Set(requested,"Configuration","Other");break;
   }
   if(Matches(source,requested,before,after)!=(mode=="promotion"||mode=="property-order"))throw new Exception("审批发布等价判定错误："+mode);
   Console.WriteLine("PASS: 审批发布派生须同图档、显式来源、相同内容、配置及完整快照："+mode);
  }
  if(string.IsNullOrWhiteSpace(evidencePath))return;
  var evidence=json.Deserialize<Dictionary<string,object>>(File.ReadAllText(evidencePath));
  var manifest=json.Deserialize(json.Serialize(evidence["Manifest"]),Type("ControlledOpenManifestDto"));
  var requestedRoot=((IList)Get(manifest,"Files")).Cast<object>().Single(file=>Equals(Get(file,"DocumentId"),Get(manifest,"RootDocumentId")));
  var versions=((IEnumerable)evidence["PartVersions"]).Cast<object>().Select(value=>json.Deserialize(json.Serialize(value),Type("DocumentVersionDto"))).ToArray();
  var released=versions.Single(version=>Equals(Get(version,"Id"),Get(requestedRoot,"VersionId")));
  var original=versions.Single(version=>Equals(Get(version,"Id"),Get(released,"SourceVersionId")));
  var localRoot=Clone(requestedRoot);Set(localRoot,"VersionId",Get(original,"Id"));Set(localRoot,"Revision",Get(Get(original,"Revision"),"Display"));Set(localRoot,"Sha256",Get(original,"Sha256"));Set(localRoot,"FileLength",Get(original,"FileLength"));
  if(Equals(Get(localRoot,"VersionId"),Get(requestedRoot,"VersionId")))throw new Exception("现场证据未包含两个不同版本，不能复现原冲突。");
  if(!Matches(localRoot,requestedRoot,original,released))throw new Exception("现场数据未通过严格审批发布来源等价校验。");
  Console.WriteLine("PASS: 现场只读离线回放：旧版本ID直接比较拒绝，新版经显式发布来源及完整证据核对允许复用；原基线="+Get(localRoot,"VersionId")+"；发布版本="+Get(requestedRoot,"VersionId"));
 }
}
