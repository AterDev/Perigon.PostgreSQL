namespace Perigon.PostgreSQL;

internal sealed class DbContextPool<TContext> : IDisposable where TContext : DbContext
{
    private readonly object _sync = new();
    private readonly Stack<TContext> _available = new();
    private readonly Func<TContext> _factory;
    private readonly int _capacity;
    private bool _disposed;

    public DbContextPool(Func<TContext> factory, int capacity)
    {
        _factory = factory;
        _capacity = capacity;
    }

    public TContext Rent()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_available.TryPop(out var existing))
            {
                existing.BeginPoolLease();
                return existing;
            }
        }

        var context = _factory();
        try { context.AttachPool(); }
        catch { context.Dispose(); throw; }
        lock (_sync)
        {
            if (_disposed)
            {
                context.Dispose();
                context.DestroyPooledWhenIdle();
                throw new ObjectDisposedException(nameof(DbContextPool<TContext>));
            }
        }
        return context;
    }

    public void Return(TContext context)
    {
        context.Dispose();
        try
        {
            if (!context.ResetForPool())
            {
                context.DestroyPooledWhenIdle();
                return;
            }
        }
        catch
        {
            context.DestroyPooledWhenIdle();
            throw;
        }

        lock (_sync)
        {
            if (!_disposed && _available.Count < _capacity)
            {
                _available.Push(context);
                return;
            }
        }

        context.DestroyPooledWhenIdle();
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            while (_available.TryPop(out var context))
                context.DestroyPooledWhenIdle();
        }
    }
}

// The scoped owner, rather than DbContext.Dispose(), returns the instance. This avoids
// re-renting a context before the original DI scope has finished disposing its services.
internal sealed class PooledContextScope<TContext> : IDisposable, IAsyncDisposable
    where TContext : DbContext
{
    private readonly DbContextPool<TContext> _pool;
    private int _disposed;

    public PooledContextScope(DbContextPool<TContext> pool)
    {
        _pool = pool;
        Context = pool.Rent();
    }

    public TContext Context { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _pool.Return(Context);
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
