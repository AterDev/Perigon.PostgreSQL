using System.Diagnostics;
using Npgsql;

namespace Perigon.PostgreSQL.IntegrationTests;

public sealed class DockerPostgresFixture : IAsyncLifetime
{
    private readonly string _containerName = "perigon-postgres-test-" + Guid.NewGuid().ToString("N")[..8];
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
            await WaitUntilReadyAsync().ConfigureAwait(false);
            await CreateSchemaAsync().ConfigureAwait(false);
            return;
        }

        var containerId = await RunAsync(
            "docker",
            $"run -d --name {_containerName} -e POSTGRES_PASSWORD=postgres -e POSTGRES_DB=perigon_test -p 127.0.0.1::5432 postgres:18.1-alpine")
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
        ConnectionString = $"Host=127.0.0.1;Port={port};Database=perigon_test;Username=postgres;Password=postgres;Timeout=5";
        await WaitUntilReadyAsync().ConfigureAwait(false);
        await CreateSchemaAsync().ConfigureAwait(false);
    }

    public async Task DisposeAsync()
    {
        if (_usesExternalConnectionString)
        {
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
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DROP TABLE IF EXISTS integration_offset_orders;
            DROP TABLE IF EXISTS integration_offset_checkpoints;
            DROP TABLE IF EXISTS integration_orders;
            DROP TABLE IF EXISTS integration_blogs;
            DROP TABLE IF EXISTS integration_users;

            CREATE TABLE integration_users (
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
            CREATE UNIQUE INDEX integration_users_user_name_uq ON integration_users (user_name);

            CREATE TABLE integration_blogs (
                id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                integration_user_id integer NOT NULL REFERENCES integration_users(id) ON DELETE CASCADE,
                name text NOT NULL,
                is_public boolean NOT NULL,
                created_at timestamp with time zone NOT NULL
            );

            CREATE TABLE integration_orders (
                id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                order_no text NOT NULL,
                status text NULL,
                order_time timestamp with time zone NOT NULL,
                total_price numeric(18, 2) NOT NULL
            );

            CREATE TABLE integration_offset_orders (
                id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                order_no text NOT NULL,
                status text NULL,
                order_time timestamp with time zone NOT NULL,
                total_price numeric(18, 2) NOT NULL
            );

            CREATE TABLE integration_offset_checkpoints (
                id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                checkpoint_no text NOT NULL,
                status text NULL,
                processed_at timestamp with time zone NULL
            );
            """;
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
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
