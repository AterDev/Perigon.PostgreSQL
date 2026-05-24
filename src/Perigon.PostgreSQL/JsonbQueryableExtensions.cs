namespace Perigon.PostgreSQL;

public static class JsonbQueryableExtensions
{
    public static bool JsonbContains(this string? source, string json)
    {
        throw new NotSupportedException("This method is only supported inside PostgreSQL query translation.");
    }

    public static bool JsonbHasKey(this string? source, string key)
    {
        throw new NotSupportedException("This method is only supported inside PostgreSQL query translation.");
    }

    public static string? JsonbText(this string? source, string key)
    {
        throw new NotSupportedException("This method is only supported inside PostgreSQL query translation.");
    }

    public static bool JsonbPathExists(this string? source, string jsonPath)
    {
        throw new NotSupportedException("This method is only supported inside PostgreSQL query translation.");
    }
}
