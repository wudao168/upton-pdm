using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Pdm.Domain.Tests;

public sealed class ArchiveWarningPolicyTests
{
    [Fact]
    public void CompleteCatalogSeparatesOfficialAndExtensionAndUnsupportedRules()
    {
        Assert.Equal(19, ArchiveWarningPolicy.Catalog.Count(item => item.Source == "PDM官方"));
        Assert.Equal(5, ArchiveWarningPolicy.Catalog.Count(item => item.Source == "UPLM扩展"));
        Assert.Equal(24, ArchiveWarningPolicy.Catalog.Select(item => item.Code).Distinct().Count());
        var policy = new ArchiveWarningPolicy();
        Assert.True(policy.Blocks("CyclicReference"));
        Assert.False(policy.Blocks("MissingConfiguration"));
        Assert.False(policy.Blocks("MissingFile"));
        Assert.False(policy.Blocks("PropertyIndexPending"));
        Assert.Throws<ArgumentException>(() => ArchiveWarningPolicy.Validate(new() { BlockingCodes = ["NotRebuilt"] }));
        Assert.Throws<ArgumentException>(() => ArchiveWarningPolicy.Validate(new() { BlockingCodes = ["CyclicReference"] }));
    }

    [Fact]
    public void StrictOnlyAppliesToSupportedWarningsAndKeepsCyclicMandatory()
    {
        var policy = new ArchiveWarningPolicy { BlockAll = true };
        foreach (var rule in ArchiveWarningPolicy.Catalog) Assert.Equal(rule.Supported, policy.Blocks(rule.Code));
        policy = new() { BlockingCodes = ["MissingConfiguration"] };
        Assert.True(policy.Blocks("MissingConfiguration"));
        Assert.False(policy.Blocks("ConfigurationUnreadable"));
    }

    [Fact]
    public void StructuredAndLegacyWarningsAreRecognizedWithoutMatchingArbitraryErrors()
    {
        Assert.Equal("MissingConfiguration", ArchiveWarningPolicy.CodeOf(ArchiveWarningPolicy.Format("MissingConfiguration", "PreviewCfg")));
        Assert.Equal("PropertyIndexPending", ArchiveWarningPolicy.CodeOf("属性索引待补齐：原文件已保留"));
        Assert.Equal("MissingFile", ArchiveWarningPolicy.CodeOf("存档引用提示：a；引用文件缺失"));
        Assert.Null(ArchiveWarningPolicy.CodeOf("上传失败"));
    }

    [Fact]
    public async Task RepositoryRejectsStaleUpdatesAndReturnsIndependentCopies()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var saved = await repository.SaveArchiveWarningPolicyAsync(new() { BlockingCodes = ["MissingConfiguration"] }, default);
        Assert.Equal(1, saved.Revision);
        saved.BlockingCodes[0] = "MissingFile";
        Assert.True((await repository.GetArchiveWarningPolicyAsync(default)).Blocks("MissingConfiguration"));
        await Assert.ThrowsAsync<PdmConflictException>(() => repository.SaveArchiveWarningPolicyAsync(new(), default));
    }

    [Fact]
    public async Task ServerCommitValidationUsesAdministratorPolicyForRecordedWarnings()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        Task Validate(string[] warnings)
        {
            var workflow = new PdmWorkflowService(repository, null!, null!, TimeProvider.System);
            var method = typeof(PdmWorkflowService).GetMethod("ValidateArchiveWarningsAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            return (Task)method.Invoke(workflow, [warnings, CancellationToken.None])!;
        }
        var warnings = new[] { ArchiveWarningPolicy.Format("MissingConfiguration", "PreviewCfg") };
        await Validate(warnings);
        await repository.SaveArchiveWarningPolicyAsync(new() { BlockingCodes = ["MissingConfiguration"] }, default);
        var error = await Assert.ThrowsAsync<PdmRuleException>(() => Validate(warnings));
        Assert.Contains("版本1", error.Message);
        Assert.Contains("PreviewCfg", error.Message);
        await Validate([]);
    }
}
