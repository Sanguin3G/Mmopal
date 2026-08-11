using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Mmopal.Infrastructure;

public sealed class CheckpointStore
{
    private readonly string _connectionString;

    static CheckpointStore()
    {
        SQLitePCL.Batteries_V2.Init();
    }

    public CheckpointStore(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath) ?? ".");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS runs (
                id TEXT PRIMARY KEY,
                bot TEXT NOT NULL,
                mode TEXT NOT NULL,
                profile TEXT NOT NULL,
                started_at TEXT NOT NULL,
                stopped_at TEXT NULL,
                state TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS checkpoints (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                run_id TEXT NOT NULL,
                state TEXT NOT NULL,
                payload TEXT NOT NULL,
                created_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS actions (
                id TEXT PRIMARY KEY,
                run_id TEXT NOT NULL,
                action_type TEXT NOT NULL,
                item_name TEXT NULL,
                status TEXT NOT NULL,
                payload TEXT NULL,
                created_at TEXT NOT NULL,
                completed_at TEXT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveCheckpointAsync(
        string runId,
        string state,
        object payload,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO checkpoints(run_id, state, payload, created_at) VALUES ($runId, $state, $payload, $createdAt);";
        command.Parameters.AddWithValue("$runId", runId);
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(payload));
        command.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task StartRunAsync(
        string runId,
        string bot,
        string mode,
        string profile,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO runs(id, bot, mode, profile, started_at, state)
            VALUES ($id, $bot, $mode, $profile, $startedAt, $state)
            ON CONFLICT(id) DO UPDATE SET state = excluded.state;
            """;
        command.Parameters.AddWithValue("$id", runId);
        command.Parameters.AddWithValue("$bot", bot);
        command.Parameters.AddWithValue("$mode", mode);
        command.Parameters.AddWithValue("$profile", profile);
        command.Parameters.AddWithValue("$startedAt", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$state", "Starting");
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task CompleteRunAsync(
        string runId,
        string state,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE runs SET state = $state, stopped_at = $stoppedAt WHERE id = $id;";
        command.Parameters.AddWithValue("$id", runId);
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$stoppedAt", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveActionAsync(
        string actionId,
        string runId,
        string actionType,
        string? itemName,
        string status,
        object? payload = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO actions(id, run_id, action_type, item_name, status, payload, created_at)
            VALUES ($id, $runId, $actionType, $itemName, $status, $payload, $createdAt)
            ON CONFLICT(id) DO UPDATE SET status = excluded.status, completed_at = $completedAt;
            """;
        command.Parameters.AddWithValue("$id", actionId);
        command.Parameters.AddWithValue("$runId", runId);
        command.Parameters.AddWithValue("$actionType", actionType);
        command.Parameters.AddWithValue("$itemName", (object?)itemName ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$payload", payload is null ? DBNull.Value : JsonSerializer.Serialize(payload));
        command.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$completedAt", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
