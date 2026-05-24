using Perigon.PostgreSQL.Attributes;

namespace Perigon.PostgreSQL.Tests.Models;

[Table("app_users", Schema = "security")]
public sealed class AttributedUser
{
    [Column("user_id", IsPrimaryKey = true, IsIdentity = true)]
    public int Key { get; set; }

    [Column("display_name")]
    public string Name { get; set; } = "";

    [Column("roles", IsArray = true)]
    public string[] Roles { get; set; } = [];

    [NotMapped]
    public string RuntimeOnly { get; set; } = "";
}
