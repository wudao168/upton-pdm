using System;
using System.Drawing;
using System.Windows.Forms;

namespace Upton.Pdm.SolidWorks;

internal sealed class RenameDocumentDialog : Form
{
    private readonly TextBox nameBox = new TextBox();

    public RenameDocumentDialog(string currentName, string extension)
    {
        Text = "重命名图档";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(500, 176);
        Font = new Font("Microsoft YaHei UI", 9F);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16, 14, 16, 12),
            ColumnCount = 2,
            RowCount = 4
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));

        layout.Controls.Add(new Label
        {
            Text = "当前文件名",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);
        layout.Controls.Add(new Label
        {
            Text = string.Concat(currentName, extension),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        }, 1, 0);

        layout.Controls.Add(new Label
        {
            Text = "新文件名",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 1);
        nameBox.Text = currentName ?? string.Empty;
        nameBox.Dock = DockStyle.Fill;
        nameBox.Margin = new Padding(0, 5, 0, 5);
        nameBox.AccessibleName = "新文件名（不含扩展名）";
        layout.Controls.Add(nameBox, 1, 1);

        var hint = new Label
        {
            Text = string.Concat("扩展名 ", extension, " 保持不变。重命名后请保存当前装配体和图档，再提交存档。"),
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(92, 110, 132),
            TextAlign = ContentAlignment.MiddleLeft
        };
        layout.Controls.Add(hint, 0, 2);
        layout.SetColumnSpan(hint, 2);

        var confirm = new Button
        {
            Text = "确认重命名",
            DialogResult = DialogResult.OK,
            Size = new Size(100, 30),
            BackColor = Color.FromArgb(21, 126, 77),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        confirm.FlatAppearance.BorderSize = 0;
        var cancel = new Button
        {
            Text = "取消",
            DialogResult = DialogResult.Cancel,
            Size = new Size(100, 30),
            BackColor = Color.FromArgb(230, 126, 34),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        cancel.FlatAppearance.BorderSize = 0;
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = Padding.Empty
        };
        actions.Controls.Add(cancel);
        actions.Controls.Add(confirm);
        layout.Controls.Add(actions, 0, 3);
        layout.SetColumnSpan(actions, 2);

        Controls.Add(layout);
        AcceptButton = confirm;
        CancelButton = cancel;
        Shown += (_, _) =>
        {
            nameBox.Focus();
            nameBox.SelectAll();
        };
    }

    public string NewName => (nameBox.Text ?? string.Empty).Trim();
}
