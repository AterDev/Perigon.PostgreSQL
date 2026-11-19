using System.Collections;
using System.Linq.Expressions;
using Perigon.PostgreSQL.Metadata;
using Perigon.PostgreSQL.Query;

namespace Perigon.PostgreSQL;

internal interface IEntityModelSource
{
    EntityModel Model { get; }
}

public sealed class DbSet<T> : IOrderedQueryable<T>, IEntityModelSource, IQueryFilterSource where T : class
{
    private readonly PostgresQueryProvider? _provider;
    private readonly DbContext _context;
    private readonly EntityModel? _model;
    private readonly Expression? _expression;
    private readonly IReadOnlySet<string>? _ignoredFilters;
    private readonly Func<DbSet<T>>? _resolve;
    private readonly long _lease;

    internal DbSet(DbContext context, Func<DbSet<T>> resolve)
    {
        _context = context;
        _resolve = resolve;
        _lease = context.CapturePoolLease();
    }

    internal DbSet(DbContext context, EntityModel model)
        : this(context, model, applyFilters: true)
    {
    }

    internal DbSet(DbContext context, EntityModel model, bool applyFilters)
    {
        _context = context;
        _model = model;
        var filterScope = applyFilters ? context.CaptureFilterScope() : null;
        _ignoredFilters = applyFilters
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(["*"], StringComparer.Ordinal);
        var root = Expression.Constant(this);
        _expression = applyFilters
            ? context.ApplyQueryFilters<T>(root)
            : root;
        _provider = new PostgresQueryProvider(context, filterScope);
    }

    internal DbSet(DbContext context, EntityModel model, IReadOnlySet<string> ignoredFilters)
    {
        _context = context;
        _model = model;
        var filterScope = context.CaptureFilterScope();
        _ignoredFilters = ignoredFilters;
        var root = Expression.Constant(this);
        _expression = context.ApplyQueryFilters<T>(root, ignoredFilters);
        _provider = new PostgresQueryProvider(context, filterScope);
    }

    internal DbSet(DbContext context, EntityModel model, Expression expression)
    {
        _context = context;
        _model = model;
        var filterScope = context.CaptureFilterScope();
        _ignoredFilters = new HashSet<string>(StringComparer.Ordinal);
        _expression = expression;
        _provider = new PostgresQueryProvider(context, filterScope);
    }

    public DbContext Context { get { ValidateLease(); return _context; } }

    public EntityModel Model => Resolve() is { } set ? set.Model : _model!;

    IReadOnlySet<string> IQueryFilterSource.IgnoredFilters => IgnoredFilters;

    internal IReadOnlySet<string> IgnoredFilters => Resolve() is { } set ? set.IgnoredFilters : _ignoredFilters!;

    public Type ElementType => typeof(T);

    public Expression Expression => Resolve() is { } set ? set.Expression : _expression!;

    public IQueryProvider Provider => Resolve() is { } set ? set.Provider : _provider!;

    private DbSet<T>? Resolve()
    {
        ValidateLease();
        return _resolve?.Invoke();
    }

    private void ValidateLease()
    {
        if (_provider is not null)
            _provider.ValidateLease();
        else
        {
            _context.ValidatePoolLease(_lease);
            _context.ThrowIfDisposed();
        }
    }

    public IEnumerator<T> GetEnumerator()
    {
        throw new NotSupportedException("Synchronous client-side enumeration is not supported. Use ToListAsync.");
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}
