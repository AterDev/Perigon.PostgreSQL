namespace Perigon.PostgreSQL.AspNetCoreSample.Contracts;

public sealed record CreateBlogRequest(int UserId, string Name, bool IsPublic);
