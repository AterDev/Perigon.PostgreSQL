using System.Diagnostics;
using Npgsql;

namespace Perigon.PostgreSQL.IntegrationTests;

public sealed class DockerPostgresFixture : IAsyncLifetime
{
    private readonly string _containerName = "perigon-postgres-test-" + Guid.NewGuid().ToString("N")[..8];
    private readonly string _schemaName = "perigon_integration_" + Guid.NewGuid().ToString("N")[..12];
    private readonly HashSet<string> _ownedSchemas = new(StringComparer.Ordinal);
    private bool _usesExternalConnectionString;

    public string ConnectionString { get; private set; } = "";

    public async Task InitializeAsync()
    {
        var externalConnectionString = Environment.GetEnvironmentVariable("PERIGON_POSTGRES_TEST_CONNECTION_STRING");
        if (!string.IsNullOrWhiteSpace(externalConnectionString))
        {
            _usesExternalConnectionString = true;
            ConnectionString = externalConnectionString;
            await EnsureDatabaseExistsAsync().ConfigureAwait(false);
            ConnectionString = ApplySchemaSearchPath(ConnectionString);
            await WaitUntilReadyAsync().ConfigureAwait(false);
            await CreateSchemaAsync().ConfigureAwait(false);
            return;
        }

        var image = ReadContainerImage();
        var containerId = await RunAsync(
            "docker",
            $"run -d --name {_containerName} -e POSTGRES_PASSWORD=postgres -e POSTGRES_DB=perigon_test -p 127.0.0.1::5432 {image}")
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(containerId))
        {
            throw new InvalidOperationException("Docker did not return a container id.");
        }

        var portOutput = "";
        for (var i = 0; i < 20; i++)
        {
            portOutput = await RunAsync("docker", $"port {_containerName} 5432/tcp").ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(portOutput))
            {
                break;
            }

            await Task.Delay(250).ConfigureAwait(false);
        }

        var port = portOutput.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Last();
        ConnectionString = ApplySchemaSearchPath(
            $"Host=127.0.0.1;Port={port};Database=perigon_test;Username=postgres;Password=postgres;Timeout=5");
        await WaitUntilReadyAsync().ConfigureAwait(false);
        await CreateSchemaAsync().ConfigureAwait(false);
    }

    public async Task DisposeAsync()
    {
        if (_usesExternalConnectionString)
        {
            await DropOwnedSchemasAsync().ConfigureAwait(false);
            return;
        }

        _ = await RunAsync("docker", $"rm -f {_containerName}").ConfigureAwait(false);
    }

    private async Task WaitUntilReadyAsync()
    {
        Exception? last = null;
        for (var i = 0; i < 60; i++)
        {
            try
            {
                await using var connection = new NpgsqlConnection(ConnectionString);
                await connection.OpenAsync().ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                await Task.Delay(500).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException("PostgreSQL container did not become ready.", last);
    }

    private async Task EnsureDatabaseExistsAsync()
    {
        var target = new NpgsqlConnectionStringBuilder(ConnectionString);
        var databaseName = target.Database;
        if (string.IsNullOrWhiteSpace(databaseName))
        {
            return;
        }

        var admin = new NpgsqlConnectionStringBuilder(ConnectionString)
        {
            Database = "postgres"
        };

        await using var connection = new NpgsqlConnection(admin.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT 1 FROM pg_database WHERE datname = @name";
        exists.Parameters.AddWithValue("name", databaseName);
        var present = await exists.ExecuteScalarAsync().ConfigureAwait(false);
        if (present is not null)
        {
            return;
        }

        await using var create = connection.CreateCommand();
        create.CommandText = $"CREATE DATABASE \"{databaseName.Replace("\"", "\"\"")}\"";
        await create.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private async Task CreateSchemaAsync()
    {
        lock (_ownedSchemas)
        {
            _ownedSchemas.Add(_schemaName);
        }

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var schema = QuoteIdentifier(_schemaName);
        command.CommandText = $"""
            DROP SCHEMA IF EXISTS {schema} CASCADE;
            CREATE SCHEMA {schema};

            CREATE TABLE {schema}.integration_users (
                id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                user_name text NOT NULL,
                age integer NOT NULL,
                is_active boolean NOT NULL,
                status text NULL,
                created_at timestamp with time zone NOT NULL,
                updated_at timestamp with time zone NULL,
                tags text[] NULL,
                profile_json jsonb NULL
            );
            CREATE UNIQUE INDEX integration_users_user_name_uq ON {schema}.integration_users (user_name);

            CREATE TABLE {schema}.integration_blogs (
                id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                integration_user_id integer NOT NULL REFERENCES {schema}.integration_users(id) ON DELETE CASCADE,
                name text NOT NULL,
                is_public boolean NOT NULL,
                created_at timestamp with time zone NOT NULL
            );

            CREATE TABLE {schema}.integration_orders (
                id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                order_no text NOT NULL,
                status text NULL,
                order_time timestamp with time zone NOT NULL,
                total_price numeric(18, 2) NOT NULL
            );

            CREATE TABLE {schema}.integration_offset_orders (
                id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                order_no text NOT NULL,
                status text NULL,
                order_time timestamp with time zone NOT NULL,
                total_price numeric(18, 2) NOT NULL
            );

            CREATE TABLE {schema}.integration_offset_checkpoints (
                id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                checkpoint_no text NOT NULL,
                status text NULL,
                processed_at timestamp with time zone NULL
            );
            """;
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public string CreateIsolatedSchema(string purpose)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        if (purpose.Any(character => !(char.IsLetterOrDigit(character) || character == '_')))
        {
            throw new ArgumentException("Schema purpose may contain only letters, digits, and underscores.", nameof(purpose));
        }

        var schema = $"{_schemaName}_{purpose}";
        if (schema.Length > 63)
        {
            throw new ArgumentException("Schema purpose is too long.", nameof(purpose));
        }

        lock (_ownedSchemas)
        {
            _ownedSchemas.Add(schema);
        }

        return schema;
    }

    private async Task DropOwnedSchemasAsync()
    {
        string[] schemas;
        lock (_ownedSchemas)
        {
            schemas = _ownedSchemas.ToArray();
        }

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = string.Join(
            Environment.NewLine,
            schemas.Select(schema => $"DROP SCHEMA IF EXISTS {QuoteIdentifier(schema)} CASCADE;"));
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private string ApplySchemaSearchPath(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            SearchPath = _schemaName
        };
        return builder.ConnectionString;
    }

    private static string QuoteIdentifier(string value)
    {
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static string ReadContainerImage()
    {
        var image = Environment.GetEnvironmentVariable("PERIGON_POSTGRES_IMAGE");
        if (string.IsNullOrWhiteSpace(image))
        {
            return "postgres:18.1-alpine@sha256:aa6eb304ddb6dd26df23d05db4e5cb05af8951cda3e0dc57731b771e0ef4ab29";
        }

        if (image.Any(character => !(char.IsLetterOrDigit(character) || character is '.' or '/' or ':' or '@' or '_' or '-')))
        {
            throw new InvalidOperationException("PERIGON_POSTGRES_IMAGE contains unsupported characters.");
        }

        return image;
    }

    private static async Task<string> RunAsync(string fileName, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException($"Failed to start {fileName}.");

        var output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        var error = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{fileName} {arguments} failed with exit code {process.ExitCode}: {error}");
        }

        return output.Trim();
    }
}
