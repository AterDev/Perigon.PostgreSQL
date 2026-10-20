using System.Text;
using System.Text.RegularExpressions;
using Perigon.PostgreSQL.Metadata;
using Perigon.PostgreSQL.Sql;

namespace Perigon.PostgreSQL.Migrations;

public sealed record MigrationDiff(
    IReadOnlyList<MigrationCommand> Up,
    IReadOnlyList<MigrationCommand> Down,
    IReadOnlyList<string> Warnings)
{
    public bool IsEmpty => Up.Count == 0;
}

/// <summary>Produces deterministic PostgreSQL DDL for changes between two schema snapshots.</summary>
public static class MigrationModelDiffer
{
    internal static MigrationModel NormalizeCatalogExpressions(MigrationModel model)
    {
        return new MigrationModel(model.Tables.Select(table => new MigrationTable(
            table.Schema,
            table.Name,
            table.Comment,
            table.Columns.Select(column => column with
            {
                DefaultSql = NormalizeExpression(column.DefaultSql),
                GeneratedColumnSql = NormalizeExpression(column.GeneratedColumnSql)
            }).ToArray(),
            table.PrimaryKey,
            table.ForeignKeys,
            table.Indexes.Select(index => index with { Filter = NormalizeExpression(index.Filter) }).ToArray())).ToArray());
    }

    public static MigrationDiff Diff(MigrationModel previous, MigrationModel current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);

        var oldTables = previous.Tables.ToDictionary(TableKey, StringComparer.Ordinal);
        var newTables = current.Tables.ToDictionary(TableKey, StringComparer.Ordinal);
        var changes = new List<Change>();
        var warnings = new List<string>();

        var removedTables = oldTables.Keys.Except(newTables.Keys, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        var addedTables = newTables.Keys.Except(oldTables.Keys, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        var changedPrimaryKeyTables = previous.Tables
            .Where(table => newTables.TryGetValue(TableKey(table), out var newTable) &&
                            (!SamePrimaryKey(table.PrimaryKey, newTable.PrimaryKey) ||
                             PrimaryKeyColumnsChanged(table, newTable)))
            .Select(TableKey)
            .ToHashSet(StringComparer.Ordinal);
        var changedColumnShapes = GetChangedColumnShapes(previous, newTables);
        var droppedForeignKeys = new HashSet<string>(StringComparer.Ordinal);
        var droppedIndexes = new HashSet<string>(StringComparer.Ordinal);
        var previousSchemas = previous.Tables.Select(static table => table.Schema)
            .Where(static schema => schema is not null)
            .ToHashSet(StringComparer.Ordinal);

        // DDL creates schemas before tables that use them. Empty schemas are retained on Down
        // because they can predate the migration or contain objects outside this model.
        foreach (var schema in current.Tables.Select(static table => table.Schema)
                     .Where(static schema => schema is not null)
                     .Distinct(StringComparer.Ordinal)
                     .OrderBy(static schema => schema, StringComparer.Ordinal))
        {
            if (!previousSchemas.Contains(schema!))
            {
                changes.Add(new Change(
                    [new MigrationCommand($"CREATE SCHEMA IF NOT EXISTS {Identifier.Quote(schema!)}")],
                    Array.Empty<MigrationCommand>()));
            }
        }

        // Remove foreign keys that are deleted, changed, or point to a table being dropped.
        foreach (var oldTable in previous.Tables)
        {
            var oldKey = TableKey(oldTable);
            var matchingNewTable = newTables.GetValueOrDefault(oldKey);
            foreach (var oldForeignKey in oldTable.ForeignKeys)
            {
                var stillExists = matchingNewTable is not null &&
                                  !removedTables.Contains(TableKey(oldForeignKey.PrincipalSchema, oldForeignKey.PrincipalTable)) &&
                                  !changedPrimaryKeyTables.Contains(TableKey(oldForeignKey.PrincipalSchema, oldForeignKey.PrincipalTable)) &&
                                  matchingNewTable.ForeignKeys.Any(item => SameForeignKey(item, oldForeignKey)) &&
                                  !ForeignKeyColumnsChanged(oldTable, oldForeignKey, changedColumnShapes, oldTables, newTables);
                if (!stillExists)
                {
                    droppedForeignKeys.Add(ForeignKeyKey(oldTable, oldForeignKey));
                    changes.Add(new Change(
                        new MigrationCommand(BuildDropForeignKey(oldTable, oldForeignKey.Name)),
                        new MigrationCommand(BuildAddForeignKey(oldTable, oldForeignKey))));
                }
            }
        }

        // Remove and replace indexes before touching their columns or tables.
        foreach (var oldTable in previous.Tables)
        {
            var oldKey = TableKey(oldTable);
            var matchingNewTable = newTables.GetValueOrDefault(oldKey);
            foreach (var oldIndex in oldTable.Indexes)
            {
                var stillExists = matchingNewTable is not null &&
                                  matchingNewTable.Indexes.Any(item => SameIndex(item, oldIndex)) &&
                                  !IndexColumnsChanged(oldTable, oldIndex, changedColumnShapes);
                if (!stillExists)
                {
                    droppedIndexes.Add(IndexKey(oldTable, oldIndex));
                    changes.Add(new Change(
                        new MigrationCommand(BuildDropIndex(oldTable, oldIndex)),
                        new MigrationCommand(BuildCreateIndex(oldTable, oldIndex))));
                }
            }
        }

        foreach (var oldTable in previous.Tables.Where(table => removedTables.Contains(TableKey(table))))
        {
            var warning = $"Dropping table {DisplayName(oldTable)} loses its data.";
            warnings.Add(warning);
            var recreate = BuildCreateTableChange(oldTable);
            changes.Add(new Change(
                [new MigrationCommand($"DROP TABLE {oldTable.StoreObjectName}", true, warning)],
                recreate.Up));
        }

        foreach (var table in current.Tables.Where(table => addedTables.Contains(TableKey(table))))
        {
            changes.Add(BuildCreateTableChange(table));
        }

        // Drop old primary keys before removing or changing their columns. Their replacements
        // are added after all columns have reached the new shape.
        foreach (var currentTable in current.Tables.Where(table => oldTables.ContainsKey(TableKey(table))))
        {
            var previousTable = oldTables[TableKey(currentTable)];
            if (previousTable.PrimaryKey is not null && changedPrimaryKeyTables.Contains(TableKey(currentTable)))
            {
                changes.Add(new Change(
                    new MigrationCommand($"ALTER TABLE {currentTable.StoreObjectName} DROP CONSTRAINT {Identifier.Quote(previousTable.PrimaryKey.Name)}", true,
                        $"Changing the primary key for {DisplayName(currentTable)} may fail if dependent foreign keys exist."),
                    new MigrationCommand(BuildAddPrimaryKey(previousTable, previousTable.PrimaryKey))));
            }
        }

        foreach (var currentTable in current.Tables.Where(table => oldTables.ContainsKey(TableKey(table))))
        {
            var previousTable = oldTables[TableKey(currentTable)];
            var oldColumns = previousTable.Columns.ToDictionary(static column => column.Name, StringComparer.Ordinal);
            var newColumns = currentTable.Columns.ToDictionary(static column => column.Name, StringComparer.Ordinal);
            var removedColumns = oldColumns.Keys.Except(newColumns.Keys, StringComparer.Ordinal).ToArray();
            var addedColumns = newColumns.Keys.Except(oldColumns.Keys, StringComparer.Ordinal).ToArray();

            if (removedColumns.Length > 0 && addedColumns.Length > 0)
            {
                warnings.Add(
                    $"Table {DisplayName(currentTable)} has removed column(s) {string.Join(", ", removedColumns)} and added column(s) {string.Join(", ", addedColumns)}. This may be a rename; generated migrations use drop/add because a rename cannot be inferred safely.");
            }

            foreach (var name in removedColumns.OrderBy(static name => name, StringComparer.Ordinal))
            {
                var oldColumn = oldColumns[name];
                var warning = $"Dropping column {DisplayName(currentTable)}.{name} loses its data.";
                warnings.Add(warning);
                changes.Add(new Change(
                    new MigrationCommand($"ALTER TABLE {currentTable.StoreObjectName} DROP COLUMN {Identifier.Quote(name)}", true, warning),
                    new MigrationCommand($"ALTER TABLE {currentTable.StoreObjectName} ADD COLUMN {BuildColumn(oldColumn)}")));
            }

            foreach (var name in addedColumns.OrderBy(static name => name, StringComparer.Ordinal))
            {
                var newColumn = newColumns[name];
                var warning = !newColumn.IsNullable && newColumn.DefaultSql is null && !newColumn.IsIdentity && !newColumn.IsGenerated
                    ? $"Adding non-null column {DisplayName(currentTable)}.{name} without a default can fail when the table contains rows."
                    : null;
                if (warning is not null)
                {
                    warnings.Add(warning);
                }

                changes.Add(new Change(
                    new MigrationCommand($"ALTER TABLE {currentTable.StoreObjectName} ADD COLUMN {BuildColumn(newColumn)}", warning is not null, warning),
                    new MigrationCommand($"ALTER TABLE {currentTable.StoreObjectName} DROP COLUMN {Identifier.Quote(name)}", true,
                        $"Dropping column {DisplayName(currentTable)}.{name} loses its data.")));
            }

            foreach (var name in oldColumns.Keys.Intersect(newColumns.Keys, StringComparer.Ordinal).OrderBy(static name => name, StringComparer.Ordinal))
            {
                var oldColumn = oldColumns[name];
                var newColumn = newColumns[name];
                if (SameColumn(oldColumn, newColumn))
                {
                    continue;
                }

                if (oldColumn.IsGenerated || newColumn.IsGenerated)
                {
                    if (oldColumn.GeneratedColumnSql != newColumn.GeneratedColumnSql || oldColumn.IsGenerated != newColumn.IsGenerated)
                    {
                        var warning = $"Changing generated column {DisplayName(currentTable)}.{name} requires replacing the column and may lose its data.";
                        warnings.Add(warning);
                        changes.Add(new Change(
                            new MigrationCommand($"ALTER TABLE {currentTable.StoreObjectName} DROP COLUMN {Identifier.Quote(name)}", true, warning),
                            new MigrationCommand($"ALTER TABLE {currentTable.StoreObjectName} ADD COLUMN {BuildColumn(oldColumn)}")));
                        changes.Add(new Change(
                            new MigrationCommand($"ALTER TABLE {currentTable.StoreObjectName} ADD COLUMN {BuildColumn(newColumn)}"),
                            new MigrationCommand($"ALTER TABLE {currentTable.StoreObjectName} DROP COLUMN {Identifier.Quote(name)}", true, warning)));
                        continue;
                    }
                }

                var columnChanges = BuildColumnAlterations(currentTable, oldColumn, newColumn, warnings);
                changes.AddRange(columnChanges);
            }

            if (!string.Equals(previousTable.Comment, currentTable.Comment, StringComparison.Ordinal))
            {
                changes.Add(new Change(
                    new MigrationCommand(BuildCommentOnTable(currentTable, currentTable.Comment)),
                    new MigrationCommand(BuildCommentOnTable(previousTable, previousTable.Comment))));
            }

            foreach (var name in oldColumns.Keys.Intersect(newColumns.Keys, StringComparer.Ordinal).OrderBy(static name => name, StringComparer.Ordinal))
            {
                var oldComment = oldColumns[name].Comment;
                var newComment = newColumns[name].Comment;
                if (!string.Equals(oldComment, newComment, StringComparison.Ordinal))
                {
                    changes.Add(new Change(
                        new MigrationCommand(BuildCommentOnColumn(currentTable, name, newComment)),
                        new MigrationCommand(BuildCommentOnColumn(previousTable, name, oldComment))));
                }
            }
        }

        foreach (var currentTable in current.Tables.Where(table => oldTables.ContainsKey(TableKey(table))))
        {
            var previousTable = oldTables[TableKey(currentTable)];
            if (currentTable.PrimaryKey is not null && changedPrimaryKeyTables.Contains(TableKey(currentTable)))
            {
                changes.Add(new Change(
                    new MigrationCommand(BuildAddPrimaryKey(currentTable, currentTable.PrimaryKey)),
                    new MigrationCommand($"ALTER TABLE {currentTable.StoreObjectName} DROP CONSTRAINT {Identifier.Quote(currentTable.PrimaryKey.Name)}", true)));
            }
        }

        // Add indexes before foreign keys so a new or replaced unique index can be a
        // referenced key when the foreign key is created.
        foreach (var currentTable in current.Tables)
        {
            var oldTable = oldTables.GetValueOrDefault(TableKey(currentTable));
            var oldIndexes = oldTable?.Indexes ?? Array.Empty<MigrationIndex>();
            foreach (var index in currentTable.Indexes)
            {
                if (!oldIndexes.Any(item => SameIndex(item, index)) ||
                    (oldTable is not null && droppedIndexes.Contains(IndexKey(oldTable, index))))
                {
                    changes.Add(new Change(
                        new MigrationCommand(BuildCreateIndex(currentTable, index)),
                        new MigrationCommand(BuildDropIndex(currentTable, index))));
                }
            }
        }

        // Add foreign keys after all new tables, columns, primary keys, and unique
        // indexes have been created.
        foreach (var currentTable in current.Tables)
        {
            var oldTable = oldTables.GetValueOrDefault(TableKey(currentTable));
            var oldForeignKeys = oldTable?.ForeignKeys ?? Array.Empty<MigrationForeignKey>();
            foreach (var foreignKey in currentTable.ForeignKeys)
            {
                if (!oldForeignKeys.Any(item => SameForeignKey(item, foreignKey)) ||
                    (oldTable is not null && droppedForeignKeys.Contains(ForeignKeyKey(oldTable, foreignKey))) ||
                    changedPrimaryKeyTables.Contains(TableKey(foreignKey.PrincipalSchema, foreignKey.PrincipalTable)))
                {
                    changes.Add(new Change(
                        new MigrationCommand(BuildAddForeignKey(currentTable, foreignKey)),
                        new MigrationCommand(BuildDropForeignKey(currentTable, foreignKey.Name), true)));
                }
            }
        }

        var up = changes.SelectMany(static change => change.Up).ToArray();
        var down = changes.AsEnumerable().Reverse().SelectMany(static change => change.Down).ToArray();
        return new MigrationDiff(up, down, warnings.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static IEnumerable<Change> BuildColumnAlterations(
        MigrationTable table,
        MigrationColumn oldColumn,
        MigrationColumn newColumn,
        List<string> warnings)
    {
        var result = new List<Change>();
        var columnName = Identifier.Quote(newColumn.Name);
        var defaultChanged = !string.Equals(oldColumn.DefaultSql, newColumn.DefaultSql, StringComparison.Ordinal);
        if (defaultChanged && oldColumn.DefaultSql is not null)
        {
            result.Add(new Change(
                new MigrationCommand($"ALTER TABLE {table.StoreObjectName} ALTER COLUMN {columnName} DROP DEFAULT"),
                new MigrationCommand($"ALTER TABLE {table.StoreObjectName} ALTER COLUMN {columnName} SET DEFAULT {oldColumn.DefaultSql}")));
        }

        if (!string.Equals(oldColumn.StoreType, newColumn.StoreType, StringComparison.Ordinal))
        {
            var warning = $"Changing the type of {DisplayName(table)}.{newColumn.Name} may fail conversion or lose data.";
            warnings.Add(warning);
            result.Add(new Change(
                new MigrationCommand($"ALTER TABLE {table.StoreObjectName} ALTER COLUMN {columnName} TYPE {newColumn.StoreType} USING {columnName}::{newColumn.StoreType}", true, warning),
                new MigrationCommand($"ALTER TABLE {table.StoreObjectName} ALTER COLUMN {columnName} TYPE {oldColumn.StoreType} USING {columnName}::{oldColumn.StoreType}", true, warning)));
        }

        if (oldColumn.IsIdentity != newColumn.IsIdentity)
        {
            var warning = "Changing identity generation does not preserve sequence state; review sequence values before applying or rolling back.";
            warnings.Add(warning);
            result.Add(new Change(
                new MigrationCommand(newColumn.IsIdentity
                    ? $"ALTER TABLE {table.StoreObjectName} ALTER COLUMN {columnName} ADD GENERATED ALWAYS AS IDENTITY"
                    : $"ALTER TABLE {table.StoreObjectName} ALTER COLUMN {columnName} DROP IDENTITY IF EXISTS", true, warning),
                new MigrationCommand(oldColumn.IsIdentity
                    ? $"ALTER TABLE {table.StoreObjectName} ALTER COLUMN {columnName} ADD GENERATED ALWAYS AS IDENTITY"
                    : $"ALTER TABLE {table.StoreObjectName} ALTER COLUMN {columnName} DROP IDENTITY IF EXISTS")));
        }

        if (oldColumn.IsNullable != newColumn.IsNullable)
        {
            var tightening = oldColumn.IsNullable && !newColumn.IsNullable;
            var warning = tightening ? $"Making {DisplayName(table)}.{newColumn.Name} NOT NULL can fail if the table contains null values." : null;
            if (warning is not null)
            {
                warnings.Add(warning);
            }

            result.Add(new Change(
                new MigrationCommand($"ALTER TABLE {table.StoreObjectName} ALTER COLUMN {columnName} {(newColumn.IsNullable ? "DROP" : "SET")} NOT NULL", tightening, warning),
                new MigrationCommand($"ALTER TABLE {table.StoreObjectName} ALTER COLUMN {columnName} {(oldColumn.IsNullable ? "DROP" : "SET")} NOT NULL")));
        }

        if (defaultChanged && newColumn.DefaultSql is not null)
        {
            result.Add(new Change(
                new MigrationCommand($"ALTER TABLE {table.StoreObjectName} ALTER COLUMN {columnName} SET DEFAULT {newColumn.DefaultSql}"),
                new MigrationCommand($"ALTER TABLE {table.StoreObjectName} ALTER COLUMN {columnName} DROP DEFAULT")));
        }

        return result;
    }

    private static Change BuildCreateTableChange(MigrationTable table)
    {
        var statements = new List<MigrationCommand> { new(BuildCreateTable(table)) };
        if (!string.IsNullOrWhiteSpace(table.Comment))
        {
            statements.Add(new MigrationCommand(BuildCommentOnTable(table, table.Comment)));
        }

        statements.AddRange(table.Columns
            .Where(static column => !string.IsNullOrWhiteSpace(column.Comment))
            .Select(column => new MigrationCommand(BuildCommentOnColumn(table, column.Name, column.Comment))));

        return new Change(
            statements,
            [new MigrationCommand($"DROP TABLE {table.StoreObjectName}", true,
                $"Dropping table {DisplayName(table)} loses its data.")]);
    }

    private static string BuildCreateTable(MigrationTable table)
    {
        var definitions = table.Columns.Select(column => "    " + BuildColumn(column)).ToList();
        if (table.PrimaryKey is not null)
        {
            definitions.Add($"    CONSTRAINT {Identifier.Quote(table.PrimaryKey.Name)} PRIMARY KEY ({QuoteColumns(table.PrimaryKey.Columns)})");
        }

        return $"CREATE TABLE {table.StoreObjectName} (\n{string.Join(",\n", definitions)}\n)";
    }

    private static string BuildColumn(MigrationColumn column)
    {
        var builder = new StringBuilder();
        builder.Append(Identifier.Quote(column.Name)).Append(' ').Append(column.StoreType);
        if (column.IsGenerated)
        {
            if (string.IsNullOrWhiteSpace(column.GeneratedColumnSql))
            {
                throw new InvalidOperationException($"Generated column '{column.Name}' has no SQL expression.");
            }

            builder.Append(" GENERATED ALWAYS AS (").Append(column.GeneratedColumnSql).Append(") STORED");
        }
        else if (column.IsIdentity)
        {
            builder.Append(" GENERATED ALWAYS AS IDENTITY");
        }
        else if (!string.IsNullOrWhiteSpace(column.DefaultSql))
        {
            builder.Append(" DEFAULT ").Append(column.DefaultSql);
        }

        if (!column.IsNullable)
        {
            builder.Append(" NOT NULL");
        }

        return builder.ToString();
    }

    private static string BuildAddPrimaryKey(MigrationTable table, MigrationPrimaryKey primaryKey)
    {
        return $"ALTER TABLE {table.StoreObjectName} ADD CONSTRAINT {Identifier.Quote(primaryKey.Name)} PRIMARY KEY ({QuoteColumns(primaryKey.Columns)})";
    }

    private static string BuildAddForeignKey(MigrationTable table, MigrationForeignKey foreignKey)
    {
        var sql = $"ALTER TABLE {table.StoreObjectName} ADD CONSTRAINT {Identifier.Quote(foreignKey.Name)} FOREIGN KEY ({QuoteColumns(foreignKey.Columns)}) REFERENCES {Identifier.Qualify(foreignKey.PrincipalSchema, foreignKey.PrincipalTable)} ({QuoteColumns(foreignKey.PrincipalColumns)})";
        return foreignKey.OnDelete switch
        {
            ReferentialAction.NoAction => sql,
            ReferentialAction.Restrict => sql + " ON DELETE RESTRICT",
            ReferentialAction.Cascade => sql + " ON DELETE CASCADE",
            ReferentialAction.SetNull => sql + " ON DELETE SET NULL",
            ReferentialAction.SetDefault => sql + " ON DELETE SET DEFAULT",
            _ => sql
        };
    }

    private static string BuildDropForeignKey(MigrationTable table, string name)
    {
        return $"ALTER TABLE {table.StoreObjectName} DROP CONSTRAINT IF EXISTS {Identifier.Quote(name)}";
    }

    private static string BuildCreateIndex(MigrationTable table, MigrationIndex index)
    {
        var sql = new StringBuilder("CREATE ");
        if (index.IsUnique)
        {
            sql.Append("UNIQUE ");
        }

        sql.Append("INDEX ").Append(Identifier.Quote(index.Name)).Append(" ON ").Append(table.StoreObjectName);
        if (!string.IsNullOrWhiteSpace(index.Method))
        {
            sql.Append(" USING ").Append(index.Method);
        }

        sql.Append(" (").Append(QuoteColumns(index.Columns)).Append(')');
        if (index.IncludeColumns.Count > 0)
        {
            sql.Append(" INCLUDE (").Append(QuoteColumns(index.IncludeColumns)).Append(')');
        }

        if (!string.IsNullOrWhiteSpace(index.Filter))
        {
            sql.Append(" WHERE ").Append(index.Filter);
        }

        return sql.ToString();
    }

    private static string BuildDropIndex(MigrationTable table, MigrationIndex index)
    {
        return $"DROP INDEX IF EXISTS {Identifier.Qualify(table.Schema, index.Name)}";
    }

    private static string BuildCommentOnTable(MigrationTable table, string? comment)
    {
        return $"COMMENT ON TABLE {table.StoreObjectName} IS {SqlLiteral(comment)}";
    }

    private static string BuildCommentOnColumn(MigrationTable table, string column, string? comment)
    {
        return $"COMMENT ON COLUMN {table.StoreObjectName}.{Identifier.Quote(column)} IS {SqlLiteral(comment)}";
    }

    private static string SqlLiteral(string? value) => value is null ? "NULL" : "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string? NormalizeExpression(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return expression;
        }

        var value = CanonicalizeExpression(expression);
        value = Regex.Replace(value, @"\((?<literal>[0-9]+(?:\.[0-9]+)?)\)::", "${literal}::", RegexOptions.CultureInvariant);
        value = Regex.Replace(
            value,
            "(?<literal>'(?:[^']|'')*'|(?:[0-9]+(?:\\.[0-9]+)?)|\\b(?:true|false|null)\\b)\\s*::\\s*(?:character varying(?:\\([0-9]+\\))?|varchar(?:\\([0-9]+\\))?|text|int2|smallint|int4|integer|int8|bigint|bool|boolean|numeric(?:\\([0-9]+(?:,[0-9]+)?\\))?|real|double precision|timestamp(?: with(?:out)? time zone)?(?:\\([0-9]+\\))?|date|uuid|jsonb|bytea)(?![a-z0-9_])",
            "${literal}",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        value = CanonicalizeExpression(value);
        return value is "current_timestamp" or "current_timestamp()" ? "now()" : value;
    }

    private static string CanonicalizeExpression(string expression)
    {
        var result = new StringBuilder(expression.Length);
        var singleQuoted = false;
        var doubleQuoted = false;
        var pendingWhitespace = false;
        for (var index = 0; index < expression.Length; index++)
        {
            var character = expression[index];
            if (!singleQuoted && !doubleQuoted && char.IsWhiteSpace(character))
            {
                pendingWhitespace = true;
                continue;
            }

            if (pendingWhitespace && result.Length > 0 && IsWordCharacter(result[^1]) && IsWordCharacter(character))
            {
                result.Append(' ');
            }

            pendingWhitespace = false;
            if (!doubleQuoted && character == '\'')
            {
                if (singleQuoted && index + 1 < expression.Length && expression[index + 1] == '\'')
                {
                    result.Append("''");
                    index++;
                    continue;
                }

                singleQuoted = !singleQuoted;
                result.Append(character);
                continue;
            }

            if (!singleQuoted && character == '"')
            {
                if (doubleQuoted && index + 1 < expression.Length && expression[index + 1] == '"')
                {
                    result.Append("\"\"");
                    index++;
                    continue;
                }

                doubleQuoted = !doubleQuoted;
                result.Append(character);
                continue;
            }

            result.Append(singleQuoted || doubleQuoted ? character : char.ToLowerInvariant(character));
        }

        var canonical = result.ToString().Trim();
        while (HasOuterParentheses(canonical))
        {
            canonical = canonical[1..^1];
        }

        return canonical;
    }

    private static bool IsWordCharacter(char value) => char.IsLetterOrDigit(value) || value == '_';

    private static bool HasOuterParentheses(string value)
    {
        if (value.Length < 2 || value[0] != '(' || value[^1] != ')')
        {
            return false;
        }

        var depth = 0;
        var singleQuoted = false;
        var doubleQuoted = false;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (!doubleQuoted && character == '\'')
            {
                if (singleQuoted && index + 1 < value.Length && value[index + 1] == '\'')
                {
                    index++;
                    continue;
                }

                singleQuoted = !singleQuoted;
            }
            else if (!singleQuoted && character == '"')
            {
                if (doubleQuoted && index + 1 < value.Length && value[index + 1] == '"')
                {
                    index++;
                    continue;
                }

                doubleQuoted = !doubleQuoted;
            }
            else if (!singleQuoted && !doubleQuoted)
            {
                if (character == '(')
                {
                    depth++;
                }
                else if (character == ')' && --depth == 0 && index != value.Length - 1)
                {
                    return false;
                }
            }
        }

        return depth == 0;
    }

    private static string QuoteColumns(IEnumerable<string> columns) => string.Join(", ", columns.Select(Identifier.Quote));

    private static string TableKey(MigrationTable table) => TableKey(table.Schema, table.Name);

    private static string TableKey(string? schema, string table) => (schema ?? string.Empty) + "\0" + table;

    private static string ForeignKeyKey(MigrationTable table, MigrationForeignKey foreignKey) =>
        TableKey(table) + "\0" + foreignKey.Name;

    private static string IndexKey(MigrationTable table, MigrationIndex index) =>
        TableKey(table) + "\0" + index.Name;

    private static Dictionary<string, HashSet<string>> GetChangedColumnShapes(
        MigrationModel previous,
        IReadOnlyDictionary<string, MigrationTable> newTables)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var oldTable in previous.Tables)
        {
            if (!newTables.TryGetValue(TableKey(oldTable), out var newTable))
            {
                continue;
            }

            var newColumns = newTable.Columns.ToDictionary(static column => column.Name, StringComparer.Ordinal);
            var changed = oldTable.Columns
                .Where(oldColumn => !newColumns.TryGetValue(oldColumn.Name, out var newColumn) || ColumnDependencyShapeChanged(oldColumn, newColumn))
                .Select(static column => column.Name)
                .ToHashSet(StringComparer.Ordinal);
            if (changed.Count > 0)
            {
                result[TableKey(oldTable)] = changed;
            }
        }

        return result;
    }

    private static bool PrimaryKeyColumnsChanged(MigrationTable oldTable, MigrationTable newTable)
    {
        if (oldTable.PrimaryKey is null || newTable.PrimaryKey is null)
        {
            return false;
        }

        var oldColumns = oldTable.Columns.ToDictionary(static column => column.Name, StringComparer.Ordinal);
        var newColumns = newTable.Columns.ToDictionary(static column => column.Name, StringComparer.Ordinal);
        return oldTable.PrimaryKey.Columns.Any(name =>
            !oldColumns.TryGetValue(name, out var oldColumn) ||
            !newColumns.TryGetValue(name, out var newColumn) ||
            ColumnDependencyShapeChanged(oldColumn, newColumn));
    }

    private static bool ColumnDependencyShapeChanged(MigrationColumn oldColumn, MigrationColumn newColumn) =>
        !string.Equals(oldColumn.StoreType, newColumn.StoreType, StringComparison.Ordinal) ||
        oldColumn.IsIdentity != newColumn.IsIdentity ||
        oldColumn.IsGenerated != newColumn.IsGenerated ||
        !string.Equals(oldColumn.GeneratedColumnSql, newColumn.GeneratedColumnSql, StringComparison.Ordinal);

    private static bool ForeignKeyColumnsChanged(
        MigrationTable dependentTable,
        MigrationForeignKey foreignKey,
        IReadOnlyDictionary<string, HashSet<string>> changedColumnShapes,
        IReadOnlyDictionary<string, MigrationTable> oldTables,
        IReadOnlyDictionary<string, MigrationTable> newTables)
    {
        if (ColumnsChanged(dependentTable, foreignKey.Columns, changedColumnShapes) ||
            ColumnsChanged(foreignKey.PrincipalSchema, foreignKey.PrincipalTable, foreignKey.PrincipalColumns, changedColumnShapes))
        {
            return true;
        }

        var principalKey = TableKey(foreignKey.PrincipalSchema, foreignKey.PrincipalTable);
        if (!oldTables.TryGetValue(principalKey, out var oldPrincipal))
        {
            return false;
        }

        var newPrincipal = newTables.GetValueOrDefault(principalKey);
        return oldPrincipal.Indexes
            .Where(index => index.IsUnique && index.Filter is null &&
                            index.Columns.SequenceEqual(foreignKey.PrincipalColumns, StringComparer.Ordinal))
            .Any(index => newPrincipal is null ||
                          !newPrincipal.Indexes.Any(item => SameIndex(item, index)) ||
                          IndexColumnsChanged(oldPrincipal, index, changedColumnShapes));
    }

    private static bool ColumnsChanged(
        MigrationTable table,
        IReadOnlyList<string> columns,
        IReadOnlyDictionary<string, HashSet<string>> changedColumnShapes) =>
        ColumnsChanged(table.Schema, table.Name, columns, changedColumnShapes);

    private static bool ColumnsChanged(
        string? schema,
        string table,
        IReadOnlyList<string> columns,
        IReadOnlyDictionary<string, HashSet<string>> changedColumnShapes) =>
        changedColumnShapes.TryGetValue(TableKey(schema, table), out var changedColumns) &&
        columns.Any(changedColumns.Contains);

    private static bool IndexColumnsChanged(
        MigrationTable table,
        MigrationIndex index,
        IReadOnlyDictionary<string, HashSet<string>> changedColumnShapes) =>
        ColumnsChanged(table, index.Columns.Concat(index.IncludeColumns).ToArray(), changedColumnShapes);

    private static string DisplayName(MigrationTable table) => table.Schema is null ? table.Name : table.Schema + "." + table.Name;

    private static bool SameColumn(MigrationColumn left, MigrationColumn right)
    {
        return string.Equals(left.StoreType, right.StoreType, StringComparison.Ordinal) &&
               left.IsNullable == right.IsNullable &&
               left.IsIdentity == right.IsIdentity &&
               left.IsGenerated == right.IsGenerated &&
               string.Equals(left.DefaultSql, right.DefaultSql, StringComparison.Ordinal) &&
               string.Equals(left.GeneratedColumnSql, right.GeneratedColumnSql, StringComparison.Ordinal);
    }

    private static bool SamePrimaryKey(MigrationPrimaryKey? left, MigrationPrimaryKey? right)
    {
        return left is null
            ? right is null
            : right is not null && left.Name == right.Name && left.Columns.SequenceEqual(right.Columns, StringComparer.Ordinal);
    }

    private static bool SameForeignKey(MigrationForeignKey left, MigrationForeignKey right)
    {
        return left.Name == right.Name &&
               left.PrincipalSchema == right.PrincipalSchema &&
               left.PrincipalTable == right.PrincipalTable &&
               left.OnDelete == right.OnDelete &&
               left.Columns.SequenceEqual(right.Columns, StringComparer.Ordinal) &&
               left.PrincipalColumns.SequenceEqual(right.PrincipalColumns, StringComparer.Ordinal);
    }

    private static bool SameIndex(MigrationIndex left, MigrationIndex right)
    {
        return left.Name == right.Name && left.IsUnique == right.IsUnique && left.Method == right.Method && left.Filter == right.Filter &&
               left.Columns.SequenceEqual(right.Columns, StringComparer.Ordinal) &&
               left.IncludeColumns.SequenceEqual(right.IncludeColumns, StringComparer.Ordinal);
    }

    private sealed record Change(IReadOnlyList<MigrationCommand> Up, IReadOnlyList<MigrationCommand> Down)
    {
        public Change(MigrationCommand up, MigrationCommand down)
            : this([up], [down])
        {
        }
    }
}
