using System.Collections.Generic;

namespace Perigon.PostgreSQL.Tools.ReverseEngineering;

public sealed record DatabaseModel(IReadOnlyList<TableModel> Tables);

public sealed record TableModel(
    string Schema,
    string Name,
    bool IsView,
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
    string? DefaultValue);

public sealed record ForeignKeyModel(
    string ColumnName,
    string PrincipalSchema,
    string PrincipalTable,
    string PrincipalColumn);

public sealed record IndexModel(
    string Name,
    IReadOnlyList<string> ColumnNames,
    bool IsUnique);

public sealed record GeneratedFile(string RelativePath, string Content);