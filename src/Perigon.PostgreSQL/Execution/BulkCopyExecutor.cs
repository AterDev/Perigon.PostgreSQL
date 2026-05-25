using NpgsqlTypes;
using Perigon.PostgreSQL.Metadata;
using Perigon.PostgreSQL.Sql;

namespace Perigon.PostgreSQL.Execution;

internal static class BulkCopyExecutor
{
    public static async Task BulkInsertAsync<T>(
        DbSet<T> set,
        IEnumerable<T> entities,
        CancellationToken cancellationToken)
        where T : class
    {
        var rows = entities as IReadOnlyCollection<T> ?? entities.ToArray();
        if (rows.Count == 0)
        {
            return;
        }

        var columns = set.Model.Columns.Where(c => c.IsWritable).ToArray();
        var columnSql = string.Join(", ", columns.Select(c => Identifier.Quote(c.ColumnName)));
        var sql = $"COPY {set.Model.StoreObjectName} ({columnSql}) FROM STDIN (FORMAT BINARY)";

        var ownsConnection = !set.Context.TryGetTransactionConnection(out var transactionConnection);
        var writer = transactionConnection ?? await set.Context.GetDataSource()
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {
            await using var importer = await writer
                .BeginBinaryImportAsync(sql, cancellationToken)
                .ConfigureAwait(false);

            foreach (var row in rows)
            {
                await importer.StartRowAsync(cancellationToken).ConfigureAwait(false);
                foreach (var column in columns)
                {
                    var value = EntityValueAccessorRegistry.GetValue(column, row);
                    var dbType = InferDbType(column);
                    if (dbType is null)
                    {
                        await importer.WriteAsync(value, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await importer.WriteAsync(value, dbType.Value, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            await importer.CompleteAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (ownsConnection)
            {
                await writer.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static NpgsqlDbType? InferDbType(ColumnModel column)
    {
        if (column.TypeName?.Equals("jsonb", StringComparison.OrdinalIgnoreCase) == true)
        {
            return NpgsqlDbType.Jsonb;
        }

        return null;
    }
}
