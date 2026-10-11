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
        private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);
        private bool _disposed;

        public SqliteStorage(string dbFilePath)
        {
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
            await _semaphore.WaitAsync();
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync();

                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS kiz_accounts (
                        steam_id TEXT PRIMARY KEY,
                        balance REAL NOT NULL,
                        last_updated TEXT NOT NULL
                    );

                    CREATE TABLE IF NOT EXISTS kiz_transactions (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        timestamp TEXT NOT NULL,
                        steam_id TEXT NOT NULL,
                        type TEXT NOT NULL,
                        amount REAL NOT NULL,
                        details TEXT
                    );

                    CREATE INDEX IF NOT EXISTS idx_kiz_tx_steam ON kiz_transactions(steam_id);
                    CREATE INDEX IF NOT EXISTS idx_kiz_tx_time ON kiz_transactions(timestamp);
                ";
                await cmd.ExecuteNonQueryAsync();
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public async Task<decimal> GetBalanceAsync(ulong steamId, decimal defaultStartingBalance)
        {
            await _semaphore.WaitAsync();
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync();

                using var selectCmd = conn.CreateCommand();
                selectCmd.CommandText = "SELECT balance FROM kiz_accounts WHERE steam_id = @id;";
                selectCmd.Parameters.AddWithValue("@id", steamId.ToString());

                var result = await selectCmd.ExecuteScalarAsync();
                if (result != null && result != DBNull.Value)
                {
                    return Convert.ToDecimal(result, CultureInfo.InvariantCulture);
                }

                // Account does not exist yet; insert with starting balance
                var now = DateTime.UtcNow.ToString("o");
                using var insertCmd = conn.CreateCommand();
                insertCmd.CommandText = @"
                    INSERT OR IGNORE INTO kiz_accounts (steam_id, balance, last_updated)
                    VALUES (@id, @bal, @now);
                ";
                insertCmd.Parameters.AddWithValue("@id", steamId.ToString());
                insertCmd.Parameters.AddWithValue("@bal", (double)defaultStartingBalance);
                insertCmd.Parameters.AddWithValue("@now", now);
                await insertCmd.ExecuteNonQueryAsync();

                return defaultStartingBalance;
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public async Task<bool> TryWithdrawAsync(ulong steamId, decimal amount, string reason)
        {
            if (amount <= 0) return false;

            await _semaphore.WaitAsync();
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync();

                using var tx = conn.BeginTransaction();
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"
                    UPDATE kiz_accounts 
                    SET balance = balance - @amount, last_updated = @now 
                    WHERE steam_id = @id AND balance >= @amount;
                ";
                cmd.Parameters.AddWithValue("@id", steamId.ToString());
                cmd.Parameters.AddWithValue("@amount", (double)amount);
                cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));

                var affected = await cmd.ExecuteNonQueryAsync();
                if (affected <= 0)
                {
                    tx.Rollback();
                    return false;
                }

                using var logCmd = conn.CreateCommand();
                logCmd.Transaction = tx;
                logCmd.CommandText = @"
                    INSERT INTO kiz_transactions (timestamp, steam_id, type, amount, details)
                    VALUES (@now, @id, 'Withdraw', @amount, @details);
                ";
                logCmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
                logCmd.Parameters.AddWithValue("@id", steamId.ToString());
                logCmd.Parameters.AddWithValue("@amount", -(double)amount);
                logCmd.Parameters.AddWithValue("@details", reason ?? string.Empty);
                await logCmd.ExecuteNonQueryAsync();

                tx.Commit();
                return true;
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public async Task<decimal> DepositAsync(ulong steamId, decimal amount, string reason, decimal defaultStartingBalance, decimal? maxBalance = null)
        {
            if (amount <= 0)
            {
                return await GetBalanceAsync(steamId, defaultStartingBalance);
            }

            await _semaphore.WaitAsync();
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync();

                using var tx = conn.BeginTransaction();

                // Ensure account exists
                var now = DateTime.UtcNow.ToString("o");
                using var ensureCmd = conn.CreateCommand();
                ensureCmd.Transaction = tx;
                ensureCmd.CommandText = @"
                    INSERT OR IGNORE INTO kiz_accounts (steam_id, balance, last_updated)
                    VALUES (@id, @startBal, @now);
                ";
                ensureCmd.Parameters.AddWithValue("@id", steamId.ToString());
                ensureCmd.Parameters.AddWithValue("@startBal", (double)defaultStartingBalance);
                ensureCmd.Parameters.AddWithValue("@now", now);
                await ensureCmd.ExecuteNonQueryAsync();

                // Check current balance
                using var getCmd = conn.CreateCommand();
                getCmd.Transaction = tx;
                getCmd.CommandText = "SELECT balance FROM kiz_accounts WHERE steam_id = @id;";
                getCmd.Parameters.AddWithValue("@id", steamId.ToString());
                var currentObj = await getCmd.ExecuteScalarAsync();
                var currentBal = Convert.ToDecimal(currentObj, CultureInfo.InvariantCulture);

                var newBal = currentBal + amount;
                if (maxBalance.HasValue && maxBalance.Value > 0 && newBal > maxBalance.Value)
                {
                    newBal = maxBalance.Value;
                }

                var actualDelta = newBal - currentBal;

                using var updateCmd = conn.CreateCommand();
                updateCmd.Transaction = tx;
                updateCmd.CommandText = "UPDATE kiz_accounts SET balance = @newBal, last_updated = @now WHERE steam_id = @id;";
                updateCmd.Parameters.AddWithValue("@newBal", (double)newBal);
                updateCmd.Parameters.AddWithValue("@now", now);
                updateCmd.Parameters.AddWithValue("@id", steamId.ToString());
                await updateCmd.ExecuteNonQueryAsync();

                using var logCmd = conn.CreateCommand();
                logCmd.Transaction = tx;
                logCmd.CommandText = @"
                    INSERT INTO kiz_transactions (timestamp, steam_id, type, amount, details)
                    VALUES (@now, @id, 'Deposit', @amount, @details);
                ";
                logCmd.Parameters.AddWithValue("@now", now);
                logCmd.Parameters.AddWithValue("@id", steamId.ToString());
                logCmd.Parameters.AddWithValue("@amount", (double)actualDelta);
                logCmd.Parameters.AddWithValue("@details", reason ?? string.Empty);
                await logCmd.ExecuteNonQueryAsync();

                tx.Commit();
                return newBal;
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public async Task<bool> TransferAsync(ulong fromSteamId, ulong toSteamId, decimal amount, string reason, decimal defaultStartingBalance, decimal? maxBalance = null)
        {
            if (amount <= 0 || fromSteamId == toSteamId) return false;

            await _semaphore.WaitAsync();
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync();

                using var tx = conn.BeginTransaction();
                var now = DateTime.UtcNow.ToString("o");

                // Ensure receiver exists
                using var ensureToCmd = conn.CreateCommand();
                ensureToCmd.Transaction = tx;
                ensureToCmd.CommandText = @"
                    INSERT OR IGNORE INTO kiz_accounts (steam_id, balance, last_updated)
                    VALUES (@toId, @startBal, @now);
                ";
                ensureToCmd.Parameters.AddWithValue("@toId", toSteamId.ToString());
                ensureToCmd.Parameters.AddWithValue("@startBal", (double)defaultStartingBalance);
                ensureToCmd.Parameters.AddWithValue("@now", now);
                await ensureToCmd.ExecuteNonQueryAsync();

                // Check receiver max balance
                if (maxBalance.HasValue && maxBalance.Value > 0)
                {
                    using var checkToCmd = conn.CreateCommand();
                    checkToCmd.Transaction = tx;
                    checkToCmd.CommandText = "SELECT balance FROM kiz_accounts WHERE steam_id = @toId;";
                    checkToCmd.Parameters.AddWithValue("@toId", toSteamId.ToString());
                    var toBalObj = await checkToCmd.ExecuteScalarAsync();
                    var toBal = Convert.ToDecimal(toBalObj, CultureInfo.InvariantCulture);
                    if (toBal + amount > maxBalance.Value)
                    {
                        tx.Rollback();
                        return false;
                    }
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
                withdrawCmd.Parameters.AddWithValue("@amount", (double)amount);
                withdrawCmd.Parameters.AddWithValue("@now", now);

                var affected = await withdrawCmd.ExecuteNonQueryAsync();
                if (affected <= 0)
                {
                    tx.Rollback();
                    return false;
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
                depositCmd.Parameters.AddWithValue("@amount", (double)amount);
                depositCmd.Parameters.AddWithValue("@now", now);
                await depositCmd.ExecuteNonQueryAsync();

                // Log sender
                using var logFromCmd = conn.CreateCommand();
                logFromCmd.Transaction = tx;
                logFromCmd.CommandText = @"
                    INSERT INTO kiz_transactions (timestamp, steam_id, type, amount, details)
                    VALUES (@now, @id, 'TransferSent', @amount, @details);
                ";
                logFromCmd.Parameters.AddWithValue("@now", now);
                logFromCmd.Parameters.AddWithValue("@id", fromSteamId.ToString());
                logFromCmd.Parameters.AddWithValue("@amount", -(double)amount);
                logFromCmd.Parameters.AddWithValue("@details", $"To:{toSteamId} - {reason}");
                await logFromCmd.ExecuteNonQueryAsync();

                // Log receiver
                using var logToCmd = conn.CreateCommand();
                logToCmd.Transaction = tx;
                logToCmd.CommandText = @"
                    INSERT INTO kiz_transactions (timestamp, steam_id, type, amount, details)
                    VALUES (@now, @id, 'TransferReceived', @amount, @details);
                ";
                logToCmd.Parameters.AddWithValue("@now", now);
                logToCmd.Parameters.AddWithValue("@id", toSteamId.ToString());
                logToCmd.Parameters.AddWithValue("@amount", (double)amount);
                logToCmd.Parameters.AddWithValue("@details", $"From:{fromSteamId} - {reason}");
                await logToCmd.ExecuteNonQueryAsync();

                tx.Commit();
                return true;
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public async Task<List<TopBalanceEntry>> GetTopBalancesAsync(int count)
        {
            if (count <= 0) count = 10;
            var list = new List<TopBalanceEntry>();

            await _semaphore.WaitAsync();
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync();

                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT steam_id, balance FROM kiz_accounts ORDER BY balance DESC LIMIT @limit;";
                cmd.Parameters.AddWithValue("@limit", count);

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    if (ulong.TryParse(reader.GetString(0), out var steamId))
                    {
                        var bal = Convert.ToDecimal(reader.GetValue(1), CultureInfo.InvariantCulture);
                        list.Add(new TopBalanceEntry(steamId, bal));
                    }
                }
            }
            finally
            {
                _semaphore.Release();
            }

            return list;
        }

        public async Task SetBalanceAsync(ulong steamId, decimal amount, string reason)
        {
            if (amount < 0) amount = 0;

            await _semaphore.WaitAsync();
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync();

                using var tx = conn.BeginTransaction();
                var now = DateTime.UtcNow.ToString("o");

                using var upsertCmd = conn.CreateCommand();
                upsertCmd.Transaction = tx;
                upsertCmd.CommandText = @"
                    INSERT INTO kiz_accounts (steam_id, balance, last_updated)
                    VALUES (@id, @bal, @now)
                    ON CONFLICT(steam_id) DO UPDATE SET balance = @bal, last_updated = @now;
                ";
                upsertCmd.Parameters.AddWithValue("@id", steamId.ToString());
                upsertCmd.Parameters.AddWithValue("@bal", (double)amount);
                upsertCmd.Parameters.AddWithValue("@now", now);
                await upsertCmd.ExecuteNonQueryAsync();

                using var logCmd = conn.CreateCommand();
                logCmd.Transaction = tx;
                logCmd.CommandText = @"
                    INSERT INTO kiz_transactions (timestamp, steam_id, type, amount, details)
                    VALUES (@now, @id, 'SetBalance', @amount, @details);
                ";
                logCmd.Parameters.AddWithValue("@now", now);
                logCmd.Parameters.AddWithValue("@id", steamId.ToString());
                logCmd.Parameters.AddWithValue("@amount", (double)amount);
                logCmd.Parameters.AddWithValue("@details", reason ?? string.Empty);
                await logCmd.ExecuteNonQueryAsync();

                tx.Commit();
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public async Task LogTransactionAsync(ulong steamId, string type, decimal amount, string details)
        {
            await _semaphore.WaitAsync();
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync();

                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO kiz_transactions (timestamp, steam_id, type, amount, details)
                    VALUES (@now, @id, @type, @amount, @details);
                ";
                cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
                cmd.Parameters.AddWithValue("@id", steamId.ToString());
                cmd.Parameters.AddWithValue("@type", type ?? "General");
                cmd.Parameters.AddWithValue("@amount", (double)amount);
                cmd.Parameters.AddWithValue("@details", details ?? string.Empty);
                await cmd.ExecuteNonQueryAsync();
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public async Task PruneLogsAsync(int retentionDays)
        {
            if (retentionDays <= 0) return;

            var cutoff = DateTime.UtcNow.AddDays(-retentionDays).ToString("o");
            await _semaphore.WaitAsync();
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync();

                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM kiz_transactions WHERE timestamp < @cutoff;";
                cmd.Parameters.AddWithValue("@cutoff", cutoff);
                await cmd.ExecuteNonQueryAsync();
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _semaphore.Dispose();
        }
    }
}
