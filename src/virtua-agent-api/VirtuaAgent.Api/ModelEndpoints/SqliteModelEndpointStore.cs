using Microsoft.Data.Sqlite;

namespace VirtuaAgent.ModelEndpoints;

public sealed class SqliteModelEndpointStore : IModelEndpointStore, IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly SqliteConnection? _sharedConnection;

    public SqliteModelEndpointStore(string connectionString)
    {
        _connectionString = connectionString;
        if (connectionString.Contains(":memory:", StringComparison.OrdinalIgnoreCase))
        {
            _sharedConnection = new SqliteConnection(connectionString);
            _sharedConnection.Open();
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var connection = await OpenConnectionAsync(cancellationToken);
        await using var cleanup = ConnectionCleanup.Create(connection, _sharedConnection is null);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS model_endpoints (
              id TEXT PRIMARY KEY,
              name TEXT NOT NULL,
              type TEXT NOT NULL DEFAULT 'openai_compatible',
              base_url TEXT NOT NULL,
              api_key TEXT NULL,
              created_at TEXT NOT NULL,
              updated_at TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);

        await using var columnsCommand = connection.CreateCommand();
        columnsCommand.Transaction = transaction;
        columnsCommand.CommandText = "PRAGMA table_info(model_endpoints);";
        var hasKind = false;
        var hasType = false;
        await using (var reader = await columnsCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                hasKind |= string.Equals(reader.GetString(1), "kind", StringComparison.OrdinalIgnoreCase);
                hasType |= string.Equals(reader.GetString(1), "type", StringComparison.OrdinalIgnoreCase);
            }
        }

        if (hasKind && !hasType)
        {
            await using var migration = connection.CreateCommand();
            migration.Transaction = transaction;
            migration.CommandText = "ALTER TABLE model_endpoints RENAME COLUMN kind TO type;";
            await migration.ExecuteNonQueryAsync(cancellationToken);
        }
        else if (!hasType)
        {
            await using var migration = connection.CreateCommand();
            migration.Transaction = transaction;
            migration.CommandText = "ALTER TABLE model_endpoints ADD COLUMN type TEXT NOT NULL DEFAULT 'openai_compatible';";
            await migration.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ModelEndpointDefinition>> ListAsync(CancellationToken cancellationToken = default)
    {
        var connection = await OpenConnectionAsync(cancellationToken);
        await using var cleanup = ConnectionCleanup.Create(connection, _sharedConnection is null);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, type, base_url, api_key, created_at, updated_at
            FROM model_endpoints
            ORDER BY name ASC, id ASC;
            """;

        var endpoints = new List<ModelEndpointDefinition>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            endpoints.Add(ReadEndpoint(reader));
        }

        return endpoints;
    }

    public async Task<ModelEndpointDefinition?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        var connection = await OpenConnectionAsync(cancellationToken);
        await using var cleanup = ConnectionCleanup.Create(connection, _sharedConnection is null);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, type, base_url, api_key, created_at, updated_at
            FROM model_endpoints
            WHERE id = $id;
            """;
        Add(command, "$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadEndpoint(reader) : null;
    }

    public async Task<ModelEndpointDefinition> SaveAsync(SaveModelEndpointRequest request, CancellationToken cancellationToken = default)
    {
        var id = string.IsNullOrWhiteSpace(request.Id) ? "endpoint_" + Guid.NewGuid().ToString("N") : request.Id.Trim();
        var name = request.Name.Trim();
        var type = string.IsNullOrWhiteSpace(request.Type)
            ? ModelEndpointTypes.OpenAiCompatible
            : request.Type.Trim().ToLowerInvariant();
        var isCodex = type == ModelEndpointTypes.CodexSubscription;
        var baseUrl = isCodex ? "" : (request.BaseUrl ?? "").Trim().TrimEnd('/');
        var now = DateTimeOffset.UtcNow;
        var existing = await GetAsync(id, cancellationToken);
        var apiKey = isCodex
            ? null
            : request.ApiKey is null ? existing?.ApiKey : NormalizeSecret(request.ApiKey);

        var connection = await OpenConnectionAsync(cancellationToken);
        await using var cleanup = ConnectionCleanup.Create(connection, _sharedConnection is null);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO model_endpoints (id, name, type, base_url, api_key, created_at, updated_at)
            VALUES ($id, $name, $type, $base_url, $api_key, $now, $now)
            ON CONFLICT(id) DO UPDATE SET
              name = excluded.name,
              type = excluded.type,
              base_url = excluded.base_url,
              api_key = excluded.api_key,
              updated_at = excluded.updated_at;
            """;
        Add(command, "$id", id);
        Add(command, "$name", name);
        Add(command, "$type", type);
        Add(command, "$base_url", baseUrl);
        Add(command, "$api_key", apiKey);
        Add(command, "$now", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);

        return (await GetAsync(id, cancellationToken))!;
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        var connection = await OpenConnectionAsync(cancellationToken);
        await using var cleanup = ConnectionCleanup.Create(connection, _sharedConnection is null);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM model_endpoints WHERE id = $id;";
        Add(command, "$id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async ValueTask DisposeAsync()
    {
        if (_sharedConnection is not null)
        {
            await _sharedConnection.DisposeAsync();
        }
    }

    private static ModelEndpointDefinition ReadEndpoint(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        Name = reader.GetString(1),
        Type = reader.GetString(2),
        BaseUrl = reader.GetString(3),
        ApiKey = reader.IsDBNull(4) ? null : reader.GetString(4),
        CreatedAt = DateTimeOffset.Parse(reader.GetString(5)),
        UpdatedAt = DateTimeOffset.Parse(reader.GetString(6))
    };

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        if (_sharedConnection is not null) return _sharedConnection;

        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static string? NormalizeSecret(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void Add(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private sealed class ConnectionCleanup : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly bool _dispose;

        private ConnectionCleanup(SqliteConnection connection, bool dispose)
        {
            _connection = connection;
            _dispose = dispose;
        }

        public static ConnectionCleanup Create(SqliteConnection connection, bool dispose) => new(connection, dispose);

        public async ValueTask DisposeAsync()
        {
            if (_dispose) await _connection.DisposeAsync();
        }
    }
}
