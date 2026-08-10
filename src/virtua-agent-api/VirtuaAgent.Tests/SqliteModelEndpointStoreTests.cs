using Microsoft.Data.Sqlite;
using VirtuaAgent.ModelEndpoints;

namespace VirtuaAgent.Tests;

public sealed class SqliteModelEndpointStoreTests
{
    [Fact]
    public async Task InitializeMigratesLegacyRowsToOpenAiCompatible()
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

                Assert.Equal(ModelEndpointKinds.OpenAiCompatible, endpoint!.Kind);
            }
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
            Kind = ModelEndpointKinds.CodexSubscription,
            BaseUrl = "http://ignored",
            ApiKey = "ignored"
        });

        Assert.Equal(ModelEndpointKinds.CodexSubscription, endpoint.Kind);
        Assert.Equal("", endpoint.BaseUrl);
        Assert.Null(endpoint.ApiKey);
    }
}
