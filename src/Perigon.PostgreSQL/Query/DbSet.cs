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
    private readonly PostgresQueryProvider _provider;

    internal DbSet(DbContext context, EntityModel model)
        : this(context, model, applyFilters: true)
    {
    }

    internal DbSet(DbContext context, EntityModel model, bool applyFilters)
    {
        Context = context;
        Model = model;
        IgnoredFilters = applyFilters
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(["*"], StringComparer.Ordinal);
        var root = Expression.Constant(this);
        Expression = applyFilters && context is IQueryFilterContext filters
            ? filters.ApplyQueryFilters(typeof(T), root)
            : root;
        _provider = new PostgresQueryProvider(context);
    }

    internal DbSet(DbContext context, EntityModel model, IReadOnlySet<string> ignoredFilters)
    {
        Context = context;
        Model = model;
        IgnoredFilters = ignoredFilters;
        var root = Expression.Constant(this);
        Expression = context is IQueryFilterContext filters
            ? filters.ApplyQueryFilters(typeof(T), root, ignoredFilters)
            : root;
        _provider = new PostgresQueryProvider(context);
    }

    internal DbSet(DbContext context, EntityModel model, Expression expression)
    {
        Context = context;
        Model = model;
        IgnoredFilters = new HashSet<string>(StringComparer.Ordinal);
        Expression = expression;
        _provider = new PostgresQueryProvider(context);
    }

    public DbContext Context { get; }

    public EntityModel Model { get; }

    IReadOnlySet<string> IQueryFilterSource.IgnoredFilters => IgnoredFilters;

    internal IReadOnlySet<string> IgnoredFilters { get; }

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
