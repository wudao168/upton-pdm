using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Upton.Pdm.SolidWorks;

internal sealed class WhereUsedDialog : Form
{
    public WhereUsedDialog(string documentName, IReadOnlyList<DocumentWhereUsedDto> usages)
    {
        Text = "使用位置";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(760, 420);
        ClientSize = new Size(860, 500);
        Font = new Font("Microsoft YaHei UI", 9F);

        var title = new Label
        {
            Dock = DockStyle.Top,
            Height = 54,
            Padding = new Padding(12, 8, 12, 6),
            Text = string.Concat(documentName, "\r\n按各项目当前最新受控结构计算，共 ", usages?.Count ?? 0, " 个引用位置。"),
            BackColor = Color.FromArgb(237, 244, 255)
        };
        var list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true };
        list.Columns.Add("项目", 150);
        list.Columns.Add("父装配体", 150);
        list.Columns.Add("名称", 170);
        list.Columns.Add("版本/状态", 100);
        list.Columns.Add("配置/数量", 110);
        list.Columns.Add("实例路径", 220);
        foreach (var usage in usages ?? Array.Empty<DocumentWhereUsedDto>())
        {
            var item = new ListViewItem(string.Concat(usage.ProjectCode, " · ", usage.ProjectName));
            item.SubItems.Add(usage.ParentDrawingNumber ?? string.Empty);
            item.SubItems.Add(usage.ParentName ?? string.Empty);
            item.SubItems.Add(string.Concat(usage.ParentRevision?.Display ?? "-", " / ", LifecycleText(usage.ParentState)));
            item.SubItems.Add(string.Concat(string.IsNullOrWhiteSpace(usage.Configuration) ? "默认" : usage.Configuration, " / ", Math.Max(1, usage.Quantity)));
            item.SubItems.Add(usage.InstancePath ?? string.Empty);
            list.Items.Add(item);
        }
        Controls.Add(list);
        Controls.Add(title);
    }

    private static string LifecycleText(int state) => state == 1 ? "审批中" : state == 2 ? "已发布" : state == 3 ? "已作废" : "工作中";
}
