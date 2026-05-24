using Perigon.PostgreSQL.Attributes;

namespace Perigon.PostgreSQL.IntegrationTests;

public sealed class IntegrationUser
{
    public int Id { get; set; }

    public string UserName { get; set; } = "";

    public int Age { get; set; }

    public bool IsActive { get; set; }

    public string? Status { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public string[]? Tags { get; set; }

    [Column(TypeName = "jsonb")]
    public string? ProfileJson { get; set; }
}
