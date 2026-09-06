using System;
using Upton.Pdm.SolidWorks;
using Xunit;

namespace Pdm.Domain.Tests;

public sealed class BatchRenameRuleTests
{
    [Fact]
    public void Replace_IsLiteralAndCaseInsensitiveByDefault()
    {
        Assert.Equal(
            "新名称-新名称",
            BatchRenameRule.Apply("OLD名称-old名称", BatchRenameTextOperation.Replace, "old", "新", false));
    }

    [Fact]
    public void Replace_CanBeCaseSensitive()
    {
        Assert.Equal(
            "新名称-old名称",
            BatchRenameRule.Apply("OLD名称-old名称", BatchRenameTextOperation.Replace, "OLD", "新", true));
    }

    [Fact]
    public void PrefixSuffixAndRemove_AreSupported()
    {
        Assert.Equal("前缀-名称", BatchRenameRule.Apply("名称", BatchRenameTextOperation.Prefix, "", "前缀-", false));
        Assert.Equal("名称-后缀", BatchRenameRule.Apply("名称", BatchRenameTextOperation.Suffix, "", "-后缀", false));
        Assert.Equal("名称", BatchRenameRule.Apply("旧名称旧", BatchRenameTextOperation.Remove, "旧", "忽略", false));
    }

    [Fact]
    public void FileNameValidation_RejectsUnsafeNamesAndKeepsExtensionSeparate()
    {
        Assert.Equal("新名称", BatchRenameRule.NormalizeFileBaseName("新名称.SLDPRT", ".SLDPRT"));
        Assert.Throws<InvalidOperationException>(() => BatchRenameRule.NormalizeFileBaseName("CON", ".SLDPRT"));
        Assert.Throws<InvalidOperationException>(() => BatchRenameRule.NormalizeFileBaseName("错误/名称", ".SLDPRT"));
    }
}
