namespace Perigon.PostgreSQL.Metadata;

/// <summary>Client-side values used by the insert pipeline. This does not track changes.</summary>
internal static class ValueGeneration
{
    internal static void PrepareInsert(EntityModel model, object entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var now = DateTimeOffset.UtcNow;
        foreach (var column in model.Columns)
        {
            if (!column.IsWritable)
                continue;
            var value = EntityValueAccessorRegistry.GetValue(column, entity);
            if (model.PrimaryKeys.Count == 1 && column.IsPrimaryKey && column.ClrType == typeof(Guid)
                && value is Guid guid && guid == Guid.Empty)
                EntityValueAccessorRegistry.SetValue(column, entity, Guid.CreateVersion7());
            else if (column.TimestampOnInsert && IsDefaultTimestamp(value))
                EntityValueAccessorRegistry.SetValue(column, entity, Timestamp(column, now));
        }
    }

    internal static object Timestamp(ColumnModel column, DateTimeOffset now) =>
        column.ClrType == typeof(DateTime) || column.ClrType == typeof(DateTime?) ? now.UtcDateTime : now;

    private static bool IsDefaultTimestamp(object? value) => value is null
        || value is DateTime dateTime && dateTime == default
        || value is DateTimeOffset offset && offset == default;
}
