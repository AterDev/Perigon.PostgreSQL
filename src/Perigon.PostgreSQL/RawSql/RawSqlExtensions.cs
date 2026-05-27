using Perigon.PostgreSQL.Sql;
using Perigon.PostgreSQL.Execution;
using Perigon.PostgreSQL.Metadata;
using System.Diagnostics.CodeAnalysis;

namespace Perigon.PostgreSQL.RawSql;

public static class RawSqlExtensions
{
    public static BoundSql ToBoundSql<T>(this RawSqlQuery<T> query)
        where T : class
    {
        return RawSqlBuilder.Build(query.Sql);
    }

    public static BoundSql ToBoundSql(this RawSqlCommand command)
    {
        return RawSqlBuilder.Build(command.Sql);
    }

    public static Task<List<T>> ToListAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        this RawSqlQuery<T> query,
        CancellationToken cancellationToken = default)
        where T : class, new()
    {
        return CommandExecutor.ExecuteQueryAsync<T>(
            query.Context,
            query.Context.ResolveEntityModel(typeof(T)),
            query.ToBoundSql(),
            cancellationToken);
    }

    public static Task<int> ExecuteAsync(
        this RawSqlCommand command,
        CancellationToken cancellationToken = default)
    {
        return CommandExecutor.ExecuteNonQueryAsync(command.Context, command.ToBoundSql(), cancellationToken);
    }
}
