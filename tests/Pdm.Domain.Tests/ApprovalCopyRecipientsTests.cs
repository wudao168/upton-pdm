using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;
namespace Upton.Pdm.Domain.Tests;

public sealed class ApprovalCopyRecipientsTests
{
    [Fact]
    public async Task DepartmentRolesDescendantsManagersAndProjectRolesResolveWithinCompany()
    {
        TenantContext.Clear();
        var contacts = new InMemoryProjectContactInformationRepository();
        var repo = new InMemoryPdmRepository(TimeProvider.System,contacts);
        var companies = (await repo.GetOrganizationDirectoryAsync(default)).Organizations.Where(item=>item.IsActive).ToArray();
        var company = companies[0].Id;
        foreach (var name in new[]{"author","engineer","child","sibling","manager","collaborative","buyer","sales","presales","custom","foreign","inactive"})
            await repo.CreateUserAsync(new(Guid.NewGuid(),name,name,"unused",name is "engineer" or "child" or "sibling" or "foreign" ? UserRole.Engineer : UserRole.Approver,name!="inactive",CompanyId:name=="foreign"?companies[1].Id:company),default);
        async Task<Guid> Unit(string code,Guid? parent,OrganizationUnitKind kind) => (await repo.SaveOrganizationUnitAsync(new(null,company,parent,code,code,kind,true,0),default)).Id;
        var division = await Unit("copy-division",null,OrganizationUnitKind.BusinessDivision);
        var department = await Unit("copy-department",division,OrganizationUnitKind.Department);
        var child = await Unit("copy-child",department,OrganizationUnitKind.Department);
        var sibling = await Unit("copy-sibling",division,OrganizationUnitKind.Department);
        foreach(var name in new[]{"author","engineer","manager","collaborative","inactive"}) await repo.SetOrganizationMembershipsAsync(name,[department],department,default);
        await repo.SetOrganizationMembershipsAsync("child",[child],child,default);await repo.SetOrganizationMembershipsAsync("sibling",[sibling],sibling,default);
        await repo.SetOrganizationUnitManagersAsync(department,"manager",["collaborative"],default);
        var project = new Project(Guid.NewGuid(),"COPY","抄送范围测试","author","unused","unused",true) {
            OrganizationId=company,PrimaryProjectManager="manager",CollaborativeProjectManagers=["collaborative"],Designers=["engineer"],
            PhaseOwners=new Dictionary<string,string>{{"StandardProcurement","buyer"}},CustomPositionMembers=new Dictionary<string,IReadOnlyList<string>>{{"custom-position",["custom"]}}
        };
        await contacts.SaveAsync(new(project.Id,SalesPeople:"sales",PresalesPeople:"presales"),0,default);
        var directory = await repo.GetOrganizationDirectoryAsync(default);
        async Task<string[]> Resolve(ApprovalCopyRecipient row) => (await ApprovalCopyRecipients.ResolveAsync(repo,row,project,"author",directory,department,default)).Order().ToArray();
        Assert.Equal(["engineer"],await Resolve(new("SubmitterDepartment",RoleCode:"Engineer")));
        Assert.Equal(["child","engineer"],await Resolve(new("Department",department.ToString(),"Engineer",true)));
        Assert.Equal(["child","engineer","sibling"],await Resolve(new("SubmitterDivision",RoleCode:"Engineer",IncludeSubdepartments:true)));
        Assert.Equal(["collaborative","manager"],await Resolve(new("Manager")));
        Assert.Equal(["collaborative","manager"],await Resolve(new("FixedManager",department.ToString())));
        Assert.Equal(["buyer"],await Resolve(new("ProjectRole","StandardProcurement")));
        Assert.Equal(["sales"],await Resolve(new("ProjectRole","SalesPeople")));
        Assert.Equal(["presales"],await Resolve(new("ProjectRole","PresalesPeople")));
        var all=await Resolve(new("ProjectMembers"));Assert.Contains("custom",all);Assert.Contains("sales",all);Assert.DoesNotContain("foreign",all);Assert.DoesNotContain("inactive",all);
        Assert.Empty(await Resolve(new("User","foreign")));
        await Assert.ThrowsAsync<PdmRuleException>(()=>ApprovalCopyRecipients.ValidateAsync(repo,[new("User","foreign")],company,default));
        await Assert.ThrowsAsync<PdmRuleException>(()=>ApprovalCopyRecipients.ValidateAsync(repo,[new("Role","deleted-role")],company,default));
    }
    [Theory]
    [InlineData("Unknown","")]
    [InlineData("User","")]
    [InlineData("Department","invalid")]
    [InlineData("ProjectRole","invalid")]
    public void InvalidRecipientsAreRejectedInChinese(string source,string target) =>
        Assert.Throws<PdmRuleException>(()=>ApprovalCopyRecipients.Normalize(new(source,target)));
}
