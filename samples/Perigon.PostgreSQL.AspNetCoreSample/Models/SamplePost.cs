using Perigon.PostgreSQL.Attributes;

namespace Perigon.PostgreSQL.AspNetCoreSample.Models;

[Table("sample_posts")]
public sealed class SamplePost
{
    public int Id { get; set; }

    public int SampleBlogId { get; set; }

    public string Title { get; set; } = "";

    public bool Published { get; set; }

    public DateTime CreatedAt { get; set; }
}
