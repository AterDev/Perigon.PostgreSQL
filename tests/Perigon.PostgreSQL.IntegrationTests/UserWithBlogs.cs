namespace Perigon.PostgreSQL.IntegrationTests;

public sealed record UserWithBlogs(IntegrationUser User, List<IntegrationBlog> Blogs);
