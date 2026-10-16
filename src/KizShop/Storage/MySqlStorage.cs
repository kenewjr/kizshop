using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using MySqlConnector;
using KizShop.Common;
using KizShop.Economy.Models;
using KizShop.Shop;
using KizShop.Shop.Models;

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
                        applied_at DATETIME(3) NOT NULL
                    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

                    CREATE TABLE IF NOT EXISTS kiz_accounts (
                        steam_id BIGINT UNSIGNED PRIMARY KEY,
                        balance BIGINT NOT NULL,
                        last_updated DATETIME(3) NOT NULL,
                        INDEX idx_balance (balance)
                    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

                    CREATE TABLE IF NOT EXISTS kiz_transactions (
                        id INT AUTO_INCREMENT PRIMARY KEY,
                        timestamp DATETIME(3) NOT NULL,
                        steam_id BIGINT UNSIGNED NOT NULL,
                        type VARCHAR(32) NOT NULL,
                        amount BIGINT NOT NULL,
                        details VARCHAR(255),
                        INDEX idx_tx_steam (steam_id),
                        INDEX idx_tx_time (timestamp)
                    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

                    CREATE TABLE IF NOT EXISTS kiz_reward_events (
                        event_key VARCHAR(128) PRIMARY KEY,
                        boss_name VARCHAR(64) NOT NULL,
                        pool BIGINT NOT NULL,
                        timestamp DATETIME(3) NOT NULL
                    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

                    CREATE TABLE IF NOT EXISTS kiz_item_prices (
                        item_id INT UNSIGNED PRIMARY KEY,
                        sell_pressure DECIMAL(18, 4) NOT NULL,
                        buy_pressure DECIMAL(18, 4) NOT NULL,
                        last_updated DATETIME(3) NOT NULL
                    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

                    CREATE TABLE IF NOT EXISTS kiz_reward_payouts (
                        id INT AUTO_INCREMENT PRIMARY KEY,
                        event_key VARCHAR(128) NOT NULL,
                        steam_id BIGINT UNSIGNED NOT NULL,
                        amount BIGINT NOT NULL,
                        status VARCHAR(32) NOT NULL,
                        created_utc DATETIME(3) NOT NULL,
                        INDEX idx_payout_key (event_key),
                        INDEX idx_payout_status (status),
                        UNIQUE KEY uq_event_player (event_key, steam_id)
                    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

                    CREATE TABLE IF NOT EXISTS kiz_bounties (
                        id INT AUTO_INCREMENT PRIMARY KEY,
                        placer_steam_id BIGINT UNSIGNED NOT NULL,
                        target_steam_id BIGINT UNSIGNED NOT NULL,
                        escrow_cents BIGINT NOT NULL,
                        net_cents BIGINT NOT NULL,
                        created_utc DATETIME(3) NOT NULL,
                        refund_pending TINYINT(1) NOT NULL DEFAULT 0,
                        INDEX idx_target (target_steam_id),
                        INDEX idx_placer (placer_steam_id),
                        INDEX idx_created (created_utc),
                        INDEX idx_bounty_pending (refund_pending)
                    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

                    CREATE TABLE IF NOT EXISTS kiz_daily_claims (
                        steam_id BIGINT UNSIGNED PRIMARY KEY,
                        last_claim_day_index INT NOT NULL,
                        streak INT NOT NULL,
                        last_claimed_utc DATETIME(3) NOT NULL
                    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

                    INSERT IGNORE INTO schema_version (version, applied_at)
                    VALUES (3, UTC_TIMESTAMP(3));
                ";
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);

                // Migrations for existing upgraded databases
                await TryExecuteMigrationAsync(conn, "ALTER TABLE kiz_bounties ADD COLUMN refund_pending TINYINT(1) NOT NULL DEFAULT 0;").ConfigureAwait(false);
                await TryExecuteMigrationAsync(conn, "ALTER TABLE kiz_transactions ADD INDEX idx_tx_steam (steam_id);").ConfigureAwait(false);
                await TryExecuteMigrationAsync(conn, "ALTER TABLE kiz_transactions ADD INDEX idx_tx_time (timestamp);").ConfigureAwait(false);
                await TryExecuteMigrationAsync(conn, "ALTER TABLE kiz_bounties ADD INDEX idx_bounty_pending (refund_pending);").ConfigureAwait(false);
                await TryExecuteMigrationAsync(conn, "ALTER TABLE kiz_reward_payouts ADD UNIQUE KEY uq_event_player (event_key, steam_id);").ConfigureAwait(false);
                await TryExecuteMigrationAsync(conn, "ALTER TABLE kiz_accounts ADD INDEX idx_balance (balance);").ConfigureAwait(false);
            }).ConfigureAwait(false);
        }

        private static async Task TryExecuteMigrationAsync(MySqlConnection conn, string sql)
        {
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            catch
            {
                // Ignore if column, index, or constraint already exists
            }
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

        public async Task<bool> TryRecordRewardEventAsync(string eventKey, string bossName, Money pool, DateTime utc)
        {
            if (string.IsNullOrEmpty(eventKey)) return false;
            return await Task.Run(async () =>
            {
                using var conn = new MySqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);

                try
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"
                        INSERT INTO kiz_reward_events (event_key, boss_name, pool, timestamp)
                        VALUES (@k, @b, @p, @t);
                    ";
                    cmd.Parameters.AddWithValue("@k", eventKey);
                    cmd.Parameters.AddWithValue("@b", bossName ?? string.Empty);
                    cmd.Parameters.AddWithValue("@p", pool.Cents);
                    cmd.Parameters.AddWithValue("@t", utc);
                    await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                    return true;
                }
                catch (MySqlException ex) when (ex.Number == 1062 || ex.Message.IndexOf("Duplicate", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return false;
                }
            }).ConfigureAwait(false);
        }

        public async Task<BossRewardEventEntry> GetRewardEventAsync(string eventKey)
        {
            if (string.IsNullOrEmpty(eventKey)) return null;
            return await Task.Run(async () =>
            {
                using var conn = new MySqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);

                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT event_key, boss_name, pool, timestamp FROM kiz_reward_events WHERE event_key = @k LIMIT 1;";
                cmd.Parameters.AddWithValue("@k", eventKey);

                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                if (await reader.ReadAsync().ConfigureAwait(false))
                {
                    return new BossRewardEventEntry
                    {
                        EventKey = reader.GetString(0),
                        BossName = reader.GetString(1),
                        Pool = Money.FromCents(reader.GetInt64(2)),
                        TimestampUtc = DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc)
                    };
                }
                return null;
            }).ConfigureAwait(false);
        }

        public async Task RecordRewardPayoutAsync(string eventKey, ulong steamId, Money amount, string status, DateTime utc)
        {
            await Task.Run(async () =>
            {
                using var conn = new MySqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);

                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    INSERT IGNORE INTO kiz_reward_payouts (event_key, steam_id, amount, status, created_utc)
                    VALUES (@k, @s, @a, @st, @c);
                ";
                cmd.Parameters.AddWithValue("@k", eventKey ?? string.Empty);
                cmd.Parameters.AddWithValue("@s", steamId);
                cmd.Parameters.AddWithValue("@a", amount.Cents);
                cmd.Parameters.AddWithValue("@st", status ?? "Unpaid");
                cmd.Parameters.AddWithValue("@c", utc);
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }).ConfigureAwait(false);
        }

        public async Task<List<RewardPayoutEntry>> GetUnpaidRewardPayoutsAsync()
        {
            return await Task.Run(async () =>
            {
                using var conn = new MySqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);

                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT id, event_key, steam_id, amount, status, created_utc
                    FROM kiz_reward_payouts
                    WHERE status != 'Paid'
                    ORDER BY id ASC;
                ";

                var list = new List<RewardPayoutEntry>();
                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    list.Add(new RewardPayoutEntry
                    {
                        Id = reader.GetInt32(0),
                        EventKey = reader.GetString(1),
                        SteamId = (ulong)reader.GetInt64(2),
                        Amount = Money.FromCents(reader.GetInt64(3)),
                        Status = reader.GetString(4),
                        CreatedUtc = DateTime.SpecifyKind(reader.GetDateTime(5), DateTimeKind.Utc)
                    });
                }
                return list;
            }).ConfigureAwait(false);
        }

        public async Task MarkRewardPayoutPaidAsync(int id, DateTime utc)
        {
            await Task.Run(async () =>
            {
                using var conn = new MySqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);

                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE kiz_reward_payouts SET status = 'Paid' WHERE id = @id;";
                cmd.Parameters.AddWithValue("@id", id);
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }).ConfigureAwait(false);
        }

        public async Task<ItemPriceState> GetItemPriceStateAsync(ushort itemId)
        {
            return await Task.Run(async () =>
            {
                using var conn = new MySqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);

                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT sell_pressure, buy_pressure, last_updated FROM kiz_item_prices WHERE item_id = @id LIMIT 1;";
                cmd.Parameters.AddWithValue("@id", itemId);

                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                if (await reader.ReadAsync().ConfigureAwait(false))
                {
                    return new ItemPriceState
                    {
                        ItemId = itemId,
                        SellPressure = reader.GetDecimal(0),
                        BuyPressure = reader.GetDecimal(1),
                        LastUpdatedUtc = DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc)
                    };
                }

                return new ItemPriceState
                {
                    ItemId = itemId,
                    SellPressure = 0,
                    BuyPressure = 0,
                    LastUpdatedUtc = DateTime.MinValue
                };
            }).ConfigureAwait(false);
        }

        public async Task UpdateItemPricePressureAsync(ushort itemId, decimal sellDelta, decimal buyDelta, DateTime nowUtc, decimal recoveryPerHour)
        {
            await Task.Run(async () =>
            {
                using var conn = new MySqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);

                decimal curSell = 0;
                decimal curBuy = 0;
                DateTime lastUpdated = DateTime.MinValue;

                using (var selCmd = conn.CreateCommand())
                {
                    selCmd.CommandText = "SELECT sell_pressure, buy_pressure, last_updated FROM kiz_item_prices WHERE item_id = @id LIMIT 1;";
                    selCmd.Parameters.AddWithValue("@id", itemId);
                    using var reader = await selCmd.ExecuteReaderAsync().ConfigureAwait(false);
                    if (await reader.ReadAsync().ConfigureAwait(false))
                    {
                        curSell = reader.GetDecimal(0);
                        curBuy = reader.GetDecimal(1);
                        lastUpdated = DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc);
                    }
                }

                decimal recSell = PriceCalculator.ComputeRecoveredPressure(curSell, lastUpdated, nowUtc, recoveryPerHour);
                decimal recBuy = PriceCalculator.ComputeRecoveredPressure(curBuy, lastUpdated, nowUtc, recoveryPerHour);

                var newSell = Math.Max(0m, recSell + sellDelta);
                var newBuy = Math.Max(0m, recBuy + buyDelta);

                using (var upsertCmd = conn.CreateCommand())
                {
                    upsertCmd.CommandText = @"
                        INSERT INTO kiz_item_prices (item_id, sell_pressure, buy_pressure, last_updated)
                        VALUES (@id, @sp, @bp, @lu)
                        ON DUPLICATE KEY UPDATE
                            sell_pressure = @sp,
                            buy_pressure = @bp,
                            last_updated = @lu;
                    ";
                    upsertCmd.Parameters.AddWithValue("@id", itemId);
                    upsertCmd.Parameters.AddWithValue("@sp", newSell);
                    upsertCmd.Parameters.AddWithValue("@bp", newBuy);
                    upsertCmd.Parameters.AddWithValue("@lu", nowUtc);
                    await upsertCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
            }).ConfigureAwait(false);
        }

        public async Task ResetItemPriceStateAsync(ushort? itemId)
        {
            await Task.Run(async () =>
            {
                using var conn = new MySqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);

                using var cmd = conn.CreateCommand();
                if (itemId.HasValue)
                {
                    cmd.CommandText = "DELETE FROM kiz_item_prices WHERE item_id = @id;";
                    cmd.Parameters.AddWithValue("@id", itemId.Value);
                }
                else
                {
                    cmd.CommandText = "DELETE FROM kiz_item_prices;";
                }
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }).ConfigureAwait(false);
        }

        public async Task<int> CreateBountyAsync(ulong placerSteamId, ulong targetSteamId, Money escrowAmount, Money netAmount, DateTime createdUtc)
        {
            return await Task.Run(async () =>
            {
                using var conn = new MySqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);

                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO kiz_bounties (placer_steam_id, target_steam_id, escrow_cents, net_cents, created_utc, refund_pending)
                    VALUES (@p, @t, @e, @n, @c, 0);
                    SELECT LAST_INSERT_ID();
                ";
                cmd.Parameters.AddWithValue("@p", placerSteamId);
                cmd.Parameters.AddWithValue("@t", targetSteamId);
                cmd.Parameters.AddWithValue("@e", escrowAmount.Cents);
                cmd.Parameters.AddWithValue("@n", netAmount.Cents);
                cmd.Parameters.AddWithValue("@c", createdUtc);

                var idObj = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
                return Convert.ToInt32(idObj);
            }).ConfigureAwait(false);
        }

        public async Task<(bool Created, Money CurrentTotal)> CreateBountyIfWithinLimitAsync(
            ulong placerSteamId,
            ulong targetSteamId,
            Money escrowAmount,
            Money netAmount,
            DateTime createdUtc,
            Money maxTotalNet)
        {
            return await Task.Run(async () =>
            {
                using var conn = new MySqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);

                using var tx = await conn.BeginTransactionAsync().ConfigureAwait(false);
                long currentTotalCents = 0;

                using (var sumCmd = conn.CreateCommand())
                {
                    sumCmd.Transaction = tx;
                    sumCmd.CommandText = "SELECT COALESCE(SUM(net_cents), 0) FROM kiz_bounties WHERE target_steam_id = @t AND refund_pending = 0 FOR UPDATE;";
                    sumCmd.Parameters.AddWithValue("@t", targetSteamId);
                    var res = await sumCmd.ExecuteScalarAsync().ConfigureAwait(false);
                    currentTotalCents = Convert.ToInt64(res);
                }

                if (maxTotalNet.Cents > 0 && currentTotalCents + netAmount.Cents > maxTotalNet.Cents)
                {
                    await tx.CommitAsync().ConfigureAwait(false);
                    return (false, Money.FromCents(currentTotalCents));
                }

                using (var insertCmd = conn.CreateCommand())
                {
                    insertCmd.Transaction = tx;
                    insertCmd.CommandText = @"
                        INSERT INTO kiz_bounties (placer_steam_id, target_steam_id, escrow_cents, net_cents, created_utc, refund_pending)
                        VALUES (@p, @t, @e, @n, @c, 0);
                    ";
                    insertCmd.Parameters.AddWithValue("@p", placerSteamId);
                    insertCmd.Parameters.AddWithValue("@t", targetSteamId);
                    insertCmd.Parameters.AddWithValue("@e", escrowAmount.Cents);
                    insertCmd.Parameters.AddWithValue("@n", netAmount.Cents);
                    insertCmd.Parameters.AddWithValue("@c", createdUtc);
                    await insertCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }

                await tx.CommitAsync().ConfigureAwait(false);
                return (true, Money.FromCents(currentTotalCents + netAmount.Cents));
            }).ConfigureAwait(false);
        }

        public async Task<List<BountyEntry>> GetActiveBountiesAsync(int limit = 50)
        {
            return await Task.Run(async () =>
            {
                using var conn = new MySqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);

                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT id, placer_steam_id, target_steam_id, escrow_cents, net_cents, created_utc, refund_pending
                    FROM kiz_bounties
                    WHERE refund_pending = 0
                    ORDER BY net_cents DESC
                    LIMIT @lim;
                ";
                cmd.Parameters.AddWithValue("@lim", limit);

                var list = new List<BountyEntry>();
                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    list.Add(new BountyEntry
                    {
                        Id = reader.GetInt32(0),
                        PlacerSteamId = (ulong)reader.GetInt64(1),
                        TargetSteamId = (ulong)reader.GetInt64(2),
                        EscrowAmount = Money.FromCents(reader.GetInt64(3)),
                        NetAmount = Money.FromCents(reader.GetInt64(4)),
                        CreatedUtc = DateTime.SpecifyKind(reader.GetDateTime(5), DateTimeKind.Utc),
                        RefundPending = reader.GetBoolean(6)
                    });
                }
                return list;
            }).ConfigureAwait(false);
        }

        public async Task<Money> GetTotalBountyOnTargetAsync(ulong targetSteamId)
        {
            return await Task.Run(async () =>
            {
                using var conn = new MySqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);

                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COALESCE(SUM(net_cents), 0) FROM kiz_bounties WHERE target_steam_id = @t AND refund_pending = 0;";
                cmd.Parameters.AddWithValue("@t", targetSteamId);

                var res = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
                return Money.FromCents(Convert.ToInt64(res));
            }).ConfigureAwait(false);
        }

        public async Task<(List<BountyEntry> ClaimedBounties, Money TotalAmount)> ClaimBountiesAsync(ulong targetSteamId, ulong killerSteamId)
        {
            return await Task.Run(async () =>
            {
                using var conn = new MySqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);

                using var tx = await conn.BeginTransactionAsync().ConfigureAwait(false);
                var list = new List<BountyEntry>();
                long totalCents = 0;

                using (var selCmd = conn.CreateCommand())
                {
                    selCmd.Transaction = tx;
                    selCmd.CommandText = @"
                        SELECT id, placer_steam_id, target_steam_id, escrow_cents, net_cents, created_utc, refund_pending
                        FROM kiz_bounties
                        WHERE target_steam_id = @t AND placer_steam_id != @k AND refund_pending = 0
                        FOR UPDATE;
                    ";
                    selCmd.Parameters.AddWithValue("@t", targetSteamId);
                    selCmd.Parameters.AddWithValue("@k", killerSteamId);

                    using var reader = await selCmd.ExecuteReaderAsync().ConfigureAwait(false);
                    while (await reader.ReadAsync().ConfigureAwait(false))
                    {
                        var net = reader.GetInt64(4);
                        totalCents += net;
                        list.Add(new BountyEntry
                        {
                            Id = reader.GetInt32(0),
                            PlacerSteamId = (ulong)reader.GetInt64(1),
                            TargetSteamId = (ulong)reader.GetInt64(2),
                            EscrowAmount = Money.FromCents(reader.GetInt64(3)),
                            NetAmount = Money.FromCents(net),
                            CreatedUtc = DateTime.SpecifyKind(reader.GetDateTime(5), DateTimeKind.Utc),
                            RefundPending = reader.GetBoolean(6)
                        });
                    }
                }

                if (list.Count > 0)
                {
                    using var delCmd = conn.CreateCommand();
                    delCmd.Transaction = tx;
                    delCmd.CommandText = "DELETE FROM kiz_bounties WHERE target_steam_id = @t AND placer_steam_id != @k AND refund_pending = 0;";
                    delCmd.Parameters.AddWithValue("@t", targetSteamId);
                    delCmd.Parameters.AddWithValue("@k", killerSteamId);
                    await delCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }

                await tx.CommitAsync().ConfigureAwait(false);
                return (list, Money.FromCents(totalCents));
            }).ConfigureAwait(false);
        }

        public async Task<List<BountyEntry>> GetAndMarkExpiredBountiesAsync(DateTime cutoffUtc)
        {
            return await Task.Run(async () =>
            {
                using var conn = new MySqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);

                using var tx = await conn.BeginTransactionAsync().ConfigureAwait(false);
                var list = new List<BountyEntry>();

                using (var selectCmd = conn.CreateCommand())
                {
                    selectCmd.Transaction = tx;
                    selectCmd.CommandText = @"
                        SELECT id, placer_steam_id, target_steam_id, escrow_cents, net_cents, created_utc, refund_pending
                        FROM kiz_bounties
                        WHERE created_utc < @co OR refund_pending = 1
                        FOR UPDATE;
                    ";
                    selectCmd.Parameters.AddWithValue("@co", cutoffUtc);

                    using var reader = await selectCmd.ExecuteReaderAsync().ConfigureAwait(false);
                    while (await reader.ReadAsync().ConfigureAwait(false))
                    {
                        list.Add(new BountyEntry
                        {
                            Id = reader.GetInt32(0),
                            PlacerSteamId = (ulong)reader.GetInt64(1),
                            TargetSteamId = (ulong)reader.GetInt64(2),
                            EscrowAmount = Money.FromCents(reader.GetInt64(3)),
                            NetAmount = Money.FromCents(reader.GetInt64(4)),
                            CreatedUtc = DateTime.SpecifyKind(reader.GetDateTime(5), DateTimeKind.Utc),
                            RefundPending = true
                        });
                    }
                }

                if (list.Count > 0)
                {
                    using var updateCmd = conn.CreateCommand();
                    updateCmd.Transaction = tx;
                    updateCmd.CommandText = "UPDATE kiz_bounties SET refund_pending = 1 WHERE created_utc < @co OR refund_pending = 1;";
                    updateCmd.Parameters.AddWithValue("@co", cutoffUtc);
                    await updateCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }

                await tx.CommitAsync().ConfigureAwait(false);
                return list;
            }).ConfigureAwait(false);
        }

        public async Task ConfirmBountyRefundAsync(int bountyId)
        {
            await Task.Run(async () =>
            {
                using var conn = new MySqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);

                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM kiz_bounties WHERE id = @id;";
                cmd.Parameters.AddWithValue("@id", bountyId);
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }).ConfigureAwait(false);
        }

        public async Task<bool> CanClaimDailyAsync(ulong steamId, int currentDayIndex)
        {
            return await Task.Run(async () =>
            {
                using var conn = new MySqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);

                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT last_claim_day_index FROM kiz_daily_claims WHERE steam_id = @id LIMIT 1;";
                cmd.Parameters.AddWithValue("@id", steamId);

                var res = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
                if (res == null || res == DBNull.Value) return true;
                return Convert.ToInt32(res) != currentDayIndex;
            }).ConfigureAwait(false);
        }

        public async Task<DailyClaimResult> TryClaimDailyAsync(ulong steamId, int currentDayIndex, int maxStreakDays, DateTime nowUtc)
        {
            return await Task.Run(async () =>
            {
                using var conn = new MySqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);

                using var tx = await conn.BeginTransactionAsync().ConfigureAwait(false);

                int? lastDay = null;
                int currentStreak = 0;

                using (var selCmd = conn.CreateCommand())
                {
                    selCmd.Transaction = tx;
                    selCmd.CommandText = "SELECT last_claim_day_index, streak FROM kiz_daily_claims WHERE steam_id = @id FOR UPDATE;";
                    selCmd.Parameters.AddWithValue("@id", steamId);
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
                        insCmd.Parameters.AddWithValue("@id", steamId);
                        insCmd.Parameters.AddWithValue("@d", currentDayIndex);
                        insCmd.Parameters.AddWithValue("@t", nowUtc);
                        await insCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                    }
                    await tx.CommitAsync().ConfigureAwait(false);
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
                    await tx.CommitAsync().ConfigureAwait(false);
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
                    updCmd.Parameters.AddWithValue("@id", steamId);
                    updCmd.Parameters.AddWithValue("@d", currentDayIndex);
                    updCmd.Parameters.AddWithValue("@s", newStreak);
                    updCmd.Parameters.AddWithValue("@t", nowUtc);
                    await updCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }

                await tx.CommitAsync().ConfigureAwait(false);
                return new DailyClaimResult
                {
                    Allowed = true,
                    Streak = newStreak,
                    PreviousDayIndex = prevDay,
                    PreviousStreak = prevStreak
                };
            }).ConfigureAwait(false);
        }

        public async Task RollbackDailyClaimAsync(ulong steamId, int previousDayIndex, int previousStreak)
        {
            await Task.Run(async () =>
            {
                using var conn = new MySqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);

                using var cmd = conn.CreateCommand();
                if (previousDayIndex < 0)
                {
                    cmd.CommandText = "DELETE FROM kiz_daily_claims WHERE steam_id = @id;";
                    cmd.Parameters.AddWithValue("@id", steamId);
                }
                else
                {
                    cmd.CommandText = "UPDATE kiz_daily_claims SET last_claim_day_index = @d, streak = @s WHERE steam_id = @id;";
                    cmd.Parameters.AddWithValue("@id", steamId);
                    cmd.Parameters.AddWithValue("@d", previousDayIndex);
                    cmd.Parameters.AddWithValue("@s", previousStreak);
                }
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
