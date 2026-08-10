namespace Perigon.PostgreSQL.Bulk;

internal static class BulkInsertCancellation
{
    public static CancellationTokenSource? CreateSource(CancellationToken cancellationToken, TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout cannot be negative except Timeout.InfiniteTimeSpan.");
        }

        if (timeout == TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan)
        {
            return cancellationToken.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : null;
        }

        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(timeout);
        return source;
    }
}
