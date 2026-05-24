namespace Perigon.PostgreSQL.AspNetCoreSample.Contracts;

public sealed record CreateUserRequest(
    string UserName,
    int Age,
    bool IsActive,
    string? Status,
    string[]? Tags,
    string? ProfileJson);
