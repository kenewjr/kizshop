using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Threading.Tasks;
using MySqlConnector;
using KizShop.Common;

namespace KizShop.Storage
{
    public class MySqlStorage : IKizStorage
    {
        private readonly string _connectionString;
        private bool _disposed;

        public MySqlStorage(string connectionString)
        {
            _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        }

        public async Task InitializeAsync()
        {
            using var conn = new MySqlConnection(_connectionString);
            await conn.OpenAsync();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS kiz_accounts (
                    steam_id BIGINT UNSIGNED PRIMARY KEY,
                    balance DECIMAL(18, 2) NOT NULL,
                    last_updated DATETIME NOT NULL
                );

                CREATE TABLE IF NOT EXISTS kiz_transactions (
                    id INT AUTO_INCREMENT PRIMARY KEY,
                    timestamp DATETIME NOT NULL,
                    steam_id BIGINT UNSIGNED NOT NULL,
                    type VARCHAR(32) NOT NULL,
                    amount DECIMAL(18, 2) NOT NULL,
                    details VARCHAR(255)
                );
            ";
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task<decimal> GetBalanceAsync(ulong steamId, decimal defaultStartingBalance)
        {
            using var conn = new MySqlConnection(_connectionString);
            await conn.OpenAsync();

            using var selectCmd = conn.CreateCommand();
            selectCmd.CommandText = "SELECT balance FROM kiz_accounts WHERE steam_id = @id;";
            selectCmd.Parameters.AddWithValue("@id", steamId);

            var result = await selectCmd.ExecuteScalarAsync();
            if (result != null && result != DBNull.Value)
            {
                return Convert.ToDecimal(result, CultureInfo.InvariantCulture);
            }

            using var insertCmd = conn.CreateCommand();
            insertCmd.CommandText = @"
                INSERT IGNORE INTO kiz_accounts (steam_id, balance, last_updated)
                VALUES (@id, @bal, UTC_TIMESTAMP());
            ";
            insertCmd.Parameters.AddWithValue("@id", steamId);
            insertCmd.Parameters.AddWithValue("@bal", defaultStartingBalance);
            await insertCmd.ExecuteNonQueryAsync();

            return defaultStartingBalance;
        }

        public async Task<bool> TryWithdrawAsync(ulong steamId, decimal amount, string reason)
        {
            if (amount <= 0) return false;

            using var conn = new MySqlConnection(_connectionString);
            await conn.OpenAsync();

            using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
                UPDATE kiz_accounts 
                SET balance = balance - @amount, last_updated = UTC_TIMESTAMP() 
                WHERE steam_id = @id AND balance >= @amount;
            ";
            cmd.Parameters.AddWithValue("@id", steamId);
            cmd.Parameters.AddWithValue("@amount", amount);

            var affected = await cmd.ExecuteNonQueryAsync();
            if (affected <= 0)
            {
                await tx.RollbackAsync();
                return false;
            }

            using var logCmd = conn.CreateCommand();
            logCmd.Transaction = tx;
            logCmd.CommandText = @"
                INSERT INTO kiz_transactions (timestamp, steam_id, type, amount, details)
                VALUES (UTC_TIMESTAMP(), @id, 'Withdraw', @amount, @details);
            ";
            logCmd.Parameters.AddWithValue("@id", steamId);
            logCmd.Parameters.AddWithValue("@amount", -amount);
            logCmd.Parameters.AddWithValue("@details", reason ?? string.Empty);
            await logCmd.ExecuteNonQueryAsync();

            await tx.CommitAsync();
            return true;
        }

        public async Task<decimal> DepositAsync(ulong steamId, decimal amount, string reason, decimal defaultStartingBalance, decimal? maxBalance = null)
        {
            if (amount <= 0)
            {
                return await GetBalanceAsync(steamId, defaultStartingBalance);
            }

            using var conn = new MySqlConnection(_connectionString);
            await conn.OpenAsync();

            using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted);

            using var ensureCmd = conn.CreateCommand();
            ensureCmd.Transaction = tx;
            ensureCmd.CommandText = @"
                INSERT IGNORE INTO kiz_accounts (steam_id, balance, last_updated)
                VALUES (@id, @startBal, UTC_TIMESTAMP());
            ";
            ensureCmd.Parameters.AddWithValue("@id", steamId);
            ensureCmd.Parameters.AddWithValue("@startBal", defaultStartingBalance);
            await ensureCmd.ExecuteNonQueryAsync();

            using var getCmd = conn.CreateCommand();
            getCmd.Transaction = tx;
            getCmd.CommandText = "SELECT balance FROM kiz_accounts WHERE steam_id = @id FOR UPDATE;";
            getCmd.Parameters.AddWithValue("@id", steamId);
            var currentObj = await getCmd.ExecuteScalarAsync();
            var currentBal = Convert.ToDecimal(currentObj, CultureInfo.InvariantCulture);

            var newBal = currentBal + amount;
            if (maxBalance.HasValue && maxBalance.Value > 0 && newBal > maxBalance.Value)
            {
                newBal = maxBalance.Value;
            }
            var delta = newBal - currentBal;

            using var updateCmd = conn.CreateCommand();
            updateCmd.Transaction = tx;
            updateCmd.CommandText = "UPDATE kiz_accounts SET balance = @newBal, last_updated = UTC_TIMESTAMP() WHERE steam_id = @id;";
            updateCmd.Parameters.AddWithValue("@newBal", newBal);
            updateCmd.Parameters.AddWithValue("@id", steamId);
            await updateCmd.ExecuteNonQueryAsync();

            using var logCmd = conn.CreateCommand();
            logCmd.Transaction = tx;
            logCmd.CommandText = @"
                INSERT INTO kiz_transactions (timestamp, steam_id, type, amount, details)
                VALUES (UTC_TIMESTAMP(), @id, 'Deposit', @amount, @details);
            ";
            logCmd.Parameters.AddWithValue("@id", steamId);
            logCmd.Parameters.AddWithValue("@amount", delta);
            logCmd.Parameters.AddWithValue("@details", reason ?? string.Empty);
            await logCmd.ExecuteNonQueryAsync();

            await tx.CommitAsync();
            return newBal;
        }

        public async Task<bool> TransferAsync(ulong fromSteamId, ulong toSteamId, decimal amount, string reason, decimal defaultStartingBalance, decimal? maxBalance = null)
        {
            if (amount <= 0 || fromSteamId == toSteamId) return false;

            using var conn = new MySqlConnection(_connectionString);
            await conn.OpenAsync();

            using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted);

            using var ensureToCmd = conn.CreateCommand();
            ensureToCmd.Transaction = tx;
            ensureToCmd.CommandText = @"
                INSERT IGNORE INTO kiz_accounts (steam_id, balance, last_updated)
                VALUES (@toId, @startBal, UTC_TIMESTAMP());
            ";
            ensureToCmd.Parameters.AddWithValue("@toId", toSteamId);
            ensureToCmd.Parameters.AddWithValue("@startBal", defaultStartingBalance);
            await ensureToCmd.ExecuteNonQueryAsync();

            if (maxBalance.HasValue && maxBalance.Value > 0)
            {
                using var checkToCmd = conn.CreateCommand();
                checkToCmd.Transaction = tx;
                checkToCmd.CommandText = "SELECT balance FROM kiz_accounts WHERE steam_id = @toId FOR UPDATE;";
                checkToCmd.Parameters.AddWithValue("@toId", toSteamId);
                var toBalObj = await checkToCmd.ExecuteScalarAsync();
                var toBal = Convert.ToDecimal(toBalObj, CultureInfo.InvariantCulture);
                if (toBal + amount > maxBalance.Value)
                {
                    await tx.RollbackAsync();
                    return false;
                }
            }

            using var withdrawCmd = conn.CreateCommand();
            withdrawCmd.Transaction = tx;
            withdrawCmd.CommandText = @"
                UPDATE kiz_accounts 
                SET balance = balance - @amount, last_updated = UTC_TIMESTAMP() 
                WHERE steam_id = @fromId AND balance >= @amount;
            ";
            withdrawCmd.Parameters.AddWithValue("@fromId", fromSteamId);
            withdrawCmd.Parameters.AddWithValue("@amount", amount);

            var affected = await withdrawCmd.ExecuteNonQueryAsync();
            if (affected <= 0)
            {
                await tx.RollbackAsync();
                return false;
            }

            using var depositCmd = conn.CreateCommand();
            depositCmd.Transaction = tx;
            depositCmd.CommandText = @"
                UPDATE kiz_accounts 
                SET balance = balance + @amount, last_updated = UTC_TIMESTAMP() 
                WHERE steam_id = @toId;
            ";
            depositCmd.Parameters.AddWithValue("@toId", toSteamId);
            depositCmd.Parameters.AddWithValue("@amount", amount);
            await depositCmd.ExecuteNonQueryAsync();

            using var logFromCmd = conn.CreateCommand();
            logFromCmd.Transaction = tx;
            logFromCmd.CommandText = @"
                INSERT INTO kiz_transactions (timestamp, steam_id, type, amount, details)
                VALUES (UTC_TIMESTAMP(), @id, 'TransferSent', @amount, @details);
            ";
            logFromCmd.Parameters.AddWithValue("@id", fromSteamId);
            logFromCmd.Parameters.AddWithValue("@amount", -amount);
            logFromCmd.Parameters.AddWithValue("@details", $"To:{toSteamId} - {reason}");
            await logFromCmd.ExecuteNonQueryAsync();

            using var logToCmd = conn.CreateCommand();
            logToCmd.Transaction = tx;
            logToCmd.CommandText = @"
                INSERT INTO kiz_transactions (timestamp, steam_id, type, amount, details)
                VALUES (UTC_TIMESTAMP(), @id, 'TransferReceived', @amount, @details);
            ";
            logToCmd.Parameters.AddWithValue("@id", toSteamId);
            logToCmd.Parameters.AddWithValue("@amount", amount);
            logToCmd.Parameters.AddWithValue("@details", $"From:{fromSteamId} - {reason}");
            await logToCmd.ExecuteNonQueryAsync();

            await tx.CommitAsync();
            return true;
        }

        public async Task<List<TopBalanceEntry>> GetTopBalancesAsync(int count)
        {
            if (count <= 0) count = 10;
            var list = new List<TopBalanceEntry>();

            using var conn = new MySqlConnection(_connectionString);
            await conn.OpenAsync();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT steam_id, balance FROM kiz_accounts ORDER BY balance DESC LIMIT @limit;";
            cmd.Parameters.AddWithValue("@limit", count);

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var steamId = reader.GetUInt64(0);
                var bal = reader.GetDecimal(1);
                list.Add(new TopBalanceEntry(steamId, bal));
            }

            return list;
        }

        public async Task SetBalanceAsync(ulong steamId, decimal amount, string reason)
        {
            if (amount < 0) amount = 0;

            using var conn = new MySqlConnection(_connectionString);
            await conn.OpenAsync();

            using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted);

            using var upsertCmd = conn.CreateCommand();
            upsertCmd.Transaction = tx;
            upsertCmd.CommandText = @"
                INSERT INTO kiz_accounts (steam_id, balance, last_updated)
                VALUES (@id, @bal, UTC_TIMESTAMP())
                ON DUPLICATE KEY UPDATE balance = @bal, last_updated = UTC_TIMESTAMP();
            ";
            upsertCmd.Parameters.AddWithValue("@id", steamId);
            upsertCmd.Parameters.AddWithValue("@bal", amount);
            await upsertCmd.ExecuteNonQueryAsync();

            using var logCmd = conn.CreateCommand();
            logCmd.Transaction = tx;
            logCmd.CommandText = @"
                INSERT INTO kiz_transactions (timestamp, steam_id, type, amount, details)
                VALUES (UTC_TIMESTAMP(), @id, 'SetBalance', @amount, @details);
            ";
            logCmd.Parameters.AddWithValue("@id", steamId);
            logCmd.Parameters.AddWithValue("@amount", amount);
            logCmd.Parameters.AddWithValue("@details", reason ?? string.Empty);
            await logCmd.ExecuteNonQueryAsync();

            await tx.CommitAsync();
        }

        public async Task LogTransactionAsync(ulong steamId, string type, decimal amount, string details)
        {
            using var conn = new MySqlConnection(_connectionString);
            await conn.OpenAsync();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO kiz_transactions (timestamp, steam_id, type, amount, details)
                VALUES (UTC_TIMESTAMP(), @id, @type, @amount, @details);
            ";
            cmd.Parameters.AddWithValue("@id", steamId);
            cmd.Parameters.AddWithValue("@type", type ?? "General");
            cmd.Parameters.AddWithValue("@amount", amount);
            cmd.Parameters.AddWithValue("@details", details ?? string.Empty);
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task PruneLogsAsync(int retentionDays)
        {
            if (retentionDays <= 0) return;

            using var conn = new MySqlConnection(_connectionString);
            await conn.OpenAsync();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM kiz_transactions WHERE timestamp < DATE_SUB(UTC_TIMESTAMP(), INTERVAL @days DAY);";
            cmd.Parameters.AddWithValue("@days", retentionDays);
            await cmd.ExecuteNonQueryAsync();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
        }
    }
}
