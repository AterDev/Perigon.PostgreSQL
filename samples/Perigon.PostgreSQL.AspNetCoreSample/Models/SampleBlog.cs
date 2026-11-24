using System.ComponentModel.DataAnnotations.Schema;

namespace Perigon.PostgreSQL.AspNetCoreSample.Models;

[Table("sample_blogs")]
public sealed class SampleBlog
{
    public int Id { get; set; }

    public int SampleUserId { get; set; }

    public string Name { get; set; } = "";

    public bool IsPublic { get; set; }

    public DateTime CreatedAt { get; set; }
}
