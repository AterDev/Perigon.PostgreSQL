using System.Diagnostics.CodeAnalysis;
using Perigon.PostgreSQL.Sql;

namespace Perigon.PostgreSQL.NativeAotSmoke;

internal static class AotExpressionTreeSmoke
{
    // The C# compiler lowers object/anonymous projections in expression trees
    // through Expression.New/Bind, whose trimming annotations are conservative.
    // This smoke intentionally exercises that DTO expression-tree path; every
    // queried entity and projection type has source-generated metadata and no
    // runtime reflection activation is used by the query translator.
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "The expression-tree DTO projection is deliberately covered by this AOT smoke. Source-generated metadata roots all projected constructors and properties; the warning is from the conservative Expression.New/Bind annotations.")]
    public static BoundSql BuildProjectedSql(SmokeDbContext context, int minimumAge)
    {
        return context.Users
            .Where(user => user.Age > minimumAge)
            .Select(user => new { user.Id, user.UserName })
            .Where(projection => projection.Id > 0)
            .OrderBy(projection => projection.UserName)
            .Take(1)
            .ToQuerySql();
    }

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "The expression-tree DTO projection is deliberately covered by this AOT smoke. Source-generated metadata roots all projected constructors and properties; the warning is from the conservative Expression.New/Bind annotations.")]
    public static BoundSql BuildAggregateSql(SmokeDbContext context)
    {
        return context.Users.Select(user => new SmokeAggregateRow
        {
            Id = user.Id,
            Count = context.Users.LongCount(other => other.Id == user.Id),
            Count32 = context.Users.Count(other => other.Id == user.Id),
            Sum = context.Users.Where(other => other.Id == user.Id).Sum(other => other.Age),
            Minimum = context.Users.Where(other => other.Id == user.Id).Min(other => (int?)other.Age),
            Maximum = context.Users.Where(other => other.Id == user.Id).Max(other => (int?)other.Age),
            Average = context.Users.Where(other => other.Id == user.Id).Average(other => (int?)other.Age),
            MinimumRequired = context.Users.Where(other => other.Id == user.Id).Min(other => other.Age)
        }).ToQuerySql();
    }

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "The expression-tree DTO projection is deliberately covered by this AOT smoke. Source-generated metadata roots all projected constructors and properties; the warning is from the conservative Expression.New/Bind annotations.")]
    public static Task<List<SmokeAggregateRow>> LoadAggregatesAsync(SmokeDbContext context)
    {
        return context.Users
            .Where(user => user.UserName == "aot-live")
            .Select(user => new SmokeAggregateRow
            {
                Id = user.Id,
                Count = context.Users.LongCount(other => other.Id == user.Id),
                Count32 = context.Users.Count(other => other.Id == user.Id),
                Sum = context.Users.Where(other => other.Id == user.Id).Sum(other => other.Age),
                Minimum = context.Users.Where(other => other.Id == user.Id).Min(other => (int?)other.Age),
                Maximum = context.Users.Where(other => other.Id == user.Id).Max(other => (int?)other.Age),
                Average = context.Users.Where(other => other.Id == user.Id).Average(other => (int?)other.Age),
                MinimumRequired = context.Users.Where(other => other.Id == user.Id).Min(other => other.Age)
            })
            .ToListAsync();
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "The generated DTO materializer roots the members used by this expression-tree smoke test.")]
    public static Task<List<SmokeAggregateRow>> LoadEmptyAggregateAsync(SmokeDbContext context)
        => context.Users.Where(user => user.UserName == "aot-live")
            .Select(user => new SmokeAggregateRow
            {
                Id = user.Id,
                MinimumRequired = context.Users.Where(other => other.Id < 0).Min(other => other.Age)
            }).ToListAsync();
}
