using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Upton.Pdm.SolidWorks;

internal sealed class ArchiveScopeReviewDialog : Form
{
    public ArchiveScopeReviewDialog(IReadOnlyList<string[]> rows, int planned, int reused)
    {
        Text = "确认本次存档范围";
        Font = new Font("Microsoft YaHei UI", 9F);
        ClientSize = new Size(920, 520); MinimumSize = new Size(660, 380);
        ShowInTaskbar = false; MinimizeBox = false; MaximizeBox = false; TopMost = false;
        DialogWindowSizing.CenterOnOwnerScreen(this);
        var summary = new Label { Dock = DockStyle.Top, Height = 70, Padding = new Padding(14), Text =
            "本次拟提交 " + planned + " 个文件；" + reused + " 个未变化文件沿用已有版本。\r\n仅处理当前设计树及关联工程图；嵌入组件随父装配保存。最终核对无变化的文件不会生成新版本。" };
        var grid = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false,
            AllowUserToDeleteRows = false, RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            BackgroundColor = Color.White, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false };
        grid.Columns.Add("file", "图档"); grid.Columns.Add("configuration", "配置"); grid.Columns.Add("action", "本次处理"); grid.Columns.Add("reason", "原因");
        grid.Columns[0].FillWeight = 120; grid.Columns[1].FillWeight = 50; grid.Columns[2].FillWeight = 65; grid.Columns[3].FillWeight = 160;
        foreach (var row in rows) grid.Rows.Add(row);
        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 60, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(12) };
        var submit = new Button { Text = "确认提交", DialogResult = DialogResult.OK, Width = 100, Height = 32 };
        var cancel = new Button { Text = "取消提交", DialogResult = DialogResult.Cancel, Width = 100, Height = 32 };
        footer.Controls.Add(submit); footer.Controls.Add(cancel);
        footer.Controls.Add(new Label { AutoSize = true, Margin = new Padding(8), Text = "取消提交会保留已保存的本地修改。" });
        Controls.Add(grid); Controls.Add(footer); Controls.Add(summary);
        AcceptButton = submit; CancelButton = cancel;
    }
}
