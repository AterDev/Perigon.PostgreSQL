using Perigon.PostgreSQL.Metadata;
using Perigon.PostgreSQL.Migrations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Perigon.PostgreSQL.Tests.Migrations;

public sealed class MigrationModelTests
{
    [Fact]
    public void JsonRoundTrip_IsStableAndCanonicalizesNestedCollections()
    {
        var first = new MigrationModel(
        [
            new MigrationTable("public", "z_table", null,
                [Column("z"), Column("a")], null, [], []),
            new MigrationTable("public", "a_table", null,
                [Column("id")], null, [], [])
        ]);
        var reordered = new MigrationModel(
        [
            new MigrationTable("public", "a_table", null,
                [Column("id")], null, [], []),
            new MigrationTable("public", "z_table", null,
                [Column("a"), Column("z")], null, [], [])
        ]);

        var json = first.ToJson();

        Assert.Equal(json, reordered.ToJson());
        Assert.Equal(json, MigrationModel.FromJson(json).ToJson());
    }

    [Fact]
    public void ModelConversion_PreservesGeneratedFlagWithoutSqlExpression()
    {
        var model = MigrationModel.FromEntityModels([EntityModel.For<ComputedWithoutSqlEntity>()]);

        var column = Assert.Single(Assert.Single(model.Tables).Columns);
        Assert.True(column.IsGenerated);
        Assert.Null(column.GeneratedColumnSql);
    }

    [Fact]
    public void Diff_FromEmpty_CreatesTableAndDownDropsIt()
    {
        var model = Model(Table("users", [Column("id", "integer", nullable: false)],
            primaryKey: new MigrationPrimaryKey("pk_users", ["id"])));

        var diff = MigrationModelDiffer.Diff(MigrationModel.Empty, model);

        Assert.Contains(diff.Up, command => command.Sql.Contains("CREATE TABLE \"public\".\"users\"", StringComparison.Ordinal));
        Assert.Contains(diff.Up, command => command.Sql.Contains("CONSTRAINT \"pk_users\" PRIMARY KEY (\"id\")", StringComparison.Ordinal));
        Assert.Equal("DROP TABLE \"public\".\"users\"", diff.Down[^1].Sql);
        Assert.Empty(diff.Warnings);
    }

    [Fact]
    public void Diff_ColumnAndTypeChanges_ProducesForwardAndReverseSqlWithWarnings()
    {
        var oldModel = Model(Table("users", [Column("id", "integer", nullable: false), Column("name", "text")]));
        var newModel = Model(Table("users", [Column("id", "bigint", nullable: false), Column("email", "text", nullable: false)]));

        var diff = MigrationModelDiffer.Diff(oldModel, newModel);

        Assert.Contains(diff.Up, command => command.Sql.Contains("ALTER COLUMN \"id\" TYPE bigint USING \"id\"::bigint", StringComparison.Ordinal));
        Assert.Contains(diff.Up, command => command.Sql.Contains("DROP COLUMN \"name\"", StringComparison.Ordinal));
        Assert.Contains(diff.Up, command => command.Sql.Contains("ADD COLUMN \"email\" text NOT NULL", StringComparison.Ordinal));
        Assert.Contains(diff.Down, command => command.Sql.Contains("TYPE integer", StringComparison.Ordinal));
        Assert.Contains(diff.Down, command => command.Sql.Contains("ADD COLUMN \"name\" text", StringComparison.Ordinal));
        Assert.Contains(diff.Warnings, warning => warning.Contains("may be a rename", StringComparison.Ordinal));
        Assert.Contains(diff.Warnings, warning => warning.Contains("loses its data", StringComparison.Ordinal));
        Assert.Contains(diff.Warnings, warning => warning.Contains("can fail when the table contains rows", StringComparison.Ordinal));
    }

    [Fact]
    public void Diff_UpdatesDefaultNullabilityCommentsAndIndexes()
    {
        var oldTable = Table("events",
            [Column("id", "integer", nullable: true, defaultSql: "0", comment: "old")],
            indexes: [new MigrationIndex("ix_events_id", ["id"], false, [], null, null)]);
        var newTable = Table("events",
            [Column("id", "integer", nullable: false, defaultSql: "1", comment: "new")],
            comment: "event table",
            indexes: [new MigrationIndex("ix_events_id", ["id"], true, [], null, "gin")]);

        var diff = MigrationModelDiffer.Diff(Model(oldTable), Model(newTable));

        Assert.Contains(diff.Up, command => command.Sql.Contains("SET NOT NULL", StringComparison.Ordinal));
        Assert.Contains(diff.Up, command => command.Sql.Contains("SET DEFAULT 1", StringComparison.Ordinal));
        Assert.Contains(diff.Up, command => command.Sql.Contains("COMMENT ON TABLE \"public\".\"events\" IS 'event table'", StringComparison.Ordinal));
        Assert.Contains(diff.Up, command => command.Sql.Contains("DROP INDEX IF EXISTS \"public\".\"ix_events_id\"", StringComparison.Ordinal));
        Assert.Contains(diff.Up, command => command.Sql.Contains("USING gin", StringComparison.Ordinal));
        Assert.Contains(diff.Down, command => command.Sql.Contains("DROP NOT NULL", StringComparison.Ordinal));
        Assert.Contains(diff.Down, command => command.Sql.Contains("SET DEFAULT 0", StringComparison.Ordinal));
        Assert.Contains(diff.Warnings, warning => warning.Contains("NOT NULL", StringComparison.Ordinal));
    }

    [Fact]
    public void Diff_ChangesForeignKeyBeforeReferencedPrimaryKey()
    {
        var oldPrincipal = Table("users", [Column("id", "integer", nullable: false)],
            primaryKey: new MigrationPrimaryKey("pk_users", ["id"]));
        var newPrincipal = Table("users", [Column("id", "integer", nullable: false), Column("code", "text", nullable: false)],
            primaryKey: new MigrationPrimaryKey("pk_users", ["code"]));
        var dependent = Table("orders", [Column("user_id", "integer", nullable: false)],
            foreignKeys: [new MigrationForeignKey("fk_orders_users", ["user_id"], "public", "users", ["id"], ReferentialAction.Cascade)]);

        var diff = MigrationModelDiffer.Diff(Model(oldPrincipal, dependent), Model(newPrincipal, dependent));

        Assert.True(Array.FindIndex(diff.Up.ToArray(), command => command.Sql.Contains("DROP CONSTRAINT \"fk_orders_users\"", StringComparison.Ordinal))
                    < Array.FindIndex(diff.Up.ToArray(), command => command.Sql.Contains("DROP CONSTRAINT \"pk_users\"", StringComparison.Ordinal)));
        Assert.True(Array.FindIndex(diff.Up.ToArray(), command => command.Sql.Contains("ADD CONSTRAINT \"pk_users\"", StringComparison.Ordinal))
                    < Array.FindIndex(diff.Up.ToArray(), command => command.Sql.Contains("ADD CONSTRAINT \"fk_orders_users\"", StringComparison.Ordinal)));
        Assert.Contains(diff.Down, command => command.Sql.Contains("ON DELETE CASCADE", StringComparison.Ordinal));
    }

    [Fact]
    public void Diff_RebuildsForeignKeysAroundReferencedColumnTypeChanges()
    {
        var oldPrincipal = Table("users", [Column("id", "integer", nullable: false)],
            primaryKey: new MigrationPrimaryKey("pk_users", ["id"]));
        var newPrincipal = Table("users", [Column("id", "bigint", nullable: false)],
            primaryKey: new MigrationPrimaryKey("pk_users", ["id"]));
        var oldDependent = Table("orders", [Column("user_id", "integer", nullable: false)],
            foreignKeys: [new MigrationForeignKey("fk_orders_users", ["user_id"], "public", "users", ["id"], ReferentialAction.NoAction)]);
        var newDependent = Table("orders", [Column("user_id", "bigint", nullable: false)],
            foreignKeys: oldDependent.ForeignKeys);

        var diff = MigrationModelDiffer.Diff(Model(oldPrincipal, oldDependent), Model(newPrincipal, newDependent));
        var dropForeignKey = Array.FindIndex(diff.Up.ToArray(), command => command.Sql.Contains("DROP CONSTRAINT IF EXISTS \"fk_orders_users\"", StringComparison.Ordinal));
        var alterPrincipal = Array.FindIndex(diff.Up.ToArray(), command => command.Sql.Contains("ALTER TABLE \"public\".\"users\" ALTER COLUMN \"id\" TYPE bigint", StringComparison.Ordinal));
        var alterDependent = Array.FindIndex(diff.Up.ToArray(), command => command.Sql.Contains("ALTER TABLE \"public\".\"orders\" ALTER COLUMN \"user_id\" TYPE bigint", StringComparison.Ordinal));
        var addForeignKey = Array.FindIndex(diff.Up.ToArray(), command => command.Sql.Contains("ADD CONSTRAINT \"fk_orders_users\"", StringComparison.Ordinal));

        Assert.True(dropForeignKey >= 0 && dropForeignKey < alterPrincipal && dropForeignKey < alterDependent, string.Join(Environment.NewLine, diff.Up.Select(static command => command.Sql)));
        Assert.True(addForeignKey > alterPrincipal && addForeignKey > alterDependent);
        Assert.Contains(diff.Down, command => command.Sql.Contains("TYPE integer", StringComparison.Ordinal));
    }

    [Fact]
    public void Diff_CreatesUniqueIndexBeforeForeignKeyAndRebuildsReferencesWhenItChanges()
    {
        var oldPrincipal = Table("principals", [Column("code")],
            indexes: [new MigrationIndex("ux_principals_code_old", ["code"], true, [], null, null)]);
        var newPrincipal = Table("principals", [Column("code")],
            indexes: [new MigrationIndex("ux_principals_code_new", ["code"], true, [], null, null)]);
        var dependent = Table("dependents", [Column("principal_code")],
            foreignKeys: [new MigrationForeignKey("fk_dependents_principal", ["principal_code"], "public", "principals", ["code"], ReferentialAction.NoAction)]);

        var diff = MigrationModelDiffer.Diff(Model(oldPrincipal, dependent), Model(newPrincipal, dependent));
        var dropForeignKey = Array.FindIndex(diff.Up.ToArray(), command => command.Sql.Contains("DROP CONSTRAINT IF EXISTS \"fk_dependents_principal\"", StringComparison.Ordinal));
        var dropIndex = Array.FindIndex(diff.Up.ToArray(), command => command.Sql.Contains("DROP INDEX IF EXISTS \"public\".\"ux_principals_code_old\"", StringComparison.Ordinal));
        var createIndex = Array.FindIndex(diff.Up.ToArray(), command => command.Sql.Contains("CREATE UNIQUE INDEX \"ux_principals_code_new\"", StringComparison.Ordinal));
        var addForeignKey = Array.FindIndex(diff.Up.ToArray(), command => command.Sql.Contains("ADD CONSTRAINT \"fk_dependents_principal\"", StringComparison.Ordinal));

        Assert.True(dropForeignKey >= 0 && dropForeignKey < dropIndex, string.Join(Environment.NewLine, diff.Up.Select(static command => command.Sql)));
        Assert.True(createIndex > dropIndex && createIndex < addForeignKey);
        Assert.Contains(diff.Down, command => command.Sql.Contains("ux_principals_code_old", StringComparison.Ordinal));
    }

    [Fact]
    public void Diff_AddsUniqueIndexesBeforeNewForeignKeys()
    {
        var previousPrincipal = Table("principals", [Column("code")]);
        var currentPrincipal = Table("principals", [Column("code")],
            indexes: [new MigrationIndex("ux_principals_code", ["code"], true, [], null, null)]);
        var previousDependent = Table("dependents", [Column("principal_code")]);
        var currentDependent = Table("dependents", [Column("principal_code")],
            foreignKeys: [new MigrationForeignKey("fk_dependents_principal", ["principal_code"], "public", "principals", ["code"], ReferentialAction.NoAction)]);

        var diff = MigrationModelDiffer.Diff(Model(previousPrincipal, previousDependent), Model(currentPrincipal, currentDependent));
        var createIndex = Array.FindIndex(diff.Up.ToArray(), command => command.Sql.Contains("CREATE UNIQUE INDEX \"ux_principals_code\"", StringComparison.Ordinal));
        var addForeignKey = Array.FindIndex(diff.Up.ToArray(), command => command.Sql.Contains("ADD CONSTRAINT \"fk_dependents_principal\"", StringComparison.Ordinal));

        Assert.True(createIndex >= 0 && addForeignKey > createIndex);
    }

    [Fact]
    public void Diff_RemovedTableRestoresItsCommentsIndexesAndForeignKeysOnDown()
    {
        var parent = Table("parents", [Column("id", "integer", nullable: false)],
            primaryKey: new MigrationPrimaryKey("pk_parents", ["id"]));
        var child = Table("children", [Column("id", "integer", nullable: false), Column("parent_id", "integer")],
            comment: "children",
            foreignKeys: [new MigrationForeignKey("fk_children_parent", ["parent_id"], "public", "parents", ["id"], ReferentialAction.NoAction)],
            indexes: [new MigrationIndex("ix_children_id", ["id"], false, [], null, null)]);

        var diff = MigrationModelDiffer.Diff(Model(parent, child), Model(parent));

        Assert.Equal("DROP TABLE \"public\".\"children\"", diff.Up[^1].Sql);
        Assert.Contains(diff.Down, command => command.Sql.Contains("CREATE TABLE \"public\".\"children\"", StringComparison.Ordinal));
        Assert.Contains(diff.Down, command => command.Sql.Contains("COMMENT ON TABLE \"public\".\"children\" IS 'children'", StringComparison.Ordinal));
        Assert.Contains(diff.Down, command => command.Sql.Contains("CREATE INDEX \"ix_children_id\"", StringComparison.Ordinal));
        Assert.Contains(diff.Down, command => command.Sql.Contains("ADD CONSTRAINT \"fk_children_parent\"", StringComparison.Ordinal));
    }

    [Fact]
    public void CatalogExpressionNormalizationIgnoresPostgresLiteralCastsAndWrappingParentheses()
    {
        var expected = Model(Table("items",
            [Column("status", defaultSql: "'draft'"), Column("value", "integer", defaultSql: "0")],
            indexes: [new MigrationIndex("ix_items_status", ["status"], false, [], "status = 'active'", null)]));
        var actual = Model(Table("items",
            [Column("status", defaultSql: "('draft'::text)"), Column("value", "integer", defaultSql: "(0)::integer")],
            indexes: [new MigrationIndex("ix_items_status", ["status"], false, [], "((status = 'active'::text))", null)]));

        var diff = MigrationModelDiffer.Diff(
            MigrationModelDiffer.NormalizeCatalogExpressions(expected),
            MigrationModelDiffer.NormalizeCatalogExpressions(actual));

        Assert.True(diff.IsEmpty);
    }

    [Fact]
    public void Diff_IdentityAndDefaultChangesAreReversible()
    {
        var oldModel = Model(Table("identity_rows", [Column("id", "integer", nullable: false, defaultSql: "0")]));
        var identityColumn = new MigrationColumn("id", "integer", false, true, false, null, null, null);
        var newModel = Model(Table("identity_rows", [identityColumn]));

        var diff = MigrationModelDiffer.Diff(oldModel, newModel);

        Assert.Contains(diff.Up, command => command.Sql.Contains("ADD GENERATED ALWAYS AS IDENTITY", StringComparison.Ordinal));
        Assert.Contains(diff.Up, command => command.Sql.Contains("DROP DEFAULT", StringComparison.Ordinal));
        Assert.Contains(diff.Down, command => command.Sql.Contains("DROP IDENTITY IF EXISTS", StringComparison.Ordinal));
        Assert.Contains(diff.Down, command => command.Sql.Contains("SET DEFAULT 0", StringComparison.Ordinal));
    }

    [Fact]
    public void Diff_ColumnRenameCandidateIsDropAddWithWarning()
    {
        var oldModel = Model(Table("rename_rows", [Column("old_name")]));
        var newModel = Model(Table("rename_rows", [Column("new_name")]));

        var diff = MigrationModelDiffer.Diff(oldModel, newModel);

        Assert.Contains(diff.Up, command => command.Sql.Contains("DROP COLUMN \"old_name\"", StringComparison.Ordinal));
        Assert.Contains(diff.Up, command => command.Sql.Contains("ADD COLUMN \"new_name\"", StringComparison.Ordinal));
        Assert.Contains(diff.Warnings, warning => warning.Contains("may be a rename", StringComparison.Ordinal));
    }

    [Fact]
    public void CustomSqlMigrationMustProvideDownCommands()
    {
        var migration = new IrreversibleTestMigration();

        Assert.Throws<InvalidOperationException>(migration.ValidateReversible);
    }

    private static MigrationModel Model(params MigrationTable[] tables) => new(tables);

    private static MigrationTable Table(
        string name,
        IReadOnlyList<MigrationColumn> columns,
        string? comment = null,
        MigrationPrimaryKey? primaryKey = null,
        IReadOnlyList<MigrationForeignKey>? foreignKeys = null,
        IReadOnlyList<MigrationIndex>? indexes = null) =>
        new("public", name, comment, columns, primaryKey, foreignKeys ?? [], indexes ?? []);

    private static MigrationColumn Column(
        string name,
        string type = "text",
        bool nullable = true,
        string? defaultSql = null,
        string? comment = null) =>
        new(name, type, nullable, false, false, defaultSql, null, comment);

    private sealed class ComputedWithoutSqlEntity
    {
        [DatabaseGenerated(DatabaseGeneratedOption.Computed)]
        public int Value { get; set; }
    }

    private sealed class IrreversibleTestMigration : Migration
    {
        public override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("UPDATE users SET name = upper(name)");

        public override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
