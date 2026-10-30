using System.Linq.Expressions;

namespace Perigon.PostgreSQL;

/// <summary>
/// Optional context hook used to apply configured query filters to a query root.
/// Implementations must return an expression rooted at <paramref name="root"/>
/// and must not evaluate the filter on the client.
/// </summary>
public interface IQueryFilterContext
{
    Expression ApplyQueryFilters(Type entityType, Expression root);

    Expression ApplyQueryFilters(Type entityType, Expression root, IReadOnlySet<string> ignoredFilters);

    IDisposable PushIgnoredFilters(IReadOnlySet<string> ignoredFilters);
}

internal interface IQueryFilterSource
{
    IReadOnlySet<string> IgnoredFilters { get; }
}
