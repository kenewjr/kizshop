using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using KizShop.Common;
using KizShop.Economy.Models;
using KizShop.Shop.Models;

namespace KizShop.Storage
{
    public class SqliteStorage : IKizStorage
    {
        private readonly string _connectionString;
        private readonly IClock _clock;
        private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);
        private bool _disposed;

        public SqliteStorage(string dbFilePath, IClock clock = null)
        {
            _clock = clock ?? SystemClock.Instance;
            var dir = Path.GetDirectoryName(dbFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = dbFilePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared
            }.ToString();
        }

        public async Task InitializeAsync()
        {
            await Task.Run(async () =>
            {
                await _semaphore.WaitAsync().ConfigureAwait(false);
                try
                {
                    using var conn = new SqliteConnection(_connectionString);
                    await conn.OpenAsync().ConfigureAwait(false);

                    using var pragmaCmd = conn.CreateCommand();
                    pragmaCmd.CommandText = "PRAGMA journal_mode=WAL;";
                    await pragmaCmd.ExecuteNonQueryAsync().ConfigureAwait(false);

                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"
                        CREATE TABLE IF NOT EXISTS schema_version (
                            version INTEGER PRIMARY KEY,
                            applied_at TEXT NOT NULL
                        );

                        CREATE TABLE IF NOT EXISTS kiz_accounts (
                            steam_id TEXT PRIMARY KEY,
                            balance INTEGER NOT NULL,
                            last_updated TEXT NOT NULL
                        );

                        CREATE TABLE IF NOT EXISTS kiz_transactions (
                            id INTEGER PRIMARY KEY AUTOINCREMENT,
                            timestamp TEXT NOT NULL,
                            steam_id TEXT NOT NULL,
                            type TEXT NOT NULL,
                            amount INTEGER NOT NULL,
                            details TEXT
                        );

                        CREATE INDEX IF NOT EXISTS idx_kiz_tx_steam ON kiz_transactions(steam_id);
                        CREATE INDEX IF NOT EXISTS idx_kiz_tx_time ON kiz_transactions(timestamp);

                        CREATE TABLE IF NOT EXISTS kiz_reward_events (
                            event_key TEXT PRIMARY KEY,
                            boss_name TEXT NOT NULL,
                            pool INTEGER NOT NULL,
                            timestamp TEXT NOT NULL
                        );

                        CREATE TABLE IF NOT EXISTS kiz_item_prices (
                            item_id INTEGER PRIMARY KEY,
                            sell_pressure TEXT NOT NULL,
                            buy_pressure TEXT NOT NULL,
                            last_updated TEXT NOT NULL
                        );

                        CREATE TABLE IF NOT EXISTS kiz_bounties (
                            id INTEGER PRIMARY KEY AUTOINCREMENT,
                            placer_steam_id TEXT NOT NULL,
                            target_steam_id TEXT NOT NULL,
                            escrow_cents INTEGER NOT NULL,
                            net_cents INTEGER NOT NULL,
                            created_utc TEXT NOT NULL
                        );
                        CREATE INDEX IF NOT EXISTS idx_kiz_bounties_target ON kiz_bounties(target_steam_id);
                        CREATE INDEX IF NOT EXISTS idx_kiz_bounties_placer ON kiz_bounties(placer_steam_id);
                        CREATE INDEX IF NOT EXISTS idx_kiz_bounties_created ON kiz_bounties(created_utc);

                        CREATE TABLE IF NOT EXISTS kiz_daily_claims (
                            steam_id TEXT PRIMARY KEY,
                            last_claim_day_index INTEGER NOT NULL,
                            streak INTEGER NOT NULL,
                            last_claimed_utc TEXT NOT NULL
                        );

                        INSERT OR IGNORE INTO schema_version (version, applied_at) 
                        VALUES (2, datetime('now'));
                    ";
                    await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
                finally
                {
                    _semaphore.Release();
                }
            }).ConfigureAwait(false);
        }

        public async Task<bool> AccountExistsAsync(ulong steamId)
        {
            return await Task.Run(async () =>
            {
                await _semaphore.WaitAsync().ConfigureAwait(false);
                try
                {
                    using var conn = new SqliteConnection(_connectionString);
                    await conn.OpenAsync().ConfigureAwait(false);

                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT 1 FROM kiz_accounts WHERE steam_id = @id LIMIT 1;";
                    cmd.Parameters.AddWithValue("@id", steamId.ToString());
                    var result = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
                    return result != null;
                }
                finally
                {
                    _semaphore.Release();
                }
            }).ConfigureAwait(false);
        }

        public async Task<Money> GetBalanceAsync(ulong steamId, Money defaultStartingBalance)
        {
            return await Task.Run(async () =>
            {
                await _semaphore.WaitAsync().ConfigureAwait(false);
                try
                {
                    using var conn = new SqliteConnection(_connectionString);
                    await conn.OpenAsync().ConfigureAwait(false);

                    using var selectCmd = conn.CreateCommand();
                    selectCmd.CommandText = "SELECT balance FROM kiz_accounts WHERE steam_id = @id;";
                    selectCmd.Parameters.AddWithValue("@id", steamId.ToString());

                    var result = await selectCmd.ExecuteScalarAsync().ConfigureAwait(false);
                    if (result != null && result != DBNull.Value)
                    {
                        return Money.FromCents(Convert.ToInt64(result));
                    }

                    var now = _clock.UtcNow.ToString("o");
                    using var insertCmd = conn.CreateCommand();
                    insertCmd.CommandText = @"
                        INSERT OR IGNORE INTO kiz_accounts (steam_id, balance, last_updated)
                        VALUES (@id, @bal, @now);
                    ";
                    insertCmd.Parameters.AddWithValue("@id", steamId.ToString());
                    insertCmd.Parameters.AddWithValue("@bal", defaultStartingBalance.Cents);
                    insertCmd.Parameters.AddWithValue("@now", now);
                    await insertCmd.ExecuteNonQueryAsync().ConfigureAwait(false);

                    return defaultStartingBalance;
                }
                finally
                {
                    _semaphore.Release();
                }
            }).ConfigureAwait(false);
        }

        public async Task<WithdrawResult> TryWithdrawAsync(ulong steamId, Money amount, string reason, Money defaultStartingBalance)
        {
            if (amount.Cents <= 0) return WithdrawResult.Failed(Money.Zero);

            return await Task.Run(async () =>
            {
                await _semaphore.WaitAsync().ConfigureAwait(false);
                try
                {
                    using var conn = new SqliteConnection(_connectionString);
                    await conn.OpenAsync().ConfigureAwait(false);

                    using var tx = conn.BeginTransaction();

                    var now = _clock.UtcNow.ToString("o");

                    // Ensure account exists
                    using var ensureCmd = conn.CreateCommand();
                    ensureCmd.Transaction = tx;
                    ensureCmd.CommandText = @"
                        INSERT OR IGNORE INTO kiz_accounts (steam_id, balance, last_updated)
                        VALUES (@id, @startBal, @now);
                    ";
                    ensureCmd.Parameters.AddWithValue("@id", steamId.ToString());
                    ensureCmd.Parameters.AddWithValue("@startBal", defaultStartingBalance.Cents);
                    ensureCmd.Parameters.AddWithValue("@now", now);
                    await ensureCmd.ExecuteNonQueryAsync().ConfigureAwait(false);

                    // Read current balance
                    using var readCmd = conn.CreateCommand();
                    readCmd.Transaction = tx;
                    readCmd.CommandText = "SELECT balance FROM kiz_accounts WHERE steam_id = @id;";
                    readCmd.Parameters.AddWithValue("@id", steamId.ToString());
                    var curObj = await readCmd.ExecuteScalarAsync().ConfigureAwait(false);
                    if (curObj == null || curObj == DBNull.Value)
                    {
                        tx.Rollback();
                        return WithdrawResult.Failed(Money.Zero);
                    }
                    var oldBal = Money.FromCents(Convert.ToInt64(curObj));
                    if (oldBal < amount)
                    {
                        tx.Rollback();
                        return WithdrawResult.Failed(oldBal);
                    }

                    using var cmd = conn.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = @"
                        UPDATE kiz_accounts 
                        SET balance = balance - @amount, last_updated = @now 
                        WHERE steam_id = @id AND balance >= @amount;
                    ";
                    cmd.Parameters.AddWithValue("@id", steamId.ToString());
                    cmd.Parameters.AddWithValue("@amount", amount.Cents);
                    cmd.Parameters.AddWithValue("@now", now);

                    var affected = await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                    if (affected <= 0)
                    {
                        tx.Rollback();
                        return WithdrawResult.Failed(oldBal);
                    }

                    using var logCmd = conn.CreateCommand();
                    logCmd.Transaction = tx;
                    logCmd.CommandText = @"
                        INSERT INTO kiz_transactions (timestamp, steam_id, type, amount, details)
                        VALUES (@now, @id, 'Withdraw', @amount, @details);
                    ";
                    logCmd.Parameters.AddWithValue("@now", now);
                    logCmd.Parameters.AddWithValue("@id", steamId.ToString());
                    logCmd.Parameters.AddWithValue("@amount", -amount.Cents);
                    logCmd.Parameters.AddWithValue("@details", reason ?? string.Empty);
                    await logCmd.ExecuteNonQueryAsync().ConfigureAwait(false);

                    tx.Commit();
                    return WithdrawResult.Succeeded(oldBal, oldBal - amount);
                }
                finally
                {
                    _semaphore.Release();
                }
            }).ConfigureAwait(false);
        }

        public async Task<DepositResult> DepositAsync(ulong steamId, Money amount, string reason, Money defaultStartingBalance, Money? maxBalance = null, bool ignoreMaxBalance = false)
        {
            return await Task.Run(async () =>
            {
                await _semaphore.WaitAsync().ConfigureAwait(false);
                try
                {
                    using var conn = new SqliteConnection(_connectionString);
                    await conn.OpenAsync().ConfigureAwait(false);

                    using var tx = conn.BeginTransaction();
                    var now = _clock.UtcNow.ToString("o");

                    using var ensureCmd = conn.CreateCommand();
                    ensureCmd.Transaction = tx;
                    ensureCmd.CommandText = @"
                        INSERT OR IGNORE INTO kiz_accounts (steam_id, balance, last_updated)
                        VALUES (@id, @startBal, @now);
                    ";
                    ensureCmd.Parameters.AddWithValue("@id", steamId.ToString());
                    ensureCmd.Parameters.AddWithValue("@startBal", defaultStartingBalance.Cents);
                    ensureCmd.Parameters.AddWithValue("@now", now);
                    await ensureCmd.ExecuteNonQueryAsync().ConfigureAwait(false);

                    using var getCmd = conn.CreateCommand();
                    getCmd.Transaction = tx;
                    getCmd.CommandText = "SELECT balance FROM kiz_accounts WHERE steam_id = @id;";
                    getCmd.Parameters.AddWithValue("@id", steamId.ToString());
                    var currentObj = await getCmd.ExecuteScalarAsync().ConfigureAwait(false);
                    var oldBal = Money.FromCents(Convert.ToInt64(currentObj));

                    long newCents = oldBal.Cents + amount.Cents;
                    if (!ignoreMaxBalance && maxBalance.HasValue && maxBalance.Value.Cents > 0 && newCents > maxBalance.Value.Cents)
                    {
                        newCents = maxBalance.Value.Cents;
                    }

                    long delta = newCents - oldBal.Cents;

                    using var updateCmd = conn.CreateCommand();
                    updateCmd.Transaction = tx;
                    updateCmd.CommandText = "UPDATE kiz_accounts SET balance = @newBal, last_updated = @now WHERE steam_id = @id;";
                    updateCmd.Parameters.AddWithValue("@newBal", newCents);
                    updateCmd.Parameters.AddWithValue("@now", now);
                    updateCmd.Parameters.AddWithValue("@id", steamId.ToString());
                    await updateCmd.ExecuteNonQueryAsync().ConfigureAwait(false);

                    if (delta != 0)
                    {
                        using var logCmd = conn.CreateCommand();
                        logCmd.Transaction = tx;
                        logCmd.CommandText = @"
                            INSERT INTO kiz_transactions (timestamp, steam_id, type, amount, details)
                            VALUES (@now, @id, @type, @amount, @details);
                        ";
                        logCmd.Parameters.AddWithValue("@now", now);
                        logCmd.Parameters.AddWithValue("@id", steamId.ToString());
                        logCmd.Parameters.AddWithValue("@type", ignoreMaxBalance ? "Refund" : "Deposit");
                        logCmd.Parameters.AddWithValue("@amount", delta);
                        logCmd.Parameters.AddWithValue("@details", reason ?? string.Empty);
                        await logCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                    }

                    tx.Commit();
                    return new DepositResult(true, oldBal, Money.FromCents(newCents));
                }
                finally
                {
                    _semaphore.Release();
                }
            }).ConfigureAwait(false);
        }

        public async Task<TransferResult> TransferAsync(ulong fromSteamId, ulong toSteamId, Money amount, string reason, Money defaultStartingBalance, Money? maxBalance = null)
        {
            if (amount.Cents <= 0 || fromSteamId == toSteamId)
            {
                return TransferResult.Failed(Money.Zero, Money.Zero);
            }

            return await Task.Run(async () =>
            {
                await _semaphore.WaitAsync().ConfigureAwait(false);
                try
                {
                    using var conn = new SqliteConnection(_connectionString);
                    await conn.OpenAsync().ConfigureAwait(false);

                    using var tx = conn.BeginTransaction();
                    var now = _clock.UtcNow.ToString("o");

                    // Ensure sender exists
                    using var ensureFromCmd = conn.CreateCommand();
                    ensureFromCmd.Transaction = tx;
                    ensureFromCmd.CommandText = @"
                        INSERT OR IGNORE INTO kiz_accounts (steam_id, balance, last_updated)
                        VALUES (@fromId, @startBal, @now);
                    ";
                    ensureFromCmd.Parameters.AddWithValue("@fromId", fromSteamId.ToString());
                    ensureFromCmd.Parameters.AddWithValue("@startBal", defaultStartingBalance.Cents);
                    ensureFromCmd.Parameters.AddWithValue("@now", now);
                    await ensureFromCmd.ExecuteNonQueryAsync().ConfigureAwait(false);

                    // Read sender balance
                    using var checkFromCmd = conn.CreateCommand();
                    checkFromCmd.Transaction = tx;
                    checkFromCmd.CommandText = "SELECT balance FROM kiz_accounts WHERE steam_id = @fromId;";
                    checkFromCmd.Parameters.AddWithValue("@fromId", fromSteamId.ToString());
                    var fromBalObj = await checkFromCmd.ExecuteScalarAsync().ConfigureAwait(false);
                    if (fromBalObj == null || fromBalObj == DBNull.Value)
                    {
                        tx.Rollback();
                        return TransferResult.Failed(Money.Zero, Money.Zero);
                    }
                    var fromOld = Money.FromCents(Convert.ToInt64(fromBalObj));
                    if (fromOld < amount)
                    {
                        tx.Rollback();
                        return TransferResult.Failed(fromOld, Money.Zero);
                    }

                    // Ensure receiver exists
                    using var ensureToCmd = conn.CreateCommand();
                    ensureToCmd.Transaction = tx;
                    ensureToCmd.CommandText = @"
                        INSERT OR IGNORE INTO kiz_accounts (steam_id, balance, last_updated)
                        VALUES (@toId, @startBal, @now);
                    ";
                    ensureToCmd.Parameters.AddWithValue("@toId", toSteamId.ToString());
                    ensureToCmd.Parameters.AddWithValue("@startBal", defaultStartingBalance.Cents);
                    ensureToCmd.Parameters.AddWithValue("@now", now);
                    await ensureToCmd.ExecuteNonQueryAsync().ConfigureAwait(false);

                    // Check receiver balance & cap
                    using var checkToCmd = conn.CreateCommand();
                    checkToCmd.Transaction = tx;
                    checkToCmd.CommandText = "SELECT balance FROM kiz_accounts WHERE steam_id = @toId;";
                    checkToCmd.Parameters.AddWithValue("@toId", toSteamId.ToString());
                    var toBalObj = await checkToCmd.ExecuteScalarAsync().ConfigureAwait(false);
                    var toOld = Money.FromCents(Convert.ToInt64(toBalObj));

                    if (maxBalance.HasValue && maxBalance.Value.Cents > 0 && (toOld.Cents + amount.Cents) > maxBalance.Value.Cents)
                    {
                        tx.Rollback();
                        return TransferResult.Failed(fromOld, toOld);
                    }

                    // Withdraw from sender
                    using var withdrawCmd = conn.CreateCommand();
                    withdrawCmd.Transaction = tx;
                    withdrawCmd.CommandText = @"
                        UPDATE kiz_accounts 
                        SET balance = balance - @amount, last_updated = @now 
                        WHERE steam_id = @fromId AND balance >= @amount;
                    ";
                    withdrawCmd.Parameters.AddWithValue("@fromId", fromSteamId.ToString());
                    withdrawCmd.Parameters.AddWithValue("@amount", amount.Cents);
                    withdrawCmd.Parameters.AddWithValue("@now", now);

                    var affected = await withdrawCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                    if (affected <= 0)
                    {
                        tx.Rollback();
                        return TransferResult.Failed(fromOld, toOld);
                    }

                    // Deposit to receiver
                    using var depositCmd = conn.CreateCommand();
                    depositCmd.Transaction = tx;
                    depositCmd.CommandText = @"
                        UPDATE kiz_accounts 
                        SET balance = balance + @amount, last_updated = @now 
                        WHERE steam_id = @toId;
                    ";
                    depositCmd.Parameters.AddWithValue("@toId", toSteamId.ToString());
                    depositCmd.Parameters.AddWithValue("@amount", amount.Cents);
                    depositCmd.Parameters.AddWithValue("@now", now);
                    await depositCmd.ExecuteNonQueryAsync().ConfigureAwait(false);

                    // Log transactions
                    using var logFromCmd = conn.CreateCommand();
                    logFromCmd.Transaction = tx;
                    logFromCmd.CommandText = @"
                        INSERT INTO kiz_transactions (timestamp, steam_id, type, amount, details)
                        VALUES (@now, @id, 'TransferSent', @amount, @details);
                    ";
                    logFromCmd.Parameters.AddWithValue("@now", now);
                    logFromCmd.Parameters.AddWithValue("@id", fromSteamId.ToString());
                    logFromCmd.Parameters.AddWithValue("@amount", -amount.Cents);
                    logFromCmd.Parameters.AddWithValue("@details", $"To:{toSteamId} - {reason}");
                    await logFromCmd.ExecuteNonQueryAsync().ConfigureAwait(false);

                    using var logToCmd = conn.CreateCommand();
                    logToCmd.Transaction = tx;
                    logToCmd.CommandText = @"
                        INSERT INTO kiz_transactions (timestamp, steam_id, type, amount, details)
                        VALUES (@now, @id, 'TransferReceived', @amount, @details);
                    ";
                    logToCmd.Parameters.AddWithValue("@now", now);
                    logToCmd.Parameters.AddWithValue("@id", toSteamId.ToString());
                    logToCmd.Parameters.AddWithValue("@amount", amount.Cents);
                    logToCmd.Parameters.AddWithValue("@details", $"From:{fromSteamId} - {reason}");
                    await logToCmd.ExecuteNonQueryAsync().ConfigureAwait(false);

                    tx.Commit();
                    return TransferResult.Succeeded(fromOld, fromOld - amount, toOld, toOld + amount);
                }
                finally
                {
                    _semaphore.Release();
                }
            }).ConfigureAwait(false);
        }

        public async Task<List<TopBalanceEntry>> GetTopBalancesAsync(int count)
        {
            if (count <= 0) count = 10;
            return await Task.Run(async () =>
            {
                var list = new List<TopBalanceEntry>();
                await _semaphore.WaitAsync().ConfigureAwait(false);
                try
                {
                    using var conn = new SqliteConnection(_connectionString);
                    await conn.OpenAsync().ConfigureAwait(false);

                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT steam_id, balance FROM kiz_accounts ORDER BY balance DESC LIMIT @limit;";
                    cmd.Parameters.AddWithValue("@limit", count);

                    using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                    while (await reader.ReadAsync().ConfigureAwait(false))
                    {
                        if (ulong.TryParse(reader.GetString(0), out var steamId))
                        {
                            var cents = reader.GetInt64(1);
                            list.Add(new TopBalanceEntry(steamId, Money.FromCents(cents)));
                        }
                    }
                }
                finally
                {
                    _semaphore.Release();
                }

                return list;
            }).ConfigureAwait(false);
        }

        public async Task SetBalanceAsync(ulong steamId, Money amount, string reason)
        {
            await Task.Run(async () =>
            {
                await _semaphore.WaitAsync().ConfigureAwait(false);
                try
                {
                    using var conn = new SqliteConnection(_connectionString);
                    await conn.OpenAsync().ConfigureAwait(false);

                    using var tx = conn.BeginTransaction();
                    var now = _clock.UtcNow.ToString("o");

                    using var upsertCmd = conn.CreateCommand();
                    upsertCmd.Transaction = tx;
                    upsertCmd.CommandText = @"
                        INSERT INTO kiz_accounts (steam_id, balance, last_updated)
                        VALUES (@id, @bal, @now)
                        ON CONFLICT(steam_id) DO UPDATE SET balance = @bal, last_updated = @now;
                    ";
                    upsertCmd.Parameters.AddWithValue("@id", steamId.ToString());
                    upsertCmd.Parameters.AddWithValue("@bal", amount.Cents);
                    upsertCmd.Parameters.AddWithValue("@now", now);
                    await upsertCmd.ExecuteNonQueryAsync().ConfigureAwait(false);

                    using var logCmd = conn.CreateCommand();
                    logCmd.Transaction = tx;
                    logCmd.CommandText = @"
                        INSERT INTO kiz_transactions (timestamp, steam_id, type, amount, details)
                        VALUES (@now, @id, 'SetBalance', @amount, @details);
                    ";
                    logCmd.Parameters.AddWithValue("@now", now);
                    logCmd.Parameters.AddWithValue("@id", steamId.ToString());
                    logCmd.Parameters.AddWithValue("@amount", amount.Cents);
                    logCmd.Parameters.AddWithValue("@details", reason ?? string.Empty);
                    await logCmd.ExecuteNonQueryAsync().ConfigureAwait(false);

                    tx.Commit();
                }
                finally
                {
                    _semaphore.Release();
                }
            }).ConfigureAwait(false);
        }

        public async Task LogTransactionAsync(ulong steamId, string type, Money amount, string details)
        {
            await Task.Run(async () =>
            {
                await _semaphore.WaitAsync().ConfigureAwait(false);
                try
                {
                    using var conn = new SqliteConnection(_connectionString);
                    await conn.OpenAsync().ConfigureAwait(false);

                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"
                        INSERT INTO kiz_transactions (timestamp, steam_id, type, amount, details)
                        VALUES (@now, @id, @type, @amount, @details);
                    ";
                    cmd.Parameters.AddWithValue("@now", _clock.UtcNow.ToString("o"));
                    cmd.Parameters.AddWithValue("@id", steamId.ToString());
                    cmd.Parameters.AddWithValue("@type", type ?? "General");
                    cmd.Parameters.AddWithValue("@amount", amount.Cents);
                    cmd.Parameters.AddWithValue("@details", details ?? string.Empty);
                    await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
                finally
                {
                    _semaphore.Release();
                }
            }).ConfigureAwait(false);
        }

        public async Task PruneLogsAsync(int retentionDays)
        {
            if (retentionDays <= 0) return;

            var cutoff = _clock.UtcNow.AddDays(-retentionDays).ToString("o");
            await Task.Run(async () =>
            {
                await _semaphore.WaitAsync().ConfigureAwait(false);
                try
                {
                    using var conn = new SqliteConnection(_connectionString);
                    await conn.OpenAsync().ConfigureAwait(false);

                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "DELETE FROM kiz_transactions WHERE timestamp < @cutoff;";
                    cmd.Parameters.AddWithValue("@cutoff", cutoff);
                    await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
                finally
                {
                    _semaphore.Release();
                }
            }).ConfigureAwait(false);
        }

        public async Task<bool> HasRewardEventAsync(string eventKey)
        {
            if (string.IsNullOrEmpty(eventKey)) return false;
            return await Task.Run(async () =>
            {
                await _semaphore.WaitAsync().ConfigureAwait(false);
                try
                {
                    using var conn = new SqliteConnection(_connectionString);
                    await conn.OpenAsync().ConfigureAwait(false);

                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT 1 FROM kiz_reward_events WHERE event_key = @k LIMIT 1;";
                    cmd.Parameters.AddWithValue("@k", eventKey);
                    var res = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
                    return res != null;
                }
                finally
                {
                    _semaphore.Release();
                }
            }).ConfigureAwait(false);
        }

        public async Task RecordRewardEventAsync(string eventKey, string bossName, Money pool, DateTime timestampUtc)
        {
            if (string.IsNullOrEmpty(eventKey)) return;
            await Task.Run(async () =>
            {
                await _semaphore.WaitAsync().ConfigureAwait(false);
                try
                {
                    using var conn = new SqliteConnection(_connectionString);
                    await conn.OpenAsync().ConfigureAwait(false);

                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"
                        INSERT OR REPLACE INTO kiz_reward_events (event_key, boss_name, pool, timestamp)
                        VALUES (@k, @b, @p, @t);
                    ";
                    cmd.Parameters.AddWithValue("@k", eventKey);
                    cmd.Parameters.AddWithValue("@b", bossName ?? string.Empty);
                    cmd.Parameters.AddWithValue("@p", pool.Cents);
                    cmd.Parameters.AddWithValue("@t", timestampUtc.ToString("o"));
                    await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
                finally
                {
                    _semaphore.Release();
                }
            }).ConfigureAwait(false);
        }

        public async Task<ItemPriceState> GetItemPriceStateAsync(ushort itemId)
        {
            return await Task.Run(async () =>
            {
                await _semaphore.WaitAsync().ConfigureAwait(false);
                try
                {
                    using var conn = new SqliteConnection(_connectionString);
                    await conn.OpenAsync().ConfigureAwait(false);

                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT sell_pressure, buy_pressure, last_updated FROM kiz_item_prices WHERE item_id = @id LIMIT 1;";
                    cmd.Parameters.AddWithValue("@id", (int)itemId);

                    using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                    if (await reader.ReadAsync().ConfigureAwait(false))
                    {
                        var spStr = reader.GetString(0);
                        var bpStr = reader.GetString(1);
                        var luStr = reader.GetString(2);
                        decimal.TryParse(spStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var sp);
                        decimal.TryParse(bpStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var bp);
                        DateTime.TryParse(luStr, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var lu);
                        return new ItemPriceState
                        {
                            ItemId = itemId,
                            SellPressure = sp,
                            BuyPressure = bp,
                            LastUpdatedUtc = lu
                        };
                    }

                    return new ItemPriceState
                    {
                        ItemId = itemId,
                        SellPressure = 0,
                        BuyPressure = 0,
                        LastUpdatedUtc = DateTime.MinValue
                    };
                }
                finally
                {
                    _semaphore.Release();
                }
            }).ConfigureAwait(false);
        }

        public async Task UpdateItemPricePressureAsync(ushort itemId, decimal sellDelta, decimal buyDelta, DateTime nowUtc)
        {
            await Task.Run(async () =>
            {
                await _semaphore.WaitAsync().ConfigureAwait(false);
                try
                {
                    using var conn = new SqliteConnection(_connectionString);
                    await conn.OpenAsync().ConfigureAwait(false);

                    decimal currentSell = 0;
                    decimal currentBuy = 0;

                    using (var selectCmd = conn.CreateCommand())
                    {
                        selectCmd.CommandText = "SELECT sell_pressure, buy_pressure FROM kiz_item_prices WHERE item_id = @id LIMIT 1;";
                        selectCmd.Parameters.AddWithValue("@id", (int)itemId);
                        using var reader = await selectCmd.ExecuteReaderAsync().ConfigureAwait(false);
                        if (await reader.ReadAsync().ConfigureAwait(false))
                        {
                            decimal.TryParse(reader.GetString(0), NumberStyles.Any, CultureInfo.InvariantCulture, out currentSell);
                            decimal.TryParse(reader.GetString(1), NumberStyles.Any, CultureInfo.InvariantCulture, out currentBuy);
                        }
                    }

                    var newSell = Math.Max(0m, currentSell + sellDelta);
                    var newBuy = Math.Max(0m, currentBuy + buyDelta);

                    using (var upsertCmd = conn.CreateCommand())
                    {
                        upsertCmd.CommandText = @"
                            INSERT OR REPLACE INTO kiz_item_prices (item_id, sell_pressure, buy_pressure, last_updated)
                            VALUES (@id, @sp, @bp, @lu);
                        ";
                        upsertCmd.Parameters.AddWithValue("@id", (int)itemId);
                        upsertCmd.Parameters.AddWithValue("@sp", newSell.ToString(CultureInfo.InvariantCulture));
                        upsertCmd.Parameters.AddWithValue("@bp", newBuy.ToString(CultureInfo.InvariantCulture));
                        upsertCmd.Parameters.AddWithValue("@lu", nowUtc.ToString("o"));
                        await upsertCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                    }
                }
                finally
                {
                    _semaphore.Release();
                }
            }).ConfigureAwait(false);
        }

        public async Task ResetItemPriceStateAsync(ushort? itemId)
        {
            await Task.Run(async () =>
            {
                await _semaphore.WaitAsync().ConfigureAwait(false);
                try
                {
                    using var conn = new SqliteConnection(_connectionString);
                    await conn.OpenAsync().ConfigureAwait(false);

                    using var cmd = conn.CreateCommand();
                    if (itemId.HasValue)
                    {
                        cmd.CommandText = "DELETE FROM kiz_item_prices WHERE item_id = @id;";
                        cmd.Parameters.AddWithValue("@id", (int)itemId.Value);
                    }
                    else
                    {
                        cmd.CommandText = "DELETE FROM kiz_item_prices;";
                    }
                    await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
                finally
                {
                    _semaphore.Release();
                }
            }).ConfigureAwait(false);
        }

        public async Task<int> CreateBountyAsync(ulong placerSteamId, ulong targetSteamId, Money escrowAmount, Money netAmount, DateTime createdUtc)
        {
            return await Task.Run(async () =>
            {
                await _semaphore.WaitAsync().ConfigureAwait(false);
                try
                {
                    using var conn = new SqliteConnection(_connectionString);
                    await conn.OpenAsync().ConfigureAwait(false);

                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"
                        INSERT INTO kiz_bounties (placer_steam_id, target_steam_id, escrow_cents, net_cents, created_utc)
                        VALUES (@p, @t, @e, @n, @c);
                        SELECT last_insert_rowid();
                    ";
                    cmd.Parameters.AddWithValue("@p", placerSteamId.ToString());
                    cmd.Parameters.AddWithValue("@t", targetSteamId.ToString());
                    cmd.Parameters.AddWithValue("@e", escrowAmount.Cents);
                    cmd.Parameters.AddWithValue("@n", netAmount.Cents);
                    cmd.Parameters.AddWithValue("@c", createdUtc.ToString("o"));

                    var idObj = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
                    return Convert.ToInt32(idObj);
                }
                finally
                {
                    _semaphore.Release();
                }
            }).ConfigureAwait(false);
        }

        public async Task<List<BountyEntry>> GetActiveBountiesAsync(int limit = 50)
        {
            return await Task.Run(async () =>
            {
                await _semaphore.WaitAsync().ConfigureAwait(false);
                try
                {
                    using var conn = new SqliteConnection(_connectionString);
                    await conn.OpenAsync().ConfigureAwait(false);

                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"
                        SELECT id, placer_steam_id, target_steam_id, escrow_cents, net_cents, created_utc
                        FROM kiz_bounties
                        ORDER BY net_cents DESC
                        LIMIT @lim;
                    ";
                    cmd.Parameters.AddWithValue("@lim", limit);

                    var list = new List<BountyEntry>();
                    using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                    while (await reader.ReadAsync().ConfigureAwait(false))
                    {
                        ulong.TryParse(reader.GetString(1), out var placer);
                        ulong.TryParse(reader.GetString(2), out var target);
                        DateTime.TryParse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var cr);

                        list.Add(new BountyEntry
                        {
                            Id = reader.GetInt32(0),
                            PlacerSteamId = placer,
                            TargetSteamId = target,
                            EscrowAmount = Money.FromCents(reader.GetInt64(3)),
                            NetAmount = Money.FromCents(reader.GetInt64(4)),
                            CreatedUtc = cr
                        });
                    }
                    return list;
                }
                finally
                {
                    _semaphore.Release();
                }
            }).ConfigureAwait(false);
        }

        public async Task<Money> GetTotalBountyOnTargetAsync(ulong targetSteamId)
        {
            return await Task.Run(async () =>
            {
                await _semaphore.WaitAsync().ConfigureAwait(false);
                try
                {
                    using var conn = new SqliteConnection(_connectionString);
                    await conn.OpenAsync().ConfigureAwait(false);

                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT COALESCE(SUM(net_cents), 0) FROM kiz_bounties WHERE target_steam_id = @t;";
                    cmd.Parameters.AddWithValue("@t", targetSteamId.ToString());

                    var res = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
                    return Money.FromCents(Convert.ToInt64(res));
                }
                finally
                {
                    _semaphore.Release();
                }
            }).ConfigureAwait(false);
        }

        public async Task<Money> ClaimBountiesAsync(ulong targetSteamId, ulong killerSteamId)
        {
            return await Task.Run(async () =>
            {
                await _semaphore.WaitAsync().ConfigureAwait(false);
                try
                {
                    using var conn = new SqliteConnection(_connectionString);
                    await conn.OpenAsync().ConfigureAwait(false);

                    using var tx = conn.BeginTransaction();
                    long totalCents = 0;

                    using (var sumCmd = conn.CreateCommand())
                    {
                        sumCmd.Transaction = tx;
                        sumCmd.CommandText = "SELECT COALESCE(SUM(net_cents), 0) FROM kiz_bounties WHERE target_steam_id = @t AND placer_steam_id != @k;";
                        sumCmd.Parameters.AddWithValue("@t", targetSteamId.ToString());
                        sumCmd.Parameters.AddWithValue("@k", killerSteamId.ToString());
                        var res = await sumCmd.ExecuteScalarAsync().ConfigureAwait(false);
                        totalCents = Convert.ToInt64(res);
                    }

                    if (totalCents > 0)
                    {
                        using var delCmd = conn.CreateCommand();
                        delCmd.Transaction = tx;
                        delCmd.CommandText = "DELETE FROM kiz_bounties WHERE target_steam_id = @t AND placer_steam_id != @k;";
                        delCmd.Parameters.AddWithValue("@t", targetSteamId.ToString());
                        delCmd.Parameters.AddWithValue("@k", killerSteamId.ToString());
                        await delCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                    }

                    tx.Commit();
                    return Money.FromCents(totalCents);
                }
                finally
                {
                    _semaphore.Release();
                }
            }).ConfigureAwait(false);
        }

        public async Task<List<BountyEntry>> RefundExpiredBountiesAsync(DateTime cutoffUtc)
        {
            return await Task.Run(async () =>
            {
                await _semaphore.WaitAsync().ConfigureAwait(false);
                try
                {
                    using var conn = new SqliteConnection(_connectionString);
                    await conn.OpenAsync().ConfigureAwait(false);

                    using var tx = conn.BeginTransaction();
                    var list = new List<BountyEntry>();

                    using (var selectCmd = conn.CreateCommand())
                    {
                        selectCmd.Transaction = tx;
                        selectCmd.CommandText = @"
                            SELECT id, placer_steam_id, target_steam_id, escrow_cents, net_cents, created_utc
                            FROM kiz_bounties
                            WHERE created_utc < @co;
                        ";
                        selectCmd.Parameters.AddWithValue("@co", cutoffUtc.ToString("o"));

                        using var reader = await selectCmd.ExecuteReaderAsync().ConfigureAwait(false);
                        while (await reader.ReadAsync().ConfigureAwait(false))
                        {
                            ulong.TryParse(reader.GetString(1), out var placer);
                            ulong.TryParse(reader.GetString(2), out var target);
                            DateTime.TryParse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var cr);

                            list.Add(new BountyEntry
                            {
                                Id = reader.GetInt32(0),
                                PlacerSteamId = placer,
                                TargetSteamId = target,
                                EscrowAmount = Money.FromCents(reader.GetInt64(3)),
                                NetAmount = Money.FromCents(reader.GetInt64(4)),
                                CreatedUtc = cr
                            });
                        }
                    }

                    if (list.Count > 0)
                    {
                        using var delCmd = conn.CreateCommand();
                        delCmd.Transaction = tx;
                        delCmd.CommandText = "DELETE FROM kiz_bounties WHERE created_utc < @co;";
                        delCmd.Parameters.AddWithValue("@co", cutoffUtc.ToString("o"));
                        await delCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                    }

                    tx.Commit();
                    return list;
                }
                finally
                {
                    _semaphore.Release();
                }
            }).ConfigureAwait(false);
        }

        public async Task<DailyClaimResult> TryClaimDailyAsync(ulong steamId, int currentDayIndex, int maxStreakDays, DateTime nowUtc)
        {
            return await Task.Run(async () =>
            {
                await _semaphore.WaitAsync().ConfigureAwait(false);
                try
                {
                    using var conn = new SqliteConnection(_connectionString);
                    await conn.OpenAsync().ConfigureAwait(false);

                    using var tx = conn.BeginTransaction();

                    int? lastDay = null;
                    int currentStreak = 0;

                    using (var selCmd = conn.CreateCommand())
                    {
                        selCmd.Transaction = tx;
                        selCmd.CommandText = "SELECT last_claim_day_index, streak FROM kiz_daily_claims WHERE steam_id = @id LIMIT 1;";
                        selCmd.Parameters.AddWithValue("@id", steamId.ToString());
                        using var reader = await selCmd.ExecuteReaderAsync().ConfigureAwait(false);
                        if (await reader.ReadAsync().ConfigureAwait(false))
                        {
                            lastDay = reader.GetInt32(0);
                            currentStreak = reader.GetInt32(1);
                        }
                    }

                    if (!lastDay.HasValue)
                    {
                        using (var insCmd = conn.CreateCommand())
                        {
                            insCmd.Transaction = tx;
                            insCmd.CommandText = @"
                                INSERT INTO kiz_daily_claims (steam_id, last_claim_day_index, streak, last_claimed_utc)
                                VALUES (@id, @d, 1, @t);
                            ";
                            insCmd.Parameters.AddWithValue("@id", steamId.ToString());
                            insCmd.Parameters.AddWithValue("@d", currentDayIndex);
                            insCmd.Parameters.AddWithValue("@t", nowUtc.ToString("o"));
                            await insCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                        }
                        tx.Commit();
                        return new DailyClaimResult
                        {
                            Allowed = true,
                            Streak = 1,
                            PreviousDayIndex = -1,
                            PreviousStreak = 0
                        };
                    }

                    if (lastDay.Value == currentDayIndex)
                    {
                        tx.Commit();
                        return new DailyClaimResult
                        {
                            Allowed = false,
                            Streak = currentStreak,
                            PreviousDayIndex = lastDay.Value,
                            PreviousStreak = currentStreak
                        };
                    }

                    int prevDay = lastDay.Value;
                    int prevStreak = currentStreak;
                    int newStreak = (prevDay == currentDayIndex - 1)
                        ? Math.Min(prevStreak + 1, maxStreakDays)
                        : 1;

                    using (var updCmd = conn.CreateCommand())
                    {
                        updCmd.Transaction = tx;
                        updCmd.CommandText = @"
                            UPDATE kiz_daily_claims 
                            SET last_claim_day_index = @d, streak = @s, last_claimed_utc = @t
                            WHERE steam_id = @id;
                        ";
                        updCmd.Parameters.AddWithValue("@id", steamId.ToString());
                        updCmd.Parameters.AddWithValue("@d", currentDayIndex);
                        updCmd.Parameters.AddWithValue("@s", newStreak);
                        updCmd.Parameters.AddWithValue("@t", nowUtc.ToString("o"));
                        await updCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                    }

                    tx.Commit();
                    return new DailyClaimResult
                    {
                        Allowed = true,
                        Streak = newStreak,
                        PreviousDayIndex = prevDay,
                        PreviousStreak = prevStreak
                    };
                }
                finally
                {
                    _semaphore.Release();
                }
            }).ConfigureAwait(false);
        }

        public async Task RollbackDailyClaimAsync(ulong steamId, int previousDayIndex, int previousStreak)
        {
            await Task.Run(async () =>
            {
                await _semaphore.WaitAsync().ConfigureAwait(false);
                try
                {
                    using var conn = new SqliteConnection(_connectionString);
                    await conn.OpenAsync().ConfigureAwait(false);

                    using var cmd = conn.CreateCommand();
                    if (previousDayIndex < 0)
                    {
                        cmd.CommandText = "DELETE FROM kiz_daily_claims WHERE steam_id = @id;";
                        cmd.Parameters.AddWithValue("@id", steamId.ToString());
                    }
                    else
                    {
                        cmd.CommandText = "UPDATE kiz_daily_claims SET last_claim_day_index = @d, streak = @s WHERE steam_id = @id;";
                        cmd.Parameters.AddWithValue("@id", steamId.ToString());
                        cmd.Parameters.AddWithValue("@d", previousDayIndex);
                        cmd.Parameters.AddWithValue("@s", previousStreak);
                    }
                    await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
                finally
                {
                    _semaphore.Release();
                }
            }).ConfigureAwait(false);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _semaphore.Dispose();
        }
    }
}
