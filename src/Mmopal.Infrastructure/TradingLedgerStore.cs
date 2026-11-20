using System.Globalization;
using Microsoft.Data.Sqlite;
using Mmopal.Contracts;

namespace Mmopal.Infrastructure;

public sealed class TradingLedgerStore
{
    private readonly string _connectionString;

    static TradingLedgerStore()
    {
        SQLitePCL.Batteries_V2.Init();
    }

    public TradingLedgerStore(string databasePath)
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
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA busy_timeout=5000;
                CREATE TABLE IF NOT EXISTS gw2_transactions (
                    id INTEGER PRIMARY KEY,
                    account_name TEXT NOT NULL DEFAULT '',
                    kind TEXT NOT NULL,
                    item_id INTEGER NOT NULL,
                    item_name TEXT NOT NULL,
                    price INTEGER NOT NULL,
                    quantity INTEGER NOT NULL,
                    created_at TEXT NOT NULL,
                    purchased_at TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_gw2_transactions_purchased ON gw2_transactions(purchased_at, id);
                CREATE TABLE IF NOT EXISTS gw2_equity_snapshots (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    account_name TEXT NOT NULL DEFAULT '',
                    captured_at TEXT NOT NULL,
                    wallet_copper INTEGER NOT NULL,
                    delivery_copper INTEGER NOT NULL,
                    delivery_items_copper INTEGER NOT NULL,
                    active_buys_copper INTEGER NOT NULL,
                    active_sells_net_copper INTEGER NOT NULL,
                    total_equity_copper INTEGER NOT NULL,
                    open_buy_count INTEGER NOT NULL,
                    open_sell_count INTEGER NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_gw2_equity_captured ON gw2_equity_snapshots(captured_at, id);
                CREATE TABLE IF NOT EXISTS gw2_item_cache (
                    item_id INTEGER PRIMARY KEY,
                    name TEXT NOT NULL,
                    rarity TEXT NOT NULL,
                    item_type TEXT NOT NULL,
                    vendor_value INTEGER NOT NULL,
                    updated_at TEXT NOT NULL
                );
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await EnsureColumnExistsAsync(connection, "gw2_transactions", "account_name", "TEXT NOT NULL DEFAULT ''", cancellationToken).ConfigureAwait(false);
        await EnsureColumnExistsAsync(connection, "gw2_equity_snapshots", "account_name", "TEXT NOT NULL DEFAULT ''", cancellationToken).ConfigureAwait(false);

        await using var idxCmd = connection.CreateCommand();
        idxCmd.CommandText = """
            CREATE INDEX IF NOT EXISTS ix_gw2_transactions_account ON gw2_transactions(account_name, purchased_at, id);
            CREATE INDEX IF NOT EXISTS ix_gw2_equity_account ON gw2_equity_snapshots(account_name, captured_at, id);
            """;
        await idxCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task AdoptUnassignedRowsAsync(string accountName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accountName))
        {
            return;
        }

        var trimmed = accountName.Trim();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var checkCmd = connection.CreateCommand();
        checkCmd.CommandText = """
            SELECT COUNT(*) FROM gw2_transactions WHERE account_name <> '' AND account_name <> $acct;
            """;
        checkCmd.Parameters.AddWithValue("$acct", trimmed);
        var otherAccountsCount = Convert.ToInt64(await checkCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        if (otherAccountsCount > 0)
        {
            return;
        }

        await using var updateTx = connection.CreateCommand();
        updateTx.CommandText = "UPDATE gw2_transactions SET account_name = $acct WHERE account_name = '';";
        updateTx.Parameters.AddWithValue("$acct", trimmed);
        await updateTx.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using var updateEq = connection.CreateCommand();
        updateEq.CommandText = "UPDATE gw2_equity_snapshots SET account_name = $acct WHERE account_name = '';";
        updateEq.Parameters.AddWithValue("$acct", trimmed);
        await updateEq.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<int> UpsertTransactionsAsync(
        IEnumerable<Gw2LedgerTransaction> rows,
        CancellationToken cancellationToken = default) =>
        UpsertTransactionsAsync(rows, string.Empty, cancellationToken);

    public async Task<int> UpsertTransactionsAsync(
        IEnumerable<Gw2LedgerTransaction> rows,
        string accountName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var list = rows as IReadOnlyList<Gw2LedgerTransaction> ?? rows.ToArray();
        if (list.Count == 0)
        {
            return 0;
        }

        var acct = accountName?.Trim() ?? string.Empty;
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var beforeCount = await CountTransactionsAsync(connection, tx, cancellationToken).ConfigureAwait(false);

        await using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO gw2_transactions (id, account_name, kind, item_id, item_name, price, quantity, created_at, purchased_at)
            VALUES ($id, $acct, $kind, $itemId, $itemName, $price, $quantity, $createdAt, $purchasedAt)
            ON CONFLICT(id) DO UPDATE SET
                item_name = CASE WHEN excluded.item_name <> '' THEN excluded.item_name ELSE gw2_transactions.item_name END,
                account_name = CASE WHEN gw2_transactions.account_name = '' AND excluded.account_name <> '' THEN excluded.account_name ELSE gw2_transactions.account_name END;
            """;
        var pId = cmd.Parameters.Add("$id", SqliteType.Integer);
        var pAcct = cmd.Parameters.Add("$acct", SqliteType.Text);
        var pKind = cmd.Parameters.Add("$kind", SqliteType.Text);
        var pItemId = cmd.Parameters.Add("$itemId", SqliteType.Integer);
        var pItemName = cmd.Parameters.Add("$itemName", SqliteType.Text);
        var pPrice = cmd.Parameters.Add("$price", SqliteType.Integer);
        var pQty = cmd.Parameters.Add("$quantity", SqliteType.Integer);
        var pCreated = cmd.Parameters.Add("$createdAt", SqliteType.Text);
        var pPurchased = cmd.Parameters.Add("$purchasedAt", SqliteType.Text);

        foreach (var row in list)
        {
            pId.Value = row.Id;
            pAcct.Value = acct;
            pKind.Value = row.Kind.ToString();
            pItemId.Value = row.ItemId;
            pItemName.Value = row.ItemName ?? string.Empty;
            pPrice.Value = row.UnitPriceCopper;
            pQty.Value = row.Quantity;
            pCreated.Value = row.CreatedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
            pPurchased.Value = row.PurchasedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var afterCount = await CountTransactionsAsync(connection, tx, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Math.Max(0, (int)(afterCount - beforeCount));
    }

    public Task<IReadOnlyList<Gw2LedgerTransaction>> GetTransactionsAsync(CancellationToken cancellationToken = default) =>
        GetTransactionsAsync(null, cancellationToken);

    public async Task<IReadOnlyList<Gw2LedgerTransaction>> GetTransactionsAsync(
        string? accountName,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        if (string.IsNullOrWhiteSpace(accountName))
        {
            command.CommandText = """
                SELECT id, kind, item_id, item_name, price, quantity, created_at, purchased_at
                FROM gw2_transactions
                ORDER BY purchased_at ASC, id ASC;
                """;
        }
        else
        {
            command.CommandText = """
                SELECT id, kind, item_id, item_name, price, quantity, created_at, purchased_at
                FROM gw2_transactions
                WHERE account_name = $acct
                ORDER BY purchased_at ASC, id ASC;
                """;
            command.Parameters.AddWithValue("$acct", accountName.Trim());
        }

        var results = new List<Gw2LedgerTransaction>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var id = reader.GetInt64(0);
            var kind = Enum.Parse<TransactionType>(reader.GetString(1), ignoreCase: true);
            var itemId = reader.GetInt32(2);
            var itemName = reader.GetString(3);
            var price = reader.GetInt64(4);
            var qty = reader.GetInt64(5);
            var createdAt = DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            var purchasedAt = DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            results.Add(new Gw2LedgerTransaction(id, kind, itemId, itemName, price, qty, createdAt, purchasedAt));
        }

        return results;
    }

    public Task SaveEquitySnapshotAsync(Gw2EquitySnapshot snapshot, CancellationToken cancellationToken = default) =>
        SaveEquitySnapshotAsync(snapshot, string.Empty, cancellationToken);

    public async Task SaveEquitySnapshotAsync(
        Gw2EquitySnapshot snapshot,
        string accountName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO gw2_equity_snapshots (
                account_name, captured_at, wallet_copper, delivery_copper, delivery_items_copper,
                active_buys_copper, active_sells_net_copper, total_equity_copper,
                open_buy_count, open_sell_count
            )
            VALUES (
                $acct, $capturedAt, $wallet, $delivery, $deliveryItems,
                $activeBuys, $activeSellsNet, $totalEquity,
                $openBuyCount, $openSellCount
            );
            """;
        command.Parameters.AddWithValue("$acct", accountName?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$capturedAt", snapshot.CapturedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$wallet", snapshot.WalletCopper);
        command.Parameters.AddWithValue("$delivery", snapshot.DeliveryCopper);
        command.Parameters.AddWithValue("$deliveryItems", snapshot.DeliveryItemsCopper);
        command.Parameters.AddWithValue("$activeBuys", snapshot.ActiveBuysCopper);
        command.Parameters.AddWithValue("$activeSellsNet", snapshot.ActiveSellsNetCopper);
        command.Parameters.AddWithValue("$totalEquity", snapshot.TotalEquityCopper);
        command.Parameters.AddWithValue("$openBuyCount", snapshot.OpenBuyCount);
        command.Parameters.AddWithValue("$openSellCount", snapshot.OpenSellCount);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<Gw2EquitySnapshot>> GetEquitySnapshotsAsync(
        DateTimeOffset? sinceUtc = null,
        CancellationToken cancellationToken = default) =>
        GetEquitySnapshotsAsync(sinceUtc, null, cancellationToken);

    public async Task<IReadOnlyList<Gw2EquitySnapshot>> GetEquitySnapshotsAsync(
        DateTimeOffset? sinceUtc,
        string? accountName,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var filters = new List<string>();
        if (sinceUtc.HasValue)
        {
            filters.Add("captured_at >= $since");
            command.Parameters.AddWithValue("$since", sinceUtc.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrWhiteSpace(accountName))
        {
            filters.Add("account_name = $acct");
            command.Parameters.AddWithValue("$acct", accountName.Trim());
        }

        var where = filters.Count > 0 ? "WHERE " + string.Join(" AND ", filters) : string.Empty;
        command.CommandText = $"""
            SELECT captured_at, wallet_copper, delivery_copper, delivery_items_copper,
                   active_buys_copper, active_sells_net_copper, total_equity_copper,
                   open_buy_count, open_sell_count
            FROM gw2_equity_snapshots
            {where}
            ORDER BY captured_at ASC, id ASC;
            """;

        var results = new List<Gw2EquitySnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new Gw2EquitySnapshot(
                CapturedAt: DateTimeOffset.Parse(reader.GetString(0), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                WalletCopper: reader.GetInt64(1),
                DeliveryCopper: reader.GetInt64(2),
                DeliveryItemsCopper: reader.GetInt64(3),
                ActiveBuysCopper: reader.GetInt64(4),
                ActiveSellsNetCopper: reader.GetInt64(5),
                TotalEquityCopper: reader.GetInt64(6),
                OpenBuyCount: reader.GetInt32(7),
                OpenSellCount: reader.GetInt32(8)));
        }

        return results;
    }

    public async Task UpsertItemsAsync(
        IEnumerable<Gw2CachedItemInfo> items,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        var list = items as IReadOnlyList<Gw2CachedItemInfo> ?? items.ToArray();
        if (list.Count == 0)
        {
            return;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO gw2_item_cache (item_id, name, rarity, item_type, vendor_value, updated_at)
            VALUES ($id, $name, $rarity, $type, $vendor, $updated)
            ON CONFLICT(item_id) DO UPDATE SET
                name = excluded.name,
                rarity = excluded.rarity,
                item_type = excluded.item_type,
                vendor_value = excluded.vendor_value,
                updated_at = excluded.updated_at;
            """;
        var pId = cmd.Parameters.Add("$id", SqliteType.Integer);
        var pName = cmd.Parameters.Add("$name", SqliteType.Text);
        var pRarity = cmd.Parameters.Add("$rarity", SqliteType.Text);
        var pType = cmd.Parameters.Add("$type", SqliteType.Text);
        var pVendor = cmd.Parameters.Add("$vendor", SqliteType.Integer);
        var pUpdated = cmd.Parameters.Add("$updated", SqliteType.Text);

        foreach (var item in list)
        {
            pId.Value = item.ItemId;
            pName.Value = item.Name ?? string.Empty;
            pRarity.Value = item.Rarity ?? "Basic";
            pType.Value = item.ItemType ?? string.Empty;
            pVendor.Value = item.VendorValueCopper;
            pUpdated.Value = item.UpdatedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<Dictionary<int, Gw2CachedItemInfo>> GetCachedItemsAsync(
        IEnumerable<int>? itemIds = null,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<int, Gw2CachedItemInfo>();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        if (itemIds is null)
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT item_id, name, rarity, item_type, vendor_value, updated_at FROM gw2_item_cache;";
            await ReadItemsIntoAsync(cmd, result, cancellationToken).ConfigureAwait(false);
            return result;
        }

        var distinctIds = itemIds.Distinct().ToArray();
        if (distinctIds.Length == 0)
        {
            return result;
        }

        foreach (var chunk in distinctIds.Chunk(500))
        {
            await using var cmd = connection.CreateCommand();
            var paramNames = new string[chunk.Length];
            for (var i = 0; i < chunk.Length; i++)
            {
                var name = $"$p{i}";
                paramNames[i] = name;
                cmd.Parameters.AddWithValue(name, chunk[i]);
            }

            cmd.CommandText = $"""
                SELECT item_id, name, rarity, item_type, vendor_value, updated_at
                FROM gw2_item_cache
                WHERE item_id IN ({string.Join(',', paramNames)});
                """;
            await ReadItemsIntoAsync(cmd, result, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    public async Task<Dictionary<string, int>> GetItemIdsByNamesAsync(
        IEnumerable<string> itemNames,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var distinctNames = itemNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (distinctNames.Length == 0)
        {
            return result;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var chunk in distinctNames.Chunk(400))
        {
            await using var cmd = connection.CreateCommand();
            var paramNames = new string[chunk.Length];
            for (var i = 0; i < chunk.Length; i++)
            {
                var paramName = $"$n{i}";
                paramNames[i] = paramName;
                cmd.Parameters.AddWithValue(paramName, chunk[i]);
            }

            cmd.CommandText = $"""
                SELECT name, item_id
                FROM gw2_item_cache
                WHERE name IN ({string.Join(',', paramNames)}) COLLATE NOCASE;
                """;
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var name = reader.GetString(0).Trim();
                var itemId = reader.GetInt32(1);
                if (!string.IsNullOrEmpty(name) && itemId > 0)
                {
                    result.TryAdd(name, itemId);
                }
            }
        }

        return result;
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

    private static async Task EnsureColumnExistsAsync(
        SqliteConnection connection,
        string tableName,
        string columnName,
        string definitionSql,
        CancellationToken cancellationToken)
    {
        await using var checkCmd = connection.CreateCommand();
        checkCmd.CommandText = $"PRAGMA table_info({tableName});";
        var found = false;
        await using (var reader = await checkCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    found = true;
                    break;
                }
            }
        }

        if (!found)
        {
            await using var alterCmd = connection.CreateCommand();
            alterCmd.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {definitionSql};";
            await alterCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<long> CountTransactionsAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        CancellationToken cancellationToken)
    {
        await using var countCmd = connection.CreateCommand();
        countCmd.Transaction = tx;
        countCmd.CommandText = "SELECT COUNT(*) FROM gw2_transactions;";
        return Convert.ToInt64(await countCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private static async Task ReadItemsIntoAsync(
        SqliteCommand cmd,
        Dictionary<int, Gw2CachedItemInfo> destination,
        CancellationToken cancellationToken)
    {
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var id = reader.GetInt32(0);
            destination[id] = new Gw2CachedItemInfo(
                ItemId: id,
                Name: reader.GetString(1),
                Rarity: reader.GetString(2),
                ItemType: reader.GetString(3),
                VendorValueCopper: reader.GetInt64(4),
                UpdatedAt: DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
        }
    }
}
