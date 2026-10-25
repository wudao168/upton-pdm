using System;
using System.Windows.Forms;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    private WorkHoursDialog workHoursDialog;
    private string workHoursTheme = "a";

    private void SyncWorkHoursTheme(string theme)
    {
        workHoursTheme = theme;
        workHoursDialog?.ApplyTheme(theme);
    }

    private void OnWorkHoursRequested(object sender, EventArgs args)
    {
        if (apiClient?.IsAuthenticated != true)
        {
            ShowError("请先登录PLM，再填写工时。");
            return;
        }
        if (workHoursDialog != null && !workHoursDialog.IsDisposed) { workHoursDialog.Activate(); return; }
        try
        {
            using (var dialog = new WorkHoursDialog(apiClient, workHoursTheme, error => LogDiagnostic("工时窗口", error)))
            {
                workHoursDialog = dialog;
                if (dialog.ShowDialog(taskPaneControl) == DialogResult.OK)
                    MessageBox.Show(taskPaneControl, "工时已保存并计入工时管理。", "填写工时", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        catch (Exception error) { LogDiagnostic("打开工时管理", error); ShowError("无法打开工时窗口，请检查服务器连接后重试。"); }
        finally { workHoursDialog = null; }
    }
}
