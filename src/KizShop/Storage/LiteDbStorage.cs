using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LiteDB;
using KizShop.Common;
using KizShop.Economy.Models;
using KizShop.Shop;
using KizShop.Shop.Models;

namespace KizShop.Storage
{
    public class LiteDbStorage : IKizStorage
    {
        private readonly string _dbPath;
        private readonly IClock _clock;
        private readonly object _dbLock = new object();
        private LiteDatabase _db;
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

        public class RewardEventDoc
        {
            [BsonId]
            public string EventKey { get; set; }
            public string BossName { get; set; }
            public long PoolCents { get; set; }
            public DateTime Timestamp { get; set; }
        }

        public class ItemPriceStateDoc
        {
            [BsonId]
            public int ItemId { get; set; }
            public decimal SellPressure { get; set; }
            public decimal BuyPressure { get; set; }
            public DateTime LastUpdatedUtc { get; set; }
        }

        public class BountyDoc
        {
            [BsonId]
            public int Id { get; set; }
            public long PlacerSteamId { get; set; }
            public long TargetSteamId { get; set; }
            public long EscrowCents { get; set; }
            public long NetCents { get; set; }
            public DateTime CreatedUtc { get; set; }
            public bool RefundPending { get; set; }
        }

        public class RewardPayoutDoc
        {
            [BsonId]
            public int Id { get; set; }
            public string EventKey { get; set; }
            public long SteamId { get; set; }
            public long AmountCents { get; set; }
            public string Status { get; set; }
            public DateTime CreatedUtc { get; set; }
        }

        public class DailyClaimDoc
        {
            [BsonId]
            public long SteamId { get; set; }
            public int LastClaimDayIndex { get; set; }
            public int Streak { get; set; }
            public DateTime LastClaimedUtc { get; set; }
        }

        public class KitCooldownDoc
        {
            [BsonId]
            public string Id { get; set; }
            public long SteamId { get; set; }
            public string KitName { get; set; }
            public DateTime LastPurchaseUtc { get; set; }
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
        }

        public async Task InitializeAsync()
        {
            await Task.Run(() =>
            {
                lock (_dbLock)
                {
                    if (_db == null)
                    {
                        _db = new LiteDatabase(_dbPath);
                    }
                    var versions = _db.GetCollection<SchemaVersionDoc>("schema_version");
                    if (versions.Count() == 0)
                    {
                        versions.Insert(new SchemaVersionDoc { Version = 2, AppliedAt = _clock.UtcNow });
                    }

                    var accounts = _db.GetCollection<AccountDoc>("accounts");
                    accounts.EnsureIndex(x => x.SteamId, true);
                    accounts.EnsureIndex(x => x.BalanceCents);

                    var txs = _db.GetCollection<TransactionDoc>("transactions");
                    txs.EnsureIndex(x => x.SteamId);
                    txs.EnsureIndex(x => x.Timestamp);

                    var rewards = _db.GetCollection<RewardEventDoc>("reward_events");
                    rewards.EnsureIndex(x => x.EventKey, true);

                    var prices = _db.GetCollection<ItemPriceStateDoc>("item_prices");
                    prices.EnsureIndex(x => x.ItemId, true);

                    var bounties = _db.GetCollection<BountyDoc>("bounties");
                    bounties.EnsureIndex(x => x.TargetSteamId);
                    bounties.EnsureIndex(x => x.PlacerSteamId);
                    bounties.EnsureIndex(x => x.CreatedUtc);
                    bounties.EnsureIndex(x => x.RefundPending);

                    var payouts = _db.GetCollection<RewardPayoutDoc>("reward_payouts");
                    payouts.EnsureIndex(x => x.EventKey);
                    payouts.EnsureIndex(x => x.Status);

                    var dailies = _db.GetCollection<DailyClaimDoc>("daily_claims");
                    dailies.EnsureIndex(x => x.SteamId, true);

                    var kitCooldowns = _db.GetCollection<KitCooldownDoc>("kit_cooldowns");
                    kitCooldowns.EnsureIndex(x => x.SteamId);
                    kitCooldowns.EnsureIndex(x => x.KitName);
                }
            }).ConfigureAwait(false);
        }

        private AccountDoc FindAccount(long steamId)
        {
            var accounts = _db.GetCollection<AccountDoc>("accounts");
            return accounts.Find(Query.EQ("_id", steamId), limit: 1).ToList().FirstOrDefault();
        }

        public async Task<bool> AccountExistsAsync(ulong steamId)
        {
            return await Task.Run(() =>
            {
                lock (_dbLock)
                {
                    return FindAccount((long)steamId) != null;
                }
            }).ConfigureAwait(false);
        }

        public async Task<Money> GetBalanceAsync(ulong steamId, Money defaultStartingBalance)
        {
            return await Task.Run(() =>
            {
                lock (_dbLock)
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
                }
            }).ConfigureAwait(false);
        }

        public async Task<WithdrawResult> TryWithdrawAsync(ulong steamId, Money amount, string reason, Money defaultStartingBalance)
        {
            if (amount.Cents <= 0) return WithdrawResult.Failed(Money.Zero);

            return await Task.Run(() =>
            {
                lock (_dbLock)
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
                }
            }).ConfigureAwait(false);
        }

        public async Task<DepositResult> DepositAsync(ulong steamId, Money amount, string reason, Money defaultStartingBalance, Money? maxBalance = null, bool ignoreMaxBalance = false)
        {
            return await Task.Run(() =>
            {
                lock (_dbLock)
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
                }
            }).ConfigureAwait(false);
        }

        public async Task<TransferResult> TransferAsync(ulong fromSteamId, ulong toSteamId, Money amount, string reason, Money defaultStartingBalance, Money? maxBalance = null)
        {
            if (amount.Cents <= 0 || fromSteamId == toSteamId)
            {
                return TransferResult.Failed(Money.Zero, Money.Zero);
            }

            return await Task.Run(() =>
            {
                lock (_dbLock)
                {
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
                }
            }).ConfigureAwait(false);
        }

        public async Task<List<TopBalanceEntry>> GetTopBalancesAsync(int count)
        {
            if (count <= 0) count = 10;
            return await Task.Run(() =>
            {
                lock (_dbLock)
                {
                    var accounts = _db.GetCollection<AccountDoc>("accounts");
                    return accounts.Find(Query.All("BalanceCents", Query.Descending), limit: count)
                        .Select(a => new TopBalanceEntry((ulong)a.SteamId, Money.FromCents(a.BalanceCents)))
                        .ToList();
                }
            }).ConfigureAwait(false);
        }

        public async Task SetBalanceAsync(ulong steamId, Money amount, string reason)
        {
            await Task.Run(() =>
            {
                lock (_dbLock)
                {
                    var accounts = _db.GetCollection<AccountDoc>("accounts");
                    var doc = FindAccount((long)steamId) ?? new AccountDoc { SteamId = (long)steamId };
                    doc.BalanceCents = amount.Cents;
                    doc.LastUpdated = _clock.UtcNow;
                    accounts.Upsert(doc);

                    LogTransactionInternal(steamId, "SetBalance", amount.Cents, reason);
                }
            }).ConfigureAwait(false);
        }

        public async Task LogTransactionAsync(ulong steamId, string type, Money amount, string details)
        {
            await Task.Run(() =>
            {
                lock (_dbLock)
                {
                    LogTransactionInternal(steamId, type, amount.Cents, details);
                }
            }).ConfigureAwait(false);
        }

        public async Task PruneLogsAsync(int retentionDays)
        {
            if (retentionDays <= 0) return;
            var cutoff = _clock.UtcNow.AddDays(-retentionDays);

            await Task.Run(() =>
            {
                lock (_dbLock)
                {
                    var txs = _db.GetCollection<TransactionDoc>("transactions");
                    txs.DeleteMany(x => x.Timestamp < cutoff);
                }
            }).ConfigureAwait(false);
        }

        public Task<List<TransactionRecord>> GetTransactionHistoryAsync(ulong steamId, int limit, int offset)
        {
            if (limit <= 0) limit = 10;
            if (offset < 0) offset = 0;

            return Task.Run(() =>
            {
                lock (_dbLock)
                {
                    var txs = _db.GetCollection<TransactionDoc>("transactions");
                    long sId = (long)steamId;
                    var results = txs.Query()
                        .Where(x => x.SteamId == sId)
                        .OrderByDescending(x => x.Timestamp)
                        .Skip(offset)
                        .Limit(limit)
                        .ToList();

                    return results.Select(r => new TransactionRecord
                    {
                        Timestamp = r.Timestamp,
                        SteamId = (ulong)r.SteamId,
                        Type = r.Type,
                        Amount = Money.FromCents(r.AmountCents),
                        Details = r.Details
                    }).ToList();
                }
            });
        }

        public Task<List<TransactionRecord>> GetAllTransactionsAsync(int limit, int offset)
        {
            if (limit <= 0) limit = 50;
            if (offset < 0) offset = 0;

            return Task.Run(() =>
            {
                lock (_dbLock)
                {
                    var txs = _db.GetCollection<TransactionDoc>("transactions");
                    var results = txs.Query()
                        .OrderByDescending(x => x.Timestamp)
                        .Skip(offset)
                        .Limit(limit)
                        .ToList();

                    return results.Select(r => new TransactionRecord
                    {
                        Timestamp = r.Timestamp,
                        SteamId = (ulong)r.SteamId,
                        Type = r.Type,
                        Amount = Money.FromCents(r.AmountCents),
                        Details = r.Details
                    }).ToList();
                }
            });
        }

        public Task<List<(ulong SteamId, Money Balance)>> GetAllAccountsAsync()
        {
            return Task.Run(() =>
            {
                lock (_dbLock)
                {
                    var accs = _db.GetCollection<AccountDoc>("accounts");
                    var all = accs.FindAll().ToList();
                    return all.Select(a => ((ulong)a.SteamId, Money.FromCents(a.BalanceCents))).ToList();
                }
            });
        }

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

        public async Task<bool> TryRecordRewardEventAsync(string eventKey, string bossName, Money pool, DateTime utc)
        {
            if (string.IsNullOrEmpty(eventKey)) return false;
            return await Task.Run(() =>
            {
                lock (_dbLock)
                {
                    var coll = _db.GetCollection<RewardEventDoc>("reward_events");
                    var existing = coll.FindOne(x => x.EventKey == eventKey);
                    if (existing != null) return false;

                    coll.Insert(new RewardEventDoc
                    {
                        EventKey = eventKey,
                        BossName = bossName ?? string.Empty,
                        PoolCents = pool.Cents,
                        Timestamp = utc
                    });
                    return true;
                }
            }).ConfigureAwait(false);
        }

        public async Task<BossRewardEventEntry> GetRewardEventAsync(string eventKey)
        {
            if (string.IsNullOrEmpty(eventKey)) return null;
            return await Task.Run(() =>
            {
                lock (_dbLock)
                {
                    var coll = _db.GetCollection<RewardEventDoc>("reward_events");
                    var doc = coll.FindOne(x => x.EventKey == eventKey);
                    if (doc == null) return null;
                    return new BossRewardEventEntry
                    {
                        EventKey = doc.EventKey,
                        BossName = doc.BossName,
                        Pool = Money.FromCents(doc.PoolCents),
                        TimestampUtc = doc.Timestamp
                    };
                }
            }).ConfigureAwait(false);
        }

        public async Task RecordRewardPayoutAsync(string eventKey, ulong steamId, Money amount, string status, DateTime utc)
        {
            await Task.Run(() =>
            {
                lock (_dbLock)
                {
                    var coll = _db.GetCollection<RewardPayoutDoc>("reward_payouts");
                    var existing = coll.FindOne(x => x.EventKey == eventKey && x.SteamId == (long)steamId);
                    if (existing != null) return;

                    coll.Insert(new RewardPayoutDoc
                    {
                        EventKey = eventKey,
                        SteamId = (long)steamId,
                        AmountCents = amount.Cents,
                        Status = status ?? "Pending",
                        CreatedUtc = utc
                    });
                }
            }).ConfigureAwait(false);
        }

        public async Task<List<RewardPayoutEntry>> GetUnpaidRewardPayoutsAsync()
        {
            return await Task.Run(() =>
            {
                lock (_dbLock)
                {
                    var coll = _db.GetCollection<RewardPayoutDoc>("reward_payouts");
                    var docs = coll.Find(x => x.Status != "Paid").ToList();
                    var list = new List<RewardPayoutEntry>(docs.Count);
                    foreach (var d in docs)
                    {
                        list.Add(new RewardPayoutEntry
                        {
                            Id = d.Id,
                            EventKey = d.EventKey,
                            SteamId = (ulong)d.SteamId,
                            Amount = Money.FromCents(d.AmountCents),
                            Status = d.Status,
                            CreatedUtc = d.CreatedUtc
                        });
                    }
                    return list;
                }
            }).ConfigureAwait(false);
        }

        public async Task MarkRewardPayoutPaidAsync(int id, DateTime utc)
        {
            await Task.Run(() =>
            {
                lock (_dbLock)
                {
                    var coll = _db.GetCollection<RewardPayoutDoc>("reward_payouts");
                    var doc = coll.FindById(id);
                    if (doc != null)
                    {
                        doc.Status = "Paid";
                        coll.Update(doc);
                    }
                }
            }).ConfigureAwait(false);
        }

        public async Task<ItemPriceState> GetItemPriceStateAsync(ushort itemId)
        {
            return await Task.Run(() =>
            {
                lock (_dbLock)
                {
                    var coll = _db.GetCollection<ItemPriceStateDoc>("item_prices");
                    var doc = coll.FindById((int)itemId);
                    if (doc == null)
                    {
                        return new ItemPriceState
                        {
                            ItemId = itemId,
                            SellPressure = 0,
                            BuyPressure = 0,
                            LastUpdatedUtc = DateTime.MinValue
                        };
                    }
                    return new ItemPriceState
                    {
                        ItemId = (ushort)doc.ItemId,
                        SellPressure = doc.SellPressure,
                        BuyPressure = doc.BuyPressure,
                        LastUpdatedUtc = doc.LastUpdatedUtc.Kind == DateTimeKind.Utc ? doc.LastUpdatedUtc : doc.LastUpdatedUtc.ToUniversalTime()
                    };
                }
            }).ConfigureAwait(false);
        }

        public async Task UpdateItemPricePressureAsync(ushort itemId, decimal sellDelta, decimal buyDelta, DateTime nowUtc, decimal recoveryPerHour)
        {
            await Task.Run(() =>
            {
                lock (_dbLock)
                {
                    var coll = _db.GetCollection<ItemPriceStateDoc>("item_prices");
                    var doc = coll.FindById((int)itemId);
                    if (doc == null)
                    {
                        doc = new ItemPriceStateDoc
                        {
                            ItemId = (int)itemId,
                            SellPressure = Math.Max(0m, sellDelta),
                            BuyPressure = Math.Max(0m, buyDelta),
                            LastUpdatedUtc = nowUtc
                        };
                    }
                    else
                    {
                        decimal recSell = PriceCalculator.ComputeRecoveredPressure(doc.SellPressure, doc.LastUpdatedUtc, nowUtc, recoveryPerHour);
                        decimal recBuy = PriceCalculator.ComputeRecoveredPressure(doc.BuyPressure, doc.LastUpdatedUtc, nowUtc, recoveryPerHour);
                        doc.SellPressure = Math.Max(0m, recSell + sellDelta);
                        doc.BuyPressure = Math.Max(0m, recBuy + buyDelta);
                        doc.LastUpdatedUtc = nowUtc;
                    }
                    coll.Upsert(doc);
                }
            }).ConfigureAwait(false);
        }

        public async Task ResetItemPriceStateAsync(ushort? itemId)
        {
            await Task.Run(() =>
            {
                lock (_dbLock)
                {
                    var coll = _db.GetCollection<ItemPriceStateDoc>("item_prices");
                    if (itemId.HasValue)
                    {
                        coll.Delete((int)itemId.Value);
                    }
                    else
                    {
                        coll.DeleteAll();
                    }
                }
            }).ConfigureAwait(false);
        }

        public async Task<int> CreateBountyAsync(ulong placerSteamId, ulong targetSteamId, Money escrowAmount, Money netAmount, DateTime createdUtc)
        {
            return await Task.Run(() =>
            {
                lock (_dbLock)
                {
                    var coll = _db.GetCollection<BountyDoc>("bounties");
                    var doc = new BountyDoc
                    {
                        PlacerSteamId = (long)placerSteamId,
                        TargetSteamId = (long)targetSteamId,
                        EscrowCents = escrowAmount.Cents,
                        NetCents = netAmount.Cents,
                        CreatedUtc = createdUtc,
                        RefundPending = false
                    };
                    var id = coll.Insert(doc);
                    return id.AsInt32;
                }
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
            return await Task.Run(() =>
            {
                lock (_dbLock)
                {
                    var coll = _db.GetCollection<BountyDoc>("bounties");
                    var existing = coll.Find(x => x.TargetSteamId == (long)targetSteamId && !x.RefundPending);
                    long currentTotalCents = 0;
                    foreach (var e in existing)
                    {
                        currentTotalCents += e.NetCents;
                    }

                    if (maxTotalNet.Cents > 0 && currentTotalCents + netAmount.Cents > maxTotalNet.Cents)
                    {
                        return (false, Money.FromCents(currentTotalCents));
                    }

                    var doc = new BountyDoc
                    {
                        PlacerSteamId = (long)placerSteamId,
                        TargetSteamId = (long)targetSteamId,
                        EscrowCents = escrowAmount.Cents,
                        NetCents = netAmount.Cents,
                        CreatedUtc = createdUtc,
                        RefundPending = false
                    };
                    coll.Insert(doc);
                    return (true, Money.FromCents(currentTotalCents + netAmount.Cents));
                }
            }).ConfigureAwait(false);
        }

        public async Task<List<BountyEntry>> GetActiveBountiesAsync(int limit = 50)
        {
            return await Task.Run(() =>
            {
                lock (_dbLock)
                {
                    var coll = _db.GetCollection<BountyDoc>("bounties");
                    var docs = coll.Find(x => !x.RefundPending).OrderByDescending(x => x.NetCents).Take(limit).ToList();
                    var list = new List<BountyEntry>(docs.Count);
                    foreach (var d in docs)
                    {
                        list.Add(new BountyEntry
                        {
                            Id = d.Id,
                            PlacerSteamId = (ulong)d.PlacerSteamId,
                            TargetSteamId = (ulong)d.TargetSteamId,
                            EscrowAmount = Money.FromCents(d.EscrowCents),
                            NetAmount = Money.FromCents(d.NetCents),
                            CreatedUtc = d.CreatedUtc,
                            RefundPending = d.RefundPending
                        });
                    }
                    return list;
                }
            }).ConfigureAwait(false);
        }

        public async Task<Money> GetTotalBountyOnTargetAsync(ulong targetSteamId)
        {
            return await Task.Run(() =>
            {
                lock (_dbLock)
                {
                    var coll = _db.GetCollection<BountyDoc>("bounties");
                    var docs = coll.Find(x => x.TargetSteamId == (long)targetSteamId && !x.RefundPending);
                    long total = 0;
                    foreach (var d in docs)
                    {
                        total += d.NetCents;
                    }
                    return Money.FromCents(total);
                }
            }).ConfigureAwait(false);
        }

        public async Task<(List<BountyEntry> ClaimedBounties, Money TotalAmount)> ClaimBountiesAsync(ulong targetSteamId, ulong killerSteamId)
        {
            return await Task.Run(() =>
            {
                lock (_dbLock)
                {
                    var coll = _db.GetCollection<BountyDoc>("bounties");
                    var docs = coll.Find(x => x.TargetSteamId == (long)targetSteamId && x.PlacerSteamId != (long)killerSteamId && !x.RefundPending).ToList();
                    var list = new List<BountyEntry>(docs.Count);
                    if (docs.Count == 0) return (list, Money.Zero);

                    long total = 0;
                    foreach (var d in docs)
                    {
                        total += d.NetCents;
                        list.Add(new BountyEntry
                        {
                            Id = d.Id,
                            PlacerSteamId = (ulong)d.PlacerSteamId,
                            TargetSteamId = (ulong)d.TargetSteamId,
                            EscrowAmount = Money.FromCents(d.EscrowCents),
                            NetAmount = Money.FromCents(d.NetCents),
                            CreatedUtc = d.CreatedUtc,
                            RefundPending = d.RefundPending
                        });
                        coll.Delete(d.Id);
                    }
                    return (list, Money.FromCents(total));
                }
            }).ConfigureAwait(false);
        }

        public async Task<List<BountyEntry>> GetAndMarkExpiredBountiesAsync(DateTime cutoffUtc)
        {
            return await Task.Run(() =>
            {
                lock (_dbLock)
                {
                    var coll = _db.GetCollection<BountyDoc>("bounties");
                    var docs = coll.Find(x => x.CreatedUtc < cutoffUtc || x.RefundPending).ToList();
                    var list = new List<BountyEntry>(docs.Count);
                    foreach (var d in docs)
                    {
                        d.RefundPending = true;
                        coll.Update(d);
                        list.Add(new BountyEntry
                        {
                            Id = d.Id,
                            PlacerSteamId = (ulong)d.PlacerSteamId,
                            TargetSteamId = (ulong)d.TargetSteamId,
                            EscrowAmount = Money.FromCents(d.EscrowCents),
                            NetAmount = Money.FromCents(d.NetCents),
                            CreatedUtc = d.CreatedUtc,
                            RefundPending = true
                        });
                    }
                    return list;
                }
            }).ConfigureAwait(false);
        }

        public async Task ConfirmBountyRefundAsync(int bountyId)
        {
            await Task.Run(() =>
            {
                lock (_dbLock)
                {
                    var coll = _db.GetCollection<BountyDoc>("bounties");
                    coll.Delete(bountyId);
                }
            }).ConfigureAwait(false);
        }

        public async Task<bool> CanClaimDailyAsync(ulong steamId, int currentDayIndex)
        {
            return await Task.Run(() =>
            {
                lock (_dbLock)
                {
                    var coll = _db.GetCollection<DailyClaimDoc>("daily_claims");
                    var doc = coll.FindById((long)steamId);
                    if (doc == null) return true;
                    return doc.LastClaimDayIndex != currentDayIndex;
                }
            }).ConfigureAwait(false);
        }

        public async Task<DailyClaimResult> TryClaimDailyAsync(ulong steamId, int currentDayIndex, int maxStreakDays, DateTime nowUtc)
        {
            return await Task.Run(() =>
            {
                lock (_dbLock)
                {
                    var coll = _db.GetCollection<DailyClaimDoc>("daily_claims");
                    var doc = coll.FindById((long)steamId);
                    if (doc == null)
                    {
                        coll.Insert(new DailyClaimDoc
                        {
                            SteamId = (long)steamId,
                            LastClaimDayIndex = currentDayIndex,
                            Streak = 1,
                            LastClaimedUtc = nowUtc
                        });
                        return new DailyClaimResult
                        {
                            Allowed = true,
                            Streak = 1,
                            PreviousDayIndex = -1,
                            PreviousStreak = 0
                        };
                    }

                    if (doc.LastClaimDayIndex == currentDayIndex)
                    {
                        return new DailyClaimResult
                        {
                            Allowed = false,
                            Streak = doc.Streak,
                            PreviousDayIndex = doc.LastClaimDayIndex,
                            PreviousStreak = doc.Streak
                        };
                    }

                    int prevDay = doc.LastClaimDayIndex;
                    int prevStreak = doc.Streak;
                    int newStreak = (doc.LastClaimDayIndex == currentDayIndex - 1)
                        ? Math.Min(doc.Streak + 1, maxStreakDays)
                        : 1;

                    doc.LastClaimDayIndex = currentDayIndex;
                    doc.Streak = newStreak;
                    doc.LastClaimedUtc = nowUtc;
                    coll.Update(doc);

                    return new DailyClaimResult
                    {
                        Allowed = true,
                        Streak = newStreak,
                        PreviousDayIndex = prevDay,
                        PreviousStreak = prevStreak
                    };
                }
            }).ConfigureAwait(false);
        }

        public async Task RollbackDailyClaimAsync(ulong steamId, int previousDayIndex, int previousStreak)
        {
            await Task.Run(() =>
            {
                lock (_dbLock)
                {
                    var coll = _db.GetCollection<DailyClaimDoc>("daily_claims");
                    if (previousDayIndex < 0)
                    {
                        coll.Delete((long)steamId);
                    }
                    else
                    {
                        var doc = coll.FindById((long)steamId);
                        if (doc != null)
                        {
                            doc.LastClaimDayIndex = previousDayIndex;
                            doc.Streak = previousStreak;
                            coll.Update(doc);
                        }
                    }
                }
            }).ConfigureAwait(false);
        }

        public virtual async Task<DateTime?> GetKitCooldownAsync(ulong steamId, string kitName)
        {
            return await Task.Run<DateTime?>(() =>
            {
                lock (_dbLock)
                {
                    var coll = _db.GetCollection<KitCooldownDoc>("kit_cooldowns");
                    string id = $"{steamId}_{kitName?.ToLowerInvariant()}";
                    var doc = coll.FindById(id);
                    if (doc == null) return null;
                    return doc.LastPurchaseUtc.Kind == DateTimeKind.Utc
                        ? doc.LastPurchaseUtc
                        : doc.LastPurchaseUtc.ToUniversalTime();
                }
            }).ConfigureAwait(false);
        }

        public virtual async Task SetKitCooldownAsync(ulong steamId, string kitName, DateTime lastPurchaseUtc)
        {
            await Task.Run(() =>
            {
                lock (_dbLock)
                {
                    var coll = _db.GetCollection<KitCooldownDoc>("kit_cooldowns");
                    string k = kitName?.ToLowerInvariant() ?? string.Empty;
                    string id = $"{steamId}_{k}";
                    var doc = new KitCooldownDoc
                    {
                        Id = id,
                        SteamId = (long)steamId,
                        KitName = k,
                        LastPurchaseUtc = lastPurchaseUtc.Kind == DateTimeKind.Utc ? lastPurchaseUtc : lastPurchaseUtc.ToUniversalTime()
                    };
                    coll.Upsert(doc);
                }
            }).ConfigureAwait(false);
        }

        public async Task ClearKitCooldownAsync(ulong steamId, string kitName)
        {
            await Task.Run(() =>
            {
                lock (_dbLock)
                {
                    var coll = _db.GetCollection<KitCooldownDoc>("kit_cooldowns");
                    string id = $"{steamId}_{kitName?.ToLowerInvariant()}";
                    coll.Delete(id);
                }
            }).ConfigureAwait(false);
        }

        public async Task<int> PurgeTransactionsOlderThanAsync(DateTime cutoffUtc)
        {
            return await Task.Run(() =>
            {
                lock (_dbLock)
                {
                    var txs = _db.GetCollection<TransactionDoc>("transactions");
                    var utcCutoff = cutoffUtc.Kind == DateTimeKind.Utc ? cutoffUtc : cutoffUtc.ToUniversalTime();
                    int deleted = txs.DeleteMany(x => x.Timestamp < utcCutoff);
                    return deleted;
                }
            }).ConfigureAwait(false);
        }

        public async Task<int> GetAccountCountAsync()
        {
            return await Task.Run(() =>
            {
                lock (_dbLock)
                {
                    return _db.GetCollection<AccountDoc>("accounts").Count();
                }
            }).ConfigureAwait(false);
        }

        public async Task<int> GetTransactionCountAsync()
        {
            return await Task.Run(() =>
            {
                lock (_dbLock)
                {
                    return _db.GetCollection<TransactionDoc>("transactions").Count();
                }
            }).ConfigureAwait(false);
        }

        public async Task<int> GetPendingPayoutCountAsync()
        {
            return await Task.Run(() =>
            {
                lock (_dbLock)
                {
                    return _db.GetCollection<RewardPayoutDoc>("reward_payouts").Count(x => x.Status != "Paid");
                }
            }).ConfigureAwait(false);
        }

        public async Task<int> GetPendingBountyRefundCountAsync()
        {
            return await Task.Run(() =>
            {
                lock (_dbLock)
                {
                    return _db.GetCollection<BountyDoc>("bounties").Count(x => x.RefundPending);
                }
            }).ConfigureAwait(false);
        }

        public void Checkpoint()
        {
            lock (_dbLock)
            {
                _db?.Checkpoint();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            lock (_dbLock)
            {
                _db?.Dispose();
            }
        }
    }
}
