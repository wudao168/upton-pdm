using Upton.Pdm.SolidWorks;
using Xunit;

namespace Pdm.Domain.Tests;

public sealed class BatchPropertyNameModelAutoFillRuleTests
{
    [Theory]
    [InlineData("文档中的型号", "另一个文件.SLDPRT", "文档中的型号")]
    [InlineData("", "LM30UU.SLDPRT", "LM30UU")]
    [InlineData(" ", "按钮（黄色）.SLDPRT", "按钮（黄色）")]
    [InlineData(null, "340.0700A.01.SLDPRT", "340.0700A.01")]
    [InlineData("", "340.0700A.01", "340.0700A.01")]
    [InlineData("", "装配体.SLDASM", "装配体")]
    [InlineData("", "工程图.SLDDRW", "工程图")]
    [InlineData("", "", "")]
    public void MissingDocumentName_UsesFileStemWithoutChangingAProvidedName(string? documentName, string fileName, string expected)
    {
        Assert.Equal(expected, BatchPropertyNameModelAutoFillRule.DocumentNameOrFileName(documentName!, fileName));
    }

    [Fact]
    public void EmptyTarget_UsesTrimmedDocumentName()
    {
        var canFill = BatchPropertyNameModelAutoFillRule.TryResolveValue(
            "  透明管 HG-10  ",
            string.Empty,
            overwriteMismatch: false,
            out var value);

        Assert.True(canFill);
        Assert.Equal("透明管 HG-10", value);
    }

    [Fact]
    public void ExistingTarget_IsNotOverwritten()
    {
        var canFill = BatchPropertyNameModelAutoFillRule.TryResolveValue(
            "新文档名称",
            "现有值",
            overwriteMismatch: false,
            out var value);

        Assert.False(canFill);
        Assert.Equal("新文档名称", value);
    }

    [Fact]
    public void ExistingTarget_IsOverwrittenWhenConfirmed()
    {
        var canFill = BatchPropertyNameModelAutoFillRule.TryResolveValue(
            "新文档名称",
            "现有值",
            overwriteMismatch: true,
            out var value);

        Assert.True(canFill);
        Assert.Equal("新文档名称", value);
    }

    [Fact]
    public void EmptyDocumentName_IsNotFilled()
    {
        Assert.False(BatchPropertyNameModelAutoFillRule.TryResolveValue(
            "  ",
            string.Empty,
            overwriteMismatch: false,
            out _));
    }

    [Fact]
    public void DrawingTarget_OverwritesMismatchedValueFromRelatedModel()
    {
        var canFill = BatchPropertyNameModelAutoFillRule.TryResolveValue(
            "垫片",
            "UPLMTSET-003 工程图",
            overwriteMismatch: true,
            out var value);

        Assert.True(canFill);
        Assert.Equal("垫片", value);
    }

    [Fact]
    public void DrawingTarget_DoesNotChangeMatchingValue()
    {
        Assert.False(BatchPropertyNameModelAutoFillRule.TryResolveValue(
            "UPLMTSET-003",
            "UPLMTSET-003",
            overwriteMismatch: true,
            out _));
    }
}
