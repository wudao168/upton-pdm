using System;
using System.IO;
using System.Windows.Forms;

internal static partial class Program
{
    static void ArchiveUiReentrancyScenario(string output)
    {
        Directory.CreateDirectory(output);
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); Console.WriteLine("PASS: " + message); }
        using (var host = new Form { Width = 470, Height = 850, ShowInTaskbar = false, Opacity = 0 })
        using (var pane = (Control)New("PdmTaskPaneControl", "界面刷新回归"))
        {
            pane.Dock = DockStyle.Fill; host.Controls.Add(pane); host.Show();
            Application.DoEvents();
            var callbacks = 0;
            host.BeginInvoke((Action)(() => callbacks++));
            TreeCall(pane, "RefreshWorkspaceOperationUi");
            Check(callbacks == 0, "刷新只处理本控件绘制，不在操作中重入排队的界面回调");
            Application.DoEvents();
            Check(callbacks == 1, "排队回调由正常消息循环处理，界面保持响应");
            host.BeginInvoke((Action)(() => callbacks++));
            TreeCall(pane, "SetWorkspaceOperationState", true, "引用读取已完成，正在核对提交范围…");
            Check(callbacks == 1, "启动工作状态不会重入其他存档或无障碍回调");
            for (var index = 0; index < 100; index++)
                TreeCall(pane, "SetWorkspaceOperationProgress", "检查进度 " + index, index, 100);
            Check(callbacks == 1, "连续100次进度刷新不启动嵌套消息循环");
            Application.DoEvents();
            Check(callbacks == 2, "批量进度后待处理的正常回调不丢失");
            TreeCall(pane, "SetWorkspaceOperationState", false, "操作已结束");
            Check(!pane.UseWaitCursor, "操作结束后正常恢复光标和交互状态");
            host.Close();
        }
    }
}
