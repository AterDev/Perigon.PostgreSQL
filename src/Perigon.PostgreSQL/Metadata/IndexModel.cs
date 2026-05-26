namespace Perigon.PostgreSQL.Metadata;

public sealed class IndexModel
{
    public IndexModel(string indexName, EntityModel entity, IReadOnlyList<ColumnModel> columns, bool isUnique)
    {
        IndexName = indexName;
        Entity = entity;
        Columns = columns;
        IsUnique = isUnique;
    }

    public string IndexName { get; }

    public EntityModel Entity { get; }

    public IReadOnlyList<ColumnModel> Columns { get; }

    public bool IsUnique { get; }
}