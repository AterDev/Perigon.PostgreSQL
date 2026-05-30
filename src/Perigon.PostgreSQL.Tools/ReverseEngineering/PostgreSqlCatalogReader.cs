using System;
using System.Collections.Generic;
using System.Globalization;
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
        var warnings = new List<string>();
        warnings.AddRange(await ReadUnsupportedIndexWarningsAsync(connection, cancellationToken).ConfigureAwait(false));

        var models = tables
            .Select(table => new TableModel(
                table.Schema,
                table.Name,
                table.IsView,
                table.Comment,
                columns.GetValueOrDefault((table.Schema, table.Name)) ?? [],
                primaryKeys.GetValueOrDefault((table.Schema, table.Name)) ?? [],
                foreignKeys.GetValueOrDefault((table.Schema, table.Name)) ?? [],
                indexes.GetValueOrDefault((table.Schema, table.Name)) ?? []))
            .ToArray();

        return new DatabaseModel(models, warnings);
    }

        private static async Task<IReadOnlyList<(string Schema, string Name, bool IsView, string? Comment)>> ReadTablesAsync(NpgsqlConnection connection, ScaffoldOptions options, CancellationToken cancellationToken)
    {
        const string sql = """
                        SELECT tables.table_schema,
                                     tables.table_name,
                                     tables.table_type,
                                     pg_catalog.obj_description(class_info.oid, 'pg_class') AS table_comment
                        FROM information_schema.tables AS tables
                        JOIN pg_catalog.pg_namespace AS namespace_info
                            ON namespace_info.nspname = tables.table_schema
                        JOIN pg_catalog.pg_class AS class_info
                            ON class_info.relnamespace = namespace_info.oid
                         AND class_info.relname = tables.table_name
                        WHERE tables.table_schema NOT IN ('pg_catalog', 'information_schema')
                            AND tables.table_type IN ('BASE TABLE', 'VIEW')
                        ORDER BY tables.table_schema, tables.table_name
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                var result = new List<(string Schema, string Name, bool IsView, string? Comment)>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var schema = reader.GetString(0);
            var name = reader.GetString(1);
            var isView = reader.GetString(2).Equals("VIEW", StringComparison.OrdinalIgnoreCase);
                        var comment = reader.IsDBNull(3) ? null : reader.GetString(3);
            if (options.ShouldIncludeSchema(schema) && options.ShouldIncludeTable(name) && (options.IncludeViews || !isView))
            {
                                result.Add((schema, name, isView, comment));
            }
        }

        return result;
    }

    private static async Task<Dictionary<(string Schema, string Table), IReadOnlyList<ColumnModel>>> ReadColumnsAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
                        SELECT columns.table_schema,
                                     columns.table_name,
                                     columns.column_name,
                                     columns.data_type,
                                     columns.udt_name,
                                     columns.is_nullable,
                                     columns.is_identity,
                                     columns.is_generated,
                                     columns.column_default,
                                     columns.character_maximum_length,
                                     columns.numeric_precision,
                                     columns.numeric_scale,
                                     columns.datetime_precision,
                                     column_comment.description,
                                     columns.generation_expression
                        FROM information_schema.columns AS columns
                        JOIN pg_catalog.pg_namespace AS namespace_info
                            ON namespace_info.nspname = columns.table_schema
                        JOIN pg_catalog.pg_class AS class_info
                            ON class_info.relnamespace = namespace_info.oid
                         AND class_info.relname = columns.table_name
                        JOIN pg_catalog.pg_attribute AS attribute_info
                            ON attribute_info.attrelid = class_info.oid
                         AND attribute_info.attname = columns.column_name
                         AND attribute_info.attnum > 0
                         AND NOT attribute_info.attisdropped
                        LEFT JOIN pg_catalog.pg_description AS column_comment
                            ON column_comment.objoid = class_info.oid
                         AND column_comment.objsubid = attribute_info.attnum
                        WHERE columns.table_schema NOT IN ('pg_catalog', 'information_schema')
                        ORDER BY columns.table_schema, columns.table_name, columns.ordinal_position
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
                reader.IsDBNull(8) ? null : reader.GetString(8),
                ReadNullableInt32(reader, 9),
                ReadColumnPrecision(reader),
                ReadNullableInt32(reader, 11),
                reader.IsDBNull(13) ? null : reader.GetString(13),
                reader.IsDBNull(14) ? null : reader.GetString(14)));
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
                                     constraint_info.conname AS constraint_name,
                                     array_agg(dependent_attribute.attname ORDER BY dependent_key.ordinality) AS column_names,
                                     principal_schema.nspname AS principal_schema,
                                     principal_table.relname AS principal_table,
                                     array_agg(principal_attribute.attname ORDER BY principal_key.ordinality) AS principal_column_names,
                                     constraint_info.confdeltype AS on_delete_action
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
                        GROUP BY dependent_schema.nspname, dependent_table.relname, constraint_info.conname, principal_schema.nspname, principal_table.relname, constraint_info.confdeltype
                        ORDER BY dependent_schema.nspname, dependent_table.relname, constraint_info.conname
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

            list.Add(new ForeignKeyModel(
                reader.GetString(2),
                reader.GetFieldValue<string[]>(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetFieldValue<string[]>(6),
                MapReferentialAction(reader.GetFieldValue<char>(7))));
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
                                     array_remove(array_agg(CASE WHEN key_column.ordinality <= index_info.indnkeyatts THEN attribute.attname END ORDER BY key_column.ordinality), NULL) AS column_names,
                                     array_remove(array_agg(CASE WHEN key_column.ordinality > index_info.indnkeyatts THEN attribute.attname END ORDER BY key_column.ordinality), NULL) AS include_column_names,
                                     pg_catalog.pg_get_expr(index_info.indpred, index_info.indrelid) AS filter_expression,
                                     access_method.amname AS method_name
                        FROM pg_catalog.pg_index index_info
                        JOIN pg_catalog.pg_class table_class
                            ON table_class.oid = index_info.indrelid
                        JOIN pg_catalog.pg_namespace ns
                            ON ns.oid = table_class.relnamespace
                        JOIN pg_catalog.pg_class index_class
                            ON index_class.oid = index_info.indexrelid
                        JOIN pg_catalog.pg_am access_method
                            ON access_method.oid = index_class.relam
                        JOIN LATERAL unnest(index_info.indkey) WITH ORDINALITY AS key_column(attnum, ordinality)
                            ON key_column.ordinality <= index_info.indnatts
                        LEFT JOIN pg_catalog.pg_attribute attribute
                            ON attribute.attrelid = table_class.oid
                         AND attribute.attnum = key_column.attnum
                         AND attribute.attnum > 0
                        WHERE ns.nspname NOT IN ('pg_catalog', 'information_schema')
                            AND NOT index_info.indisprimary
                            AND index_info.indexprs IS NULL
                        GROUP BY ns.nspname, table_class.relname, index_class.relname, index_info.indisunique, index_info.indpred, index_info.indrelid, access_method.amname
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

                        list.Add(new IndexModel(
                            reader.GetString(2),
                            reader.GetFieldValue<string[]>(4),
                            reader.GetBoolean(3),
                            reader.IsDBNull(5) ? [] : reader.GetFieldValue<string[]>(5),
                            reader.IsDBNull(6) ? null : reader.GetString(6),
                            reader.IsDBNull(7) ? null : reader.GetString(7)));
                }

                return result.ToDictionary(static item => item.Key, static item => (IReadOnlyList<IndexModel>)item.Value);
        }

    private static int? ReadColumnPrecision(NpgsqlDataReader reader)
    {
        var numericPrecision = ReadNullableInt32(reader, 10);
        if (numericPrecision is not null)
        {
            return numericPrecision;
        }

        return ReadNullableInt32(reader, 12);
    }

    private static int? ReadNullableInt32(NpgsqlDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        return Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    private static string MapReferentialAction(char action)
    {
        return action switch
        {
            'r' => "Restrict",
            'c' => "Cascade",
            'n' => "SetNull",
            'd' => "SetDefault",
            _ => "NoAction"
        };
    }

    private static async Task<IReadOnlyList<string>> ReadUnsupportedIndexWarningsAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT ns.nspname AS table_schema,
                   table_class.relname AS table_name,
                   index_class.relname AS index_name,
                   'expression index' AS reason
            FROM pg_catalog.pg_index index_info
            JOIN pg_catalog.pg_class table_class
              ON table_class.oid = index_info.indrelid
            JOIN pg_catalog.pg_namespace ns
              ON ns.oid = table_class.relnamespace
            JOIN pg_catalog.pg_class index_class
              ON index_class.oid = index_info.indexrelid
            WHERE ns.nspname NOT IN ('pg_catalog', 'information_schema')
              AND NOT index_info.indisprimary
                            AND index_info.indexprs IS NOT NULL
            ORDER BY ns.nspname, table_class.relname, index_class.relname
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var warnings = new List<string>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            warnings.Add($"Index '{reader.GetString(2)}' on table '{reader.GetString(0)}.{reader.GetString(1)}' is a {reader.GetString(3)} and is currently skipped by scaffolding.");
        }

        return warnings;
    }
}