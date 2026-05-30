using System.Collections.Generic;

namespace Perigon.PostgreSQL.Tools.ReverseEngineering;

public sealed record DatabaseModel(IReadOnlyList<TableModel> Tables, IReadOnlyList<string> Warnings)
{
    public DatabaseModel(IReadOnlyList<TableModel> Tables)
        : this(Tables, [])
    {
    }
}

public sealed record TableModel(
    string Schema,
    string Name,
    bool IsView,
    string? Comment,
    IReadOnlyList<ColumnModel> Columns,
    IReadOnlyList<string> PrimaryKeyColumns,
    IReadOnlyList<ForeignKeyModel> ForeignKeys,
    IReadOnlyList<IndexModel> Indexes);

public sealed record ColumnModel(
    string Name,
    string DataType,
    string UdtName,
    bool IsNullable,
    bool IsIdentity,
    bool IsGenerated,
    string? DefaultValue,
    int? MaxLength,
    int? Precision,
    int? Scale,
    string? Comment,
    string? GeneratedExpression);

public sealed record ForeignKeyModel(
    string ConstraintName,
    IReadOnlyList<string> ColumnNames,
    string PrincipalSchema,
    string PrincipalTable,
    IReadOnlyList<string> PrincipalColumnNames,
    string OnDeleteAction)
{
    public ForeignKeyModel(
        string constraintName,
        string columnName,
        string principalSchema,
        string principalTable,
        string principalColumn,
        string onDeleteAction)
        : this(constraintName, [columnName], principalSchema, principalTable, [principalColumn], onDeleteAction)
    {
    }

    public string ColumnName => ColumnNames[0];

    public string PrincipalColumn => PrincipalColumnNames[0];
}

public sealed record IndexModel(
    string Name,
    IReadOnlyList<string> ColumnNames,
    bool IsUnique,
    IReadOnlyList<string>? IncludeColumnNames = null,
    string? Filter = null,
    string? Method = null)
{
    public IReadOnlyList<string> IncludeColumnNames { get; init; } = IncludeColumnNames ?? [];
}

public sealed record GeneratedFile(string RelativePath, string Content);