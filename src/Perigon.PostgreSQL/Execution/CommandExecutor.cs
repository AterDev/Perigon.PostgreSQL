using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
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
        using var operation = context.EnterOperation();
        var stopwatch = Stopwatch.StartNew();
        LogExecuting(context, sql, "NonQuery");
        try
        {
            await using var command = context.CreateCommand(sql);
            var affectedRows = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            LogCompleted(context, sql, stopwatch, "NonQuery", affectedRows: affectedRows);
            return affectedRows;
        }
        catch (Exception exception)
        {
            LogFailed(context, sql, stopwatch, exception);
            throw;
        }
    }

    public static async Task<List<T>> ExecuteQueryAsync<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        DbContext context,
        EntityModel? model,
        BoundSql sql,
        CancellationToken cancellationToken)
        where T : class, new()
    {
        using var operation = context.EnterOperation();
        var stopwatch = Stopwatch.StartNew();
        LogExecuting(context, sql, "Query");
        try
        {
            await using var command = context.CreateCommand(sql);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var results = new List<T>();
            var nonNullableAggregateOrdinals = ResolveNonNullableAggregateOrdinals(reader, sql);
            if (EntityMaterializerRegistry.TryGetFactory<T>(out var generatedFactory) &&
                (model is null || CanUseGeneratedMaterializer<T>(model)))
            {
                var resultMaterializer = generatedFactory(reader);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    EnsureNonNullableAggregates(reader, nonNullableAggregateOrdinals);
                    results.Add(resultMaterializer(reader));
                }

                LogCompleted(context, sql, stopwatch, "Query", resultCount: results.Count);
                return results;
            }

            if (EntityMaterializerRegistry.TryGet<T>(out var generatedMaterializer) &&
                (model is null || CanUseGeneratedMaterializer<T>(model)))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    EnsureNonNullableAggregates(reader, nonNullableAggregateOrdinals);
                    results.Add(generatedMaterializer(reader));
                }

                LogCompleted(context, sql, stopwatch, "Query", resultCount: results.Count);
                return results;
            }

            model ??= context.ResolveEntityModel(typeof(T));
            var ordinals = BuildOrdinals(reader, model);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                EnsureNonNullableAggregates(reader, nonNullableAggregateOrdinals);
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

            LogCompleted(context, sql, stopwatch, "Query", resultCount: results.Count);
            return results;
        }
        catch (Exception exception)
        {
            LogFailed(context, sql, stopwatch, exception);
            throw;
        }
    }

    private static int[] ResolveNonNullableAggregateOrdinals(NpgsqlDataReader reader, BoundSql sql)
    {
        if (sql.NonNullableAggregateColumns.Count == 0)
        {
            return Array.Empty<int>();
        }

        return sql.NonNullableAggregateColumns.Select(reader.GetOrdinal).ToArray();
    }

    private static void EnsureNonNullableAggregates(NpgsqlDataReader reader, IReadOnlyList<int> ordinals)
    {
        foreach (var ordinal in ordinals)
        {
            if (reader.IsDBNull(ordinal))
            {
                throw new InvalidOperationException("Sequence contains no elements.");
            }
        }
    }

    private static bool CanUseGeneratedMaterializer<T>(EntityModel? model)
        where T : class, new()
    {
        if (model is null)
        {
            return false;
        }

        if (!EntityModelRegistry.TryGet(model.ClrType, out var defaultModel))
        {
            // DTO projections and Raw SQL result types do not need a DbSet-backed
            // EntityModel. Their generated materializer is the authoritative
            // mapping and must remain usable in NativeAOT.
            return true;
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

        return ConvertToTargetType(value, effectiveType);
    }

    public static async Task<T> ExecuteScalarAsync<T>(
        DbContext context,
        BoundSql sql,
        CancellationToken cancellationToken)
    {
        using var operation = context.EnterOperation();
        var stopwatch = Stopwatch.StartNew();
        LogExecuting(context, sql, "Scalar");
        try
        {
            await using var command = context.CreateCommand(sql);
            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (value is null || value is DBNull)
            {
                LogCompleted(context, sql, stopwatch, "Scalar", resultCount: 0);
                return default!;
            }

            var targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
            var result = (T)ConvertToTargetType(value, targetType);
            LogCompleted(context, sql, stopwatch, "Scalar", resultCount: 1);
            return result;
        }
        catch (Exception exception)
        {
            LogFailed(context, sql, stopwatch, exception);
            throw;
        }
    }

    public static async Task<List<T>> ExecuteScalarListAsync<T>(
        DbContext context,
        BoundSql sql,
        CancellationToken cancellationToken)
    {
        using var operation = context.EnterOperation();
        var stopwatch = Stopwatch.StartNew();
        LogExecuting(context, sql, "ScalarList");
        try
        {
            await using var command = context.CreateCommand(sql);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var results = new List<T>();
            var useTypedGetter = reader.GetFieldType(0) == typeof(T);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (await reader.IsDBNullAsync(0, cancellationToken).ConfigureAwait(false))
                {
                    results.Add(default!);
                    continue;
                }

                results.Add(ReadScalarValue<T>(reader, useTypedGetter));
            }

            LogCompleted(context, sql, stopwatch, "ScalarList", resultCount: results.Count);
            return results;
        }
        catch (Exception exception)
        {
            LogFailed(context, sql, stopwatch, exception);
            throw;
        }
    }

    private static void LogExecuting(DbContext context, BoundSql sql, string operation)
    {
        var logger = context.Logger;
        if (logger is null || !logger.IsEnabled(LogLevel.Debug))
        {
            return;
        }

        logger.LogDebug(
            "Executing PostgreSQL command {Operation}; CommandText={CommandText}; ParameterCount={ParameterCount}; ParameterTypes={ParameterTypes}; Parameters={Parameters}",
            operation,
            sql.CommandText,
            sql.Parameters.Count,
            string.Join(", ", sql.Parameters.Select(parameter =>
                parameter.DbType?.ToString() ?? parameter.Value?.GetType().FullName ?? "null")),
            context.SensitiveLoggingEnabled
                ? string.Join(", ", sql.Parameters.Select(parameter => parameter.Value ?? "<null>"))
                : "<redacted>");
    }

    private static void LogCompleted(
        DbContext context,
        BoundSql sql,
        Stopwatch stopwatch,
        string operation,
        int? resultCount = null,
        int? affectedRows = null)
    {
        context.Logger?.LogDebug(
            "Completed PostgreSQL command {Operation} in {ElapsedMilliseconds} ms; ResultCount={ResultCount}; AffectedRows={AffectedRows}; CommandText={CommandText}",
            operation,
            stopwatch.ElapsedMilliseconds,
            resultCount,
            affectedRows,
            sql.CommandText);
    }

    private static void LogFailed(DbContext context, BoundSql sql, Stopwatch stopwatch, Exception exception)
    {
        context.Logger?.LogError(
            exception,
            "PostgreSQL command failed after {ElapsedMilliseconds} ms; CommandText={CommandText}",
            stopwatch.ElapsedMilliseconds,
            sql.CommandText);
    }

    private static object ConvertToTargetType(object value, Type targetType)
    {
        if (targetType == typeof(DateTimeOffset) && value is DateTime dateTime)
        {
            if (dateTime.Kind == DateTimeKind.Local)
            {
                return new DateTimeOffset(dateTime);
            }

            var utc = dateTime.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)
                : dateTime;
            return new DateTimeOffset(utc, TimeSpan.Zero);
        }

        return Convert.ChangeType(value, targetType, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static T ReadScalarValue<T>(NpgsqlDataReader reader, bool useTypedGetter)
    {
        if (useTypedGetter)
        {
            try
            {
                return reader.GetFieldValue<T>(0);
            }
            catch (InvalidCastException)
            {
                // Preserve the existing conversion path for provider values whose
                // CLR type differs from the requested scalar type.
            }
        }

        var value = reader.GetValue(0);
        var targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        return (T)ConvertToTargetType(value, targetType);
    }

}
