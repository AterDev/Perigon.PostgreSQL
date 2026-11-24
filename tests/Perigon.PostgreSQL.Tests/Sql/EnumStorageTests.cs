using Perigon.PostgreSQL.Metadata;
using Perigon.PostgreSQL.Sql;
using System.ComponentModel.DataAnnotations.Schema;

namespace Perigon.PostgreSQL.Tests.Sql;

public sealed class EnumStorageTests
{
    [Fact]
    public void Enum_columns_and_insert_parameters_use_matching_integer_types()
    {
        var model = EntityModel.For<EnumRow>();
        var ddl = CreateTableSqlBuilder.BuildCreateTable(model);
        Assert.Contains("\"status\" smallint NOT NULL", ddl);
        Assert.Contains("\"optional_status\" smallint", ddl);
        Assert.Contains("\"extended_status\" integer NOT NULL", ddl);

        var insert = InsertSqlBuilder.Build(model, new EnumRow
        {
            Status = RowStatus.Active,
            OptionalStatus = RowStatus.Active,
            ExtendedStatus = RowStatus.Extended
        }, returning: false);
        Assert.Contains(insert.Parameters, parameter => parameter.Value is short and 1);
        Assert.Contains(insert.Parameters, parameter => parameter.Value is int and 40000);
    }

    public enum RowStatus
    {
        Active = 1,
        Extended = 40000
    }

    public sealed class EnumRow
    {
        public int Id { get; set; }
        public RowStatus Status { get; set; }
        public RowStatus? OptionalStatus { get; set; }

        [Column(TypeName = "integer")]
        public RowStatus ExtendedStatus { get; set; }
    }
}
