using Microsoft.Extensions.DependencyInjection;
using Upton.Pdm.Domain;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using Upton.Pdm.Application;
namespace Upton.Pdm.Api.Tests;
public sealed class ProjectReportingApiTests
{
 [Fact] public async Task ReportRequiresAuthenticationAndProvidesScopedContract()
 {
  await using var factory=new PdmApiFactory();using var client=factory.CreateClient();
  Assert.Equal(HttpStatusCode.Unauthorized,(await client.GetAsync("/api/project-report")).StatusCode);
  await Initialize(factory,"admin",UserRole.Administrator);
  client.DefaultRequestHeaders.Authorization=new("Bearer",Token("admin","Administrator"));
  var response=await client.GetAsync("/api/project-report");Assert.True(response.IsSuccessStatusCode,await response.Content.ReadAsStringAsync());
  var view=(await response.Content.ReadFromJsonAsync<ProjectReportingView>())!;Assert.True(view.CanViewCompany);
  Assert.All(view.Details,d=>Assert.Contains(view.Workbench.Projects,p=>p.Project.Id==d.ProjectId));
 }
 [Fact] public async Task ScopeQueryCannotPromoteEngineerToCompanyAccess()
 {
  await using var factory=new PdmApiFactory();using var client=factory.CreateClient();
  await Initialize(factory,"engineer",UserRole.Engineer);
  client.DefaultRequestHeaders.Authorization=new("Bearer",Token("engineer","Engineer"));
  var response=await client.GetAsync("/api/project-report?scope=company&view=division");Assert.True(response.IsSuccessStatusCode,await response.Content.ReadAsStringAsync());
  var view=(await response.Content.ReadFromJsonAsync<ProjectReportingView>())!;Assert.False(view.CanViewCompany);Assert.DoesNotContain("company",view.Workbench.Scopes!);
  Assert.Empty(view.Workbench.Projects);Assert.Empty(view.Details);Assert.Empty(view.Workbench.Risks);
 }
 private static async Task Initialize(PdmApiFactory factory,string actor,UserRole role)
 {
  var repository=factory.Services.GetRequiredService<IPdmRepository>();
  var project=(await repository.ListProjectsAsync(default)).First();
  await repository.CreateUserAsync(new(Guid.NewGuid(),actor,actor,"unused",role,true),default);
  if(project.OrganizationId is Guid company)await repository.SetUserCompanyScopeAsync(actor,company,true,[company],"admin",default);
 }
 private static string Token(string actor,string role)=>new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("upton-pdm","upton-pdm-clients",[new Claim(ClaimTypes.Name,actor),new Claim(ClaimTypes.Role,role)],expires:DateTime.UtcNow.AddMinutes(10),signingCredentials:new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")),SecurityAlgorithms.HmacSha256)));
}
