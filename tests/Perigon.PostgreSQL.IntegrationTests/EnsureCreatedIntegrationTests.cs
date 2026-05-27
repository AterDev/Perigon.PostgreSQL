using Npgsql;
using Perigon.PostgreSQL;
using Perigon.PostgreSQL.Attributes;
using Perigon.PostgreSQL.Execution;
using Perigon.PostgreSQL.RawSql;

namespace Perigon.PostgreSQL.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class EnsureCreatedIntegrationTests
{
    private readonly DockerPostgresFixture _fixture;

    public EnsureCreatedIntegrationTests(DockerPostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task EnsureCreated_creates_tables_and_convention_foreign_keys_idempotently()
    {
        await using var db = new EnsureCreatedDbContext(_fixture.ConnectionString);

        await db.EnsureCreatedAsync();
        await db.EnsureCreatedAsync();

        var user = await db.EnsureCreatedUsers.InsertAsync(new EnsureCreatedUser
        {
            UserName = "EnsureCreated Alice"
        });

        var blog = await db.EnsureCreatedBlogs.InsertAsync(new EnsureCreatedBlog
        {
            EnsureCreatedUserId = user.Id,
            Name = "Created by EnsureCreated"
        });

        var blogs = await db.EnsureCreatedBlogs.Where(item => item.Id == blog.Id).ToListAsync();
        Assert.Single(blogs);

        var error = await Assert.ThrowsAsync<PostgresException>(() =>
            db.EnsureCreatedBlogs.InsertAsync(new EnsureCreatedBlog
            {
                EnsureCreatedUserId = -1,
                Name = "Invalid FK"
            }));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);

        var unique = await Assert.ThrowsAsync<PostgresException>(() =>
            db.EnsureCreatedUsers.InsertAsync(new EnsureCreatedUser
            {
                UserName = "EnsureCreated Alice"
            }));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, unique.SqlState);
    }

    [Fact]
    public async Task EnsureCreated_creates_tables_and_fluent_foreign_keys_with_constraint_name()
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using (var cleanup = connection.CreateCommand())
        {
            cleanup.CommandText = "DROP SCHEMA IF EXISTS ensure_created_fluent_fk CASCADE;";
            await cleanup.ExecuteNonQueryAsync();
        }

        await using var db = new FluentForeignKeyDbContext(_fixture.ConnectionString);
        await db.EnsureCreatedAsync();

        var user = await db.Users.InsertAsync(new FluentForeignKeyUser
        {
            Name = "Fluent Alice"
        });

        var post = await db.Posts.InsertAsync(new FluentForeignKeyPost
        {
            AuthorId = user.Id,
            Title = "Created with fluent fk"
        });

        var posts = await db.Posts.Where(item => item.Id == post.Id).ToListAsync();
        Assert.Single(posts);

        var error = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Posts.InsertAsync(new FluentForeignKeyPost
            {
                AuthorId = -1,
                Title = "Invalid fluent fk"
            }));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM pg_constraint constraint_item
            INNER JOIN pg_class table_item ON table_item.oid = constraint_item.conrelid
            INNER JOIN pg_namespace schema_item ON schema_item.oid = table_item.relnamespace
            WHERE schema_item.nspname = 'ensure_created_fluent_fk'
              AND table_item.relname = 'fluent_posts'
              AND constraint_item.conname = 'fk_fluent_posts_author_id'
            """;
        var count = (long)(await command.ExecuteScalarAsync() ?? 0L);
        Assert.Equal(1L, count);
    }

    [Fact]
    public async Task Raw_sql_query_materializes_using_context_level_fluent_column_mapping()
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using (var cleanup = connection.CreateCommand())
        {
            cleanup.CommandText = "DROP SCHEMA IF EXISTS ensure_created_raw_sql_fluent CASCADE;";
            await cleanup.ExecuteNonQueryAsync();
        }

        await using var db = new FluentRawSqlDbContext(_fixture.ConnectionString);
        await db.EnsureCreatedAsync();

        var inserted = await db.Users.InsertAsync(new FluentRawSqlUser
        {
            Name = "Raw Fluent Alice"
        });

        var users = await db
            .SqlQuery<FluentRawSqlUser>($"""
                select id, full_name
                from ensure_created_raw_sql_fluent.raw_sql_users
                where id = {inserted.Id}
                """)
            .ToListAsync();

        var user = Assert.Single(users);
        Assert.Equal(inserted.Id, user.Id);
        Assert.Equal("Raw Fluent Alice", user.Name);
    }
}

public sealed class EnsureCreatedDbContext : DbContext
{
    public EnsureCreatedDbContext(string connectionString)
        : base(builder => builder.UsePostgres(connectionString))
    {
    }

    public DbSet<EnsureCreatedUser> EnsureCreatedUsers => Set<EnsureCreatedUser>();

    public DbSet<EnsureCreatedBlog> EnsureCreatedBlogs => Set<EnsureCreatedBlog>();
}

[Index(nameof(UserName), Name = "uq_ensure_created_users_user_name", IsUnique = true)]
public sealed class EnsureCreatedUser
{
    public int Id { get; set; }

    public string UserName { get; set; } = "";
}

public sealed class EnsureCreatedBlog
{
    public int Id { get; set; }

    public int EnsureCreatedUserId { get; set; }

    public string Name { get; set; } = "";
}

public sealed class FluentForeignKeyDbContext : DbContext
{
    public FluentForeignKeyDbContext(string connectionString)
        : base(builder => builder.UsePostgres(connectionString))
    {
    }

    public DbSet<FluentForeignKeyUser> Users => Set<FluentForeignKeyUser>();

    public DbSet<FluentForeignKeyPost> Posts => Set<FluentForeignKeyPost>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FluentForeignKeyUser>(entity => entity.ToTable("fluent_users", "ensure_created_fluent_fk"));
        modelBuilder.Entity<FluentForeignKeyPost>(entity =>
        {
            entity.ToTable("fluent_posts", "ensure_created_fluent_fk");
            entity.Property(post => post.AuthorId).HasColumnName("author_id");
            entity.HasOne(post => post.Author)
                .WithMany(user => user.Posts)
                .HasForeignKey(post => post.AuthorId)
                .HasConstraintName("fk_fluent_posts_author_id");
        });
    }
}

public sealed class FluentRawSqlDbContext : DbContext
{
    public FluentRawSqlDbContext(string connectionString)
        : base(builder => builder.UsePostgres(connectionString))
    {
    }

    public DbSet<FluentRawSqlUser> Users => Set<FluentRawSqlUser>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FluentRawSqlUser>(entity =>
        {
            entity.ToTable("raw_sql_users", "ensure_created_raw_sql_fluent");
            entity.Property(user => user.Name).HasColumnName("full_name");
        });
    }
}

public sealed class FluentForeignKeyUser
{
    public int Id { get; set; }

    public string Name { get; set; } = "";

    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public List<FluentForeignKeyPost> Posts { get; set; } = [];
}

public sealed class FluentForeignKeyPost
{
    public int Id { get; set; }

    public int AuthorId { get; set; }

    public string Title { get; set; } = "";

    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public FluentForeignKeyUser? Author { get; set; }
}

public sealed class FluentRawSqlUser
{
    public int Id { get; set; }

    public string Name { get; set; } = "";
}