using Npgsql;

namespace Perigon.PostgreSQL.Execution;

public static class EntityMaterializerRegistry
{
    private static readonly Dictionary<Type, Delegate> Materializers = new();
    private static readonly object Sync = new();

    public static void Register<T>(Func<NpgsqlDataReader, T> materializer)
        where T : class, new()
    {
        lock (Sync)
        {
            Materializers[typeof(T)] = materializer;
        }
    }

    public static bool TryGet<T>(out Func<NpgsqlDataReader, T> materializer)
        where T : class, new()
    {
        lock (Sync)
        {
            if (Materializers.TryGetValue(typeof(T), out var candidate) && candidate is Func<NpgsqlDataReader, T> typed)
            {
                materializer = typed;
                return true;
            }
        }

        materializer = null!;
        return false;
    }
}