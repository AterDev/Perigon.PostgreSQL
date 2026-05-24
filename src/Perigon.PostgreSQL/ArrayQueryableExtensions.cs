namespace Perigon.PostgreSQL;

public static class ArrayQueryableExtensions
{
    public static bool ContainsAll<T>(this IEnumerable<T> source, IEnumerable<T> values)
    {
        throw new NotSupportedException("This method is only supported inside PostgreSQL query translation.");
    }

    public static bool Overlaps<T>(this IEnumerable<T> source, IEnumerable<T> values)
    {
        throw new NotSupportedException("This method is only supported inside PostgreSQL query translation.");
    }

    public static bool IsContainedBy<T>(this IEnumerable<T> source, IEnumerable<T> values)
    {
        throw new NotSupportedException("This method is only supported inside PostgreSQL query translation.");
    }
}
