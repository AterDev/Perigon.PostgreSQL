using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace Perigon.PostgreSQL.Tools.ReverseEngineering;

public sealed class PostgreSqlCatalogReader
{
    private readonly string _connectionString;

    public PostgreSqlCatalogReader(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<DatabaseModel> ReadAsync(ScaffoldOptions options, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var tables = await ReadTablesAsync(connection, options, cancellationToken).ConfigureAwait(false);
        var columns = await ReadColumnsAsync(connection, cancellationToken).ConfigureAwait(false);
        var primaryKeys = await ReadPrimaryKeysAsync(connection, cancellationToken).ConfigureAwait(false);
        var foreignKeys = await ReadForeignKeysAsync(connection, cancellationToken).ConfigureAwait(false);
        var indexes = await ReadIndexesAsync(connection, cancellationToken).ConfigureAwait(false);

        var models = tables
            .Select(table => new TableModel(
                table.Schema,
                table.Name,
                table.IsView,
                columns.GetValueOrDefault((table.Schema, table.Name)) ?? [],
                primaryKeys.GetValueOrDefault((table.Schema, table.Name)) ?? [],
                foreignKeys.GetValueOrDefault((table.Schema, table.Name)) ?? [],
                indexes.GetValueOrDefault((table.Schema, table.Name)) ?? []))
            .ToArray();

        return new DatabaseModel(models);
    }

    private static async Task<IReadOnlyList<(string Schema, string Name, bool IsView)>> ReadTablesAsync(NpgsqlConnection connection, ScaffoldOptions options, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT table_schema, table_name, table_type
            FROM information_schema.tables
            WHERE table_schema NOT IN ('pg_catalog', 'information_schema')
              AND table_type IN ('BASE TABLE', 'VIEW')
            ORDER BY table_schema, table_name
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<(string Schema, string Name, bool IsView)>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var schema = reader.GetString(0);
            var name = reader.GetString(1);
            var isView = reader.GetString(2).Equals("VIEW", StringComparison.OrdinalIgnoreCase);
            if (options.ShouldIncludeSchema(schema) && options.ShouldIncludeTable(name) && (options.IncludeViews || !isView))
            {
                result.Add((schema, name, isView));
            }
        }

        return result;
    }

    private static async Task<Dictionary<(string Schema, string Table), IReadOnlyList<ColumnModel>>> ReadColumnsAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT table_schema, table_name, column_name, data_type, udt_name, is_nullable, is_identity, is_generated, column_default
            FROM information_schema.columns
            WHERE table_schema NOT IN ('pg_catalog', 'information_schema')
            ORDER BY table_schema, table_name, ordinal_position
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new Dictionary<(string Schema, string Table), List<ColumnModel>>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var key = (reader.GetString(0), reader.GetString(1));
            if (!result.TryGetValue(key, out var list))
            {
                list = [];
                result[key] = list;
            }

            list.Add(new ColumnModel(
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5).Equals("YES", StringComparison.OrdinalIgnoreCase),
                reader.GetString(6).Equals("YES", StringComparison.OrdinalIgnoreCase),
                !reader.GetString(7).Equals("NEVER", StringComparison.OrdinalIgnoreCase),
                reader.IsDBNull(8) ? null : reader.GetString(8)));
        }

        return result.ToDictionary(static item => item.Key, static item => (IReadOnlyList<ColumnModel>)item.Value);
    }

    private static async Task<Dictionary<(string Schema, string Table), IReadOnlyList<string>>> ReadPrimaryKeysAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT kcu.table_schema, kcu.table_name, kcu.column_name
            FROM information_schema.table_constraints tc
            JOIN information_schema.key_column_usage kcu
              ON tc.constraint_schema = kcu.constraint_schema
             AND tc.constraint_name = kcu.constraint_name
             AND tc.table_schema = kcu.table_schema
             AND tc.table_name = kcu.table_name
            WHERE tc.constraint_type = 'PRIMARY KEY'
            ORDER BY kcu.table_schema, kcu.table_name, kcu.ordinal_position
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new Dictionary<(string Schema, string Table), List<string>>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var key = (reader.GetString(0), reader.GetString(1));
            if (!result.TryGetValue(key, out var list))
            {
                list = [];
                result[key] = list;
            }

            list.Add(reader.GetString(2));
        }

        return result.ToDictionary(static item => item.Key, static item => (IReadOnlyList<string>)item.Value);
    }

    private static async Task<Dictionary<(string Schema, string Table), IReadOnlyList<ForeignKeyModel>>> ReadForeignKeysAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
                        SELECT dependent_schema.nspname AS table_schema,
                                     dependent_table.relname AS table_name,
                                     dependent_attribute.attname AS column_name,
                                     principal_schema.nspname AS principal_schema,
                                     principal_table.relname AS principal_table,
                                     principal_attribute.attname AS principal_column
                        FROM pg_catalog.pg_constraint constraint_info
                        JOIN pg_catalog.pg_class dependent_table
                            ON dependent_table.oid = constraint_info.conrelid
                        JOIN pg_catalog.pg_namespace dependent_schema
                            ON dependent_schema.oid = dependent_table.relnamespace
                        JOIN pg_catalog.pg_class principal_table
                            ON principal_table.oid = constraint_info.confrelid
                        JOIN pg_catalog.pg_namespace principal_schema
                            ON principal_schema.oid = principal_table.relnamespace
                        JOIN LATERAL unnest(constraint_info.conkey) WITH ORDINALITY AS dependent_key(attnum, ordinality)
                            ON true
                        JOIN LATERAL unnest(constraint_info.confkey) WITH ORDINALITY AS principal_key(attnum, ordinality)
                            ON principal_key.ordinality = dependent_key.ordinality
                        JOIN pg_catalog.pg_attribute dependent_attribute
                            ON dependent_attribute.attrelid = dependent_table.oid
                         AND dependent_attribute.attnum = dependent_key.attnum
                        JOIN pg_catalog.pg_attribute principal_attribute
                            ON principal_attribute.attrelid = principal_table.oid
                         AND principal_attribute.attnum = principal_key.attnum
                        WHERE constraint_info.contype = 'f'
                            AND dependent_schema.nspname NOT IN ('pg_catalog', 'information_schema')
                            AND array_length(constraint_info.conkey, 1) = 1
                            AND array_length(constraint_info.confkey, 1) = 1
                        ORDER BY dependent_schema.nspname, dependent_table.relname, dependent_attribute.attnum
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new Dictionary<(string Schema, string Table), List<ForeignKeyModel>>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var key = (reader.GetString(0), reader.GetString(1));
            if (!result.TryGetValue(key, out var list))
            {
                list = [];
                result[key] = list;
            }

            list.Add(new ForeignKeyModel(reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5)));
        }

        return result.ToDictionary(static item => item.Key, static item => (IReadOnlyList<ForeignKeyModel>)item.Value);
    }

        private static async Task<Dictionary<(string Schema, string Table), IReadOnlyList<IndexModel>>> ReadIndexesAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
        {
                const string sql = """
                        SELECT ns.nspname AS table_schema,
                                     table_class.relname AS table_name,
                                     index_class.relname AS index_name,
                                     index_info.indisunique,
                                     array_agg(attribute.attname ORDER BY key_column.ordinality) AS column_names
                        FROM pg_catalog.pg_index index_info
                        JOIN pg_catalog.pg_class table_class
                            ON table_class.oid = index_info.indrelid
                        JOIN pg_catalog.pg_namespace ns
                            ON ns.oid = table_class.relnamespace
                        JOIN pg_catalog.pg_class index_class
                            ON index_class.oid = index_info.indexrelid
                        JOIN LATERAL unnest(index_info.indkey) WITH ORDINALITY AS key_column(attnum, ordinality)
                            ON key_column.ordinality <= index_info.indnkeyatts
                        JOIN pg_catalog.pg_attribute attribute
                            ON attribute.attrelid = table_class.oid
                         AND attribute.attnum = key_column.attnum
                        WHERE ns.nspname NOT IN ('pg_catalog', 'information_schema')
                            AND NOT index_info.indisprimary
                            AND index_info.indpred IS NULL
                            AND index_info.indexprs IS NULL
                            AND index_info.indnatts = index_info.indnkeyatts
                        GROUP BY ns.nspname, table_class.relname, index_class.relname, index_info.indisunique
                        ORDER BY ns.nspname, table_class.relname, index_class.relname
                        """;
                await using var command = new NpgsqlCommand(sql, connection);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                var result = new Dictionary<(string Schema, string Table), List<IndexModel>>();
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                        var key = (reader.GetString(0), reader.GetString(1));
                        if (!result.TryGetValue(key, out var list))
                        {
                                list = [];
                                result[key] = list;
                        }

                        list.Add(new IndexModel(reader.GetString(2), reader.GetFieldValue<string[]>(4), reader.GetBoolean(3)));
                }

                return result.ToDictionary(static item => item.Key, static item => (IReadOnlyList<IndexModel>)item.Value);
        }
}