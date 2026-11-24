using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Perigon.PostgreSQL.Tests.Models;

[Table("app_users", Schema = "security")]
public sealed class AttributedUser
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    [Column("user_id")]
    public int Key { get; set; }

    [Column("display_name")]
    public string Name { get; set; } = "";

    [Column("roles")]
    public string[] Roles { get; set; } = [];

    [NotMapped]
    public string RuntimeOnly { get; set; } = "";
}
