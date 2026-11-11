using Upton.Pdm.Application;
using Upton.Pdm.Domain;
namespace Upton.Pdm.Tests;
public sealed class ProjectReportingScopeTests
{
    [Fact]
    public void DivisionMembershipIncludesDescendantsButNeverSiblingsOrOtherCompany()
    {
        var company=Guid.NewGuid();var other=Guid.NewGuid();var division=Guid.NewGuid();var department=Guid.NewGuid();var sibling=Guid.NewGuid();var foreign=Guid.NewGuid();
        OrganizationUnit Unit(Guid id,Guid org,Guid? parent,OrganizationUnitKind kind)=>new(id,org,parent,"U","组织",kind,true,0);
        var directory=new OrganizationDirectory([], [Unit(division,company,null,OrganizationUnitKind.BusinessDivision),Unit(department,company,division,OrganizationUnitKind.Department),Unit(sibling,company,null,OrganizationUnitKind.BusinessDivision),Unit(foreign,other,division,OrganizationUnitKind.Department)], [new(department,"manager",true)], [], []);
        var result=ProjectReportingService.AllowedUnits(directory,"MANAGER",company);
        Assert.Contains(division,result);Assert.Contains(department,result);Assert.DoesNotContain(sibling,result);Assert.DoesNotContain(foreign,result);
        Assert.Empty(ProjectReportingService.AllowedUnits(directory,"outsider",company));
    }
    [Fact]
    public void ManagingOneDivisionDoesNotExpandToItsParentOrSibling()
    {
        var company=Guid.NewGuid();var division=Guid.NewGuid();var sibling=Guid.NewGuid();
        var directory=new OrganizationDirectory([], [new(division,company,null,"D1","事业部一",OrganizationUnitKind.BusinessDivision,true,0),new(sibling,company,null,"D2","事业部二",OrganizationUnitKind.BusinessDivision,true,0)], [], [new(division,"manager",[])], []);
        Assert.Equal([division],ProjectReportingService.AllowedUnits(directory,"manager",company));
    }
}
