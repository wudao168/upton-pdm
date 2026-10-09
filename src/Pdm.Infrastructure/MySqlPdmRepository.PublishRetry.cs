using MySqlConnector;

namespace Upton.Pdm.Infrastructure;

public sealed partial class MySqlPdmRepository
{
    private static async Task<T> RetryPublishTransactionAsync<T>(Func<Task<T>> transaction, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await transaction();
            }
            catch (MySqlException exception) when (exception.Number == 1213 && attempt < 2)
            {
                // The operation owns/disposes its connection and transaction before retrying.
                // Retry only deadlocks: connection/commit failures may have an unknown outcome.
                await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)), cancellationToken);
            }
        }
    }
}
