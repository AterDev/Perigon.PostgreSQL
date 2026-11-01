namespace Perigon.PostgreSQL.RawSql;

public sealed class RawSqlQuery<T> where T : class
{
    private readonly DbContext _context;
    private readonly FormattableString _sql;
    private readonly long _lease;
    internal RawSqlQuery(DbContext context, FormattableString sql)
    {
        _context = context;
        _sql = sql;
        _lease = context.CapturePoolLease();
    }

    public DbContext Context { get { _context.ValidatePoolLease(_lease); return _context; } }

    public FormattableString Sql { get { _context.ValidatePoolLease(_lease); return _sql; } }
}
