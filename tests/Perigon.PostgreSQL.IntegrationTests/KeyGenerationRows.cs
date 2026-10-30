using Perigon.PostgreSQL.Attributes;

namespace Perigon.PostgreSQL.IntegrationTests;

public abstract class TimestampedGenerationEntity
{
    [AutoTimestamp]
    public DateTimeOffset CreatedTime { get; set; }

    [AutoTimestamp(onInsert: true, onUpdate: true)]
    public DateTimeOffset UpdatedTime { get; set; }
}

[Index(nameof(Code), IsUnique = true)]
public sealed class GuidGenerationRow : TimestampedGenerationEntity
{
    public Guid Id { get; set; }

    public string Code { get; set; } = "";

    public string? Value { get; set; }
}

[Index(nameof(Code), IsUnique = true)]
public sealed class LongGenerationRow : TimestampedGenerationEntity
{
    [Column(IsIdentity = true)]
    public long Id { get; set; }

    public string Code { get; set; } = "";
}

public sealed class StringGenerationRow : TimestampedGenerationEntity
{
    public string Id { get; set; } = "";

    public string Value { get; set; } = "";
}
