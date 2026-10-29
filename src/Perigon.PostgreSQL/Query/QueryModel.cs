using System.Linq.Expressions;
using Perigon.PostgreSQL.Expressions;

namespace Perigon.PostgreSQL.Query;

internal sealed class QueryModel
{
    private bool _hasPrimaryOrdering;

    public List<LambdaExpression> Predicates { get; } = [];

    public List<OrderingModel> Orderings { get; } = [];

    public int? Skip { get; set; }

    public int? Take { get; set; }

    public void AddPredicate(LambdaExpression predicate)
    {
        if (Skip is not null || Take is not null)
        {
            throw new UnsupportedQueryExpressionException(
                "Where after Skip or Take is not supported because it changes query semantics.");
        }

        Predicates.Add(predicate);
    }

    public void AddOrdering(LambdaExpression selector, bool descending, bool primary)
    {
        if (Skip is not null || Take is not null)
        {
            throw new UnsupportedQueryExpressionException(
                "OrderBy after Skip or Take is not supported because it changes query semantics.");
        }

        if (primary && _hasPrimaryOrdering)
        {
            throw new UnsupportedQueryExpressionException(
                "Multiple OrderBy operations are not supported; use ThenBy for secondary ordering.");
        }

        if (!primary && !_hasPrimaryOrdering)
        {
            throw new UnsupportedQueryExpressionException(
                "ThenBy requires a preceding OrderBy operation.");
        }

        _hasPrimaryOrdering |= primary;
        Orderings.Add(new OrderingModel(selector, descending));
    }

    public void AddSkip(int value)
    {
        if (Skip is not null || Take is not null)
        {
            throw new UnsupportedQueryExpressionException(
                "Multiple or reordered Skip operations are not supported.");
        }

        Skip = value;
    }

    public void AddTake(int value)
    {
        if (Take is not null)
        {
            throw new UnsupportedQueryExpressionException(
                "Multiple or reordered Take operations are not supported.");
        }

        Take = value;
    }
}
