namespace Perigon.PostgreSQL.Metadata;

public static class EntityValueAccessorRegistry
{
    private static readonly Dictionary<Type, IReadOnlyDictionary<string, Func<object, object?>>> Accessors = new();
    private static readonly Dictionary<Type, IReadOnlyDictionary<string, Action<object, object?>>> Setters = new();
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

    /// <summary>
    /// Registers source-generated property setters used by client-side value generation.
    /// Setters are emitted only for public, non-init properties so this remains AOT-safe.
    /// </summary>
    public static void RegisterSetters<T>(IReadOnlyDictionary<string, Action<T, object?>> setters)
        where T : class
    {
        var wrapped = setters.ToDictionary(
            pair => pair.Key,
            pair => new Action<object, object?>((entity, value) => pair.Value((T)entity, value)),
            StringComparer.Ordinal);

        lock (Sync)
        {
            Setters[typeof(T)] = wrapped;
        }
    }

    /// <summary>
    /// Assigns a generated value through a source-generated setter, falling back to
    /// the reflection path for non-generated metadata.
    /// </summary>
    public static void SetValue(ColumnModel column, object entity, object? value)
    {
        lock (Sync)
        {
            if (Setters.TryGetValue(entity.GetType(), out var setters) ||
                Setters.TryGetValue(column.DeclaringType, out setters))
            {
                if (setters.TryGetValue(column.PropertyName, out var setter))
                {
                    setter(entity, value);
                    return;
                }
                throw new InvalidOperationException(
                    $"Property '{entity.GetType().Name}.{column.PropertyName}' must have a public non-init setter for client-generated values.");
            }
        }

        if (column.Property.SetMethod is not { IsPublic: true } || IsInitOnly(column.Property))
        {
            throw new InvalidOperationException(
                $"Property '{column.DeclaringType.Name}.{column.PropertyName}' must have a public setter for client-generated values.");
        }

        column.Property.SetValue(entity, value);
    }

    private static bool IsInitOnly(System.Reflection.PropertyInfo property)
    {
        return property.SetMethod?.ReturnParameter
            .GetRequiredCustomModifiers()
            .Any(type => type == typeof(System.Runtime.CompilerServices.IsExternalInit)) == true;
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
        return TryGetAccessor(entity.GetType(), column.PropertyName, out var accessor) ||
            TryGetAccessor(column.DeclaringType, column.PropertyName, out accessor)
            ? accessor(entity)
            : column.Property.GetValue(entity);
    }

}
