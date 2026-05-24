namespace Perigon.PostgreSQL.RawSql;

public sealed class RawSqlQuery<T> where T : class
{
    internal RawSqlQuery(PostgresDbContext context, FormattableString sql)
    {
        Context = context;
        Sql = sql;
    }

    public PostgresDbContext Context { get; }

    public FormattableString Sql { get; }
}
