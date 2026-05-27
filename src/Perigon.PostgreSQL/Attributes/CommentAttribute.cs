namespace Perigon.PostgreSQL.Attributes;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
public sealed class CommentAttribute : Attribute
{
    public CommentAttribute(string text)
    {
        Text = text ?? throw new ArgumentNullException(nameof(text));
    }

    public string Text { get; }
}