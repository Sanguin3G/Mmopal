using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mmopal.Contracts;

namespace Mmopal.Infrastructure;

public sealed class SessionReportStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _connectionString;

    static SessionReportStore()
    {
        SQLitePCL.Batteries_V2.Init();
    }

    public SessionReportStore(string databasePath)
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
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA busy_timeout=5000;
            CREATE TABLE IF NOT EXISTS trading_session_reports (
                id TEXT PRIMARY KEY,
                account_name TEXT NOT NULL DEFAULT '',
                run_mode TEXT NOT NULL,
                outcome TEXT NOT NULL,
                started_at TEXT NOT NULL,
                ended_at TEXT NOT NULL,
                duration_seconds REAL NOT NULL,
                total_trades INTEGER NOT NULL,
                error_count INTEGER NOT NULL,
                payload_json TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_trading_session_reports_started ON trading_session_reports(started_at DESC);
            CREATE INDEX IF NOT EXISTS ix_trading_session_reports_account ON trading_session_reports(account_name, started_at DESC);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveReportAsync(
        TradingSessionReport report,
        int maxSavedReports = 100,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        var limit = Math.Clamp(maxSavedReports, 10, 1000);
        var json = JsonSerializer.Serialize(report, JsonOptions);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var upsert = connection.CreateCommand())
        {
            upsert.Transaction = tx;
            upsert.CommandText = """
                INSERT INTO trading_session_reports (
                    id, account_name, run_mode, outcome, started_at, ended_at,
                    duration_seconds, total_trades, error_count, payload_json
                )
                VALUES (
                    $id, $account, $mode, $outcome, $started, $ended,
                    $duration, $trades, $errors, $payload
                )
                ON CONFLICT(id) DO UPDATE SET
                    account_name = excluded.account_name,
                    run_mode = excluded.run_mode,
                    outcome = excluded.outcome,
                    started_at = excluded.started_at,
                    ended_at = excluded.ended_at,
                    duration_seconds = excluded.duration_seconds,
                    total_trades = excluded.total_trades,
                    error_count = excluded.error_count,
                    payload_json = excluded.payload_json;
                """;
            upsert.Parameters.AddWithValue("$id", report.Id);
            upsert.Parameters.AddWithValue("$account", report.AccountName ?? string.Empty);
            upsert.Parameters.AddWithValue("$mode", report.RunMode);
            upsert.Parameters.AddWithValue("$outcome", report.Outcome);
            upsert.Parameters.AddWithValue("$started", report.StartedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            upsert.Parameters.AddWithValue("$ended", report.EndedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            upsert.Parameters.AddWithValue("$duration", report.DurationSeconds);
            upsert.Parameters.AddWithValue("$trades", report.TotalTradeActions);
            upsert.Parameters.AddWithValue("$errors", report.ErrorCount);
            upsert.Parameters.AddWithValue("$payload", json);
            await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var prune = connection.CreateCommand())
        {
            prune.Transaction = tx;
            prune.CommandText = """
                DELETE FROM trading_session_reports
                WHERE id NOT IN (
                    SELECT id FROM trading_session_reports
                    ORDER BY started_at DESC
                    LIMIT $limit
                );
                """;
            prune.Parameters.AddWithValue("$limit", limit);
            await prune.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TradingSessionReport>> GetReportsAsync(
        string? accountName = null,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        var effectiveLimit = Math.Clamp(limit, 1, 1000);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var hasAccount = !string.IsNullOrWhiteSpace(accountName);
        command.CommandText = hasAccount
            ? """
              SELECT payload_json FROM trading_session_reports
              WHERE account_name = $account
              ORDER BY started_at DESC
              LIMIT $limit;
              """
            : """
              SELECT payload_json FROM trading_session_reports
              ORDER BY started_at DESC
              LIMIT $limit;
              """;
        if (hasAccount)
        {
            command.Parameters.AddWithValue("$account", accountName!.Trim());
        }

        command.Parameters.AddWithValue("$limit", effectiveLimit);
        var reports = new List<TradingSessionReport>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var json = reader.GetString(0);
            try
            {
                var report = JsonSerializer.Deserialize<TradingSessionReport>(json, JsonOptions);
                if (report is not null)
                {
                    reports.Add(report);
                }
            }
            catch (JsonException)
            {
                // Skip malformed legacy rows instead of breaking report history load.
            }
        }

        return reports;
    }

    public async Task DeleteReportAsync(string id, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM trading_session_reports WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.Trim());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout=5000;";
        await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }
}
