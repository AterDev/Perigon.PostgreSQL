using Npgsql;
using Perigon.PostgreSQL.Metadata;
using Perigon.PostgreSQL.Sql;

namespace Perigon.PostgreSQL.Execution;

internal static class CommandExecutor
{
    public static async Task<int> ExecuteNonQueryAsync(
        DbContext context,
        BoundSql sql,
        CancellationToken cancellationToken)
    {
        await using var command = context.CreateCommand(sql);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<List<T>> ExecuteQueryAsync<T>(
        DbContext context,
        EntityModel model,
        BoundSql sql,
        CancellationToken cancellationToken)
        where T : class, new()
    {
        await using var command = context.CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<T>();
        if (CanUseGeneratedMaterializer<T>(model) && EntityMaterializerRegistry.TryGet<T>(out var generatedMaterializer))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                results.Add(generatedMaterializer(reader));
            }

            return results;
        }

        var ordinals = BuildOrdinals(reader, model);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var entity = new T();
            foreach (var (column, ordinal) in ordinals)
            {
                if (!column.Property.CanWrite || await reader.IsDBNullAsync(ordinal, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                var value = reader.GetValue(ordinal);
                column.Property.SetValue(entity, ConvertValue(value, column.Property.PropertyType));
            }

            results.Add(entity);
        }

        return results;
    }

    private static bool CanUseGeneratedMaterializer<T>(EntityModel model)
        where T : class, new()
    {
        if (!EntityModelRegistry.TryGet(model.ClrType, out var defaultModel))
        {
            return false;
        }

        if (model.Columns.Count != defaultModel.Columns.Count)
        {
            return false;
        }

        for (var i = 0; i < model.Columns.Count; i++)
        {
            var activeColumn = model.Columns[i];
            var defaultColumn = defaultModel.Columns[i];
            if (!string.Equals(activeColumn.PropertyName, defaultColumn.PropertyName, StringComparison.Ordinal) ||
                !string.Equals(activeColumn.ColumnName, defaultColumn.ColumnName, StringComparison.Ordinal) ||
                activeColumn.IsWritable != defaultColumn.IsWritable)
            {
                return false;
            }
        }

        return true;
    }

    private static List<(ColumnModel Column, int Ordinal)> BuildOrdinals(NpgsqlDataReader reader, EntityModel model)
    {
        var result = new List<(ColumnModel Column, int Ordinal)>(model.Columns.Count);
        foreach (var column in model.Columns)
        {
            var ordinal = TryGetOrdinal(reader, column.ColumnName);
            if (ordinal < 0)
            {
                ordinal = TryGetOrdinal(reader, column.PropertyName);
            }

            if (ordinal >= 0)
            {
                result.Add((column, ordinal));
            }
        }

        return result;
    }

    private static int TryGetOrdinal(NpgsqlDataReader reader, string name)
    {
        try
        {
            return reader.GetOrdinal(name);
        }
        catch (IndexOutOfRangeException)
        {
            return -1;
        }
    }

    private static object? ConvertValue(object value, Type targetType)
    {
        var effectiveType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (effectiveType.IsInstanceOfType(value))
        {
            return value;
        }

        if (effectiveType.IsEnum)
        {
            return Enum.ToObject(effectiveType, value);
        }

        return Convert.ChangeType(value, effectiveType, System.Globalization.CultureInfo.InvariantCulture);
    }

    public static async Task<T> ExecuteScalarAsync<T>(
        DbContext context,
        BoundSql sql,
        CancellationToken cancellationToken)
    {
        await using var command = context.CreateCommand(sql);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is null || value is DBNull)
        {
            return default!;
        }

        var targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        return (T)Convert.ChangeType(value, targetType, System.Globalization.CultureInfo.InvariantCulture);
    }

    public static async Task<List<T>> ExecuteScalarListAsync<T>(
        DbContext context,
        BoundSql sql,
        CancellationToken cancellationToken)
    {
        await using var command = context.CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<T>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.IsDBNullAsync(0, cancellationToken).ConfigureAwait(false))
            {
                results.Add(default!);
                continue;
            }

            var value = reader.GetValue(0);
            var targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
            results.Add((T)Convert.ChangeType(value, targetType, System.Globalization.CultureInfo.InvariantCulture));
        }

        return results;
    }

}
