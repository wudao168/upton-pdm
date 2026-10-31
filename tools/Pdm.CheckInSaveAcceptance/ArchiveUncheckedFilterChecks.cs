using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static partial class Program
{
    static async Task ArchiveUncheckedFilterScenario(Form host, string output)
    {
        output=Path.GetFullPath(output);Directory.CreateDirectory(output);
        object Node(string name,string kind="Assembly")
        {
            var node=LinkedNode(output,name,kind);Set(node,"DocumentId",null);Set(node,"DisplayName",name);return node;
        }
        void Child(object parent,object child)=>((IList)Get(parent,"Children")).Add(child);
        var root=Node("总装.SLDASM");var branch=Node("已选子装配.SLDASM");
        var selected=Node("已选零件.SLDPRT","Part");var part=Node("未选零件.SLDPRT","Part");
        var drawing=Node("未选工程图.SLDDRW","Drawing");var selectedOwner=Node("已选嵌入宿主.SLDASM");
        var uncheckedOwner=Node("未选嵌入宿主.SLDASM");var orphan=Node("未选补充文件.SLDPRT","Part");
        var hiddenEmbedded=Node("已选宿主内部件.SLDPRT","Part");var embedded=Node("未选宿主内部装配.SLDASM");var nested=Node("嵌入内部件.SLDPRT","Part");
        foreach(var model in new[]{hiddenEmbedded,embedded,nested})Set(model,"Status",Enum.Parse(Type("CadReferenceStatus"),"Virtual"));
        Child(root,branch);Child(branch,selected);Child(branch,part);Child(selected,drawing);
        Child(root,selectedOwner);Child(selectedOwner,hiddenEmbedded);Child(root,uncheckedOwner);Child(uncheckedOwner,embedded);Child(embedded,nested);
        var repeated=New("CadTreeNode");foreach(var key in new[]{"FullPath","FileName","DisplayName","Kind"})Set(repeated,key,Get(part,key));Child(uncheckedOwner,repeated);
        var items=LinkedItems(root,branch,selected,part,drawing,selectedOwner,uncheckedOwner,orphan,repeated);
        var paths=new[]{root,branch,selected,selectedOwner}.Select(node=>(string)Get(node,"FullPath")).ToArray();
        archiveRuleVersions=new Dictionary<Guid,object[]>();archiveRuleDelay=0;
        using(var dialog=(Form)New("BatchOperationDialog",root,items,Array.CreateInstance(Type("ProjectDto"),0),null,"tester",Enum.Parse(Type("BatchOperationKind"),"CheckIn"),paths,null,null,false,false,0))
        {
            dialog.TopLevel=false;dialog.Dock=DockStyle.Fill;dialog.MinimumSize=Size.Empty;host.ClientSize=new Size(820,840);host.Controls.Add(dialog);
            var loader=dialog.GetType().GetProperty("LoadArchiveVersionsAsync",Flags);
            loader.SetValue(dialog,Delegate.CreateDelegate(loader.PropertyType,typeof(Program).GetMethod("ArchiveRuleVersionLoader",Flags).MakeGenericMethod(Type("CadTreeNode"),Type("DocumentVersionDto"))));
            dialog.Show();await (Task)TreeCall(dialog,"AnalyzeArchiveSelectionAsync",false);
            await TreeWait(()=>!(bool)Field(dialog,"archiveAnalysisInProgress"),"分析未完成");
            var tree=(TreeView)Field(dialog,"files");var summary=(Label)Field(dialog,"selectionSummary");
            IEnumerable<TreeNode> Nodes()=>tree.Nodes.Cast<TreeNode>().SelectMany(CheckedNodes);
            TreeNode Find(object model)=>Nodes().Single(node=>node.Tag!=null&&ReferenceEquals(Get(node.Tag,"Model"),model));
            bool Has(object model)=>Nodes().Any(node=>node.Tag!=null&&ReferenceEquals(Get(node.Tag,"Model"),model));
            string Scope()=>string.Join("|",((IEnumerable)Get(dialog,"SelectedItems")).Cast<object>().Select(item=>(string)Get(Get(item,"Node"),"FullPath")).OrderBy(path=>path));
            void Check(bool value,string message){TreeCheck(value,message);Console.WriteLine("PASS: "+message);}
            Find(root).Expand();Find(branch).Collapse();Find(selectedOwner).Expand();Find(uncheckedOwner).Collapse();
            var before=Scope();
            var buttons=ArchiveControls(dialog).OfType<Button>().ToArray();
            Check(!buttons.Any(button=>button.Text=="待补全引用"),"移除历史引用查看按钮，保留自动存档处理");
            var uncheckedButton=buttons.Single(button=>button.Text=="仅显示未勾选");
            var checkedButton=(Button)Field(dialog,"showCheckedOnly");
            Check(uncheckedButton.Size==new Size(92,28)&&uncheckedButton.Right<=uncheckedButton.Parent.ClientSize.Width,
                "新增按钮完整显示于原操作栏，保持统一高度与分组");
            uncheckedButton.PerformClick();
            Check(Scope()==before&&summary.Text.Contains("已勾选 4 个图档，未勾选 4 个图档"),"未勾选筛选不改变勾选计数和实际提交范围");
            Check(new[]{part,drawing,uncheckedOwner,orphan,repeated}.All(Has),"未勾选零件、工程图、子装配、重复实例及补充文件全部可见");
            Check(!Has(selectedOwner)&&!Has(hiddenEmbedded),"已勾选且不承载未选子件的宿主及嵌入层级隐藏");
            Check(new[]{root,branch,selected}.All(model=>Find(model).StateImageIndex==1&&Find(model).ToolTipText.Contains("仅显示层级，已勾选")),
                "必要的已选父级保留真实勾选状态并明确标注定位层级");
            TreeCall(dialog,"ToggleNode",Find(root));
            Check(Scope()==before,"点击筛选中的定位父级不会改变勾选范围");
            Check(new[]{part,drawing,repeated}.All(model=>CheckedParents(Find(model)).All(parent=>parent.IsExpanded)),"未选项的全部定位路径自动展开");
            Check(Has(embedded)&&Has(nested)&&Find(embedded).IsExpanded,"未选宿主的完整嵌入层级随父文件展示，不作为独立未选文件计数");
            using(var bitmap=new Bitmap(dialog.Width,dialog.Height)){dialog.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));bitmap.Save(Path.Combine(output,"unchecked-filter.png"));}
            TreeCall(dialog,"ToggleNode",Find(part));
            Check(!Has(part)&&!Has(repeated)&&summary.Text.Contains("已勾选 5 个图档，未勾选 3 个图档"),"补勾后全部重复实例即时移出未选视图，计数按文件同步变化");
            var updated=Scope();checkedButton.PerformClick();
            Check((bool)Field(dialog,"showCheckedOnlyActive")&&!(bool)Field(dialog,"showUncheckedOnlyActive")&&Scope()==updated,
                "两种筛选互斥切换且保留完整已选范围");
            uncheckedButton.PerformClick();uncheckedButton.PerformClick();
            Check(!(bool)Field(dialog,"showCheckedOnlyActive")&&!(bool)Field(dialog,"showUncheckedOnlyActive")
                &&!Find(branch).IsExpanded&&Find(selectedOwner).IsExpanded&&!Find(uncheckedOwner).IsExpanded,
                "显示全部恢复进入筛选前各实例的展开状态");
            uncheckedButton.PerformClick();TreeCall(dialog,"SetAllChecked",true);
            Check(summary.Text.Contains("未勾选 0 个图档")&&tree.Nodes.Count==1&&tree.Nodes[0].Text=="没有未勾选图档",
                "全部勾选后显示清晰空态，嵌入组件不产生虚假的未选项");
            TreeCall(dialog,"SetAllChecked",false);
            Check(summary.Text.Contains("已勾选 0 个图档，未勾选 8 个图档")&&new[]{part,drawing,orphan,repeated}.All(Has),
                "清除后未选清单完整显示，数量按独立文件去重");
            await (Task)TreeCall(dialog,"AnalyzeArchiveSelectionAsync",false);
            Check(summary.Text.Contains("未勾选 8 个图档")&&CheckedParents(Find(drawing)).All(parent=>parent.IsExpanded),
                "重新检查保留未选筛选及手动选择，不把条目藏回折叠层级");
            TreeCall(dialog,"SetAllChecked",true);TreeCall(dialog,"ToggleShowUncheckedOnly");TreeCall(dialog,"ToggleNode",Find(part));
            var frozen=Scope();TreeCall(dialog,"SetWorkflowBusy");uncheckedButton.PerformClick();
            Check(uncheckedButton.Enabled&&checkedButton.Enabled&&Has(part),"存档准备及执行期间仍可筛选查看未选项");
            TreeCall(dialog,"ToggleNode",Find(part));
            Check(Scope()==frozen&&!((Button)Field(dialog,"execute")).Enabled,"存档期间查看筛选保持只读，不能改变已确认范围或重复提交");
            TreeCall(dialog,"FinishWorkflow");checkedButton.PerformClick();uncheckedButton.PerformClick();
            Check(uncheckedButton.Enabled&&checkedButton.Enabled&&Has(part)&&Scope()==frozen,"结果页仍可核对未选项且不改变存档范围");
        }
    }
}
