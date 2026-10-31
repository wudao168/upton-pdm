using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

internal static partial class Program
{
    static void UnverifiedReferenceScenario()
    {
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); Console.WriteLine("PASS: " + message); }
        object Node(string name, string role = "Component")
        {
            var node = New("CadTreeNode"); Set(node,"FullPath",@"C:\隔离回归\"+name);Set(node,"FileName",name);
            Set(node,"Role",Enum.Parse(Type("CadReferenceRole"),role));Set(node,"Kind",Enum.Parse(Type("CadDocumentKind"),name.EndsWith("SLDASM")?"Assembly":"Part"));return node;
        }
        void Add(object owner, object child) => ((IList)Get(owner,"Children")).Add(child);
        var root=Node("总装.SLDASM");var part=Node("零件.SLDPRT");var back=Node("总装.SLDASM","ExternalFileDependency");Add(root,part);Add(part,back);
        var items=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(Type("BatchOperationItem")));items.Add(New("BatchOperationItem",root,1));items.Add(New("BatchOperationItem",part,1));
        var policyType=assembly.GetType("Upton.Pdm.Domain.ArchiveWarningPolicy",true);
        void Policy(bool strict){var policy=Activator.CreateInstance(policyType);Set(policy,"BlockAll",strict);Type("ArchiveWarningGate").GetMethod("Configure",Flags).Invoke(null,new[]{policy});}
        var validate=Type("ArchiveReferenceCycles").GetMethod("Validate",Flags);
        Policy(false);validate.Invoke(null,new object[]{items});
        Check((bool)Get(back,"ReferenceTypeUnverified")&&!(bool)Get(back,"IsContextReference"),"未知回边保留独立标记，不冒充上下文");
        Check(((IList)Get(part,"ReferenceWarnings")).Count==1&&(bool)Get(root,"StructureIndexPending"),"默认放行但保留警告和结构待核对状态");
        validate.Invoke(null,new object[]{items});Check(((IList)Get(part,"ReferenceWarnings")).Count==1,"重复校验不会重复添加同一警告");
        Policy(true);try{validate.Invoke(null,new object[]{items});throw new Exception("严格模式未阻止");}catch(TargetInvocationException e){Check(e.InnerException.Message.Contains("管理员存档警告策略"),"管理员严格策略阻止未知回边");}
        Policy(false);Set(back,"ReferenceTypeVerified",true);try{validate.Invoke(null,new object[]{items});throw new Exception("普通循环未阻止");}catch(TargetInvocationException e){Check(e.InnerException.Message.Contains("已确认"),"真实普通引用循环不受警告关闭影响");}
        Set(back,"ReferenceTypeVerified",false);var ordinary=Node("总装.SLDASM","ExternalFileDependency");Set(ordinary,"ReferenceTypeVerified",true);Add(part,ordinary);
        try{validate.Invoke(null,new object[]{items});throw new Exception("混合分组真实循环未阻止");}catch(TargetInvocationException e){Check(e.InnerException.Message.Contains("已确认"),"含未知边的分组内真实子循环仍被阻止");}
        Check((string)Get(back,"FullPath")==@"C:\隔离回归\总装.SLDASM"&&((IList)Get(part,"Children")).Contains(back),"原引用未删除、未替换、未改名");
    }
}
