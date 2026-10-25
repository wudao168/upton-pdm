using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Remoting.Messaging;
using System.Runtime.Remoting.Proxies;

internal static class Program
{
    private const BindingFlags Flags=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    private static int Main(string[] args)
    {
        var folder=Path.GetDirectoryName(Path.GetFullPath(args[0]));
        AppDomain.CurrentDomain.AssemblyResolve+=(_,e)=>{
            var file=Path.Combine(folder,new AssemblyName(e.Name).Name+".dll");
            return File.Exists(file)?Assembly.LoadFrom(file):null;
        };
        try
        {
            var worker=Assembly.LoadFrom(Path.GetFullPath(args[0]));
            var sessionType=worker.GetType("Upton.Pdm.SolidWorks.PreviewWorker.Program+ConversionSession",true);
            var appField=sessionType.GetField("application",Flags);
            foreach(var mode in new[]{"unowned","missing-owner","wrong-start","wrong-pid","foreground","visible","com-disconnected","private","takeover","double-reset"})
            {
                var session=Activator.CreateInstance(sessionType,Flags,null,new object[]{true},null);
                var calls=new List<string>();var userControlled=mode=="foreground";var visible=mode=="visible";
                using(var current=Process.GetCurrentProcess())
                {
                    var fake=new Proxy(appField.FieldType,call=>{
                        switch(call.MethodName)
                        {
                            case "GetProcessID":if(mode=="com-disconnected")throw new COMException("disconnected");return mode=="wrong-pid"?current.Id+1:current.Id;
                            case "get_UserControl":return userControlled;
                            case "get_Visible":return visible;
                            case "CloseAllDocuments":calls.Add("CloseAll");return true;
                            case "set_CommandInProgress":calls.Add("Command");return null;
                            case "ExitApp":calls.Add("Exit");return null;
                            default:throw new Exception("Unexpected app mutation: "+call.MethodName);
                        }
                    }).GetTransparentProxy();
                    appField.SetValue(session,fake);
                    var processField=sessionType.GetField("ownedProcess",Flags);
                    if(processField!=null&&mode!="unowned"&&mode!="missing-owner")
                    {
                        processField.SetValue(session,current);
                        sessionType.GetField("ownedStartTicksUtc",Flags).SetValue(session,current.StartTime.ToUniversalTime().Ticks+(mode=="wrong-start"?1:0));
                    }
                    var authorized=mode=="private"||mode=="takeover"||mode=="double-reset";
                    var close=sessionType.GetMethod("CloseSessionDocuments",Flags);
                    if(close!=null)
                    {
                        var rejected=false;
                        try{close.Invoke(session,null);}catch(TargetInvocationException error){rejected=error.InnerException is InvalidOperationException||error.InnerException is COMException;}
                        if(rejected==authorized)throw new Exception("Unexpected close authorization: "+mode);
                    }
                    if(mode=="takeover")userControlled=true;
                    sessionType.GetMethod("Reset",Flags).Invoke(session,null);
                    if(mode=="double-reset")sessionType.GetMethod("Reset",Flags).Invoke(session,null);
                    var exits=mode=="private"||mode=="double-reset"?1:0;
                    if(calls.Count(call=>call=="Exit")!=exits||calls.Count(call=>call=="Command")!=exits
                        ||calls.Count(call=>call=="CloseAll")!=(authorized?1:0))
                        throw new Exception("Worker touched an unowned/foreground session or repeated cleanup: "+mode+"; "+string.Join(",",calls));
                    if((mode=="foreground"||mode=="takeover")&&!userControlled||mode=="visible"&&!visible)
                        throw new Exception("Worker changed user's visibility/control state");
                    Console.WriteLine("PASS: preview session "+mode+"; mutations="+string.Join(",",calls));
                }
            }
            // This check only enumerates processes and rejects before COM activation.
            // Do not create a real CAD session as part of this managed regression.
            var existing=Process.GetProcessesByName("SLDWORKS");
            try
            {
                if(existing.Length>0)
                {
                    var session=Activator.CreateInstance(sessionType,Flags,null,new object[]{true},null);
                    var rejected=false;
                    try{sessionType.GetMethod("EnsureApplication",Flags).Invoke(session,null);}
                    catch(TargetInvocationException error){rejected=error.InnerException is InvalidOperationException&&error.InnerException.Message.Contains("已有SolidWorks");}
                    if(!rejected||appField.GetValue(session)!=null)throw new Exception("Existing CAD was not rejected before activation");
                    sessionType.GetMethod("Reset",Flags).Invoke(session,null);
                    Console.WriteLine("PASS: pre-existing CAD prevents COM activation; no CAD object created");
                }
                else Console.WriteLine("SKIP: no pre-existing CAD for read-only activation guard check");
            }
            finally{foreach(var process in existing)process.Dispose();}
            return 0;
        }
        catch(Exception error){Console.WriteLine("FAIL: "+error);return 1;}
    }

    private sealed class Proxy:RealProxy
    {
        private readonly Func<IMethodCallMessage,object> dispatch;
        public Proxy(Type type,Func<IMethodCallMessage,object> dispatch):base(type){this.dispatch=dispatch;}
        public override IMessage Invoke(IMessage message)
        {
            var call=(IMethodCallMessage)message;
            try{return new ReturnMessage(dispatch(call),null,0,call.LogicalCallContext,call);}
            catch(Exception error){return new ReturnMessage(error,call);}
        }
    }
}
