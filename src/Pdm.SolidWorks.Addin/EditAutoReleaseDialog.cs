using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Upton.Pdm.SolidWorks;

internal sealed class EditAutoReleaseDialog : Form
{
    private readonly Timer timer = new Timer { Interval = 1000 };
    private readonly Label countdown = new Label { AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(0, 10, 0, 10) };
    private readonly Func<bool> canRelease;
    private readonly Func<Task> release;
    private readonly Action postpone;
    private int remaining;
    private bool completing;

    public EditAutoReleaseDialog(int count, int seconds, Func<bool> canRelease, Func<Task> release, Action postpone)
    {
        this.canRelease = canRelease; this.release = release; this.postpone = postpone; remaining = seconds;
        Text = "闲置图档编辑权限"; Font = new Font("Microsoft YaHei UI", 9F);
        ClientSize = new Size(510, 180); FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        DialogWindowSizing.CenterOnOwnerScreen(this);
        var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(18) };
        panel.Controls.Add(new Label { Text = count + " 个图档长时间未编辑且没有未提交修改。\r\n释放后保留本地文件，可随时重新获取编辑权限。", Dock = DockStyle.Top, Height = 50 });
        panel.Controls.Add(countdown); countdown.BringToFront();
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 38, FlowDirection = FlowDirection.RightToLeft };
        var keep = new Button { Text = "继续编辑", AutoSize = true };
        keep.Click += (_, _) => Close();
        buttons.Controls.Add(keep); panel.Controls.Add(buttons); Controls.Add(panel);
        AcceptButton = keep; CancelButton = keep;
        Shown += (_, _) => { UpdateText(); timer.Start(); };
        timer.Tick += async (_, _) =>
        {
            if (completing) return;
            try
            {
                if (!canRelease()) { Close(); return; }
                remaining--; UpdateText();
                if (remaining > 0) return;
                timer.Stop(); completing = true; keep.Enabled = false;
                countdown.Text = "正在核对并释放编辑权限…";
                await release();
                Close();
            }
            catch { completing = false; Close(); }
        };
        FormClosed += (_, _) => { timer.Dispose(); if (!completing) postpone(); };
    }

    private void UpdateText() => countdown.Text = remaining + " 秒后自动释放；点击“继续编辑”可延期。";
}
