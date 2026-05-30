namespace Perigon.PostgreSQL.Metadata;

public sealed class IndexModel
{
    public IndexModel(
        string indexName,
        EntityModel entity,
        IReadOnlyList<ColumnModel> columns,
        bool isUnique,
        IReadOnlyList<ColumnModel>? includeColumns = null,
        string? filter = null,
        string? method = null)
    {
        IndexName = indexName;
        Entity = entity;
        Columns = columns;
        IsUnique = isUnique;
        IncludeColumns = includeColumns ?? [];
        Filter = filter;
        Method = method;
    }

    public string IndexName { get; }

    public EntityModel Entity { get; }

    public IReadOnlyList<ColumnModel> Columns { get; }

    public bool IsUnique { get; }

    public IReadOnlyList<ColumnModel> IncludeColumns { get; }

    public string? Filter { get; }

    public string? Method { get; }
}