using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace Upton.Pdm.SolidWorks;

internal sealed partial class BatchOperationDialog
{
    private static string FileColumnSettingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UPLM", "solidworks-archive-selection-columns.json");
    private int fileNameWidth;
    private int fileVersionWidth;
    private int draggingFileDivider;
    private int FileHeaderClientOffset => filesHeader.PointToClient(files.PointToScreen(Point.Empty)).X;

    private int ScaleFileColumn(int width) => (int)Math.Round(width * DeviceDpi / 96d);

    private void GetFileDetailColumns(int width, out int nameX, out int versionX)
    {
        var usable = Math.Max(1, width);
        var minimumName = FileSelectionColumnWidth + ScaleFileColumn(64);
        var minimumVersion = ScaleFileColumn(60);
        var minimumStatus = ScaleFileColumn(80);
        if (fileNameWidth > 0 && fileVersionWidth > 0)
        {
            nameX = Math.Max(minimumName, Math.Min(usable - minimumVersion - minimumStatus,
                FileSelectionColumnWidth + ScaleFileColumn(fileNameWidth)));
            versionX = Math.Max(nameX + minimumVersion, Math.Min(usable - minimumStatus, nameX + ScaleFileColumn(fileVersionWidth)));
        }
        else
        {
            var versionWidth = Math.Max(116, Math.Min(172, usable * 31 / 100));
            var statusWidth = Math.Max(210, Math.Min(260, usable * 21 / 100));
            nameX = Math.Max(minimumName, usable - versionWidth - statusWidth);
            versionX = Math.Max(nameX + minimumVersion, Math.Min(usable - minimumStatus, usable - statusWidth));
        }
    }

    private bool IsInFileNameColumn(int x)
    {
        GetFileDetailColumns(files.ClientSize.Width, out var nameX, out _);
        return x >= FileSelectionColumnWidth && x < nameX;
    }

    private int FileHeaderDividerAt(int x)
    {
        x -= FileHeaderClientOffset;
        GetFileDetailColumns(files.ClientSize.Width, out var nameX, out var versionX);
        if (Math.Abs(x - nameX) <= 4) return 1;
        return Math.Abs(x - versionX) <= 4 ? 2 : 0;
    }

    private void InitializeFileColumnResizing()
    {
        filesHeader.MouseDown += (_, args) =>
        {
            if (args.Button != MouseButtons.Left) return;
            draggingFileDivider = FileHeaderDividerAt(args.X);
            if (draggingFileDivider == 0) return;
            filesHeader.Cursor = Cursors.VSplit;
            filesHeader.Capture = true;
        };
        filesHeader.MouseMove += (_, args) =>
        {
            if (draggingFileDivider != 0) UpdateFileColumnWidths(args.X);
            filesHeader.Cursor = draggingFileDivider != 0 || FileHeaderDividerAt(args.X) != 0 ? Cursors.VSplit : Cursors.Default;
        };
        filesHeader.MouseUp += (_, args) =>
        {
            if (args.Button != MouseButtons.Left || draggingFileDivider == 0) return;
            UpdateFileColumnWidths(args.X);
            EndFileColumnResize();
            filesHeader.Capture = false;
        };
        filesHeader.MouseCaptureChanged += (_, __) => { if (!filesHeader.Capture) EndFileColumnResize(); };
    }

    private void UpdateFileColumnWidths(int x)
    {
        x -= FileHeaderClientOffset;
        GetFileDetailColumns(files.ClientSize.Width, out var nameX, out var versionX);
        if (draggingFileDivider == 1)
            nameX = Math.Max(FileSelectionColumnWidth + ScaleFileColumn(64), Math.Min(versionX - ScaleFileColumn(60), x));
        else
            versionX = Math.Max(nameX + ScaleFileColumn(60), Math.Min(files.ClientSize.Width - ScaleFileColumn(80), x));
        fileNameWidth = (int)Math.Round((nameX - FileSelectionColumnWidth) * 96d / DeviceDpi);
        fileVersionWidth = (int)Math.Round((versionX - nameX) * 96d / DeviceDpi);
        filesHeader.Invalidate(); files.Invalidate();
    }

    private void EndFileColumnResize()
    {
        if (draggingFileDivider == 0) return;
        draggingFileDivider = 0;
        filesHeader.Cursor = Cursors.Default;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FileColumnSettingsPath));
            File.WriteAllText(FileColumnSettingsPath, new JavaScriptSerializer().Serialize(new { name = fileNameWidth, version = fileVersionWidth }), new UTF8Encoding(false));
        }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
        {
            PluginDiagnosticLog.Record("保存存档图档列宽", error.ToString());
            SetSelectionHint("列宽已调整，但未能保存；请检查本机设置目录的写入权限和磁盘空间。");
        }
    }

    private void LoadFileColumnWidths()
    {
        try
        {
            if (!File.Exists(FileColumnSettingsPath)) return;
            var widths = new JavaScriptSerializer().Deserialize<Dictionary<string, int>>(File.ReadAllText(FileColumnSettingsPath, Encoding.UTF8));
            if (widths != null && widths.TryGetValue("name", out var name) && widths.TryGetValue("version", out var version)
                && name >= 64 && name <= 4000 && version >= 60 && version <= 4000)
            {
                fileNameWidth = name; fileVersionWidth = version;
            }
        }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is ArgumentException || error is InvalidOperationException)
        {
            PluginDiagnosticLog.Record("读取存档图档列宽", error.ToString());
        }
    }

    private sealed class FileSelectionTreeView : TreeView
    {
        public FileSelectionTreeView() => TreeControlDiagnostics.Attach(this, "存档选择树");

        protected override CreateParams CreateParams
        {
            get { var parameters = base.CreateParams; parameters.Style |= 0x8000; return parameters; }
        }
    }
}
