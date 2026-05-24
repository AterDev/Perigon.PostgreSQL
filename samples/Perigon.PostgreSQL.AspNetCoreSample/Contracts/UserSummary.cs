namespace Perigon.PostgreSQL.AspNetCoreSample.Contracts;

public sealed class UserSummary
{
    public int Id { get; set; }

    public string UserName { get; set; } = "";

    public int Age { get; set; }

    public string? Status { get; set; }
}
