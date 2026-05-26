using System.Linq.Expressions;

namespace Perigon.PostgreSQL.Query;

internal sealed class PostgresQueryProvider : IQueryProvider
{
    public PostgresQueryProvider(DbContext context)
    {
        Context = context;
    }

    public DbContext Context { get; }

    public IQueryable CreateQuery(Expression expression)
    {
        throw new NotSupportedException("Non-generic query creation is not supported in NativeAOT mode.");
    }

    public IQueryable<TElement> CreateQuery<TElement>(Expression expression)
    {
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
