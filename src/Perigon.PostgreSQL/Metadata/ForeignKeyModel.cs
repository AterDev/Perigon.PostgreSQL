namespace Perigon.PostgreSQL.Metadata;

public sealed class ForeignKeyModel
{
    public ForeignKeyModel(
        string constraintName,
        EntityModel dependentEntity,
        IReadOnlyList<ColumnModel> dependentColumns,
        EntityModel principalEntity,
        IReadOnlyList<ColumnModel> principalColumns,
        ReferentialAction onDelete = ReferentialAction.NoAction,
        string? dependentNavigationName = null,
        string? principalNavigationName = null)
    {
        ConstraintName = constraintName;
        DependentEntity = dependentEntity;
        DependentColumns = dependentColumns;
        PrincipalEntity = principalEntity;
        PrincipalColumns = principalColumns;
        OnDelete = onDelete;
        DependentNavigationName = dependentNavigationName;
        PrincipalNavigationName = principalNavigationName;
    }

    public ForeignKeyModel(
        string constraintName,
        EntityModel dependentEntity,
        ColumnModel dependentColumn,
        EntityModel principalEntity,
        ColumnModel principalColumn,
        ReferentialAction onDelete = ReferentialAction.NoAction,
        string? dependentNavigationName = null,
        string? principalNavigationName = null)
        : this(constraintName, dependentEntity, [dependentColumn], principalEntity, [principalColumn], onDelete, dependentNavigationName, principalNavigationName)
    {
    }

    public string ConstraintName { get; }

    public EntityModel DependentEntity { get; }

    public IReadOnlyList<ColumnModel> DependentColumns { get; }

    public EntityModel PrincipalEntity { get; }

    public IReadOnlyList<ColumnModel> PrincipalColumns { get; }

    public ReferentialAction OnDelete { get; }

    public string? DependentNavigationName { get; }

    public string? PrincipalNavigationName { get; }

    public ColumnModel DependentColumn => DependentColumns[0];

    public ColumnModel PrincipalColumn => PrincipalColumns[0];
}
