using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Upton.Pdm.SolidWorks;

// Called only for private archive copies. Never recreate a design table: that
// deletes configuration-publisher features in existing standard parts.
internal static class ArchiveDesignTableLinks
{
    internal static string PrepareAndCapture(ISldWorks app, IModelDoc2 model, string stage)
    {
        var objects = (model.Extension.GetOLEObjects(0) as Array)?.OfType<ISwOLEObject>().ToArray() ?? new ISwOLEObject[0];
        if (objects.All(ArchiveEmbeddedPackageInspection.IsSelfContained)) return null;
        if (!(model.GetDesignTable() is IDesignTable table) || objects.Length != 1)
            throw new IOException("嵌入内容不是可安全原位处理的单一设计表，已停止存档。");
        var original = ArchiveEmbeddedPackageInspection.ReadWorkbook(objects[0]);
        if (original == null) throw new IOException("内嵌设计表格式尚不能安全处理，已停止存档。");
        var clean = ArchiveEmbeddedPackageInspection.RemoveWorkbookLinks(original);
        ArchiveEmbeddedPackageInspection.RequireUnavailableLocalWorkbookSources(original);
        var path = Path.Combine(stage, "design-table-" + Guid.NewGuid().ToString("N") + ".xlsx");
        File.WriteAllBytes(path, clean);
        var names = (model.GetConfigurationNames() as Array)?.Cast<object>().Select(Convert.ToString).ToArray();
        var before = ArchiveSmartComponentLinks.ConfigurationParameters(model);
        var geometry = ArchiveReferenceIsolation.Capture(model);
        var notes = ArchiveSmartComponentLinks.Features(model).Where(feature => feature.GetTypeName2() == "NotesAreaFtrFolder")
            .ToDictionary(feature => feature.Name, feature => (feature.IsSuppressed2((int)swInConfigurationOpts_e.swSpecifyConfiguration, names) as Array)
                ?.Cast<object>().Select(Convert.ToBoolean).ToArray());
        if (notes.Any(pair => pair.Value == null || pair.Value.Length != names.Length)) throw new IOException("设计表处理前注释状态未完整读取。");
        var configuration = model.ConfigurationManager.ActiveConfiguration.Name;
        var previous = app.ActiveDoc as IModelDoc2; var visible = model.Visible;
        var existing = Process.GetProcessesByName("EXCEL").Select(process => process.Id).ToArray();
        dynamic excel = null; Process processOwner = null; long started = 0;
        ArchiveOwnedProcess lifetime = null;
        try
        {
            var excelType = Type.GetTypeFromProgID("Excel.Application");
            if (excelType == null) throw new IOException("此图档需要本机 Excel 完成设计表原位断链，当前未安装兼容的 Excel，已停止存档。");
            excel = Activator.CreateInstance(excelType);
            GetWindowThreadProcessId(new IntPtr((int)excel.Hwnd), out var pid);
            if (existing.Contains((int)pid)) throw new IOException("设计表接口返回了正在使用的 Excel，未接管或修改该会话。");
            processOwner = Process.GetProcessById((int)pid); started = processOwner.StartTime.ToUniversalTime().Ticks;
            lifetime = new ArchiveOwnedProcess(processOwner);
            excel.Visible = false; excel.DisplayAlerts = false; excel.EnableEvents = false; excel.AutomationSecurity = 3;
            excel.AskToUpdateLinks = false;
            dynamic blank = excel.Workbooks.Add(); excel.Calculation = -4135; blank.Close(false); Marshal.ReleaseComObject((object)blank);
            dynamic expected = excel.Workbooks.Open(path, 0, true);
            var expectedCells = WorkbookCells((object)expected);
            model.Visible = true; int activation = 0;
            app.ActivateDoc3(model.GetTitle(), false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref activation);
            if (!string.Equals((app.ActiveDoc as IModelDoc2)?.GetPathName(), model.GetPathName(), StringComparison.OrdinalIgnoreCase))
                throw new IOException("未能激活正确的隔离图档，未修改设计表。");
            dynamic sheet = table.EditTable2(true);
            if ((object)sheet == null) throw new IOException("无法打开隔离副本的原生设计表。");
            dynamic actual = sheet.Application;
            GetWindowThreadProcessId(new IntPtr((int)actual.Hwnd), out var actualPid);
            if (actualPid != pid) throw new IOException("原生设计表未连接本次专用 Excel，未修改其他会话的内容。");
            dynamic workbook = sheet.Parent;
            var links = workbook.LinkSources(1) as Array;
            foreach (var link in links?.Cast<object>() ?? Enumerable.Empty<object>()) workbook.BreakLink(Convert.ToString(link), 1);
            foreach (dynamic page in workbook.Worksheets) page.UsedRange.ClearHyperlinks();
            RestorePreservedValues((object)expected, (object)workbook);
            var actualCells = WorkbookCells((object)workbook);
            if (expectedCells != actualCells)
            {
                if (System.Environment.GetEnvironmentVariable("PDM_ADDIN_VERBOSE_SCAN") == "1")
                {
                    File.WriteAllText(Path.Combine(stage, "design-table-expected.txt"), expectedCells);
                    File.WriteAllText(Path.Combine(stage, "design-table-actual.txt"), actualCells);
                }
                throw new IOException("设计表断链后的单元格数值或内部公式不一致，已停止存档。");
            }
            expected.Close(false); Marshal.ReleaseComObject((object)expected);
            Marshal.ReleaseComObject((object)workbook); Marshal.ReleaseComObject((object)sheet);
            if (!table.UpdateTable((int)swDesignTableUpdateOptions_e.swUpdateDesignTableNone, true))
                throw new IOException("设计表原位断链结果未保存，已停止存档。");
            // Restoring a folder also updates its closing marker. Restore the
            // saved end-tag state afterwards, regardless of inventory ordering.
            foreach (var pair in notes.OrderBy(pair => pair.Key.IndexOf("___EndTag___", StringComparison.Ordinal) >= 0 ? 1 : 0))
            {
                var feature = ArchiveSmartComponentLinks.Features(model).Single(candidate => candidate.Name == pair.Key);
                foreach (var suppressed in new[] { false, true })
                {
                    var selected = names.Where((name, index) => pair.Value[index] == suppressed).ToArray();
                    if (selected.Length > 0 && !feature.SetSuppression2(suppressed ? 0 : 1, (int)swInConfigurationOpts_e.swSpecifyConfiguration, selected))
                        throw new IOException("设计表处理后未恢复原注释状态，已停止存档。");
                }
            }
            if (model.ConfigurationManager.ActiveConfiguration.Name != configuration && !model.ShowConfiguration2(configuration))
                throw new IOException("设计表处理后未恢复原配置，已停止存档。");
            var after = ArchiveSmartComponentLinks.ConfigurationParameters(model);
            // Annotation-view suppression temporarily inherits its restored folder.
            // The caller checks the complete, unmodified baseline again after the
            // saved package is closed and reopened, before accepting any output.
            string Stable(string text) => string.Join("\n", text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                .Select(line => line.Contains("|AnnotationViewFeat|")
                    ? line.Substring(0, line.IndexOf("|AnnotationViewFeat|", StringComparison.Ordinal) + "|AnnotationViewFeat".Length) : line));
            if (Stable(before) != Stable(after))
            {
                if (System.Environment.GetEnvironmentVariable("PDM_ADDIN_VERBOSE_SCAN") == "1")
                {
                    File.WriteAllText(Path.Combine(stage, "design-parameters-before.txt"), before);
                    File.WriteAllText(Path.Combine(stage, "design-parameters-after.txt"), after);
                }
                throw new IOException("设计表处理后配置尺寸、特征或抑制状态不一致，已停止存档。");
            }
            geometry.Verify(ArchiveReferenceIsolation.Capture(model), configuration);
            ArchiveResourceIsolation.ValidateEmbeddedObjects(model);
            PluginDiagnosticLog.Record("设计表断链", Path.GetFileName(model.GetPathName()) + "：已保存的外部结果和全部内部公式保持；全部配置参数、发布特征及原注释状态核对一致。");
            return before;
        }
        catch (Exception error) when (!(error is IOException))
        {
            PluginDiagnosticLog.Record("设计表原位处理", error.ToString());
            throw new IOException("原生设计表接口未能完成断链，已停止存档；具体图档和技术原因已记录在诊断日志中。", error);
        }
        finally
        {
            if (processOwner != null)
            {
                try { excel.Quit(); } catch (COMException error) { PluginDiagnosticLog.Record("设计表清理", error.ToString()); }
                try { processOwner.Refresh(); if (!processOwner.HasExited && processOwner.StartTime.ToUniversalTime().Ticks == started) processOwner.Kill(); }
                catch (InvalidOperationException error) { PluginDiagnosticLog.Record("设计表清理", error.ToString()); }
            }
            if ((object)excel != null && Marshal.IsComObject((object)excel)) Marshal.ReleaseComObject((object)excel);
            lifetime?.Dispose();
            model.Visible = visible;
            if (previous != null && !string.Equals((app.ActiveDoc as IModelDoc2)?.GetPathName(), previous.GetPathName(), StringComparison.OrdinalIgnoreCase))
            { int error = 0; app.ActivateDoc3(previous.GetTitle(), false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref error); }
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static void RestorePreservedValues(object source, object target)
    {
        dynamic original = source; dynamic workbook = target;
        if ((int)original.Worksheets.Count != (int)workbook.Worksheets.Count) throw new IOException("设计表图页数量发生变化，已停止存档。");
        object Cell(object values, int row, int column) => values is Array array
            ? array.GetValue(row + array.GetLowerBound(0), column + array.GetLowerBound(1)) : values;
        for (var index = 1; index <= (int)original.Worksheets.Count; index++)
        {
            dynamic sourceSheet = original.Worksheets[index]; dynamic targetSheet = workbook.Worksheets[index];
            dynamic expected = sourceSheet.UsedRange; dynamic actual = targetSheet.UsedRange;
            try
            {
                var rows = (int)expected.Rows.Count; var columns = (int)expected.Columns.Count;
                if ((string)sourceSheet.Name != (string)targetSheet.Name || rows != (int)actual.Rows.Count || columns != (int)actual.Columns.Count
                    || (int)expected.Row != (int)actual.Row || (int)expected.Column != (int)actual.Column)
                    throw new IOException("设计表单元格范围发生变化，已停止存档。");
                object values = expected.Value2; object formulas = expected.Formula; object received = actual.Value2;
                for (var row = 0; row < rows; row++)
                    for (var column = 0; column < columns; column++)
                    {
                        if (Cell(formulas, row, column) is string formula && formula.StartsWith("=", StringComparison.Ordinal)) continue;
                        var value = Cell(values, row, column);
                        if (Equals(value, Cell(received, row, column))) continue;
                        dynamic cell = actual.Cells[row + 1, column + 1];
                        try
                        {
                            object format = cell.NumberFormat;
                            if (value is string) cell.NumberFormat = "@";
                            cell.Value2 = value;
                            cell.NumberFormat = format;
                        }
                        finally { Marshal.ReleaseComObject((object)cell); }
                    }
            }
            finally
            {
                Marshal.ReleaseComObject((object)expected); Marshal.ReleaseComObject((object)actual);
                Marshal.ReleaseComObject((object)sourceSheet); Marshal.ReleaseComObject((object)targetSheet);
            }
        }
    }

    private static string WorkbookCells(object value)
    {
        dynamic workbook = value; var text = new StringBuilder();
        for (var index = 1; index <= (int)workbook.Worksheets.Count; index++)
        {
            dynamic sheet = workbook.Worksheets[index]; dynamic range = sheet.UsedRange;
            try
            {
                if ((long)range.Rows.Count * (long)range.Columns.Count > 2000000) throw new IOException("内嵌设计表范围过大，未开始改写图档。");
                text.Append((string)sheet.Name).Append('|').Append((int)range.Row).Append('|').Append((int)range.Column)
                    .Append('|').Append((int)range.Rows.Count).Append('|').Append((int)range.Columns.Count);
                foreach (var values in new object[] { range.Value2, range.Formula })
                    foreach (var item in values is Array array ? array.Cast<object>() : new[] { values })
                    {
                        var content = item is double number ? number.ToString("R", CultureInfo.InvariantCulture) : Convert.ToString(item, CultureInfo.InvariantCulture) ?? "";
                        text.Append('|').Append(item?.GetType().FullName).Append(':').Append(content.Length).Append(':').Append(content);
                    }
            }
            finally { Marshal.ReleaseComObject((object)range); Marshal.ReleaseComObject((object)sheet); }
        }
        return text.ToString();
    }

    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
