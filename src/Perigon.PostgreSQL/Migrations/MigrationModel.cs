using System.Text.Json.Serialization;
using System.Text.Json;
using Perigon.PostgreSQL.Metadata;
using Perigon.PostgreSQL.Sql;

namespace Perigon.PostgreSQL.Migrations;

/// <summary>A deterministic PostgreSQL schema model used by migration snapshots and diffing.</summary>
public sealed record MigrationModel
{
    [JsonConstructor]
    public MigrationModel(IReadOnlyList<MigrationTable> tables)
    {
        Tables = (tables ?? throw new ArgumentNullException(nameof(tables)))
            .OrderBy(static table => table.Schema, StringComparer.Ordinal)
            .ThenBy(static table => table.Name, StringComparer.Ordinal)
            .ToArray();
    }

    public IReadOnlyList<MigrationTable> Tables { get; }

    public static MigrationModel Empty { get; } = new(Array.Empty<MigrationTable>());

    public string ToJson() => JsonSerializer.Serialize(this, MigrationJsonContext.Default.MigrationModel);

    public static MigrationModel FromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return JsonSerializer.Deserialize(json, MigrationJsonContext.Default.MigrationModel)
            ?? throw new InvalidOperationException("Migration model snapshot did not contain a model.");
    }

    public static MigrationModel FromEntityModels(IReadOnlyList<EntityModel> models)
    {
        ArgumentNullException.ThrowIfNull(models);
        var tableModels = models.Where(static model => !model.IsView).ToArray();
        var foreignKeys = RelationshipConventions.InferForeignKeys(tableModels);

        var tables = tableModels.Select(model => new MigrationTable(
            model.Schema,
            model.TableName,
            model.Comment,
            model.Columns.Select(column => new MigrationColumn(
                column.ColumnName,
                PostgresTypeMapper.Map(column),
                column.IsNullable,
                column.IsIdentity,
                column.IsGenerated,
                column.DefaultSql,
                column.GeneratedColumnSql,
                column.Comment)).ToArray(),
            model.PrimaryKeys.Count == 0
                ? null
                : new MigrationPrimaryKey(
                    "pk_" + model.TableName,
                    model.PrimaryKeys.Select(static column => column.ColumnName).ToArray()),
            foreignKeys
                .Where(foreignKey => ReferenceEquals(foreignKey.DependentEntity, model))
                .Select(foreignKey => new MigrationForeignKey(
                    foreignKey.ConstraintName,
                    foreignKey.DependentColumns.Select(static column => column.ColumnName).ToArray(),
                    foreignKey.PrincipalEntity.Schema,
                    foreignKey.PrincipalEntity.TableName,
                    foreignKey.PrincipalColumns.Select(static column => column.ColumnName).ToArray(),
                    foreignKey.OnDelete))
                .ToArray(),
            model.Indexes.Select(index => new MigrationIndex(
                index.IndexName,
                index.Columns.Select(static column => column.ColumnName).ToArray(),
                index.IsUnique,
                index.IncludeColumns.Select(static column => column.ColumnName).ToArray(),
                index.Filter,
                index.Method)).ToArray())).ToArray();

        return new MigrationModel(tables);
    }
}

public sealed record MigrationTable
{
    [JsonConstructor]
    public MigrationTable(
        string? schema,
        string name,
        string? comment,
        IReadOnlyList<MigrationColumn> columns,
        MigrationPrimaryKey? primaryKey,
        IReadOnlyList<MigrationForeignKey> foreignKeys,
        IReadOnlyList<MigrationIndex> indexes)
    {
        Schema = schema;
        Name = string.IsNullOrWhiteSpace(name) ? throw new ArgumentException("Table name cannot be empty.", nameof(name)) : name;
        Comment = comment;
        Columns = (columns ?? throw new ArgumentNullException(nameof(columns)))
            .OrderBy(static column => column.Name, StringComparer.Ordinal)
            .ToArray();
        PrimaryKey = primaryKey;
        ForeignKeys = (foreignKeys ?? throw new ArgumentNullException(nameof(foreignKeys)))
            .OrderBy(static foreignKey => foreignKey.Name, StringComparer.Ordinal)
            .ToArray();
        Indexes = (indexes ?? throw new ArgumentNullException(nameof(indexes)))
            .OrderBy(static index => index.Name, StringComparer.Ordinal)
            .ToArray();
    }

    public string? Schema { get; }

    public string Name { get; }

    public string? Comment { get; }

    public IReadOnlyList<MigrationColumn> Columns { get; }

    public MigrationPrimaryKey? PrimaryKey { get; }

    public IReadOnlyList<MigrationForeignKey> ForeignKeys { get; }

    public IReadOnlyList<MigrationIndex> Indexes { get; }

    public string StoreObjectName => Identifier.Qualify(Schema, Name);
}

public sealed record MigrationColumn(
    string Name,
    string StoreType,
    bool IsNullable,
    bool IsIdentity,
    bool IsGenerated,
    string? DefaultSql,
    string? GeneratedColumnSql,
    string? Comment);

public sealed record MigrationPrimaryKey(string Name, IReadOnlyList<string> Columns);

public sealed record MigrationForeignKey(
    string Name,
    IReadOnlyList<string> Columns,
    string? PrincipalSchema,
    string PrincipalTable,
    IReadOnlyList<string> PrincipalColumns,
    ReferentialAction OnDelete);

public sealed record MigrationIndex(
    string Name,
    IReadOnlyList<string> Columns,
    bool IsUnique,
    IReadOnlyList<string> IncludeColumns,
    string? Filter,
    string? Method);

[JsonSerializable(typeof(MigrationModel))]
[JsonSerializable(typeof(MigrationTable))]
[JsonSerializable(typeof(MigrationColumn))]
[JsonSerializable(typeof(MigrationPrimaryKey))]
[JsonSerializable(typeof(MigrationForeignKey))]
[JsonSerializable(typeof(MigrationIndex))]
internal partial class MigrationJsonContext : JsonSerializerContext
{
}
