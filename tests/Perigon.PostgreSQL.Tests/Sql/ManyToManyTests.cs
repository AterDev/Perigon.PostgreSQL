using System.ComponentModel.DataAnnotations.Schema;
using Perigon.PostgreSQL.Tests.Models;

namespace Perigon.PostgreSQL.Tests.Sql;

public sealed class ManyToManyTests
{
    [Fact]
    public void Child_filter_is_parameterized_after_the_key_array()
    {
        using var db = new TestDbContext();
        var selectedName = "selected";
        var sql = IncludeManyExtensions.BuildChildrenSql(db.Blogs, db.Blogs.Model.GetColumn(nameof(Blog.Id)),
            new[] { 3, 5 }, query => query.Where(blog => blog.Name == selectedName));

        Assert.Contains("e.\"id\" = ANY($1)", sql.CommandText);
        Assert.Contains("e.\"name\" = $2", sql.CommandText);
        Assert.Equal(new[] { 3, 5 }, Assert.IsType<int[]>(sql.Parameters[0].Value));
        Assert.Equal(selectedName, sql.Parameters[1].Value);
    }

    [Fact]
    public async Task Rejects_sets_from_different_contexts_before_executing()
    {
        using var first = new TestDbContext();
        using var second = new TestDbContext();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            first.RichUsers.IncludeManyToManyAsync(first.Blogs, second.RichUsers,
                user => user.Id, blog => blog.RichUserId, blog => blog.RichUserId,
                user => user.Id, (user, children) => children));
        Assert.Contains("same DbContext", error.Message);

        var unifiedError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            first.RichUsers.IncludeAsync(first.Blogs, second.RichUsers,
                user => user.Id, blog => blog.RichUserId, blog => blog.RichUserId,
                user => user.Id, (user, children) => children));
        Assert.Contains("same DbContext", unifiedError.Message);
    }

    [Fact]
    public async Task Rejects_non_direct_key_expression_before_executing()
    {
        using var db = new TestDbContext();
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            db.RichUsers.IncludeManyToManyAsync(db.Blogs, db.RichUsers,
                user => user.Id + 1, blog => blog.RichUserId, blog => blog.RichUserId,
                user => user.Id, (user, children) => children));
    }

    [Fact]
    public async Task Include_many_rejects_sets_from_different_contexts_before_executing()
    {
        using var first = new TestDbContext();
        using var second = new TestDbContext();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            first.RichUsers.IncludeManyAsync(second.Blogs,
                user => user.Id,
                blog => blog.RichUserId,
                (user, children) => children));

        Assert.Contains("same DbContext", error.Message);
    }

    [Fact]
    public async Task Include_many_rejects_nested_key_member_access()
    {
        using var db = new NestedKeyContext();

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            db.Parents.IncludeManyAsync(db.Children,
                parent => parent.Key.Id,
                child => child.ParentId,
                (parent, children) => children));

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            db.Parents.IncludeManyAsync(db.Children,
                parent => parent.Id,
                child => child.Key.Id,
                (parent, children) => children));
    }
}

public sealed class NestedKeyContext : DbContext
{
    public NestedKeyContext() : base(_ => { }) { }

    public DbSet<NestedKeyParent> Parents => Set<NestedKeyParent>();
    public DbSet<NestedKeyChild> Children => Set<NestedKeyChild>();
}

public sealed class NestedKeyParent
{
    public int Id { get; set; }

    [NotMapped]
    public NestedKeyValue Key { get; set; } = new();
}

public sealed class NestedKeyValue
{
    public int Id { get; set; }
}

public sealed class NestedKeyChild
{
    public int Id { get; set; }
    public int ParentId { get; set; }

    [NotMapped]
    public NestedKeyValue Key { get; set; } = new();
}
