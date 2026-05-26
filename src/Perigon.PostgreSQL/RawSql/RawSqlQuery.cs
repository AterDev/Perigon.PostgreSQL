namespace Perigon.PostgreSQL.RawSql;

public sealed class RawSqlQuery<T> where T : class
{
    internal RawSqlQuery(DbContext context, FormattableString sql)
    {
        Context = context;
        Sql = sql;
    }

    public DbContext Context { get; }

    public FormattableString Sql { get; }
}
