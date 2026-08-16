using System;
using System.Drawing;
using System.Windows.Forms;

namespace Upton.Pdm.SolidWorks;

internal sealed class DuplicateRegistrationReasonDialog : Form
{
    private readonly TextBox reason = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };

    public DuplicateRegistrationReasonDialog(string localFileName, string existingFileName)
    {
        Text = "确认独立登记重复内容";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(520, 270);
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
            Text = string.Concat("本地图档“", localFileName, "”与已有图档“", existingFileName, "”内容完全相同。"),
            AutoSize = true,
            MaximumSize = new Size(480, 0),
            Margin = new Padding(0, 0, 0, 10)
        }, 0, 0);
        layout.Controls.Add(new Label
        {
            Text = "如仍需建立独立图档，请填写业务原因。该原因会写入审计记录。",
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 8)
        }, 0, 1);
        layout.Controls.Add(reason, 0, 2);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 8, 0, 0) };
        var confirm = new Button { Text = "确认独立登记", DialogResult = DialogResult.OK, Width = 112, Height = 30 };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Width = 82, Height = 30 };
        buttons.Controls.Add(confirm);
        buttons.Controls.Add(cancel);
        layout.Controls.Add(buttons, 0, 3);
        Controls.Add(layout);

        FormClosing += (_, eventArgs) =>
        {
            if (DialogResult != DialogResult.OK) return;
            if (!string.IsNullOrWhiteSpace(reason.Text)) return;
            MessageBox.Show(this, "请填写独立登记原因。", "确认独立登记", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            eventArgs.Cancel = true;
            reason.Focus();
        };
        AcceptButton = confirm;
        CancelButton = cancel;
    }

    public string Reason => reason.Text.Trim();
}
