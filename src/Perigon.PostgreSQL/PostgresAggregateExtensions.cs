namespace Perigon.PostgreSQL;

public static class PostgresAggregateExtensions
{
    public static int CountDistinct<TSource, TProperty>(
        this IEnumerable<TSource> source,
        Func<TSource, TProperty> selector)
    {
        throw new NotSupportedException("This method is only supported inside PostgreSQL query translation.");
    }

    public static long LongCountDistinct<TSource, TProperty>(
        this IEnumerable<TSource> source,
        Func<TSource, TProperty> selector)
    {
        throw new NotSupportedException("This method is only supported inside PostgreSQL query translation.");
    }

    public static TProperty[] ArrayAgg<TSource, TProperty>(
        this IEnumerable<TSource> source,
        Func<TSource, TProperty> selector)
    {
        throw new NotSupportedException("This method is only supported inside PostgreSQL query translation.");
    }

    public static string JsonbAgg<TSource, TProperty>(
        this IEnumerable<TSource> source,
        Func<TSource, TProperty> selector)
    {
        throw new NotSupportedException("This method is only supported inside PostgreSQL query translation.");
    }
}
