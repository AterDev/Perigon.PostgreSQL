namespace Perigon.PostgreSQL.Metadata;

public sealed class ForeignKeyModel
{
    public ForeignKeyModel(
        string constraintName,
        EntityModel dependentEntity,
        IReadOnlyList<ColumnModel> dependentColumns,
        EntityModel principalEntity,
        IReadOnlyList<ColumnModel> principalColumns,
        ReferentialAction onDelete = ReferentialAction.NoAction)
    {
        ConstraintName = constraintName;
        DependentEntity = dependentEntity;
        DependentColumns = dependentColumns;
        PrincipalEntity = principalEntity;
        PrincipalColumns = principalColumns;
        OnDelete = onDelete;
    }

    public ForeignKeyModel(
        string constraintName,
        EntityModel dependentEntity,
        ColumnModel dependentColumn,
        EntityModel principalEntity,
        ColumnModel principalColumn,
        ReferentialAction onDelete = ReferentialAction.NoAction)
        : this(constraintName, dependentEntity, [dependentColumn], principalEntity, [principalColumn], onDelete)
    {
    }

    public string ConstraintName { get; }

    public EntityModel DependentEntity { get; }

    public IReadOnlyList<ColumnModel> DependentColumns { get; }

    public EntityModel PrincipalEntity { get; }

    public IReadOnlyList<ColumnModel> PrincipalColumns { get; }

    public ReferentialAction OnDelete { get; }

    public ColumnModel DependentColumn => DependentColumns[0];

    public ColumnModel PrincipalColumn => PrincipalColumns[0];
}