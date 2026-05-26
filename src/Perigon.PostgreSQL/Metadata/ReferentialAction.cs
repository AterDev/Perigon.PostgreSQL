namespace Perigon.PostgreSQL.Metadata;

public enum ReferentialAction
{
    NoAction,
    Restrict,
    Cascade,
    SetNull,
    SetDefault
}