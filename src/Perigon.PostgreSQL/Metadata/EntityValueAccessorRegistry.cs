namespace Perigon.PostgreSQL.Metadata;

public static class EntityValueAccessorRegistry
{
    private static readonly Dictionary<Type, IReadOnlyDictionary<string, Func<object, object?>>> Accessors = new();
    private static readonly object Sync = new();

    public static void Register<T>(IReadOnlyDictionary<string, Func<T, object?>> accessors)
        where T : class
    {
        var wrapped = accessors.ToDictionary(
            pair => pair.Key,
            pair => new Func<object, object?>(entity => pair.Value((T)entity)),
            StringComparer.Ordinal);

        lock (Sync)
        {
            Accessors[typeof(T)] = wrapped;
        }
    }

    public static bool TryGetAccessor(Type entityType, string propertyName, out Func<object, object?> accessor)
    {
        lock (Sync)
        {
            if (Accessors.TryGetValue(entityType, out var accessors) &&
                accessors.TryGetValue(propertyName, out accessor!))
            {
                return true;
            }
        }

        accessor = null!;
        return false;
    }

    public static object? GetValue(ColumnModel column, object entity)
    {
        return TryGetAccessor(column.DeclaringType, column.PropertyName, out var accessor)
            ? accessor(entity)
            : column.Property.GetValue(entity);
    }
}