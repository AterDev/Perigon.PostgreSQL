namespace Perigon.PostgreSQL.AspNetCoreSample.Contracts;

public sealed record CreatePostRequest(int BlogId, string Title, bool Published);
