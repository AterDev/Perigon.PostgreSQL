namespace Perigon.PostgreSQL.NativeAotSmoke;

public sealed class SmokeUser
{
    public int Id { get; set; }

    public string UserName { get; set; } = "";

    public int Age { get; set; }

    public string[]? Tags { get; set; }
}
