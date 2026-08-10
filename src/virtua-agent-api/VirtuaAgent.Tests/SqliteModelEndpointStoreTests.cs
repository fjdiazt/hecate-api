using Microsoft.Data.Sqlite;
using VirtuaAgent.ModelEndpoints;

namespace VirtuaAgent.Tests;

public sealed class SqliteModelEndpointStoreTests
{
    [Fact]
    public async Task InitializeAddsTypeToLegacyRowsWithoutDiscriminator()
    {
        var path = Path.Combine(Path.GetTempPath(), $"virtua-agent-endpoints-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={path};Pooling=False";
        try
        {
            await using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync();
                var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE model_endpoints (
                      id TEXT PRIMARY KEY,
                      name TEXT NOT NULL,
                      base_url TEXT NOT NULL,
                      api_key TEXT NULL,
                      created_at TEXT NOT NULL,
                      updated_at TEXT NOT NULL
                    );
                    INSERT INTO model_endpoints VALUES (
                      'legacy', 'Legacy', 'http://localhost:8080', NULL,
                      '2026-01-01T00:00:00.0000000+00:00', '2026-01-01T00:00:00.0000000+00:00'
                    );
                    """;
                await command.ExecuteNonQueryAsync();
            }

            await using (var store = new SqliteModelEndpointStore(connectionString))
            {
                await store.InitializeAsync();
                var endpoint = await store.GetAsync("legacy");

                Assert.Equal(ModelEndpointTypes.OpenAiCompatible, endpoint!.Type);

                await using var connection = new SqliteConnection(connectionString);
                await connection.OpenAsync();
                var columns = connection.CreateCommand();
                columns.CommandText = "PRAGMA table_info(model_endpoints);";
                var names = new List<string>();
                await using var reader = await columns.ExecuteReaderAsync();
                while (await reader.ReadAsync()) names.Add(reader.GetString(1));
                Assert.Contains("type", names);
                Assert.DoesNotContain("kind", names);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task InitializeRenamesKindColumnAndPreservesValue()
    {
        var path = Path.Combine(Path.GetTempPath(), $"virtua-agent-endpoints-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={path};Pooling=False";
        try
        {
            await using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync();
                var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE model_endpoints (
                      id TEXT PRIMARY KEY,
                      name TEXT NOT NULL,
                      kind TEXT NOT NULL DEFAULT 'openai_compatible',
                      base_url TEXT NOT NULL,
                      api_key TEXT NULL,
                      created_at TEXT NOT NULL,
                      updated_at TEXT NOT NULL
                    );
                    INSERT INTO model_endpoints VALUES (
                      'codex', 'Codex', 'codex_subscription', '', NULL,
                      '2026-01-01T00:00:00.0000000+00:00', '2026-01-01T00:00:00.0000000+00:00'
                    );
                    """;
                await command.ExecuteNonQueryAsync();
            }

            await using var store = new SqliteModelEndpointStore(connectionString);
            await store.InitializeAsync();

            var endpoint = await store.GetAsync("codex");

            Assert.Equal(ModelEndpointTypes.CodexSubscription, endpoint!.Type);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SaveAndReloadCodexEndpointClearsHttpCredentials()
    {
        await using var store = new SqliteModelEndpointStore("Data Source=:memory:");
        await store.InitializeAsync();

        var endpoint = await store.SaveAsync(new SaveModelEndpointRequest
        {
            Id = "codex",
            Name = "Codex",
            Type = ModelEndpointTypes.CodexSubscription,
            BaseUrl = "http://ignored",
            ApiKey = "ignored"
        });

        Assert.Equal(ModelEndpointTypes.CodexSubscription, endpoint.Type);
        Assert.Equal("", endpoint.BaseUrl);
        Assert.Null(endpoint.ApiKey);
    }
}
