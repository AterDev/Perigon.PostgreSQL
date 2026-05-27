using System.Collections;
using System.Linq.Expressions;
using Perigon.PostgreSQL.Metadata;
using Perigon.PostgreSQL.Query;

namespace Perigon.PostgreSQL;

internal interface IEntityModelSource
{
    EntityModel Model { get; }
}

public sealed class DbSet<T> : IOrderedQueryable<T>, IEntityModelSource where T : class
{
    private readonly PostgresQueryProvider _provider;

    internal DbSet(DbContext context, EntityModel model)
    {
        Context = context;
        Model = model;
        Expression = Expression.Constant(this);
        _provider = new PostgresQueryProvider(context);
    }

    internal DbSet(DbContext context, EntityModel model, Expression expression)
    {
        Context = context;
        Model = model;
        Expression = expression;
        _provider = new PostgresQueryProvider(context);
    }

    public DbContext Context { get; }

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
