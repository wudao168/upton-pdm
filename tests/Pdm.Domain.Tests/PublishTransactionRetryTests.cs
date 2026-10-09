using System.Reflection;
using MySqlConnector;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class PublishTransactionRetryTests
{
    private static Task<int> Run(Func<Task<int>> operation, CancellationToken cancellationToken = default) =>
        (Task<int>)typeof(MySqlPdmRepository).GetMethod("RetryPublishTransactionAsync", BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(typeof(int)).Invoke(null, [operation, cancellationToken])!;

    private static MySqlException Error(int number) => (MySqlException)typeof(MySqlException)
        .GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, [typeof(MySqlErrorCode), typeof(string)], null)!
        .Invoke([(MySqlErrorCode)number, "Test database failure"]);

    [Fact]
    public async Task DeadlockRetriesAfterCleanupAndReturnsOnlySuccessfulResult()
    {
        var attempts = 0;
        var cleanedUp = 0;
        var committed = 0;
        var result = await Run(() =>
        {
            Assert.Equal(attempts, cleanedUp);
            attempts++;
            try
            {
                if (attempts < 3) throw Error(1213);
                committed++;
                return Task.FromResult(42);
            }
            finally { cleanedUp++; }
        });
        Assert.Equal(42, result);
        Assert.Equal(3, attempts);
        Assert.Equal(1, committed);
    }

    [Fact]
    public async Task PersistentDeadlockStopsAfterThreeAttempts()
    {
        var attempts = 0;
        await Assert.ThrowsAsync<MySqlException>(() => Run(() => { attempts++; throw Error(1213); }));
        Assert.Equal(3, attempts);
    }

    [Theory]
    [InlineData(1205)]
    [InlineData(1062)]
    [InlineData(0)]
    public async Task OtherDatabaseFailuresAreNotReplayed(int number)
    {
        var attempts = 0;
        await Assert.ThrowsAsync<MySqlException>(() => Run(() => { attempts++; throw Error(number); }));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task CancellationStopsRetryDelay()
    {
        using var cancellation = new CancellationTokenSource();
        var attempts = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(() =>
        {
            attempts++;
            cancellation.Cancel();
            throw Error(1213);
        }, cancellation.Token));
        Assert.Equal(1, attempts);
    }
}
