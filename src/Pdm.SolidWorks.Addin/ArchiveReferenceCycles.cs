using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Upton.Pdm.Domain;
namespace Upton.Pdm.SolidWorks;
internal static class ArchiveReferenceCycles
{
 internal sealed class Edge { internal CadTreeNode Owner, Target; internal bool Verified => Target.Role == CadReferenceRole.Component || Target.ReferenceTypeVerified; }
 internal sealed class Group { internal IReadOnlyList<string> Files; internal IReadOnlyList<Edge> Edges; }
 internal static IReadOnlyList<Group> Analyze(IReadOnlyList<BatchOperationItem> items) => AnalyzeGraph(items, false);
 private static IReadOnlyList<Group> AnalyzeGraph(IReadOnlyList<BatchOperationItem> items, bool confirmedOnly)
 {
  var edges=new List<Edge>();var seen=new HashSet<CadTreeNode>();var pending=new Stack<CadTreeNode>(items.Select(item=>item.Node));
  while(pending.Count!=0){var owner=pending.Pop();if(!seen.Add(owner)||owner.IsOutsideNativeFileScope||string.IsNullOrWhiteSpace(owner.FullPath))continue;
   foreach(var child in owner.Children.Concat(owner.ReferencedModels)){
    if(child.IsOutsideNativeFileScope||child.Kind==CadDocumentKind.Drawing)continue;
    if(child.IsContextReference){if(child.Role!=CadReferenceRole.ExternalFileDependency||child.Kind!=CadDocumentKind.Assembly||child.Children.Count!=0||child.ReferencedModels.Count!=0)throw new InvalidOperationException("上下文关联记录不完整，未开始上传："+owner.FileName+" → "+child.FileName);continue;}
    if(child.Status!=CadReferenceStatus.Virtual&&!string.IsNullOrWhiteSpace(child.FullPath)){var edge=new Edge{Owner=owner,Target=child};if(!confirmedOnly||edge.Verified)edges.Add(edge);}pending.Push(child);
   }
  }
  return ReferenceCycleGroups.Find(edges.Select(edge=>new KeyValuePair<string,string>(edge.Owner.FullPath,edge.Target.FullPath)),StringComparer.OrdinalIgnoreCase)
   .Select(files=>{var set=new HashSet<string>(files,StringComparer.OrdinalIgnoreCase);return new Group{Files=files,Edges=edges.Where(edge=>set.Contains(edge.Owner.FullPath)&&set.Contains(edge.Target.FullPath)).ToArray()};}).ToArray();
 }
 internal static void Validate(IReadOnlyList<BatchOperationItem> items)
 {
  // A proven cycle may be a subgraph of a larger group containing unknown links.
  var confirmed=AnalyzeGraph(items,true);
  if(confirmed.Count!=0)throw new InvalidOperationException("已确认文件依赖存在循环，尚未上传："+string.Join("；",confirmed.Select(group=>string.Join("、",group.Files.Select(Path.GetFileName)))));
  var groups=Analyze(items);if(groups.Count==0)return;
  var messages=new List<string>();
  foreach(var group in groups)
  {
   var unknown=group.Edges.Where(edge=>!edge.Verified).ToArray();
   var detail="引用类型待核对："+string.Join("；",unknown.Select(edge=>edge.Owner.FullPath+" → "+edge.Target.FullPath).Distinct())+"；原文件和关联保留，未确认为普通依赖或上下文，结构待核对。";
   foreach(var edge in unknown)
   {
    if(edge.Target.Role!=CadReferenceRole.ExternalFileDependency||edge.Target.IsContextReference||edge.Target.Status==CadReferenceStatus.Virtual||edge.Target.Children.Count!=0||edge.Target.ReferencedModels.Count!=0)
     throw new InvalidOperationException("待核对引用记录包含装配结构，不能安全按警告存档："+edge.Owner.FileName+" → "+edge.Target.FileName);
    edge.Target.ReferenceTypeUnverified=true;
    edge.Owner.StructureIndexPending=true;
    ArchiveWarningGate.Record(edge.Owner,"ReferenceTypeUnknown",detail);
   }
   foreach(var item in items.Where(item=>group.Files.Contains(item.Node.FullPath,StringComparer.OrdinalIgnoreCase)))
   {
    item.Node.StructureIndexPending=true;
    ArchiveWarningGate.Record(item.Node,"ReferenceTypeUnknown",detail);
   }
   messages.Add(detail);
  }
  ArchiveWarningGate.Check("ReferenceTypeUnknown",string.Join("\r\n",messages));
 }
}
