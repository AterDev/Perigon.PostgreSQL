using System.Linq.Expressions;

namespace Perigon.PostgreSQL.Query;

internal sealed class PostgresQueryProvider : IQueryProvider
{
    private readonly DbContext _context;
    private readonly long _lease;
    private readonly object? _filterScope;
    public PostgresQueryProvider(DbContext context, object? filterScope)
    {
        _context = context;
        _lease = context.CapturePoolLease();
        _filterScope = filterScope;
    }

    public DbContext Context { get { ValidateLease(); return _context; } }

    internal void ValidateLease()
    {
        _context.ValidatePoolLease(_lease);
        _context.ValidateFilterScope(_filterScope);
    }

    public IQueryable CreateQuery(Expression expression)
    {
        throw new NotSupportedException("Non-generic query creation is not supported in NativeAOT mode.");
    }

    public IQueryable<TElement> CreateQuery<TElement>(Expression expression)
    {
        ValidateLease();
        return new PostgresQueryable<TElement>(this, expression);
    }

    public object? Execute(Expression expression)
    {
        throw new NotSupportedException("Synchronous query execution is not supported.");
    }

    public TResult Execute<TResult>(Expression expression)
    {
        throw new NotSupportedException("Synchronous query execution is not supported.");
    }
}
