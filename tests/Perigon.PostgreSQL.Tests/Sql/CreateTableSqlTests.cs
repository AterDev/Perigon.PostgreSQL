using Perigon.PostgreSQL.Metadata;
using Perigon.PostgreSQL.Sql;
using Perigon.PostgreSQL.Tests.Models;

namespace Perigon.PostgreSQL.Tests.Sql;

public sealed class CreateTableSqlTests
{
    [Fact]
    public void BuildCreateTable_generates_columns_primary_key_and_nullability()
    {
        var sql = CreateTableSqlBuilder.BuildCreateTable(EntityModel.For<ConventionUser>());

        Assert.Contains("CREATE TABLE IF NOT EXISTS \"convention_users\"", sql);
        Assert.Contains("\"id\" integer GENERATED ALWAYS AS IDENTITY NOT NULL", sql);
        Assert.Contains("\"user_name\" text NOT NULL", sql);
        Assert.Contains("\"status\" text", sql);
        Assert.Contains("\"tags\" text[]", sql);
        Assert.Contains("CONSTRAINT \"pk_convention_users\" PRIMARY KEY (\"id\")", sql);
    }

    [Fact]
    public void BuildCreateTable_uses_schema_and_explicit_column_names()
    {
        var sql = CreateTableSqlBuilder.BuildCreateTable(EntityModel.For<AttributedUser>());

        Assert.Contains("CREATE TABLE IF NOT EXISTS \"security\".\"app_users\"", sql);
        Assert.Contains("\"user_id\" integer GENERATED ALWAYS AS IDENTITY NOT NULL", sql);
        Assert.Contains("\"display_name\" text NOT NULL", sql);
        Assert.Contains("\"roles\" text[] NOT NULL", sql);
        Assert.Contains("CONSTRAINT \"pk_app_users\" PRIMARY KEY (\"user_id\")", sql);
    }

    [Fact]
    public void InferForeignKeys_detects_principal_type_id_convention()
    {
        var models = new[] { EntityModel.For<RichUser>(), EntityModel.For<Blog>() };

        var foreignKey = Assert.Single(RelationshipConventions.InferForeignKeys(models));

        Assert.Equal("fk_blogs_rich_user_id", foreignKey.ConstraintName);
        Assert.Equal(EntityModel.For<Blog>().TableName, foreignKey.DependentEntity.TableName);
        Assert.Equal("rich_user_id", foreignKey.DependentColumn.ColumnName);
        Assert.Equal(EntityModel.For<RichUser>().TableName, foreignKey.PrincipalEntity.TableName);
        Assert.Equal("id", foreignKey.PrincipalColumn.ColumnName);
    }

    [Fact]
    public void BuildAddForeignKey_generates_idempotent_constraint_sql()
    {
        var models = new[] { EntityModel.For<RichUser>(), EntityModel.For<Blog>() };
        var foreignKey = Assert.Single(RelationshipConventions.InferForeignKeys(models));

        var sql = CreateTableSqlBuilder.BuildAddForeignKey(foreignKey);

        Assert.Contains("DO $$", sql);
        Assert.Contains("IF NOT EXISTS", sql);
        Assert.Contains("conname = 'fk_blogs_rich_user_id'", sql);
        Assert.Contains("ALTER TABLE \"blogs\" ADD CONSTRAINT \"fk_blogs_rich_user_id\" FOREIGN KEY (\"rich_user_id\") REFERENCES \"rich_users\" (\"id\")", sql);
    }

    [Fact]
    public void InferForeignKeys_uses_fluent_relationship_configuration()
    {
        using var db = new FluentRelationshipDbContext();
        var models = db.ReadModels();

        var foreignKey = Assert.Single(RelationshipConventions.InferForeignKeys(models));

        Assert.Equal("fk_fluent_blogs_author_id", foreignKey.ConstraintName);
        Assert.Equal("author_id", foreignKey.DependentColumn.ColumnName);
        Assert.Equal("fluent_blogs", foreignKey.DependentEntity.TableName);
        Assert.Equal("fluent_users", foreignKey.PrincipalEntity.TableName);
    }

    [Fact]
    public void BuildAddForeignKey_uses_fluent_constraint_name()
    {
        using var db = new FluentRelationshipDbContext();
        var foreignKey = Assert.Single(RelationshipConventions.InferForeignKeys(db.ReadModels()));

        var sql = CreateTableSqlBuilder.BuildAddForeignKey(foreignKey);

        Assert.Contains("conname = 'fk_fluent_blogs_author_id'", sql);
        Assert.Contains("ALTER TABLE \"fluent_blogs\" ADD CONSTRAINT \"fk_fluent_blogs_author_id\" FOREIGN KEY (\"author_id\") REFERENCES \"fluent_users\" (\"id\")", sql);
    }

    [Fact]
    public void BuildCreateIndex_generates_unique_index_sql()
    {
        var model = EntityModel.For<IndexedUser>();
        var index = Assert.Single(model.Indexes);

        var sql = CreateTableSqlBuilder.BuildCreateIndex(index);

        Assert.Equal("CREATE UNIQUE INDEX IF NOT EXISTS \"uq_indexed_users_email\" ON \"indexed_users\" (\"email\")", sql);
    }

    [Fact]
    public void BuildCreateTable_supports_composite_primary_keys_default_sql_and_generated_column_sql()
    {
        using var db = new AdvancedSchemaDbContext();
        var model = Assert.Single(db.ReadModels(), static model => model.ClrType == typeof(AdvancedOrderLine));

        var sql = CreateTableSqlBuilder.BuildCreateTable(model);

        Assert.Contains("CONSTRAINT \"pk_advanced_order_lines\" PRIMARY KEY (\"order_id\", \"line_no\")", sql);
        Assert.Contains("\"status\" text DEFAULT 'draft' NOT NULL", sql);
        Assert.Contains("\"normalized_status\" text GENERATED ALWAYS AS (lower(status)) STORED", sql);
    }

    [Fact]
    public void BuildAddForeignKey_supports_composite_foreign_keys()
    {
        using var db = new AdvancedSchemaDbContext();
        var foreignKey = Assert.Single(RelationshipConventions.InferForeignKeys(db.ReadModels()), static foreignKey => foreignKey.ConstraintName == "fk_advanced_shipments_order_id_line_no");

        var sql = CreateTableSqlBuilder.BuildAddForeignKey(foreignKey);

        Assert.Contains("ALTER TABLE \"advanced_shipments\" ADD CONSTRAINT \"fk_advanced_shipments_order_id_line_no\" FOREIGN KEY (\"order_id\", \"line_no\") REFERENCES \"advanced_order_lines\" (\"order_id\", \"line_no\")", sql);
    }

    [Fact]
    public void BuildCreateIndex_supports_include_filter_and_method()
    {
        using var db = new AdvancedSchemaDbContext();
        var model = Assert.Single(db.ReadModels(), static model => model.ClrType == typeof(AdvancedOrderLine));
        var index = Assert.Single(model.Indexes, static index => index.IndexName == "ix_advanced_order_lines_status");

        var sql = CreateTableSqlBuilder.BuildCreateIndex(index);

        Assert.Equal("CREATE INDEX IF NOT EXISTS \"ix_advanced_order_lines_status\" ON \"advanced_order_lines\" USING gin (\"status\") INCLUDE (\"normalized_status\") WHERE status <> ''", sql);
    }

    [Fact]
    public void BuildCreateTable_rejects_generated_columns_without_sql_expression()
    {
        var model = EntityModel.For<ComputedColumnUser>();

        var error = Assert.Throws<InvalidOperationException>(() => CreateTableSqlBuilder.BuildCreateTable(model));

        Assert.Contains("Generated column", error.Message);
    }

    private sealed class ComputedColumnUser
    {
        public int Id { get; set; }

        [System.ComponentModel.DataAnnotations.Schema.DatabaseGenerated(System.ComponentModel.DataAnnotations.Schema.DatabaseGeneratedOption.Computed)]
        public string NormalizedName { get; set; } = "";
    }

    public sealed class FluentRelationshipDbContext : DbContext
    {
        public FluentRelationshipDbContext()
            : base(_ => { })
        {
        }

        public DbSet<FluentRelationshipUser> Users => Set<FluentRelationshipUser>();

        public DbSet<FluentRelationshipBlog> Blogs => Set<FluentRelationshipBlog>();

        public IReadOnlyList<EntityModel> ReadModels()
        {
            return GetEntityModels();
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<FluentRelationshipUser>().ToTable("fluent_users");
            modelBuilder.Entity<FluentRelationshipBlog>(entity =>
            {
                entity.ToTable("fluent_blogs");
                entity.Property(blog => blog.AuthorId).HasColumnName("author_id");
                entity.HasOne(blog => blog.Author)
                    .WithMany(user => user.Blogs)
                    .HasForeignKey(blog => blog.AuthorId)
                    .HasConstraintName("fk_fluent_blogs_author_id");
            });
        }
    }

    public sealed class FluentRelationshipUser
    {
        public int Id { get; set; }

        [System.ComponentModel.DataAnnotations.Schema.NotMapped]
        public List<FluentRelationshipBlog> Blogs { get; set; } = [];
    }

    public sealed class FluentRelationshipBlog
    {
        public int Id { get; set; }

        public int AuthorId { get; set; }

        [System.ComponentModel.DataAnnotations.Schema.NotMapped]
        public FluentRelationshipUser? Author { get; set; }
    }

    public sealed class AdvancedSchemaDbContext : DbContext
    {
        public AdvancedSchemaDbContext()
            : base(_ => { })
        {
        }

        public DbSet<AdvancedOrderLine> OrderLines => Set<AdvancedOrderLine>();

        public DbSet<AdvancedShipment> Shipments => Set<AdvancedShipment>();

        public IReadOnlyList<EntityModel> ReadModels()
        {
            return GetEntityModels();
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<AdvancedOrderLine>(entity =>
            {
                entity.ToTable("advanced_order_lines");
                entity.HasKey(x => new { x.OrderId, x.LineNo });
                entity.Property(x => x.Status)
                    .HasColumnName("status")
                    .HasDefaultSql("'draft'");
                entity.Property(x => x.NormalizedStatus)
                    .HasColumnName("normalized_status")
                    .HasGeneratedColumnSql("lower(status)");
                entity.HasIndex(x => x.Status)
                    .HasDatabaseName("ix_advanced_order_lines_status")
                    .IncludeProperties(x => x.NormalizedStatus)
                    .HasFilter("status <> ''")
                    .HasMethod("gin");
            });

            modelBuilder.Entity<AdvancedShipment>(entity =>
            {
                entity.ToTable("advanced_shipments");
                entity.HasOne(x => x.OrderLine)
                    .WithMany()
                    .HasForeignKey(x => new { x.OrderId, x.LineNo })
                    .HasConstraintName("fk_advanced_shipments_order_id_line_no");
            });
        }
    }

    public sealed class AdvancedOrderLine
    {
        public int OrderId { get; set; }

        public int LineNo { get; set; }

        public string Status { get; set; } = "";

        public string NormalizedStatus { get; set; } = "";
    }

    public sealed class AdvancedShipment
    {
        public int Id { get; set; }

        public int OrderId { get; set; }

        public int LineNo { get; set; }

        [System.ComponentModel.DataAnnotations.Schema.NotMapped]
        public AdvancedOrderLine? OrderLine { get; set; }
    }
}