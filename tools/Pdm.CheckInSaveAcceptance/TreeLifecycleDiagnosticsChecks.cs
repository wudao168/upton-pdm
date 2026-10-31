using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

internal static partial class Program
{
    static void TreeLifecycleDiagnosticsScenario()
    {
        void Check(bool ok,string message){if(!ok)throw new Exception(message);Console.WriteLine("PASS: "+message);}
        var log=Type("PluginDiagnosticLog");
        string Snapshot()=>(string)log.GetMethod("Snapshot",Flags).Invoke(null,null);
        int Revision()=>(int)log.GetProperty("Revision",Flags).GetValue(null);
        TreeView Create(string name)=>(TreeView)Activator.CreateInstance(Type(name),true);
        var tree=Create("PdmTaskPaneControl+StructureTreeView");
        Check(!tree.IsHandleCreated,"诊断绑定不会提前创建树窗口");
        var first=tree.Handle;
        var thread=GetWindowThreadProcessId(first,out var process);
        var row=Snapshot().Split('\n').Last(line=>line.Contains("控件=插件设计树")&&line.Contains("事件=创建完成"));
        Check(row.Contains("句柄=0x"+first.ToInt64().ToString("x"))&&row.Contains("；进程="+Process.GetCurrentProcess().Id+"；"),"创建记录包含可与崩溃转储比对的原始句柄和进程号");
        Check(row.Contains("；所属原生线程="+thread+"；当前原生线程="+thread+"；"),"记录所属及当前原生线程，便于核对跨线程调用");
        var revision=Revision();
        for(var i=0;i<1000;i++)tree.Nodes.Add("测试节点"+i);
        Check(Revision()==revision,"插入一千个节点不增加逐节点诊断记录");
        typeof(Control).GetMethod("RecreateHandle",Flags).Invoke(tree,null);
        var created=Snapshot().Split('\n').Where(line=>line.Contains("控件=插件设计树")&&line.Contains("事件=创建完成")).ToArray();
        string Identity(string line)=>line.Split('；').Single(value=>value.StartsWith("实例="));
        Check(created.Length==2&&Identity(created[0])==Identity(created[1])&&created[1].Contains("；代次=2；"),"同一控件重建保留实例身份并递增句柄代次");
        var last=tree.Handle;tree.Dispose();
        var destroyed=Snapshot().Split('\n').Last(line=>line.Contains("控件=插件设计树")&&line.Contains("事件=句柄销毁"));
        Check(!tree.IsHandleCreated&&destroyed.Contains("句柄=0x"+last.ToInt64().ToString("x")),"销毁时使用缓存句柄，日志不会重新创建窗口");
        Check(Snapshot().Contains("事件=控件释放"),"控件释放事件进入现有诊断记录");
        Exception failure=null;string selectionRow=null;
        var secondThread=new Thread(()=>
        {
            try{using(var selection=Create("BatchOperationDialog+FileSelectionTreeView")){var handle=selection.Handle;selectionRow=Snapshot().Split('\n').Last(line=>line.Contains("控件=存档选择树")&&line.Contains("事件=创建完成"));}}
            catch(Exception error){failure=error;}
        });
        secondThread.SetApartmentState(ApartmentState.STA);secondThread.Start();
        Check(secondThread.Join(10000)&&failure==null&&selectionRow!=null&&!selectionRow.Contains("；所属原生线程="+thread+"；"),"独立 STA 上的存档树可与主线程设计树明确区分");
        Check(Identity(created[0])!=Identity(selectionRow),"不同树实例不会混用身份标识");
    }
}
