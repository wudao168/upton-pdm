using System;
using System.Drawing;
using System.Windows.Forms;

namespace Upton.Pdm.SolidWorks;

internal sealed class ProjectAdmissionConfirmationDialog : Form
{
    private readonly string expectedProjectCode;
    private readonly TextBox projectCode = new TextBox { Dock = DockStyle.Fill };
    private readonly CheckBox confirmed = new CheckBox { Text = "已确认归属项目", AutoSize = true };

    public ProjectAdmissionConfirmationDialog(string expectedProjectCode, string projectDisplay, int documentCount)
    {
        this.expectedProjectCode = expectedProjectCode ?? string.Empty;
        Text = "新增图档归属确认";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(500, 230);
        Font = new Font("Microsoft YaHei UI", 9F);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 5 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label
        {
            Text = string.Concat("本次将新增登记", documentCount, "个图档到：", projectDisplay),
            AutoSize = true,
            MaximumSize = new Size(460, 0),
            Margin = new Padding(0, 0, 0, 12)
        }, 0, 0);
        layout.Controls.Add(new Label
        {
            Text = string.Concat("请再次输入项目号：", this.expectedProjectCode),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 6)
        }, 0, 1);
        layout.Controls.Add(projectCode, 0, 2);
        confirmed.Margin = new Padding(0, 10, 0, 6);
        layout.Controls.Add(confirmed, 0, 3);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 42, Padding = new Padding(0, 8, 0, 0) };
        var execute = new Button { Text = "确认", DialogResult = DialogResult.OK, Width = 86, Height = 30 };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Width = 82, Height = 30 };
        buttons.Controls.Add(execute);
        buttons.Controls.Add(cancel);
        layout.Controls.Add(buttons, 0, 4);
        Controls.Add(layout);

        FormClosing += ValidateBeforeClose;
        AcceptButton = execute;
        CancelButton = cancel;
    }

    private void ValidateBeforeClose(object sender, FormClosingEventArgs eventArgs)
    {
        if (DialogResult != DialogResult.OK) return;
        if (!string.Equals(projectCode.Text.Trim(), expectedProjectCode, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, string.Concat("请输入项目号“", expectedProjectCode, "”。"), "新增图档归属确认", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            eventArgs.Cancel = true;
            projectCode.Focus();
            return;
        }
        if (!confirmed.Checked)
        {
            MessageBox.Show(this, "请勾选“已确认归属项目”。", "新增图档归属确认", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            eventArgs.Cancel = true;
            confirmed.Focus();
        }
    }
}
