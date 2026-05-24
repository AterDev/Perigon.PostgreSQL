namespace Perigon.PostgreSQL.Tests.Models;

public sealed class ConventionUser
{
    public int Id { get; set; }

    public string UserName { get; set; } = "";

    public int Age { get; set; }

    public string? Status { get; set; }

    public string[]? Tags { get; set; }
}
