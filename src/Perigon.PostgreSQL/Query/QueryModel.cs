using System.Linq.Expressions;

namespace Perigon.PostgreSQL.Query;

internal sealed class QueryModel
{
    public List<LambdaExpression> Predicates { get; } = [];

    public List<OrderingModel> Orderings { get; } = [];

    public int? Skip { get; set; }

    public int? Take { get; set; }
}
