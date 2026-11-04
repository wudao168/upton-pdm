using System;
using System.Drawing;
using System.Windows.Forms;

namespace Upton.Pdm.SolidWorks;

internal sealed class WorkspaceCacheConflictControl : UserControl
{
    private readonly Button restore = Button("恢复修改继续编辑", Color.FromArgb(224, 228, 233), Color.Black);
    private readonly Button latest = Button("保留备份并打开PLM最新版", Color.FromArgb(21, 126, 77), Color.White);
    private readonly Button cancel = Button("取消打开", Color.FromArgb(224, 228, 233), Color.Black);
    private readonly DataGridView files = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true,
        AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BackgroundColor = Color.White,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false };

    internal WorkspaceCacheConflictControl(WorkspaceCacheConflictException conflict, bool canRestore,
        bool canBackup, string restoreReason, Action<WorkspaceConflictAction> selected)
    {
        Dock = DockStyle.Fill;
        BackColor = Color.FromArgb(244, 247, 251);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(3) };
        foreach (var height in new[] { 32, 76 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.Controls.Add(new Label { Text = "工作区图档需要核对", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        layout.Controls.Add(new Label { Text = "以下文件与本次PLM版本有差异。文件和编辑记录已保留，请在此选择处理方式，无需到本地文件夹找图。",
            Dock = DockStyle.Fill }, 0, 1);
        files.Columns.Add("File", "图档"); files.Columns.Add("Local", "本地基准"); files.Columns.Add("Server", "PLM版本");
        files.Columns[0].FillWeight = 180;
        foreach (var file in conflict.Files)
        {
            var row = files.Rows[files.Rows.Add(file.FileName, file.LocalRevision, file.RequiredRevision)];
            foreach (DataGridViewCell cell in row.Cells) cell.ToolTipText = file.Reason + "\r\n" + file.Path;
        }
        layout.Controls.Add(files, 0, 2);
        layout.Controls.Add(new Label { Dock = DockStyle.Fill, Text = !canBackup
            ? "相关图档仍在打开，请先保留当前工作并关闭相关图档后重试。本次不会自动关闭窗口。"
            : canRestore ? "恢复编辑会保留本机修改并核对编辑权限；打开最新版会先自动备份上述文件及恢复记录。"
            : restoreReason + "\r\n可先自动保留备份，再打开PLM已存档版本。" }, 0, 3);
        restore.Enabled = canRestore;
        latest.Enabled = canBackup;
        if (!latest.Enabled) { latest.BackColor = SystemColors.Control; latest.ForeColor = SystemColors.GrayText; }
        if (!restore.Enabled) restore.ForeColor = SystemColors.GrayText;
        restore.Click += (_, _) => selected(WorkspaceConflictAction.RestoreEditing);
        latest.Click += (_, _) => selected(WorkspaceConflictAction.BackupAndGetLatest);
        cancel.Click += (_, _) => selected(WorkspaceConflictAction.Cancel);
        layout.Controls.Add(latest, 0, 4);
        var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 64)); bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 36));
        bottom.Controls.Add(restore, 0, 0); bottom.Controls.Add(cancel, 1, 0); layout.Controls.Add(bottom, 0, 5);
        Controls.Add(layout);
    }

    private static Button Button(string text, Color background, Color foreground) => new Button
    { Text = text, Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat, BackColor = background,
        ForeColor = foreground, UseVisualStyleBackColor = false, Margin = new Padding(1, 2, 1, 2) };
}
