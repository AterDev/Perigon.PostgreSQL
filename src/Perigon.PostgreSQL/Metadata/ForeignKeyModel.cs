namespace Perigon.PostgreSQL.Metadata;

public sealed class ForeignKeyModel
{
    public ForeignKeyModel(
        string constraintName,
        EntityModel dependentEntity,
        ColumnModel dependentColumn,
        EntityModel principalEntity,
        ColumnModel principalColumn,
        ReferentialAction onDelete = ReferentialAction.NoAction)
    {
        ConstraintName = constraintName;
        DependentEntity = dependentEntity;
        DependentColumn = dependentColumn;
        PrincipalEntity = principalEntity;
        PrincipalColumn = principalColumn;
        OnDelete = onDelete;
    }

    public string ConstraintName { get; }

    public EntityModel DependentEntity { get; }

    public ColumnModel DependentColumn { get; }

    public EntityModel PrincipalEntity { get; }

    public ColumnModel PrincipalColumn { get; }

    public ReferentialAction OnDelete { get; }
}