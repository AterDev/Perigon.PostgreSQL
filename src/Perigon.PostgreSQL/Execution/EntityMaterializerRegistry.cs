using Npgsql;

namespace Perigon.PostgreSQL.Execution;

public static class EntityMaterializerRegistry
{
    private static Dictionary<Type, Registration> Registrations = new();
    private static readonly object Sync = new();

    private sealed class Registration
    {
        public Registration(Delegate handler, bool isFactory)
        {
            Handler = handler;
            IsFactory = isFactory;
        }

        public Delegate Handler { get; }

        public bool IsFactory { get; }
    }

    public static void Register<T>(Func<NpgsqlDataReader, T> materializer)
        where T : class, new()
    {
        lock (Sync)
        {
            var updated = new Dictionary<Type, Registration>(Registrations)
            {
                [typeof(T)] = new Registration(materializer, isFactory: false)
            };
            Volatile.Write(ref Registrations, updated);
        }
    }

    /// <summary>
    /// Registers a result-set-aware materializer factory. The factory is called once
    /// per data reader so column ordinals can be resolved outside the row hot path.
    /// </summary>
    public static void RegisterFactory<T>(Func<NpgsqlDataReader, Func<NpgsqlDataReader, T>> factory)
        where T : class, new()
    {
        lock (Sync)
        {
            var updated = new Dictionary<Type, Registration>(Registrations)
            {
                [typeof(T)] = new Registration(factory, isFactory: true)
            };
            Volatile.Write(ref Registrations, updated);
        }
    }

    public static bool TryGet<T>(out Func<NpgsqlDataReader, T> materializer)
        where T : class, new()
    {
        var registrations = Volatile.Read(ref Registrations);
        if (registrations.TryGetValue(typeof(T), out var registration) &&
            !registration.IsFactory && registration.Handler is Func<NpgsqlDataReader, T> typed)
        {
            materializer = typed;
            return true;
        }

        // Preserve the original TryGet contract for generated registrations. The
        // adapter is intentionally only a compatibility surface; query execution
        // uses TryGetFactory so the result-set-aware path remains allocation-free
        // inside the row loop.
        if (registration is { IsFactory: true } &&
            registration.Handler is Func<NpgsqlDataReader, Func<NpgsqlDataReader, T>> factory)
        {
            materializer = reader => factory(reader)(reader);
            return true;
        }

        materializer = null!;
        return false;
    }

    public static bool TryGetFactory<T>(out Func<NpgsqlDataReader, Func<NpgsqlDataReader, T>> factory)
        where T : class, new()
    {
        var registrations = Volatile.Read(ref Registrations);
        if (registrations.TryGetValue(typeof(T), out var registration) &&
            registration.IsFactory &&
            registration.Handler is Func<NpgsqlDataReader, Func<NpgsqlDataReader, T>> typed)
        {
            factory = typed;
            return true;
        }

        factory = null!;
        return false;
    }
}
