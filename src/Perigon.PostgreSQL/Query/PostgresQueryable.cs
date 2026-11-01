using System.Collections;
using System.Linq.Expressions;

namespace Perigon.PostgreSQL.Query;

internal sealed class PostgresQueryable<T> : IOrderedQueryable<T>
{
    private readonly PostgresQueryProvider _provider;
    private readonly Expression _expression;
    public PostgresQueryable(PostgresQueryProvider provider, Expression expression)
    {
        _provider = provider;
        _expression = expression;
    }

    public Type ElementType => typeof(T);

    public Expression Expression { get { _provider.ValidateLease(); return _expression; } }

    public IQueryProvider Provider { get { _provider.ValidateLease(); return _provider; } }

    public IEnumerator<T> GetEnumerator()
    {
        throw new NotSupportedException("Synchronous client-side enumeration is not supported. Use ToListAsync.");
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}
