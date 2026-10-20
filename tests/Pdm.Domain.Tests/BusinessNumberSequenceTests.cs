using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class BusinessNumberSequenceTests
{
    [Fact]
    public async Task ConcurrentReservationsAreUniqueAndPrefixesHaveIndependentSequences()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        const string prefix = "LX20261019-";
        var numbers = await Task.WhenAll(Enumerable.Range(0, 100)
            .Select(_ => Task.Run(() => repository.ReserveBusinessNumberAsync(prefix, default))));
        Assert.Equal(Enumerable.Range(1, 100).Select(value => prefix + value.ToString("D4")), numbers.Order());
        Assert.Equal("LX20261020-0001", await repository.ReserveBusinessNumberAsync("LX20261020-", default));
        Assert.Equal("RP-P700012-0-20261019-0001", await repository.ReserveBusinessNumberAsync("RP-P700012-0-20261019-", default));
        Assert.Equal("RP-P700013-0-20261019-0001", await repository.ReserveBusinessNumberAsync("RP-P700013-0-20261019-", default));
    }

    [Fact]
    public async Task ExistingFourDigitNumbersAdvanceTheCounterAndDeletedDraftNumbersAreNotReused()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        const string prefix = "RP-P700012-0-20261019-";
        var legacy = Package(prefix + "A1C4FCFA");
        await repository.CreateReleasePackageAsync(legacy, default);
        Assert.Equal(prefix + "0001", await repository.ReserveBusinessNumberAsync(prefix, default));
        var numbered = Package(prefix + "0042");
        await repository.CreateReleasePackageAsync(numbered, default);
        Assert.Equal(prefix + "0043", await repository.ReserveBusinessNumberAsync(prefix, default));
        await repository.DeleteDraftReleasePackageAsync(numbered.Id, default);
        Assert.Equal(prefix + "0044", await repository.ReserveBusinessNumberAsync(prefix, default));
        Assert.Equal(legacy.Number, (await repository.FindReleasePackageAsync(legacy.Id, default))!.Number);
    }

    [Fact]
    public async Task ExhaustionNeverProducesMoreThanFourDigits()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        const string prefix = "RP-P700012-0-20261019-";
        await repository.CreateReleasePackageAsync(Package(prefix + "9999"), default);
        var error = await Assert.ThrowsAsync<PdmRuleException>(() => repository.ReserveBusinessNumberAsync(prefix, default));
        Assert.Contains("4位流水号已用尽", error.Message);
        Assert.Equal("RP-P700012-0-20261020-0001", await repository.ReserveBusinessNumberAsync("RP-P700012-0-20261020-", default));
    }

    private static ReleasePackage Package(string number) => new(Guid.NewGuid(), Guid.Parse("11111111-1111-1111-1111-111111111111"),
        number, ReleasePackageState.Draft, null, "M", "E", [], DateTimeOffset.UtcNow, null, null);
}
