using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LiteDB;
using KizShop.Common;

namespace KizShop.Storage
{
    public class LiteDbStorage : IKizStorage
    {
        private readonly string _dbPath;
        private readonly IClock _clock;
        private LiteDatabase _db;
        private readonly BlockingCollection<Action> _queue = new BlockingCollection<Action>();
        private readonly Thread _workerThread;
        private bool _disposed;

        public class AccountDoc
        {
            [BsonId]
            public long SteamId { get; set; }
            public long BalanceCents { get; set; }
            public DateTime LastUpdated { get; set; }
        }

        public class TransactionDoc
        {
            [BsonId]
            public int Id { get; set; }
            public DateTime Timestamp { get; set; }
            public long SteamId { get; set; }
            public string Type { get; set; }
            public long AmountCents { get; set; }
            public string Details { get; set; }
        }

        public class SchemaVersionDoc
        {
            [BsonId]
            public int Version { get; set; }
            public DateTime AppliedAt { get; set; }
        }

        public LiteDbStorage(string dbPath, IClock clock = null)
        {
            _dbPath = dbPath ?? throw new ArgumentNullException(nameof(dbPath));
            _clock = clock ?? SystemClock.Instance;
            var dir = Path.GetDirectoryName(_dbPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            _workerThread = new Thread(ProcessQueue)
            {
                IsBackground = true,
                Name = "KizShop-LiteDB"
            };
            _workerThread.Start();
        }

        private void ProcessQueue()
        {
            foreach (var action in _queue.GetConsumingEnumerable())
            {
                try
                {
                    action();
                }
                catch { }
            }
        }

        private Task<T> EnqueueAsync<T>(Func<T> func)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                _queue.Add(() =>
                {
                    try
                    {
                        tcs.SetResult(func());
                    }
                    catch (Exception ex)
                    {
                        tcs.SetException(ex);
                    }
                });
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
            return tcs.Task;
        }

        private Task EnqueueAsync(Action action)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                _queue.Add(() =>
                {
                    try
                    {
                        action();
                        tcs.SetResult(true);
                    }
                    catch (Exception ex)
                    {
                        tcs.SetException(ex);
                    }
                });
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
            return tcs.Task;
        }

        public Task InitializeAsync() => EnqueueAsync(() =>
        {
            _db = new LiteDatabase(_dbPath);
            var versions = _db.GetCollection<SchemaVersionDoc>("schema_version");
            if (versions.Count() == 0)
            {
                versions.Insert(new SchemaVersionDoc { Version = 2, AppliedAt = _clock.UtcNow });
            }

            var accounts = _db.GetCollection<AccountDoc>("accounts");
            accounts.EnsureIndex(x => x.SteamId, true);

            var txs = _db.GetCollection<TransactionDoc>("transactions");
            txs.EnsureIndex(x => x.SteamId);
            txs.EnsureIndex(x => x.Timestamp);
        });

        private AccountDoc FindAccount(long steamId)
        {
            var accounts = _db.GetCollection<AccountDoc>("accounts");
            return accounts.Find(Query.EQ("_id", steamId), limit: 1).ToList().FirstOrDefault();
        }

        public Task<bool> AccountExistsAsync(ulong steamId) => EnqueueAsync(() =>
        {
            return FindAccount((long)steamId) != null;
        });

        public Task<Money> GetBalanceAsync(ulong steamId, Money defaultStartingBalance) => EnqueueAsync(() =>
        {
            var accounts = _db.GetCollection<AccountDoc>("accounts");
            var doc = FindAccount((long)steamId);
            if (doc != null)
            {
                return Money.FromCents(doc.BalanceCents);
            }

            doc = new AccountDoc
            {
                SteamId = (long)steamId,
                BalanceCents = defaultStartingBalance.Cents,
                LastUpdated = _clock.UtcNow
            };
            accounts.Insert(doc);
            return defaultStartingBalance;
        });

        public Task<WithdrawResult> TryWithdrawAsync(ulong steamId, Money amount, string reason, Money defaultStartingBalance) => EnqueueAsync(() =>
        {
            if (amount.Cents <= 0) return WithdrawResult.Failed(Money.Zero);

            var accounts = _db.GetCollection<AccountDoc>("accounts");
            var doc = FindAccount((long)steamId);
            if (doc == null)
            {
                doc = new AccountDoc
                {
                    SteamId = (long)steamId,
                    BalanceCents = defaultStartingBalance.Cents,
                    LastUpdated = _clock.UtcNow
                };
                accounts.Insert(doc);
            }

            if (doc.BalanceCents < amount.Cents)
            {
                return WithdrawResult.Failed(Money.FromCents(doc.BalanceCents));
            }

            var oldBal = Money.FromCents(doc.BalanceCents);
            doc.BalanceCents -= amount.Cents;
            doc.LastUpdated = _clock.UtcNow;
            accounts.Update(doc);

            LogTransactionInternal(steamId, "Withdraw", -amount.Cents, reason);
            return WithdrawResult.Succeeded(oldBal, Money.FromCents(doc.BalanceCents));
        });

        public Task<DepositResult> DepositAsync(ulong steamId, Money amount, string reason, Money defaultStartingBalance, Money? maxBalance = null, bool ignoreMaxBalance = false) => EnqueueAsync(() =>
        {
            var accounts = _db.GetCollection<AccountDoc>("accounts");
            var doc = FindAccount((long)steamId);
            if (doc == null)
            {
                doc = new AccountDoc
                {
                    SteamId = (long)steamId,
                    BalanceCents = defaultStartingBalance.Cents,
                    LastUpdated = _clock.UtcNow
                };
                accounts.Insert(doc);
            }

            var oldBal = Money.FromCents(doc.BalanceCents);
            long newCents = doc.BalanceCents + amount.Cents;

            if (!ignoreMaxBalance && maxBalance.HasValue && maxBalance.Value.Cents > 0 && newCents > maxBalance.Value.Cents)
            {
                newCents = maxBalance.Value.Cents;
            }

            long actualDelta = newCents - doc.BalanceCents;
            doc.BalanceCents = newCents;
            doc.LastUpdated = _clock.UtcNow;
            accounts.Update(doc);

            if (actualDelta != 0)
            {
                LogTransactionInternal(steamId, ignoreMaxBalance ? "Refund" : "Deposit", actualDelta, reason);
            }

            return new DepositResult(true, oldBal, Money.FromCents(newCents));
        });

        public Task<TransferResult> TransferAsync(ulong fromSteamId, ulong toSteamId, Money amount, string reason, Money defaultStartingBalance, Money? maxBalance = null) => EnqueueAsync(() =>
        {
            if (amount.Cents <= 0 || fromSteamId == toSteamId)
            {
                return TransferResult.Failed(Money.Zero, Money.Zero);
            }

            var accounts = _db.GetCollection<AccountDoc>("accounts");
            var fromDoc = FindAccount((long)fromSteamId);
            if (fromDoc == null)
            {
                fromDoc = new AccountDoc
                {
                    SteamId = (long)fromSteamId,
                    BalanceCents = defaultStartingBalance.Cents,
                    LastUpdated = _clock.UtcNow
                };
                accounts.Insert(fromDoc);
            }

            if (fromDoc.BalanceCents < amount.Cents)
            {
                var toDocCur = FindAccount((long)toSteamId);
                var toBalCur = toDocCur != null ? Money.FromCents(toDocCur.BalanceCents) : Money.Zero;
                return TransferResult.Failed(Money.FromCents(fromDoc.BalanceCents), toBalCur);
            }

            var toDoc = FindAccount((long)toSteamId);
            if (toDoc == null)
            {
                toDoc = new AccountDoc
                {
                    SteamId = (long)toSteamId,
                    BalanceCents = defaultStartingBalance.Cents,
                    LastUpdated = _clock.UtcNow
                };
                accounts.Insert(toDoc);
            }

            if (maxBalance.HasValue && maxBalance.Value.Cents > 0 && (toDoc.BalanceCents + amount.Cents) > maxBalance.Value.Cents)
            {
                return TransferResult.Failed(Money.FromCents(fromDoc.BalanceCents), Money.FromCents(toDoc.BalanceCents));
            }

            var fromOld = Money.FromCents(fromDoc.BalanceCents);
            var toOld = Money.FromCents(toDoc.BalanceCents);

            fromDoc.BalanceCents -= amount.Cents;
            fromDoc.LastUpdated = _clock.UtcNow;
            accounts.Update(fromDoc);

            toDoc.BalanceCents += amount.Cents;
            toDoc.LastUpdated = _clock.UtcNow;
            accounts.Update(toDoc);

            LogTransactionInternal(fromSteamId, "TransferSent", -amount.Cents, $"To:{toSteamId} - {reason}");
            LogTransactionInternal(toSteamId, "TransferReceived", amount.Cents, $"From:{fromSteamId} - {reason}");

            return TransferResult.Succeeded(
                fromOld, Money.FromCents(fromDoc.BalanceCents),
                toOld, Money.FromCents(toDoc.BalanceCents));
        });

        public Task<List<TopBalanceEntry>> GetTopBalancesAsync(int count) => EnqueueAsync(() =>
        {
            if (count <= 0) count = 10;
            var accounts = _db.GetCollection<AccountDoc>("accounts");
            return accounts.Find(Query.All("BalanceCents", Query.Descending), limit: count)
                .Select(a => new TopBalanceEntry((ulong)a.SteamId, Money.FromCents(a.BalanceCents)))
                .ToList();
        });

        public Task SetBalanceAsync(ulong steamId, Money amount, string reason) => EnqueueAsync(() =>
        {
            var accounts = _db.GetCollection<AccountDoc>("accounts");
            var doc = FindAccount((long)steamId) ?? new AccountDoc { SteamId = (long)steamId };
            doc.BalanceCents = amount.Cents;
            doc.LastUpdated = _clock.UtcNow;
            accounts.Upsert(doc);

            LogTransactionInternal(steamId, "SetBalance", amount.Cents, reason);
        });

        public Task LogTransactionAsync(ulong steamId, string type, Money amount, string details) => EnqueueAsync(() =>
        {
            LogTransactionInternal(steamId, type, amount.Cents, details);
        });

        public Task PruneLogsAsync(int retentionDays) => EnqueueAsync(() =>
        {
            if (retentionDays <= 0) return;
            var cutoff = _clock.UtcNow.AddDays(-retentionDays);
            var txs = _db.GetCollection<TransactionDoc>("transactions");
            txs.DeleteMany(x => x.Timestamp < cutoff);
        });

        private void LogTransactionInternal(ulong steamId, string type, long amountCents, string details)
        {
            var txs = _db.GetCollection<TransactionDoc>("transactions");
            txs.Insert(new TransactionDoc
            {
                Timestamp = _clock.UtcNow,
                SteamId = (long)steamId,
                Type = type ?? "General",
                AmountCents = amountCents,
                Details = details ?? string.Empty
            });
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                EnqueueAsync(() =>
                {
                    _db?.Dispose();
                }).GetAwaiter().GetResult();
            }
            catch { }

            _queue.CompleteAdding();
            _workerThread.Join(2000);
            _queue.Dispose();
        }
    }
}
