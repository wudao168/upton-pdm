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

    [Fact]
    public void HierarchyNumbering_UsesDotsForAssembliesAndDashesForParts()
    {
        var items = new[]
        {
            new BatchRenameHierarchyItem("root", "", BatchRenameHierarchyKind.Assembly, true),
            new BatchRenameHierarchyItem("asm-1", "root", BatchRenameHierarchyKind.Assembly),
            new BatchRenameHierarchyItem("sub-1", "asm-1", BatchRenameHierarchyKind.Assembly),
            new BatchRenameHierarchyItem("sub-2", "asm-1", BatchRenameHierarchyKind.Assembly),
            new BatchRenameHierarchyItem("part-1", "sub-2", BatchRenameHierarchyKind.Part),
            new BatchRenameHierarchyItem("part-2", "asm-1", BatchRenameHierarchyKind.Part),
            new BatchRenameHierarchyItem("part-3", "asm-1", BatchRenameHierarchyKind.Part),
            new BatchRenameHierarchyItem("asm-2", "root", BatchRenameHierarchyKind.Assembly),
            new BatchRenameHierarchyItem("part-4", "asm-2", BatchRenameHierarchyKind.Part),
            new BatchRenameHierarchyItem("root-part", "root", BatchRenameHierarchyKind.Part)
        };
        var numbers = BatchRenameRule.BuildHierarchyNumbers(items);

        Assert.Equal("00", numbers["root"]);
        Assert.Equal("01", numbers["asm-1"]);
        Assert.Equal("01.01", numbers["sub-1"]);
        Assert.Equal("01.02", numbers["sub-2"]);
        Assert.Equal("01.02-01", numbers["part-1"]);
        Assert.Equal("01-01", numbers["part-2"]);
        Assert.Equal("01-02", numbers["part-3"]);
        Assert.Equal("02", numbers["asm-2"]);
        Assert.Equal("02-01", numbers["part-4"]);
        Assert.Equal("00-01", numbers["root-part"]);
        Assert.Equal(
            new[] { "root", "root-part", "asm-1", "part-2", "part-3", "sub-1", "sub-2", "part-1", "asm-2", "part-4" },
            BatchRenameRule.BuildHierarchyOrder(items));
    }

    [Fact]
    public void HierarchyFileName_UsesOnlySerialAndGeneratedNumber()
    {
        Assert.Equal("70000030.00", BatchRenameRule.HierarchyFileBaseName("70000030", "00"));
        Assert.Equal("70000030.01.02-01", BatchRenameRule.HierarchyFileBaseName("70000030", "01.02-01"));
        Assert.Throws<InvalidOperationException>(() => BatchRenameRule.HierarchyFileBaseName("", "01"));
    }
}
