using System;
using System.Drawing;
using System.Windows.Forms;

namespace Upton.Pdm.SolidWorks;

internal sealed class LifecycleActionDialog : Form
{
    private readonly TextBox comment = new TextBox { AcceptsReturn = true, Multiline = true, ScrollBars = ScrollBars.Vertical };
    private readonly Button confirm = new Button();

    public LifecycleActionDialog(string title, string message, string confirmText)
    {
        Text = title;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(440, 250);
        Font = new Font("Microsoft YaHei UI", 9F);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 3 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.Controls.Add(new Label { Text = message, AutoSize = true, MaximumSize = new Size(400, 0), Margin = new Padding(0, 0, 0, 10) }, 0, 0);
        comment.Dock = DockStyle.Fill;
        comment.TextChanged += (_, _) => confirm.Enabled = !string.IsNullOrWhiteSpace(comment.Text);
        layout.Controls.Add(comment, 0, 1);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 8, 0, 0) };
        confirm.Text = confirmText;
        confirm.DialogResult = DialogResult.OK;
        confirm.Width = 96;
        confirm.Height = 30;
        confirm.Enabled = false;
        confirm.BackColor = Color.FromArgb(47, 109, 224);
        confirm.ForeColor = Color.White;
        confirm.FlatStyle = FlatStyle.Flat;
        confirm.FlatAppearance.BorderSize = 0;
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Width = 82, Height = 30 };
        buttons.Controls.Add(confirm);
        buttons.Controls.Add(cancel);
        layout.Controls.Add(buttons, 0, 2);
        Controls.Add(layout);
        AcceptButton = confirm;
        CancelButton = cancel;
        Shown += (_, _) => comment.Focus();
    }

    public string Comment => comment.Text.Trim();
}
