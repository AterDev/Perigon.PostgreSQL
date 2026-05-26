namespace Perigon.PostgreSQL.RawSql;

public sealed class RawSqlCommand
{
    internal RawSqlCommand(DbContext context, FormattableString sql)
    {
        Context = context;
        Sql = sql;
    }

    public DbContext Context { get; }

    public FormattableString Sql { get; }
}
