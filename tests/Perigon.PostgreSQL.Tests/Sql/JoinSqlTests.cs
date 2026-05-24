using Perigon.PostgreSQL;

namespace Perigon.PostgreSQL.Tests.Sql;

public sealed class JoinSqlTests
{
    [Fact]
    public void Inner_join_with_projection_generates_join_sql()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers
            .Join(
                db.Blogs,
                u => u.Id,
                b => b.RichUserId,
                (u, b) => new { UserName = u.UserName, BlogName = b.Name })
            .ToSql();

        Assert.Equal(
            "SELECT o.\"user_name\" AS \"UserName\", i.\"name\" AS \"BlogName\" FROM \"rich_users\" AS o INNER JOIN \"blogs\" AS i ON o.\"id\" = i.\"rich_user_id\"",
            sql.CommandText);
    }

    [Fact]
    public void Inner_join_preserves_outer_where_parameters()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers
            .Where(u => u.Status == "active")
            .Join(
                db.Blogs,
                u => u.Id,
                b => b.RichUserId,
                (u, b) => new { u.Id, b.Name })
            .ToSql();

        Assert.Equal(
            "SELECT o.\"id\" AS \"Id\", i.\"name\" AS \"Name\" FROM \"rich_users\" AS o INNER JOIN \"blogs\" AS i ON o.\"id\" = i.\"rich_user_id\" WHERE (o.\"status\" = $1)",
            sql.CommandText);
        Assert.Equal("active", sql.Parameters[0].Value);
    }

    [Fact]
    public void Inner_join_preserves_inner_where_parameters()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers
            .Join(
                db.Blogs.Where(b => b.IsPublic),
                u => u.Id,
                b => b.RichUserId,
                (u, b) => new { u.UserName, BlogName = b.Name })
            .ToSql();

        Assert.Equal(
            "SELECT o.\"user_name\" AS \"UserName\", i.\"name\" AS \"BlogName\" FROM \"rich_users\" AS o INNER JOIN \"blogs\" AS i ON o.\"id\" = i.\"rich_user_id\" WHERE i.\"is_public\"",
            sql.CommandText);
    }

    [Fact]
    public void GroupJoin_selectmany_default_if_empty_generates_left_join_sql()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers
            .GroupJoin(
                db.Blogs,
                u => u.Id,
                b => b.RichUserId,
                (u, blogs) => new { u, blogs })
            .SelectMany(
                x => x.blogs.DefaultIfEmpty(),
                (x, b) => new { x.u.UserName, BlogName = b!.Name })
            .ToSql();

        Assert.Equal(
            "SELECT o.\"user_name\" AS \"UserName\", i.\"name\" AS \"BlogName\" FROM \"rich_users\" AS o LEFT JOIN \"blogs\" AS i ON o.\"id\" = i.\"rich_user_id\"",
            sql.CommandText);
    }

    [Fact]
    public void Left_join_preserves_outer_where()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers
            .Where(u => u.IsActive)
            .GroupJoin(
                db.Blogs,
                u => u.Id,
                b => b.RichUserId,
                (u, blogs) => new { u, blogs })
            .SelectMany(
                x => x.blogs.DefaultIfEmpty(),
                (x, b) => new { x.u.UserName, BlogName = b!.Name })
            .ToSql();

        Assert.Equal(
            "SELECT o.\"user_name\" AS \"UserName\", i.\"name\" AS \"BlogName\" FROM \"rich_users\" AS o LEFT JOIN \"blogs\" AS i ON o.\"id\" = i.\"rich_user_id\" WHERE o.\"is_active\"",
            sql.CommandText);
    }

    [Fact]
    public void Left_join_places_inner_where_in_join_condition()
    {
        using var db = new TestDbContext();

        var sql = db.RichUsers
            .GroupJoin(
                db.Blogs.Where(b => b.IsPublic),
                u => u.Id,
                b => b.RichUserId,
                (u, blogs) => new { u, blogs })
            .SelectMany(
                x => x.blogs.DefaultIfEmpty(),
                (x, b) => new { x.u.UserName, BlogName = b!.Name })
            .ToSql();

        Assert.Equal(
            "SELECT o.\"user_name\" AS \"UserName\", i.\"name\" AS \"BlogName\" FROM \"rich_users\" AS o LEFT JOIN \"blogs\" AS i ON o.\"id\" = i.\"rich_user_id\" AND i.\"is_public\"",
            sql.CommandText);
    }
}
