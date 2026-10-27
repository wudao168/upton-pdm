using System;
using System.Collections;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static partial class Program
{
    static async Task ArchiveProgressRefreshScenario(Form host, string output)
    {
        Directory.CreateDirectory(output);
        var root = LinkedNode(output, "进度回归.SLDASM", "Assembly");
        var nodes = Enumerable.Range(1, 120).Select(index =>
        {
            var node = LinkedNode(output, "进度样件-" + index + ".SLDPRT", "Part");
            Set(node, "DocumentId", null); ((IList)Get(root, "Children")).Add(node); return node;
        }).Prepend(root).ToArray();
        var session = Type("ArchiveSession").GetMethod("Start", Flags).Invoke(null, new object[] { host.Handle, CancellationToken.None });
        await (Task)Get(session, "Ready");
        void Check(bool condition, string message) { TreeCheck(condition, message); Console.WriteLine("PASS: " + message); }
        try
        {
            var driver = Task.Run(async () =>
            {
                try
                {
                    await TreeWait(() => ArchiveUi(session, () => ((Panel)Field(ArchiveWindow(session), "selection")).Controls.OfType<Form>().Any()), "未显示存档列表");
                    ArchiveUi(session, () =>
                    {
                        var window = ArchiveWindow(session);
                        var form = ((Panel)Field(window, "selection")).Controls.OfType<Form>().Single();
                        window.BeginInvoke((Action)(() => ((Button)Field(form, "operationCancel")).PerformClick())); return true;
                    });
                }
                catch { TreeCall(session, "RequestCancellation"); throw; }
            });
            ArchiveUi(session, () =>
            {
                var form = (Form)New("BatchOperationDialog", root, LinkedItems(nodes), Array.CreateInstance(Type("ProjectDto"), 0), null, "tester",
                    Enum.Parse(Type("BatchOperationKind"), "CheckIn"), null, null, null, false, false, 0);
                return TreeCall(session, "ShowInline", form, true);
            });
            await driver;
            Console.WriteLine("INFO: 存档列表已就绪，开始阶段3/4和4/4的密集进度检查");
            foreach (var phase in new[] { 3, 4 })
            {
                TreeCall(session, "SetPhase", phase, "开始密集进度检查");
                var beforeTicks = ArchiveUi(session, () => (int)Get(ArchiveWindow(session), "RefreshTicks"));
                var beforeElapsed = ArchiveUi(session, () => ((Label)Field(ArchiveWindow(session), "elapsed")).Text);
                var phaseWatch = Stopwatch.StartNew();
                const int updates = 1200;
                var producer = Task.Run(() =>
                {
                    for (var index = 0; index < updates; index++)
                    {
                        var fileName = (string)Get(nodes[1 + index % (nodes.Length - 1)], "FileName");
                        TreeCall(session, "ReportFile", index, updates, fileName, "阶段" + phase + "/4：进度回归记录 " + index);
                        if (index % 16 == 15) Thread.Sleep(20);
                    }
                });
                Thread.Sleep(1200); // CAD caller remains blocked while progress comes from another thread.
                var sample = Task.Run(() => ArchiveUi(session, () => new[]
                {
                    Convert.ToString(Get(ArchiveWindow(session), "RefreshTicks")),
                    ((Label)Field(ArchiveWindow(session), "elapsed")).Text,
                    phaseWatch.ElapsedMilliseconds.ToString()
                }));
                var responsive = await Task.WhenAny(sample, Task.Delay(2500)) == sample;
                await producer;
                Console.WriteLine("INFO: 阶段" + phase + "/4已发送" + updates + "条进度");
                Check(responsive, "阶段" + phase + "/4：密集进度期间存档窗口可及时响应");
                var snapshot = await sample;
                Console.WriteLine("INFO: 阶段" + phase + "，刷新次数=" + beforeTicks + "→" + snapshot[0]
                    + "，计时=" + beforeElapsed + "→" + snapshot[1] + "，采样时刻=" + snapshot[2] + "毫秒");
                Check(int.Parse(snapshot[0]) >= beforeTicks + 2 && snapshot[1] != beforeElapsed,
                    "阶段" + phase + "/4：CAD线程阻塞和密集更新期间，计时持续刷新");
                TreeCall(session, "ReportFile", updates, updates, (string)Get(nodes[1], "FileName"), "阶段" + phase + "/4：本阶段已完成");
                ArchiveUi(session, () =>
                {
                    var window = ArchiveWindow(session); var history = ((TextBox)Field(window, "history")).Text;
                    Check(Enumerable.Range(0, updates).All(index => history.Contains("阶段" + phase + "/4："
                        + (phase == 3 ? "存档前校验" : "上传提交与收尾") + "｜进度回归记录 " + index + "　")),
                        "阶段" + phase + "/4：批量刷新保留每一条过程记录");
                    Check(((Control)Field(window, "progress")).Text.Contains("100%")
                        && ((Label)Field(window, "stage")).Text.Contains("本阶段已完成"), "阶段" + phase + "/4：显示最新实际进度");
                    using (var bitmap = new Bitmap(window.Width, window.Height))
                    {
                        window.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                        bitmap.Save(Path.Combine(output, "phase-" + phase + ".png"));
                    }
                    return true;
                });
            }
            TreeCall(session, "SetStage", "阶段4/4：正在提交整批版本，等待服务器确认…");
            ArchiveUi(session, () => { Check((bool)Get(Field(ArchiveWindow(session), "progress"), "IsIndeterminate"), "等待服务器提交时使用活动进度，不提前宣布存档完成"); return true; });
            TreeCall(session, "Complete", "服务器已确认整批存档。", false, false);
            ArchiveUi(session, () => { Check(((Label)Field(ArchiveWindow(session), "resultState")).Text.Contains("成功") && ((Control)Field(ArchiveWindow(session), "progress")).Text.Contains("100%"), "最终结果保持全部进度记录的顺序并在服务器确认后成功结束"); return true; });
        }
        finally { ArchiveClose(session); await (Task)Get(session, "Closed"); ((IDisposable)session).Dispose(); }
    }
}
