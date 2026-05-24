using Npgsql;

namespace Perigon.PostgreSQL.AspNetCoreSample.Data;

public static class SampleDatabase
{
    public static async Task InitializeAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS sample_users (
                id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                user_name text NOT NULL UNIQUE,
                age integer NOT NULL,
                is_active boolean NOT NULL,
                status text NULL,
                created_at timestamp with time zone NOT NULL,
                tags text[] NULL,
                profile_json jsonb NULL
            );

            CREATE TABLE IF NOT EXISTS sample_blogs (
                id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                sample_user_id integer NOT NULL REFERENCES sample_users(id) ON DELETE CASCADE,
                name text NOT NULL,
                is_public boolean NOT NULL,
                created_at timestamp with time zone NOT NULL
            );

            CREATE TABLE IF NOT EXISTS sample_posts (
                id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                sample_blog_id integer NOT NULL REFERENCES sample_blogs(id) ON DELETE CASCADE,
                title text NOT NULL,
                published boolean NOT NULL,
                created_at timestamp with time zone NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
