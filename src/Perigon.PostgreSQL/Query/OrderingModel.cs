using System.Linq.Expressions;

namespace Perigon.PostgreSQL.Query;

internal sealed record OrderingModel(LambdaExpression KeySelector, bool Descending);
