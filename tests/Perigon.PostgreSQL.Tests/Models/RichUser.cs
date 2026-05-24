namespace Perigon.PostgreSQL.Tests.Models;

public sealed class RichUser
{
    public int Id { get; set; }

    public string UserName { get; set; } = "";

    public int Age { get; set; }

    public bool IsActive { get; set; }

    public string? Status { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public string[]? Tags { get; set; }

    public string? ProfileJson { get; set; }
}
