using Upton.Pdm.SolidWorks;
using Xunit;

namespace Pdm.Domain.Tests;

public sealed class BatchPropertyNameModelAutoFillRuleTests
{
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
