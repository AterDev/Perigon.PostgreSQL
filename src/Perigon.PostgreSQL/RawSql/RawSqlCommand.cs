namespace Perigon.PostgreSQL.RawSql;

public sealed class RawSqlCommand
{
    internal RawSqlCommand(PostgresDbContext context, FormattableString sql)
    {
        Context = context;
        Sql = sql;
    }

    public PostgresDbContext Context { get; }

    public FormattableString Sql { get; }
}
