using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using KizShop.Common;

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

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _semaphore.Dispose();
        }
    }
}
