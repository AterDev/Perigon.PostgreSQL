using System.Collections;
using System.Linq.Expressions;

namespace Perigon.PostgreSQL.Query;

internal sealed class PostgresQueryable<T> : IOrderedQueryable<T>
{
    public PostgresQueryable(PostgresQueryProvider provider, Expression expression)
    {
        Provider = provider;
        Expression = expression;
    }

    public Type ElementType => typeof(T);

    public Expression Expression { get; }

    public IQueryProvider Provider { get; }

    public IEnumerator<T> GetEnumerator()
    {
        throw new NotSupportedException("Synchronous client-side enumeration is not supported. Use ToListAsync.");
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}
