using Upton.Pdm.Domain;

namespace Upton.Pdm.Domain.Tests;

public sealed class RevisionLabelTests
{
    [Fact]
    public void InitialWork_ReleasesAsA()
    {
        var released = RevisionLabel.InitialWork().Release();

        Assert.True(released.IsReleased);
        Assert.Equal("A", released.Display);
    }

    [Fact]
    public void ReleasedRevision_StartsNewWorkAndAdvancesOnRelease()
    {
        var work = RevisionLabel.Released('A').NextWork();

        Assert.Equal("A-W1", work.Display);
        Assert.Equal("B", work.Release().Display);
    }

    [Theory]
    [InlineData("W3")]
    [InlineData("A-W2")]
    [InlineData("C")]
    public void Parse_RoundTripsSupportedLabels(string value)
    {
        Assert.Equal(value, RevisionLabel.Parse(value).Display);
    }

    [Fact]
    public void RestoringOldWork_AdvancesFromCurrentLatest()
    {
        var current = RevisionLabel.Parse("W3");

        Assert.Equal("W4", current.NextWork().Display);
    }

    [Fact]
    public void RestoringAfterRelease_CreatesNewReleasedBranchWork()
    {
        Assert.Equal("A-W1", RevisionLabel.Released('A').NextWork().Display);
        Assert.Equal("A-W2", RevisionLabel.Parse("A-W1").NextWork().Display);
    }
}
