using System.Linq.Expressions;

namespace Perigon.PostgreSQL.Metadata;

/// <summary>
/// AOT-safe registry for statically generated navigation loaders.
/// Loaders are registered by generated or application code; lookup never scans assemblies.
/// </summary>
public static class NavigationIncludeRegistry
{
    private static readonly Dictionary<(Type ContextType, Type EntityType, string NavigationName), Delegate> Loaders = new();
    private static readonly object Sync = new();

    public static void Register<TEntity>(
        Type contextType,
        string navigationName,
        Func<DbContext, List<TEntity>, Expression?, CancellationToken, Task> loader)
        where TEntity : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(navigationName);
        ArgumentNullException.ThrowIfNull(loader);
        lock (Sync)
        {
            ArgumentNullException.ThrowIfNull(contextType);
            Loaders[(contextType, typeof(TEntity), navigationName)] = loader;
        }
    }

    public static Func<DbContext, List<TEntity>, Expression?, CancellationToken, Task> GetLoader<TEntity>(Type contextType, string navigationName)
        where TEntity : class
    {
        lock (Sync)
        {
            ArgumentNullException.ThrowIfNull(contextType);
            if (Loaders.TryGetValue((contextType, typeof(TEntity), navigationName), out var loader) &&
                loader is Func<DbContext, List<TEntity>, Expression?, CancellationToken, Task> typed)
            {
                return typed;
            }
        }

        throw new InvalidOperationException(
            $"No AOT navigation loader is registered for '{contextType.FullName}:{typeof(TEntity).FullName}.{navigationName}'.");
    }
}
