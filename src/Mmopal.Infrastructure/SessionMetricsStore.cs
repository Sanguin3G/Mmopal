using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mmopal.Contracts;

namespace Mmopal.Infrastructure;

/// <summary>
/// Per-cycle metrics and per-item events of live sessions, written after every cycle so a crash loses at most one.
/// The payloads are JSON: new fields need no migration, and SQLite's json_extract reads them.
/// </summary>
public sealed class SessionMetricsStore
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(180);
    private readonly string _connectionString;

    static SessionMetricsStore()
    {
        SQLitePCL.Batteries_V2.Init();
    }

    public SessionMetricsStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath)) ?? ".");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 5
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA busy_timeout=5000;
            CREATE TABLE IF NOT EXISTS cycle_metrics (
                run_id TEXT NOT NULL,
                at TEXT NOT NULL,
                payload TEXT NOT NULL,
                PRIMARY KEY (run_id, at)
            );
            CREATE TABLE IF NOT EXISTS session_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                run_id TEXT NOT NULL,
                at TEXT NOT NULL,
                kind TEXT NOT NULL,
                outcome TEXT NOT NULL,
                item_name TEXT NULL,
                character TEXT NULL,
                unit_copper INTEGER NULL,
                detail TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_cycle_metrics_at ON cycle_metrics(at);
            CREATE INDEX IF NOT EXISTS ix_session_events_run ON session_events(run_id, kind);
            CREATE INDEX IF NOT EXISTS ix_session_events_at ON session_events(at);
            DELETE FROM cycle_metrics WHERE at < $cutoff;
            DELETE FROM session_events WHERE at < $cutoff;
            """;
        command.Parameters.AddWithValue("$cutoff", Text(DateTimeOffset.UtcNow - Retention));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveAsync(
        string runId,
        CycleMetrics? cycle,
        IReadOnlyList<SessionEvent> events,
        CancellationToken cancellationToken = default)
    {
        if (cycle is null && events.Count == 0)
        {
            return;
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (cycle is not null)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT OR REPLACE INTO cycle_metrics(run_id, at, payload) VALUES ($run, $at, $payload);";
            command.Parameters.AddWithValue("$run", runId);
            command.Parameters.AddWithValue("$at", Text(cycle.At));
            command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(cycle));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var item in events)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO session_events(run_id, at, kind, outcome, item_name, character, unit_copper, detail)
                VALUES ($run, $at, $kind, $outcome, $item, $character, $unit, $detail);
                """;
            command.Parameters.AddWithValue("$run", runId);
            command.Parameters.AddWithValue("$at", Text(item.At));
            command.Parameters.AddWithValue("$kind", item.Kind);
            command.Parameters.AddWithValue("$outcome", item.Outcome);
            command.Parameters.AddWithValue("$item", (object?)item.ItemName ?? DBNull.Value);
            command.Parameters.AddWithValue("$character", (object?)item.Character ?? DBNull.Value);
            command.Parameters.AddWithValue("$unit", (object?)item.UnitCopper ?? DBNull.Value);
            command.Parameters.AddWithValue("$detail", (object?)item.Detail ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CycleMetrics>> GetCyclesAsync(string runId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM cycle_metrics WHERE run_id = $run ORDER BY at;";
        command.Parameters.AddWithValue("$run", runId);
        var cycles = new List<CycleMetrics>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                if (JsonSerializer.Deserialize<CycleMetrics>(reader.GetString(0)) is { } cycle) cycles.Add(cycle);
            }
            catch (JsonException)
            {
                // A row from a version with another shape: skip it.
            }
        }

        return cycles;
    }

    public async Task<IReadOnlyList<SessionEvent>> GetEventsAsync(string runId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT at, kind, outcome, item_name, character, unit_copper, detail FROM session_events WHERE run_id = $run ORDER BY id;";
        command.Parameters.AddWithValue("$run", runId);
        var events = new List<SessionEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            events.Add(new SessionEvent(
                DateTimeOffset.Parse(reader.GetString(0), CultureInfo.InvariantCulture),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetInt64(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        return events;
    }

    private static string Text(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }
}
