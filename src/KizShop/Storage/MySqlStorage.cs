using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using MySqlConnector;
using KizShop.Common;

namespace KizShop.Storage
{
    public class MySqlStorage : IKizStorage
    {
        private readonly string _connectionString;
        private readonly IClock _clock;
        private bool _disposed;

        public MySqlStorage(string connectionString, IClock clock = null)
        {
            _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
            _clock = clock ?? SystemClock.Instance;
        }

        public async Task InitializeAsync()
        {
            await Task.Run(async () =>
            {
                using var conn = new MySqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);

                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS schema_version (
                        version INT PRIMARY KEY,
                        applied_at DATETIME NOT NULL
                    );

                    CREATE TABLE IF NOT EXISTS kiz_accounts (
                        steam_id BIGINT UNSIGNED PRIMARY KEY,
                        balance BIGINT NOT NULL,
                        last_updated DATETIME NOT NULL
                    );

                    CREATE TABLE IF NOT EXISTS kiz_transactions (
                        id INT AUTO_INCREMENT PRIMARY KEY,
                        timestamp DATETIME NOT NULL,
                        steam_id BIGINT UNSIGNED NOT NULL,
                        type VARCHAR(32) NOT NULL,
                        amount BIGINT NOT NULL,
                        details VARCHAR(255)
                    );

                    INSERT IGNORE INTO schema_version (version, applied_at)
                    VALUES (2, UTC_TIMESTAMP());
                ";
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }).ConfigureAwait(false);
        }

        public async Task<bool> AccountExistsAsync(ulong steamId)
        {
            return await Task.Run(async () =>
            {
                using var conn = new MySqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);

                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT 1 FROM kiz_accounts WHERE steam_id = @id LIMIT 1;";
                cmd.Parameters.AddWithValue("@id", steamId);
                var result = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
                return result != null;
            }).ConfigureAwait(false);
        }

        public async Task<Money> GetBalanceAsync(ulong steamId, Money defaultStartingBalance)
        {
            return await Task.Run(async () =>
            {
                using var conn = new MySqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);

                using var selectCmd = conn.CreateCommand();
                selectCmd.CommandText = "SELECT balance FROM kiz_accounts WHERE steam_id = @id;";
                selectCmd.Parameters.AddWithValue("@id", steamId);

                var result = await selectCmd.ExecuteScalarAsync().ConfigureAwait(false);
                if (result != null && result != DBNull.Value)
                {
                    return Money.FromCents(Convert.ToInt64(result));
                }

                using var insertCmd = conn.CreateCommand();
                insertCmd.CommandText = @"
                    INSERT IGNORE INTO kiz_accounts (steam_id, balance, last_updated)
                    VALUES (@id, @bal, UTC_TIMESTAMP());
                ";
                insertCmd.Parameters.AddWithValue("@id", steamId);
                insertCmd.Parameters.AddWithValue("@bal", defaultStartingBalance.Cents);
                await insertCmd.ExecuteNonQueryAsync().ConfigureAwait(false);

                return defaultStartingBalance;
            }).ConfigureAwait(false);
        }

        public async Task<WithdrawResult> TryWithdrawAsync(ulong steamId, Money amount, string reason, Money defaultStartingBalance)
        {
            if (amount.Cents <= 0) return WithdrawResult.Failed(Money.Zero);

            return await Task.Run(async () =>
            {
                using var conn = new MySqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);

                using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted).ConfigureAwait(false);

                // Ensure account exists
                using var ensureCmd = conn.CreateCommand();
                ensureCmd.Transaction = tx;
                ensureCmd.CommandText = @"
                    INSERT IGNORE INTO kiz_accounts (steam_id, balance, last_updated)
                    VALUES (@id, @startBal, UTC_TIMESTAMP());
                ";
                ensureCmd.Parameters.AddWithValue("@id", steamId);
                ensureCmd.Parameters.AddWithValue("@startBal", defaultStartingBalance.Cents);
                await ensureCmd.ExecuteNonQueryAsync().ConfigureAwait(false);

                using var readCmd = conn.CreateCommand();
                readCmd.Transaction = tx;
                readCmd.CommandText = "SELECT balance FROM kiz_accounts WHERE steam_id = @id FOR UPDATE;";
                readCmd.Parameters.AddWithValue("@id", steamId);
                var curObj = await readCmd.ExecuteScalarAsync().ConfigureAwait(false);
                if (curObj == null || curObj == DBNull.Value)
                {
                    await tx.RollbackAsync().ConfigureAwait(false);
                    return WithdrawResult.Failed(Money.Zero);
                }
                var oldBal = Money.FromCents(Convert.ToInt64(curObj));
                if (oldBal < amount)
                {
                    await tx.RollbackAsync().ConfigureAwait(false);
                    return WithdrawResult.Failed(oldBal);
                }

                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"
                    UPDATE kiz_accounts 
                    SET balance = balance - @amount, last_updated = UTC_TIMESTAMP() 
                    WHERE steam_id = @id AND balance >= @amount;
                ";
                cmd.Parameters.AddWithValue("@id", steamId);
                cmd.Parameters.AddWithValue("@amount", amount.Cents);

                var affected = await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                if (affected <= 0)
                {
                    await tx.RollbackAsync().ConfigureAwait(false);
                    return WithdrawResult.Failed(oldBal);
                }

                using var logCmd = conn.CreateCommand();
                logCmd.Transaction = tx;
                logCmd.CommandText = @"
                    INSERT INTO kiz_transactions (timestamp, steam_id, type, amount, details)
                    VALUES (UTC_TIMESTAMP(), @id, 'Withdraw', @amount, @details);
                ";
                logCmd.Parameters.AddWithValue("@id", steamId);
                logCmd.Parameters.AddWithValue("@amount", -amount.Cents);
                logCmd.Parameters.AddWithValue("@details", reason ?? string.Empty);
                await logCmd.ExecuteNonQueryAsync().ConfigureAwait(false);

                await tx.CommitAsync().ConfigureAwait(false);
                return WithdrawResult.Succeeded(oldBal, oldBal - amount);
            }).ConfigureAwait(false);
        }

        public async Task<DepositResult> DepositAsync(ulong steamId, Money amount, string reason, Money defaultStartingBalance, Money? maxBalance = null, bool ignoreMaxBalance = false)
        {
            return await Task.Run(async () =>
            {
                using var conn = new MySqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);

                using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted).ConfigureAwait(false);

                using var ensureCmd = conn.CreateCommand();
                ensureCmd.Transaction = tx;
                ensureCmd.CommandText = @"
                    INSERT IGNORE INTO kiz_accounts (steam_id, balance, last_updated)
                    VALUES (@id, @startBal, UTC_TIMESTAMP());
                ";
                ensureCmd.Parameters.AddWithValue("@id", steamId);
                ensureCmd.Parameters.AddWithValue("@startBal", defaultStartingBalance.Cents);
                await ensureCmd.ExecuteNonQueryAsync().ConfigureAwait(false);

                using var getCmd = conn.CreateCommand();
                getCmd.Transaction = tx;
                getCmd.CommandText = "SELECT balance FROM kiz_accounts WHERE steam_id = @id FOR UPDATE;";
                getCmd.Parameters.AddWithValue("@id", steamId);
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
                updateCmd.CommandText = "UPDATE kiz_accounts SET balance = @newBal, last_updated = UTC_TIMESTAMP() WHERE steam_id = @id;";
                updateCmd.Parameters.AddWithValue("@newBal", newCents);
                updateCmd.Parameters.AddWithValue("@id", steamId);
                await updateCmd.ExecuteNonQueryAsync().ConfigureAwait(false);

                if (delta != 0)
                {
                    using var logCmd = conn.CreateCommand();
                    logCmd.Transaction = tx;
                    logCmd.CommandText = @"
                        INSERT INTO kiz_transactions (timestamp, steam_id, type, amount, details)
                        VALUES (UTC_TIMESTAMP(), @id, @type, @amount, @details);
                    ";
                    logCmd.Parameters.AddWithValue("@id", steamId);
                    logCmd.Parameters.AddWithValue("@type", ignoreMaxBalance ? "Refund" : "Deposit");
                    logCmd.Parameters.AddWithValue("@amount", delta);
                    logCmd.Parameters.AddWithValue("@details", reason ?? string.Empty);
                    await logCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }

                await tx.CommitAsync().ConfigureAwait(false);
                return new DepositResult(true, oldBal, Money.FromCents(newCents));
            }).ConfigureAwait(false);
        }

        public async Task<TransferResult> TransferAsync(ulong fromSteamId, ulong toSteamId, Money amount, string reason, Money defaultStartingBalance, Money? maxBalance = null)
        {
            if (amount.Cents <= 0 || fromSteamId == toSteamId)
            {
                return TransferResult.Failed(Money.Zero, Money.Zero);
            }

            // Retry once on deadlock (MySQL error 1213)
            int retries = 2;
            while (retries-- > 0)
            {
                try
                {
                    return await ExecuteTransferInternal(fromSteamId, toSteamId, amount, reason, defaultStartingBalance, maxBalance).ConfigureAwait(false);
                }
                catch (MySqlException ex) when (ex.Number == 1213 && retries > 0)
                {
                    await Task.Delay(50).ConfigureAwait(false);
                }
            }

            return TransferResult.Failed(Money.Zero, Money.Zero);
        }

        private async Task<TransferResult> ExecuteTransferInternal(ulong fromSteamId, ulong toSteamId, Money amount, string reason, Money defaultStartingBalance, Money? maxBalance)
        {
            return await Task.Run(async () =>
            {
                using var conn = new MySqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);

                using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted).ConfigureAwait(false);

                // Lock rows in strictly ascending steam_id order to eliminate deadlocks
                ulong firstLock = Math.Min(fromSteamId, toSteamId);
                ulong secondLock = Math.Max(fromSteamId, toSteamId);

                // Ensure both exist first
                using var ensureCmd = conn.CreateCommand();
                ensureCmd.Transaction = tx;
                ensureCmd.CommandText = @"
                    INSERT IGNORE INTO kiz_accounts (steam_id, balance, last_updated) VALUES (@id1, @bal, UTC_TIMESTAMP());
                    INSERT IGNORE INTO kiz_accounts (steam_id, balance, last_updated) VALUES (@id2, @bal, UTC_TIMESTAMP());
                ";
                ensureCmd.Parameters.AddWithValue("@id1", firstLock);
                ensureCmd.Parameters.AddWithValue("@id2", secondLock);
                ensureCmd.Parameters.AddWithValue("@bal", defaultStartingBalance.Cents);
                await ensureCmd.ExecuteNonQueryAsync().ConfigureAwait(false);

                // Lock both in ascending order
                using var lockCmd = conn.CreateCommand();
                lockCmd.Transaction = tx;
                lockCmd.CommandText = "SELECT steam_id, balance FROM kiz_accounts WHERE steam_id IN (@id1, @id2) ORDER BY steam_id ASC FOR UPDATE;";
                lockCmd.Parameters.AddWithValue("@id1", firstLock);
                lockCmd.Parameters.AddWithValue("@id2", secondLock);

                Money fromOld = Money.Zero;
                Money toOld = Money.Zero;
                using (var reader = await lockCmd.ExecuteReaderAsync().ConfigureAwait(false))
                {
                    while (await reader.ReadAsync().ConfigureAwait(false))
                    {
                        var id = reader.GetUInt64(0);
                        var bal = Money.FromCents(reader.GetInt64(1));
                        if (id == fromSteamId) fromOld = bal;
                        if (id == toSteamId) toOld = bal;
                    }
                }

                if (fromOld < amount)
                {
                    await tx.RollbackAsync().ConfigureAwait(false);
                    return TransferResult.Failed(fromOld, toOld);
                }

                if (maxBalance.HasValue && maxBalance.Value.Cents > 0 && (toOld.Cents + amount.Cents) > maxBalance.Value.Cents)
                {
                    await tx.RollbackAsync().ConfigureAwait(false);
                    return TransferResult.Failed(fromOld, toOld);
                }

                // Withdraw from sender
                using var withdrawCmd = conn.CreateCommand();
                withdrawCmd.Transaction = tx;
                withdrawCmd.CommandText = @"
                    UPDATE kiz_accounts 
                    SET balance = balance - @amount, last_updated = UTC_TIMESTAMP() 
                    WHERE steam_id = @fromId AND balance >= @amount;
                ";
                withdrawCmd.Parameters.AddWithValue("@fromId", fromSteamId);
                withdrawCmd.Parameters.AddWithValue("@amount", amount.Cents);
                var affected = await withdrawCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                if (affected <= 0)
                {
                    await tx.RollbackAsync().ConfigureAwait(false);
                    return TransferResult.Failed(fromOld, toOld);
                }

                // Deposit to receiver
                using var depositCmd = conn.CreateCommand();
                depositCmd.Transaction = tx;
                depositCmd.CommandText = @"
                    UPDATE kiz_accounts 
                    SET balance = balance + @amount, last_updated = UTC_TIMESTAMP() 
                    WHERE steam_id = @toId;
                ";
                depositCmd.Parameters.AddWithValue("@toId", toSteamId);
                depositCmd.Parameters.AddWithValue("@amount", amount.Cents);
                await depositCmd.ExecuteNonQueryAsync().ConfigureAwait(false);

                // Logs
                using var logFromCmd = conn.CreateCommand();
                logFromCmd.Transaction = tx;
                logFromCmd.CommandText = @"
                    INSERT INTO kiz_transactions (timestamp, steam_id, type, amount, details)
                    VALUES (UTC_TIMESTAMP(), @id, 'TransferSent', @amount, @details);
                ";
                logFromCmd.Parameters.AddWithValue("@id", fromSteamId);
                logFromCmd.Parameters.AddWithValue("@amount", -amount.Cents);
                logFromCmd.Parameters.AddWithValue("@details", $"To:{toSteamId} - {reason}");
                await logFromCmd.ExecuteNonQueryAsync().ConfigureAwait(false);

                using var logToCmd = conn.CreateCommand();
                logToCmd.Transaction = tx;
                logToCmd.CommandText = @"
                    INSERT INTO kiz_transactions (timestamp, steam_id, type, amount, details)
                    VALUES (UTC_TIMESTAMP(), @id, 'TransferReceived', @amount, @details);
                ";
                logToCmd.Parameters.AddWithValue("@id", toSteamId);
                logToCmd.Parameters.AddWithValue("@amount", amount.Cents);
                logToCmd.Parameters.AddWithValue("@details", $"From:{fromSteamId} - {reason}");
                await logToCmd.ExecuteNonQueryAsync().ConfigureAwait(false);

                await tx.CommitAsync().ConfigureAwait(false);
                return TransferResult.Succeeded(fromOld, fromOld - amount, toOld, toOld + amount);
            }).ConfigureAwait(false);
        }

        public async Task<List<TopBalanceEntry>> GetTopBalancesAsync(int count)
        {
            if (count <= 0) count = 10;
            return await Task.Run(async () =>
            {
                var list = new List<TopBalanceEntry>();
                using var conn = new MySqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);

                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT steam_id, balance FROM kiz_accounts ORDER BY balance DESC LIMIT @limit;";
                cmd.Parameters.AddWithValue("@limit", count);

                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    var steamId = reader.GetUInt64(0);
                    var cents = reader.GetInt64(1);
                    list.Add(new TopBalanceEntry(steamId, Money.FromCents(cents)));
                }

                return list;
            }).ConfigureAwait(false);
        }

        public async Task SetBalanceAsync(ulong steamId, Money amount, string reason)
        {
            await Task.Run(async () =>
            {
                using var conn = new MySqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);

                using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted).ConfigureAwait(false);

                using var upsertCmd = conn.CreateCommand();
                upsertCmd.Transaction = tx;
                upsertCmd.CommandText = @"
                    INSERT INTO kiz_accounts (steam_id, balance, last_updated)
                    VALUES (@id, @bal, UTC_TIMESTAMP())
                    ON DUPLICATE KEY UPDATE balance = @bal, last_updated = UTC_TIMESTAMP();
                ";
                upsertCmd.Parameters.AddWithValue("@id", steamId);
                upsertCmd.Parameters.AddWithValue("@bal", amount.Cents);
                await upsertCmd.ExecuteNonQueryAsync().ConfigureAwait(false);

                using var logCmd = conn.CreateCommand();
                logCmd.Transaction = tx;
                logCmd.CommandText = @"
                    INSERT INTO kiz_transactions (timestamp, steam_id, type, amount, details)
                    VALUES (UTC_TIMESTAMP(), @id, 'SetBalance', @amount, @details);
                ";
                logCmd.Parameters.AddWithValue("@id", steamId);
                logCmd.Parameters.AddWithValue("@amount", amount.Cents);
                logCmd.Parameters.AddWithValue("@details", reason ?? string.Empty);
                await logCmd.ExecuteNonQueryAsync().ConfigureAwait(false);

                await tx.CommitAsync().ConfigureAwait(false);
            }).ConfigureAwait(false);
        }

        public async Task LogTransactionAsync(ulong steamId, string type, Money amount, string details)
        {
            await Task.Run(async () =>
            {
                using var conn = new MySqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);

                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO kiz_transactions (timestamp, steam_id, type, amount, details)
                    VALUES (UTC_TIMESTAMP(), @id, @type, @amount, @details);
                ";
                cmd.Parameters.AddWithValue("@id", steamId);
                cmd.Parameters.AddWithValue("@type", type ?? "General");
                cmd.Parameters.AddWithValue("@amount", amount.Cents);
                cmd.Parameters.AddWithValue("@details", details ?? string.Empty);
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }).ConfigureAwait(false);
        }

        public async Task PruneLogsAsync(int retentionDays)
        {
            if (retentionDays <= 0) return;

            await Task.Run(async () =>
            {
                using var conn = new MySqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);

                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM kiz_transactions WHERE timestamp < DATE_SUB(UTC_TIMESTAMP(), INTERVAL @days DAY);";
                cmd.Parameters.AddWithValue("@days", retentionDays);
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }).ConfigureAwait(false);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
        }
    }
}
