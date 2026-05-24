namespace Perigon.PostgreSQL.IntegrationTests;

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<DockerPostgresFixture>
{
    public const string Name = "postgres";
}
