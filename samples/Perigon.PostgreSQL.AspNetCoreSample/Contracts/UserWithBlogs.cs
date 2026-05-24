using Perigon.PostgreSQL.AspNetCoreSample.Models;

namespace Perigon.PostgreSQL.AspNetCoreSample.Contracts;

public sealed record UserWithBlogs(SampleUser User, List<SampleBlog> Blogs);
