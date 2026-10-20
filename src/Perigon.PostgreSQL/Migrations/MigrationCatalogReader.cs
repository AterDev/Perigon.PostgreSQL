using Npgsql;
using NpgsqlTypes;
using Perigon.PostgreSQL.Metadata;

namespace Perigon.PostgreSQL.Migrations;

internal static class MigrationCatalogReader
{
    public static async Task<MigrationModel> ReadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MigrationModel expected,
        CancellationToken cancellationToken)
    {
        var tables = new List<MigrationTable>(expected.Tables.Count);
        var currentSchema = await ReadCurrentSchemaAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        foreach (var expectedTable in expected.Tables)
        {
            tables.Add(await ReadTableAsync(connection, transaction, expectedTable, expected, currentSchema, cancellationToken).ConfigureAwait(false));
        }

        return new MigrationModel(tables);
    }

    private static async Task<string> ReadCurrentSchemaAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT current_schema()", connection);
        command.Transaction = transaction;
        return (string?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("PostgreSQL returned no current schema.");
    }

    private static async Task<MigrationTable> ReadTableAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MigrationTable expected,
        MigrationModel expectedModel,
        string currentSchema,
        CancellationToken cancellationToken)
    {
        const string tableSql = """
            SELECT relation.oid, pg_catalog.obj_description(relation.oid, 'pg_class')
            FROM pg_catalog.pg_class AS relation
            JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = relation.relnamespace
            WHERE namespace.nspname = COALESCE($1, current_schema())
              AND relation.relname = $2
              AND relation.relkind IN ('r', 'p')
            """;
        await using var tableCommand = new NpgsqlCommand(tableSql, connection);
        tableCommand.Transaction = transaction;
        tableCommand.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = expected.Schema is null ? DBNull.Value : expected.Schema });
        tableCommand.Parameters.AddWithValue(expected.Name);
        await using var tableReader = await tableCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await tableReader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Cannot baseline migration: table '{DisplayName(expected)}' does not exist.");
        }

        var comment = tableReader.IsDBNull(1) ? null : tableReader.GetString(1);
        await tableReader.DisposeAsync().ConfigureAwait(false);

        var columns = await ReadColumnsAsync(connection, transaction, expected, cancellationToken).ConfigureAwait(false);
        var primaryKey = await ReadPrimaryKeyAsync(connection, transaction, expected, cancellationToken).ConfigureAwait(false);
        var foreignKeys = await ReadForeignKeysAsync(connection, transaction, expected, expectedModel, currentSchema, cancellationToken).ConfigureAwait(false);
        var indexes = await ReadIndexesAsync(connection, transaction, expected, cancellationToken).ConfigureAwait(false);
        return new MigrationTable(expected.Schema, expected.Name, comment, columns, primaryKey, foreignKeys, indexes);
    }

    private static async Task<IReadOnlyList<MigrationColumn>> ReadColumnsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MigrationTable table,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT attribute.attname,
                   pg_catalog.format_type(attribute.atttypid, attribute.atttypmod),
                   NOT attribute.attnotnull,
                   attribute.attidentity <> '',
                   attribute.attgenerated <> '',
                   pg_catalog.pg_get_expr(default_value.adbin, default_value.adrelid),
                   pg_catalog.col_description(relation.oid, attribute.attnum)
            FROM pg_catalog.pg_class AS relation
            JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = relation.relnamespace
            JOIN pg_catalog.pg_attribute AS attribute ON attribute.attrelid = relation.oid
            LEFT JOIN pg_catalog.pg_attrdef AS default_value
              ON default_value.adrelid = relation.oid AND default_value.adnum = attribute.attnum
            WHERE namespace.nspname = COALESCE($1, current_schema())
              AND relation.relname = $2
              AND attribute.attnum > 0
              AND NOT attribute.attisdropped
            ORDER BY attribute.attnum
            """;
        await using var command = CreateTableCommand(connection, transaction, sql, table);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<MigrationColumn>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var isIdentity = reader.GetBoolean(3);
            var isGenerated = reader.GetBoolean(4);
            var expression = reader.IsDBNull(5) ? null : reader.GetString(5);
            result.Add(new MigrationColumn(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetBoolean(2),
                isIdentity,
                isGenerated,
                isIdentity || isGenerated ? null : expression,
                isGenerated ? expression : null,
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        return result;
    }

    private static async Task<MigrationPrimaryKey?> ReadPrimaryKeyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MigrationTable table,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT constraint_info.conname,
                   array_agg(attribute.attname ORDER BY key.ordinality)
            FROM pg_catalog.pg_constraint AS constraint_info
            JOIN pg_catalog.pg_class AS relation ON relation.oid = constraint_info.conrelid
            JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = relation.relnamespace
            JOIN LATERAL unnest(constraint_info.conkey) WITH ORDINALITY AS key(attnum, ordinality) ON true
            JOIN pg_catalog.pg_attribute AS attribute ON attribute.attrelid = relation.oid AND attribute.attnum = key.attnum
            WHERE constraint_info.contype = 'p'
              AND namespace.nspname = COALESCE($1, current_schema())
              AND relation.relname = $2
            GROUP BY constraint_info.conname
            """;
        await using var command = CreateTableCommand(connection, transaction, sql, table);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new MigrationPrimaryKey(reader.GetString(0), reader.GetFieldValue<string[]>(1));
    }

    private static async Task<IReadOnlyList<MigrationForeignKey>> ReadForeignKeysAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MigrationTable table,
        MigrationModel expectedModel,
        string currentSchema,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT constraint_info.conname,
                   array_agg(dependent_attribute.attname ORDER BY dependent_key.ordinality),
                   principal_schema.nspname,
                   principal_table.relname,
                   array_agg(principal_attribute.attname ORDER BY principal_key.ordinality),
                   constraint_info.confdeltype
            FROM pg_catalog.pg_constraint AS constraint_info
            JOIN pg_catalog.pg_class AS dependent_table ON dependent_table.oid = constraint_info.conrelid
            JOIN pg_catalog.pg_namespace AS dependent_schema ON dependent_schema.oid = dependent_table.relnamespace
            JOIN pg_catalog.pg_class AS principal_table ON principal_table.oid = constraint_info.confrelid
            JOIN pg_catalog.pg_namespace AS principal_schema ON principal_schema.oid = principal_table.relnamespace
            JOIN LATERAL unnest(constraint_info.conkey) WITH ORDINALITY AS dependent_key(attnum, ordinality) ON true
            JOIN LATERAL unnest(constraint_info.confkey) WITH ORDINALITY AS principal_key(attnum, ordinality)
              ON principal_key.ordinality = dependent_key.ordinality
            JOIN pg_catalog.pg_attribute AS dependent_attribute
              ON dependent_attribute.attrelid = dependent_table.oid AND dependent_attribute.attnum = dependent_key.attnum
            JOIN pg_catalog.pg_attribute AS principal_attribute
              ON principal_attribute.attrelid = principal_table.oid AND principal_attribute.attnum = principal_key.attnum
            WHERE constraint_info.contype = 'f'
              AND dependent_schema.nspname = COALESCE($1, current_schema())
              AND dependent_table.relname = $2
            GROUP BY constraint_info.conname, principal_schema.nspname, principal_table.relname, constraint_info.confdeltype
            ORDER BY constraint_info.conname
            """;
        await using var command = CreateTableCommand(connection, transaction, sql, table);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<MigrationForeignKey>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var principalSchema = reader.GetString(2);
            var principalTable = reader.GetString(3);
            var expectedPrincipal = expectedModel.Tables.FirstOrDefault(item =>
                item.Name == principalTable &&
                (item.Schema ?? currentSchema) == principalSchema);
            result.Add(new MigrationForeignKey(
                reader.GetString(0),
                reader.GetFieldValue<string[]>(1),
                expectedPrincipal?.Schema ?? principalSchema,
                principalTable,
                reader.GetFieldValue<string[]>(4),
                ToReferentialAction(reader.GetFieldValue<char>(5))));
        }

        return result;
    }

    private static async Task<IReadOnlyList<MigrationIndex>> ReadIndexesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MigrationTable table,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT index_class.relname,
                   index_info.indisunique,
                   array_remove(array_agg(CASE WHEN key_column.ordinality <= index_info.indnkeyatts THEN attribute.attname END ORDER BY key_column.ordinality), NULL),
                   array_remove(array_agg(CASE WHEN key_column.ordinality > index_info.indnkeyatts THEN attribute.attname END ORDER BY key_column.ordinality), NULL),
                   pg_catalog.pg_get_expr(index_info.indpred, index_info.indrelid),
                   access_method.amname
            FROM pg_catalog.pg_index AS index_info
            JOIN pg_catalog.pg_class AS table_class ON table_class.oid = index_info.indrelid
            JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = table_class.relnamespace
            JOIN pg_catalog.pg_class AS index_class ON index_class.oid = index_info.indexrelid
            JOIN pg_catalog.pg_am AS access_method ON access_method.oid = index_class.relam
            JOIN LATERAL unnest(index_info.indkey) WITH ORDINALITY AS key_column(attnum, ordinality)
              ON key_column.ordinality <= index_info.indnatts
            LEFT JOIN pg_catalog.pg_attribute AS attribute
              ON attribute.attrelid = table_class.oid AND attribute.attnum = key_column.attnum AND attribute.attnum > 0
            WHERE namespace.nspname = COALESCE($1, current_schema())
              AND table_class.relname = $2
              AND NOT index_info.indisprimary
              AND index_info.indexprs IS NULL
            GROUP BY index_class.relname, index_info.indisunique, index_info.indpred, index_info.indrelid, access_method.amname
            ORDER BY index_class.relname
            """;
        await using var command = CreateTableCommand(connection, transaction, sql, table);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<MigrationIndex>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new MigrationIndex(
                reader.GetString(0),
                reader.GetFieldValue<string[]>(2),
                reader.GetBoolean(1),
                reader.IsDBNull(3) ? [] : reader.GetFieldValue<string[]>(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                NormalizeIndexMethod(reader.GetString(5))));
        }

        return result;
    }

    private static NpgsqlCommand CreateTableCommand(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, MigrationTable table)
    {
        var command = new NpgsqlCommand(sql, connection);
        command.Transaction = transaction;
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = table.Schema is null ? DBNull.Value : table.Schema });
        command.Parameters.AddWithValue(table.Name);
        return command;
    }

    private static string? NormalizeIndexMethod(string method) => method == "btree" ? null : method;

    private static ReferentialAction ToReferentialAction(char value)
    {
        return value switch
        {
            'r' => ReferentialAction.Restrict,
            'c' => ReferentialAction.Cascade,
            'n' => ReferentialAction.SetNull,
            'd' => ReferentialAction.SetDefault,
            _ => ReferentialAction.NoAction
        };
    }

    private static string DisplayName(MigrationTable table) => table.Schema is null ? table.Name : table.Schema + "." + table.Name;
}
