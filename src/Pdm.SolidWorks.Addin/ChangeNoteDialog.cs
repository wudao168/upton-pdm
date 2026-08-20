using System;
using System.Drawing;
using System.Windows.Forms;

namespace Upton.Pdm.SolidWorks;

internal sealed class ChangeNoteDialog : Form
{
    private readonly TextBox changeNote = new TextBox
    {
        AcceptsReturn = true,
        Multiline = true,
        ScrollBars = ScrollBars.Vertical
    };

    public ChangeNoteDialog(string fileName)
    {
        Text = "提交存档";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(440, 260);
        Font = new Font("Microsoft YaHei UI", 9F);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18),
            ColumnCount = 1,
            RowCount = 4
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.Controls.Add(new Label
        {
            Text = string.Concat("图档：", fileName),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 10)
        }, 0, 0);
        layout.Controls.Add(new Label
        {
            Text = "请输入本次变更内容（必填），该内容将随工作版本保存，便于后期追溯。",
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 8)
        }, 0, 1);
        changeNote.Dock = DockStyle.Fill;
        layout.Controls.Add(changeNote, 0, 2);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, 8, 0, 0)
        };
        var submit = new Button
        {
            Text = "确认提交",
            DialogResult = DialogResult.OK,
            Width = 92,
            Height = 30,
            BackColor = Color.FromArgb(21, 126, 77),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        submit.Enabled = false;
        submit.FlatAppearance.BorderSize = 0;
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Width = 82, Height = 30 };
        buttons.Controls.Add(submit);
        buttons.Controls.Add(cancel);
        layout.Controls.Add(buttons, 0, 3);
        Controls.Add(layout);

        AcceptButton = submit;
        CancelButton = cancel;
        changeNote.TextChanged += (_, _) => submit.Enabled = !string.IsNullOrWhiteSpace(changeNote.Text);
        Shown += (_, _) => changeNote.Focus();
    }

    public string ChangeNote => changeNote.Text.Trim();
}
