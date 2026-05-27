using System.Text;
using Perigon.PostgreSQL.Metadata;

namespace Perigon.PostgreSQL.Sql;

internal static class CreateTableSqlBuilder
{
    public static string BuildCreateSchema(string schema)
    {
        return $"CREATE SCHEMA IF NOT EXISTS {Identifier.Quote(schema)}";
    }

    public static string BuildCreateTable(EntityModel model)
    {
        var builder = new StringBuilder();
        builder.Append("CREATE TABLE IF NOT EXISTS ");
        builder.Append(model.StoreObjectName);
        builder.AppendLine(" (");

        var definitions = new List<string>(model.Columns.Count + 1);
        foreach (var column in model.Columns)
        {
            definitions.Add("    " + BuildColumn(column));
        }

        if (model.PrimaryKey is not null)
        {
            definitions.Add($"    CONSTRAINT {Identifier.Quote(DefaultPrimaryKeyName(model))} PRIMARY KEY ({Identifier.Quote(model.PrimaryKey.ColumnName)})");
        }

        builder.AppendLine(string.Join("," + Environment.NewLine, definitions));
        builder.Append(')');
        return builder.ToString();
    }

    public static string BuildAddForeignKey(ForeignKeyModel foreignKey)
    {
        var alter = new StringBuilder();
        alter.Append("ALTER TABLE ");
        alter.Append(foreignKey.DependentEntity.StoreObjectName);
        alter.Append(" ADD CONSTRAINT ");
        alter.Append(Identifier.Quote(foreignKey.ConstraintName));
        alter.Append(" FOREIGN KEY (");
        alter.Append(Identifier.Quote(foreignKey.DependentColumn.ColumnName));
        alter.Append(") REFERENCES ");
        alter.Append(foreignKey.PrincipalEntity.StoreObjectName);
        alter.Append(" (");
        alter.Append(Identifier.Quote(foreignKey.PrincipalColumn.ColumnName));
        alter.Append(')');

        var action = ToSql(foreignKey.OnDelete);
        if (action is not null)
        {
            alter.Append(" ON DELETE ");
            alter.Append(action);
        }

        var builder = new StringBuilder();
        builder.AppendLine("DO $$");
        builder.AppendLine("BEGIN");
        builder.Append("    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = ");
        builder.Append(SqlLiteral(foreignKey.ConstraintName));
        builder.Append(" AND conrelid = ");
        builder.Append(SqlLiteral(foreignKey.DependentEntity.StoreObjectName));
        builder.AppendLine("::regclass) THEN");
        builder.Append("        ");
        builder.Append(alter);
        builder.AppendLine(";");
        builder.AppendLine("    END IF;");
        builder.AppendLine("END $$;");
        return builder.ToString();
    }

    public static string BuildAddForeignKeyStatement(ForeignKeyModel foreignKey)
    {
        var builder = new StringBuilder();
        builder.Append("ALTER TABLE ");
        builder.Append(foreignKey.DependentEntity.StoreObjectName);
        builder.Append(" ADD CONSTRAINT ");
        builder.Append(Identifier.Quote(foreignKey.ConstraintName));
        builder.Append(" FOREIGN KEY (");
        builder.Append(Identifier.Quote(foreignKey.DependentColumn.ColumnName));
        builder.Append(") REFERENCES ");
        builder.Append(foreignKey.PrincipalEntity.StoreObjectName);
        builder.Append(" (");
        builder.Append(Identifier.Quote(foreignKey.PrincipalColumn.ColumnName));
        builder.Append(')');

        var action = ToSql(foreignKey.OnDelete);
        if (action is not null)
        {
            builder.Append(" ON DELETE ");
            builder.Append(action);
        }

        return builder.ToString();
    }

    public static string BuildCreateIndex(IndexModel index)
    {
        var unique = index.IsUnique ? "UNIQUE " : "";
        var columns = string.Join(", ", index.Columns.Select(column => Identifier.Quote(column.ColumnName)));
        return $"CREATE {unique}INDEX IF NOT EXISTS {Identifier.Quote(index.IndexName)} ON {index.Entity.StoreObjectName} ({columns})";
    }

    public static IReadOnlyList<string> BuildComments(EntityModel model)
    {
        var commands = new List<string>();
        if (!string.IsNullOrWhiteSpace(model.Comment))
        {
            commands.Add($"COMMENT ON TABLE {model.StoreObjectName} IS {SqlLiteral(model.Comment!)}");
        }

        foreach (var column in model.Columns)
        {
            if (string.IsNullOrWhiteSpace(column.Comment))
            {
                continue;
            }

            commands.Add($"COMMENT ON COLUMN {model.StoreObjectName}.{Identifier.Quote(column.ColumnName)} IS {SqlLiteral(column.Comment!)}");
        }

        return commands;
    }

    private static string BuildColumn(ColumnModel column)
    {
        if (column.IsGenerated)
        {
            throw new InvalidOperationException(
                $"Generated column '{column.DeclaringType.Name}.{column.PropertyName}' requires a SQL generation expression and is not supported by EnsureCreated yet.");
        }

        var builder = new StringBuilder();
        builder.Append(Identifier.Quote(column.ColumnName));
        builder.Append(' ');
        builder.Append(PostgresTypeMapper.Map(column));

        if (column.IsIdentity)
        {
            builder.Append(" GENERATED ALWAYS AS IDENTITY");
        }

        if (column.IsPrimaryKey || !column.IsNullable)
        {
            builder.Append(" NOT NULL");
        }

        return builder.ToString();
    }

    private static string DefaultPrimaryKeyName(EntityModel model)
    {
        return "pk_" + model.TableName;
    }

    private static string? ToSql(ReferentialAction action)
    {
        return action switch
        {
            ReferentialAction.NoAction => null,
            ReferentialAction.Restrict => "RESTRICT",
            ReferentialAction.Cascade => "CASCADE",
            ReferentialAction.SetNull => "SET NULL",
            ReferentialAction.SetDefault => "SET DEFAULT",
            _ => null
        };
    }

    private static string SqlLiteral(string value)
    {
        return "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    }
}