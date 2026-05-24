using Perigon.PostgreSQL.Attributes;

namespace Perigon.PostgreSQL.AspNetCoreSample.Models;

[Table("sample_users")]
public sealed class SampleUser
{
    public int Id { get; set; }

    public string UserName { get; set; } = "";

    public int Age { get; set; }

    public bool IsActive { get; set; }

    public string? Status { get; set; }

    public DateTime CreatedAt { get; set; }

    public string[]? Tags { get; set; }

    [Column(TypeName = "jsonb")]
    public string? ProfileJson { get; set; }
}
