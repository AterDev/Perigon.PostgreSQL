using System.ComponentModel.DataAnnotations.Schema;
using Npgsql;
using Perigon.PostgreSQL;

namespace Perigon.PostgreSQL.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class ManyToManyIntegrationTests(DockerPostgresFixture fixture)
{
    [Fact]
    public async Task Loads_shared_children_and_preserves_parents_without_links()
    {
        await using var db = new ManyToManyDbContext(fixture.ConnectionString);
        await db.EnsureCreatedAsync();
        var first = await db.Parents.InsertAsync(new ManyParent { Name = "m2m-first" });
        var second = await db.Parents.InsertAsync(new ManyParent { Name = "m2m-second" });
        var empty = await db.Parents.InsertAsync(new ManyParent { Name = "m2m-empty" });
        var shared = await db.Children.InsertAsync(new ManyChild { Name = "shared" });
        var other = await db.Children.InsertAsync(new ManyChild { Name = "other" });
        await db.Links.InsertAsync(new ManyLink { ParentId = first.Id, ChildId = shared.Id });
        await db.Links.InsertAsync(new ManyLink { ParentId = first.Id, ChildId = other.Id });
        await db.Links.InsertAsync(new ManyLink { ParentId = second.Id, ChildId = shared.Id });

        var rows = await db.Parents.Where(p => p.Id == first.Id || p.Id == second.Id || p.Id == empty.Id)
            .IncludeAsync(db.Links, db.Children, p => p.Id, l => l.ParentId,
                l => l.ChildId, c => c.Id, (p, children) => (p, children));

        Assert.Equal(3, rows.Count);
        Assert.Equal(new[] { "other", "shared" }, rows.Single(row => row.p.Id == first.Id).children.Select(c => c.Name).Order().ToArray());
        Assert.Equal("shared", Assert.Single(rows.Single(row => row.p.Id == second.Id).children).Name);
        Assert.Empty(rows.Single(row => row.p.Id == empty.Id).children);

        var selectedName = "other";
        var filtered = await db.Parents.Where(p => p.Id == first.Id || p.Id == second.Id)
            .IncludeManyToManyAsync(db.Links, db.Children, p => p.Id, l => l.ParentId,
                l => l.ChildId, c => c.Id, c => c.Name == selectedName, (p, children) => (p, children));
        Assert.Equal("other", Assert.Single(filtered.Single(row => row.p.Id == first.Id).children).Name);
        Assert.Empty(filtered.Single(row => row.p.Id == second.Id).children);
    }

    [Fact]
    public async Task Fluent_join_entity_creates_two_foreign_keys_and_composite_key()
    {
        await using var db = new FluentManyToManyDbContext(fixture.ConnectionString);
        await db.EnsureCreatedAsync();

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT conname, pg_get_constraintdef(constraint_oid)
            FROM (
                SELECT c.conname, c.oid AS constraint_oid
                FROM pg_constraint c
                JOIN pg_class t ON t.oid = c.conrelid
                WHERE t.relname = 'm2m_fluent_enrollments'
                  AND c.contype IN ('f', 'p')
            ) constraints
            ORDER BY conname
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var definitions = new List<(string Name, string Definition)>();
        while (await reader.ReadAsync())
        {
            definitions.Add((reader.GetString(0), reader.GetString(1)));
        }
        await reader.DisposeAsync();

        Assert.Equal(3, definitions.Count);
        Assert.Contains(definitions, item => item.Definition.Contains("PRIMARY KEY (student_id, course_id)", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(definitions, item => item.Definition.Contains("REFERENCES m2m_fluent_students", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(definitions, item => item.Definition.Contains("REFERENCES m2m_fluent_courses", StringComparison.OrdinalIgnoreCase));

        await using var columnsCommand = new NpgsqlCommand("""
            SELECT a.attname
            FROM pg_attribute a
            JOIN pg_class t ON t.oid = a.attrelid
            WHERE t.relname = 'm2m_fluent_enrollments'
              AND a.attnum > 0
              AND NOT a.attisdropped
            ORDER BY a.attnum
            """, connection);
        await using var columnsReader = await columnsCommand.ExecuteReaderAsync();
        var columns = new List<string>();
        while (await columnsReader.ReadAsync())
        {
            columns.Add(columnsReader.GetString(0));
        }

        Assert.Equal(["student_id", "course_id"], columns);
    }
}

public sealed class ManyToManyDbContext(string connectionString) : DbContext(options => options.UsePostgres(connectionString))
{
    public DbSet<ManyParent> Parents => Set<ManyParent>();
    public DbSet<ManyLink> Links => Set<ManyLink>();
    public DbSet<ManyChild> Children => Set<ManyChild>();
}

[Table("m2m_test_parents")]
public sealed class ManyParent { public int Id { get; set; } public string Name { get; set; } = ""; }
[Table("m2m_test_children")]
public sealed class ManyChild { public int Id { get; set; } public string Name { get; set; } = ""; }
[Table("m2m_test_links")]
public sealed class ManyLink { public int Id { get; set; } public int ParentId { get; set; } public int ChildId { get; set; } }

public sealed class FluentManyToManyDbContext(string connectionString) : DbContext(options => options.UsePostgres(connectionString))
{
    public DbSet<FluentManyStudent> Students => Set<FluentManyStudent>();
    public DbSet<FluentManyCourse> Courses => Set<FluentManyCourse>();
    public DbSet<FluentManyEnrollment> Enrollments => Set<FluentManyEnrollment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FluentManyStudent>()
            .ToTable("m2m_fluent_students")
            .HasMany(student => student.Courses)
            .WithMany(course => course.Students)
            .UsingEntity<FluentManyEnrollment>(
                enrollment => enrollment.Student,
                enrollment => enrollment.Course,
                enrollment => enrollment.StudentId,
                enrollment => enrollment.CourseId)
            .ToTable("m2m_fluent_enrollments")
            .HasKey(enrollment => new { enrollment.StudentId, enrollment.CourseId });

        modelBuilder.Entity<FluentManyCourse>().ToTable("m2m_fluent_courses");
    }
}

public sealed class FluentManyStudent
{
    public int Id { get; set; }
    [NotMapped]
    public List<FluentManyCourse> Courses { get; set; } = [];
}

public sealed class FluentManyCourse
{
    public int Id { get; set; }
    [NotMapped]
    public List<FluentManyStudent> Students { get; set; } = [];
}

public sealed class FluentManyEnrollment
{
    public int StudentId { get; set; }
    public int CourseId { get; set; }
    [NotMapped]
    public FluentManyStudent? Student { get; set; }
    [NotMapped]
    public FluentManyCourse? Course { get; set; }
}
