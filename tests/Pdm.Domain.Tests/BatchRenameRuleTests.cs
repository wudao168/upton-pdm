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

    [Fact]
    public void HierarchyNumbering_PreservesExistingNumbersAndAppendsNewSiblings()
    {
        var items = new[]
        {
            new BatchRenameHierarchyItem("root", "", BatchRenameHierarchyKind.Assembly, true, "00"),
            new BatchRenameHierarchyItem("asm-existing", "root", BatchRenameHierarchyKind.Assembly, false, "01"),
            new BatchRenameHierarchyItem("asm-new", "root", BatchRenameHierarchyKind.Assembly),
            new BatchRenameHierarchyItem("part-new", "asm-existing", BatchRenameHierarchyKind.Part),
            new BatchRenameHierarchyItem("part-existing-2", "asm-existing", BatchRenameHierarchyKind.Part, false, "01-02"),
            new BatchRenameHierarchyItem("part-existing-5", "asm-existing", BatchRenameHierarchyKind.Part, false, "01-05")
        };

        var numbers = BatchRenameRule.BuildHierarchyNumbers(items);

        Assert.Equal("01", numbers["asm-existing"]);
        Assert.Equal("02", numbers["asm-new"]);
        Assert.Equal("01-06", numbers["part-new"]);
        Assert.Equal("01-02", numbers["part-existing-2"]);
        Assert.Equal("01-05", numbers["part-existing-5"]);
    }

    [Theory]
    [InlineData("7000034.01-05", "01-05")]
    [InlineData("7000030.01.02-03", "01.02-03")]
    [InlineData("普通零件", "")]
    [InlineData("7000034.虚拟件", "")]
    public void ExistingHierarchyNumber_IsExtractedOnlyFromNumberedNames(string fileBaseName, string expected)
    {
        Assert.Equal(expected, BatchRenameRule.ExtractExistingHierarchyNumber(fileBaseName));
    }

    [Fact]
    public void HierarchyScope_DefaultCategoriesSelectAssembliesAndNonStandardParts()
    {
        Assert.True(BatchRenameRule.IsInHierarchyScope(BatchRenameHierarchyKind.Assembly, "标准件", true, true));
        Assert.True(BatchRenameRule.IsInHierarchyScope(BatchRenameHierarchyKind.Part, "非标件", true, true));
        Assert.False(BatchRenameRule.IsInHierarchyScope(BatchRenameHierarchyKind.Part, "标准件", true, true));
        Assert.True(BatchRenameRule.IsInHierarchyScope(BatchRenameHierarchyKind.Part, "部件图", true, false));
    }

    [Fact]
    public void HierarchyRename_RequiresBothRowSelectionAndCurrentScopeMatch()
    {
        Assert.False(BatchRenameRule.CanGenerateHierarchyRename(
            false, BatchRenameHierarchyKind.Part, "非标件", true, true));
        Assert.False(BatchRenameRule.CanGenerateHierarchyRename(
            true, BatchRenameHierarchyKind.Part, "虚拟件", true, true));
        Assert.True(BatchRenameRule.CanGenerateHierarchyRename(
            true, BatchRenameHierarchyKind.Part, "非标件", true, true));
    }

    [Fact]
    public void PropertyFilter_CanIncludeOrExcludeMatchesAcrossAllValues()
    {
        var values = new[] { "文件.SLDPRT", "AIRTEC", "非标件" };

        Assert.True(BatchPropertyFilterRule.MatchesQuery(values, "air", false));
        Assert.False(BatchPropertyFilterRule.MatchesQuery(values, "air", true));
        Assert.True(BatchPropertyFilterRule.MatchesQuery(values, "SMC", true));
        Assert.True(BatchPropertyFilterRule.MatchesQuery(values, "", true));
    }

    [Fact]
    public void LocalBatchPropertyEdit_IncludesLoadedUnsavedDocumentWithoutDiskFallback()
    {
        Assert.True(BatchLocalDocumentRule.CanEdit(true, false, false, false));
        Assert.True(BatchLocalDocumentRule.CanEdit(false, true, false, false));
        Assert.False(BatchLocalDocumentRule.CanEdit(false, false, false, false));
        Assert.False(BatchLocalDocumentRule.CanEdit(true, true, true, false));
    }
}
