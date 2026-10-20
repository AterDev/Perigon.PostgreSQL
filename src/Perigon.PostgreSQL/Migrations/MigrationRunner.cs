using Npgsql;
using Perigon.PostgreSQL.Sql;

namespace Perigon.PostgreSQL.Migrations;

internal static class MigrationRunner
{
    public static async Task MigrateAsync(
        DbContext context,
        string? targetMigrationId,
        NpgsqlDataSource? dataSource,
        CancellationToken cancellationToken)
    {
        var migrations = GetMigrations(context);
        var targetIndex = ResolveTargetIndex(migrations, targetMigrationId);
        var historyTable = context.MigrationHistoryTable;
        var historySchema = context.MigrationHistorySchema;
        await using var connection = await (dataSource ?? context.GetDataSource()).OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await AcquireLockAsync(connection, historyTable, cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureHistoryTableAsync(connection, historySchema, historyTable, cancellationToken).ConfigureAwait(false);
            var applied = await ReadAppliedMigrationsAsync(connection, historyTable, cancellationToken).ConfigureAwait(false);
            var appliedCount = ValidateAppliedPrefix(migrations, applied);

            while (appliedCount > targetIndex + 1)
            {
                var migration = migrations[appliedCount - 1];
                migration.Instance.ValidateReversible();
                await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await ExecuteCommandsAsync(connection, transaction, migration.Instance.GetDownCommands(), cancellationToken).ConfigureAwait(false);
                    await DeleteHistoryRowAsync(connection, transaction, historyTable, migration.Id, cancellationToken).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    await RollbackPreservingOriginalAsync(transaction).ConfigureAwait(false);
                    throw;
                }

                appliedCount--;
            }

            while (appliedCount < targetIndex + 1)
            {
                var migration = migrations[appliedCount];
                migration.Instance.ValidateReversible();
                await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await ExecuteCommandsAsync(connection, transaction, migration.Instance.GetUpCommands(), cancellationToken).ConfigureAwait(false);
                    await InsertHistoryRowAsync(connection, transaction, historyTable, migration.Id, cancellationToken).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    await RollbackPreservingOriginalAsync(transaction).ConfigureAwait(false);
                    throw;
                }

                appliedCount++;
            }
        }
        finally
        {
            await ReleaseLockAsync(connection, historyTable).ConfigureAwait(false);
        }
    }

    public static async Task BaselineAsync(
        DbContext context,
        string migrationId,
        MigrationModel expectedModel,
        NpgsqlDataSource? dataSource,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expectedModel);
        var migrations = GetMigrations(context);
        if (!migrations.Any(migration => string.Equals(migration.Id, migrationId, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"Migration '{migrationId}' is not registered for DbContext '{context.GetType().FullName}'.");
        }

        if (!string.Equals(migrations[0].Id, migrationId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Baseline can only mark the initial migration '{migrations[0].Id}'. A baseline writes a single history row, so baselining a later migration would create a history gap.");
        }

        var historyTable = context.MigrationHistoryTable;
        await using var connection = await (dataSource ?? context.GetDataSource()).OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await AcquireLockAsync(connection, historyTable, cancellationToken).ConfigureAwait(false);
        try
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await EnsureHistoryTableAsync(connection, context.MigrationHistorySchema, historyTable, transaction, cancellationToken).ConfigureAwait(false);
                var applied = await ReadAppliedMigrationsAsync(connection, historyTable, transaction, cancellationToken).ConfigureAwait(false);
                if (applied.Count > 0)
                {
                    throw new InvalidOperationException("Cannot baseline a database that already has migration history.");
                }

                var actualModel = await MigrationCatalogReader.ReadAsync(connection, transaction, expectedModel, cancellationToken).ConfigureAwait(false);
                var difference = MigrationModelDiffer.Diff(
                    MigrationModelDiffer.NormalizeCatalogExpressions(expectedModel),
                    MigrationModelDiffer.NormalizeCatalogExpressions(actualModel));
                if (!difference.IsEmpty)
                {
                    var details = string.Join(Environment.NewLine, difference.Up.Select(static command => "  " + command.Sql));
                    throw new InvalidOperationException(
                        "The database schema does not match the migration snapshot. Baseline was not written. Differences:" + Environment.NewLine + details);
                }

                await InsertHistoryRowAsync(connection, transaction, historyTable, migrationId, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await RollbackPreservingOriginalAsync(transaction).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            await ReleaseLockAsync(connection, historyTable).ConfigureAwait(false);
        }
    }

    private static IReadOnlyList<RegisteredMigration> GetMigrations(DbContext context)
    {
        var migrations = MigrationRegistry.GetContextMigrations(context.GetType());
        if (migrations.Count == 0)
        {
            throw new InvalidOperationException(
                $"No migrations are registered for DbContext '{context.GetType().FullName}'. Add at least one migration and ensure the Perigon.PostgreSQL source generator is referenced.");
        }

        return migrations;
    }

    private static int ResolveTargetIndex(IReadOnlyList<RegisteredMigration> migrations, string? targetMigrationId)
    {
        if (string.IsNullOrWhiteSpace(targetMigrationId))
        {
            return migrations.Count - 1;
        }

        if (targetMigrationId == "0")
        {
            return -1;
        }

        for (var index = 0; index < migrations.Count; index++)
        {
            if (string.Equals(migrations[index].Id, targetMigrationId, StringComparison.Ordinal))
            {
                return index;
            }
        }

        throw new InvalidOperationException($"Target migration '{targetMigrationId}' is not registered.");
    }

    private static int ValidateAppliedPrefix(IReadOnlyList<RegisteredMigration> migrations, IReadOnlyList<string> applied)
    {
        if (applied.Count > migrations.Count)
        {
            throw new InvalidOperationException("The database contains migration IDs that are not registered in this application.");
        }

        for (var index = 0; index < applied.Count; index++)
        {
            if (!string.Equals(applied[index], migrations[index].Id, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Migration history is not a prefix of the registered migration sequence. Expected '{migrations[index].Id}', found '{applied[index]}'.");
            }
        }

        return applied.Count;
    }

    private static async Task AcquireLockAsync(NpgsqlConnection connection, string historyTable, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT pg_advisory_lock(hashtextextended($1, 0))";
        command.Parameters.AddWithValue(historyTable);
        await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ReleaseLockAsync(NpgsqlConnection connection, string historyTable)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT pg_advisory_unlock(hashtextextended($1, 0))";
            command.Parameters.AddWithValue(historyTable);
            await command.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Closing the connection releases a session advisory lock.
        }
    }

    private static Task EnsureHistoryTableAsync(
        NpgsqlConnection connection,
        string schema,
        string historyTable,
        CancellationToken cancellationToken) =>
        EnsureHistoryTableAsync(connection, schema, historyTable, null, cancellationToken);

    private static async Task EnsureHistoryTableAsync(
        NpgsqlConnection connection,
        string schema,
        string historyTable,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using (var schemaCommand = connection.CreateCommand())
        {
            schemaCommand.Transaction = transaction;
            schemaCommand.CommandText = $"CREATE SCHEMA IF NOT EXISTS {Identifier.Quote(schema)}";
            await schemaCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var tableCommand = connection.CreateCommand();
        tableCommand.Transaction = transaction;
        tableCommand.CommandText = $"CREATE TABLE IF NOT EXISTS {historyTable} (\"MigrationId\" text PRIMARY KEY, \"AppliedAt\" timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP)";
        await tableCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<string>> ReadAppliedMigrationsAsync(
        NpgsqlConnection connection,
        string historyTable,
        CancellationToken cancellationToken) =>
        await ReadAppliedMigrationsAsync(connection, historyTable, null, cancellationToken).ConfigureAwait(false);

    private static async Task<IReadOnlyList<string>> ReadAppliedMigrationsAsync(
        NpgsqlConnection connection,
        string historyTable,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT \"MigrationId\" FROM {historyTable} ORDER BY \"MigrationId\"";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<string>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    private static Task InsertHistoryRowAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string historyTable,
        string migrationId,
        CancellationToken cancellationToken) =>
        ExecuteHistoryCommandAsync(connection, transaction,
            $"INSERT INTO {historyTable} (\"MigrationId\") VALUES ($1)", migrationId, cancellationToken);

    private static Task DeleteHistoryRowAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string historyTable,
        string migrationId,
        CancellationToken cancellationToken) =>
        ExecuteHistoryCommandAsync(connection, transaction,
            $"DELETE FROM {historyTable} WHERE \"MigrationId\" = $1", migrationId, cancellationToken);

    private static async Task ExecuteHistoryCommandAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        string migrationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue(migrationId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExecuteCommandsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<MigrationCommand> commands,
        CancellationToken cancellationToken)
    {
        foreach (var migrationCommand in commands)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = migrationCommand.Sql;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task RollbackPreservingOriginalAsync(NpgsqlTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Preserve the original migration exception.
        }
    }
}
