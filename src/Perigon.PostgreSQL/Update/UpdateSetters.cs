using System.Linq.Expressions;

namespace Perigon.PostgreSQL.Update;

public sealed class UpdateSetters<T> where T : class
{
    public UpdateSetters<T> Set<TProperty>(Expression<Func<T, TProperty>> property, TProperty value)
    {
        throw new NotSupportedException("UpdateSetters is only used as an expression DSL.");
    }

    public UpdateSetters<T> SetExpression<TProperty>(
        Expression<Func<T, TProperty>> property,
        Expression<Func<T, TProperty>> valueExpression)
    {
        throw new NotSupportedException("UpdateSetters is only used as an expression DSL.");
    }
}
