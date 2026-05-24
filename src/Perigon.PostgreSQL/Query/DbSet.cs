using System.Collections;
using System.Linq.Expressions;
using Perigon.PostgreSQL.Metadata;
using Perigon.PostgreSQL.Query;

namespace Perigon.PostgreSQL;

public sealed class DbSet<T> : IOrderedQueryable<T> where T : class
{
    private readonly PostgresQueryProvider _provider;

    internal DbSet(PostgresDbContext context, EntityModel model)
    {
        Context = context;
        Model = model;
        Expression = Expression.Constant(this);
        _provider = new PostgresQueryProvider(context);
    }

    internal DbSet(PostgresDbContext context, EntityModel model, Expression expression)
    {
        Context = context;
        Model = model;
        Expression = expression;
        _provider = new PostgresQueryProvider(context);
    }

    public PostgresDbContext Context { get; }

    public EntityModel Model { get; }

    public Type ElementType => typeof(T);

    public Expression Expression { get; }

    public IQueryProvider Provider => _provider;

    public IEnumerator<T> GetEnumerator()
    {
        throw new NotSupportedException("Synchronous client-side enumeration is not supported. Use ToListAsync.");
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}
