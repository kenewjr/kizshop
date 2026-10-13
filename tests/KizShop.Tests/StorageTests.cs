using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using KizShop.Common;
using KizShop.Storage;
using KizShop.Economy.Models;
using KizShop.Shop.Models;

namespace KizShop.Tests
{
    public abstract class StorageTestBase : IDisposable
    {
        protected readonly string _dbPath;
        protected readonly IKizStorage _storage;

        protected StorageTestBase(string extension)
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"kizshop_test_{Guid.NewGuid():N}.{extension}");
            _storage = CreateStorage(_dbPath);
            _storage.InitializeAsync().GetAwaiter().GetResult();
        }

        protected abstract IKizStorage CreateStorage(string path);

        public void Dispose()
        {
            _storage?.Dispose();
            if (File.Exists(_dbPath))
            {
                try { File.Delete(_dbPath); } catch { }
            }
        }

        [Fact]
        public async Task SetBalance_SetsAndPersistsCorrectly()
        {
            ulong steamId = 76561198000000010;
            await _storage.SetBalanceAsync(steamId, Money.FromDecimal(500.50m), "ManualSet");

            var bal = await _storage.GetBalanceAsync(steamId, Money.Zero);
            Assert.Equal(500.50m, bal.ToDecimal());
        }

        [Fact]
        public async Task GetTopBalances_ReturnsSortedDescending()
        {
            await _storage.SetBalanceAsync(1001, Money.FromDecimal(50m), "Test");
            await _storage.SetBalanceAsync(1002, Money.FromDecimal(500m), "Test");
            await _storage.SetBalanceAsync(1003, Money.FromDecimal(250m), "Test");

            var top = await _storage.GetTopBalancesAsync(2);

            Assert.Equal(2, top.Count);
            Assert.Equal(1002ul, top[0].SteamId);
            Assert.Equal(500m, top[0].Balance.ToDecimal());
            Assert.Equal(1003ul, top[1].SteamId);
            Assert.Equal(250m, top[1].Balance.ToDecimal());
        }

        [Fact]
        public async Task RewardEvents_RecordAndCheckExistence_Idempotent()
        {
            string key = "boss_megazombie_101";
            var existing = await _storage.GetRewardEventAsync(key);
            Assert.Null(existing);

            bool first = await _storage.TryRecordRewardEventAsync(key, "MegaZombie", Money.FromDecimal(1000m), DateTime.UtcNow);
            Assert.True(first);

            var stored = await _storage.GetRewardEventAsync(key);
            Assert.NotNull(stored);
            Assert.Equal("MegaZombie", stored.BossName);
            Assert.Equal(1000m, stored.Pool.ToDecimal());

            // Calling again with same key returns false atomically
            bool second = await _storage.TryRecordRewardEventAsync(key, "MegaZombie", Money.FromDecimal(1000m), DateTime.UtcNow);
            Assert.False(second);
        }

        [Fact]
        public async Task ItemPriceState_UpdatePressure_AndReset()
        {
            ushort itemId = 363;
            var initial = await _storage.GetItemPriceStateAsync(itemId);
            Assert.Equal(0m, initial.SellPressure);
            Assert.Equal(0m, initial.BuyPressure);

            var now = DateTime.UtcNow;
            await _storage.UpdateItemPricePressureAsync(itemId, sellDelta: 5.5m, buyDelta: 2.0m, now, recoveryPerHour: 0m);

            var updated = await _storage.GetItemPriceStateAsync(itemId);
            Assert.Equal(5.5m, updated.SellPressure);
            Assert.Equal(2.0m, updated.BuyPressure);

            // Reset single
            await _storage.ResetItemPriceStateAsync(itemId);
            var reset = await _storage.GetItemPriceStateAsync(itemId);
            Assert.Equal(0m, reset.SellPressure);
            Assert.Equal(0m, reset.BuyPressure);

            // Reset all
            await _storage.UpdateItemPricePressureAsync(itemId, sellDelta: 3m, buyDelta: 1m, now, recoveryPerHour: 0m);
            await _storage.ResetItemPriceStateAsync(null);
            var resetAll = await _storage.GetItemPriceStateAsync(itemId);
            Assert.Equal(0m, resetAll.SellPressure);
        }

        [Fact]
        public async Task Bounties_CreateAndGetActive_AndGetTotal()
        {
            ulong placer = 76561198000000001;
            ulong target = 76561198000000002;
            var created = DateTime.UtcNow;

            int id1 = await _storage.CreateBountyAsync(placer, target, Money.FromDecimal(100m), Money.FromDecimal(90m), created);
            Assert.True(id1 > 0);

            var total = await _storage.GetTotalBountyOnTargetAsync(target);
            Assert.Equal(90m, total.ToDecimal());

            var active = await _storage.GetActiveBountiesAsync(10);
            Assert.Single(active);
            Assert.Equal(target, active[0].TargetSteamId);
            Assert.Equal(placer, active[0].PlacerSteamId);
            Assert.Equal(90m, active[0].NetAmount.ToDecimal());
        }

        [Fact]
        public async Task Bounties_ClaimBounties_PlacerCannotClaimOwnBounty_AndClaimsAreAtomic()
        {
            ulong placerA = 76561198000000001;
            ulong placerB = 76561198000000002;
            ulong target = 76561198000000003;
            var created = DateTime.UtcNow;

            await _storage.CreateBountyAsync(placerA, target, Money.FromDecimal(100m), Money.FromDecimal(90m), created);
            await _storage.CreateBountyAsync(placerB, target, Money.FromDecimal(200m), Money.FromDecimal(180m), created);

            // Placer A kills target -> Placer A cannot claim their own bounty (90m), only placer B's bounty (180m)
            var (claimedEntries, claimedAmount) = await _storage.ClaimBountiesAsync(target, placerA);
            Assert.Equal(180m, claimedAmount.ToDecimal());
            Assert.Single(claimedEntries);
            Assert.Equal(placerB, claimedEntries[0].PlacerSteamId);

            // Target still has placer A's bounty
            var remaining = await _storage.GetTotalBountyOnTargetAsync(target);
            Assert.Equal(90m, remaining.ToDecimal());

            // A second claim by someone else (e.g. placer C) claims the remaining 90m
            ulong placerC = 76561198000000004;
            var (_, claimedAmount2) = await _storage.ClaimBountiesAsync(target, placerC);
            Assert.Equal(90m, claimedAmount2.ToDecimal());

            // Third claim returns 0
            var (_, claimedAmount3) = await _storage.ClaimBountiesAsync(target, placerC);
            Assert.Equal(0m, claimedAmount3.ToDecimal());
        }

        [Fact]
        public async Task Bounties_RefundExpiredBounties_ReturnsExpiredAndDeletes()
        {
            ulong placer = 76561198000000001;
            ulong target = 76561198000000002;
            var oldDate = DateTime.UtcNow.AddDays(-10);
            var freshDate = DateTime.UtcNow;

            await _storage.CreateBountyAsync(placer, target, Money.FromDecimal(100m), Money.FromDecimal(90m), oldDate);
            await _storage.CreateBountyAsync(placer, target, Money.FromDecimal(200m), Money.FromDecimal(180m), freshDate);

            var cutoff = DateTime.UtcNow.AddDays(-7);
            var expired = await _storage.GetAndMarkExpiredBountiesAsync(cutoff);

            Assert.Single(expired);
            Assert.Equal(90m, expired[0].NetAmount.ToDecimal());

            // Delete confirmed refund
            await _storage.ConfirmBountyRefundAsync(expired[0].Id);

            // Only fresh remains active
            var remaining = await _storage.GetTotalBountyOnTargetAsync(target);
            Assert.Equal(180m, remaining.ToDecimal());
        }

        [Fact]
        public async Task DailyClaims_StreakProgression_GapReset_AndSameDayRefusal()
        {
            ulong steamId = 76561198000000010;
            var now = DateTime.UtcNow;

            // Day 100: first claim -> streak 1
            var res1 = await _storage.TryClaimDailyAsync(steamId, currentDayIndex: 100, maxStreakDays: 7, now);
            Assert.True(res1.Allowed);
            Assert.Equal(1, res1.Streak);

            // Day 100 again -> fails
            var resSameDay = await _storage.TryClaimDailyAsync(steamId, currentDayIndex: 100, maxStreakDays: 7, now);
            Assert.False(resSameDay.Allowed);
            Assert.Equal(1, resSameDay.Streak);

            // Day 101 (next day) -> streak 2
            var res2 = await _storage.TryClaimDailyAsync(steamId, currentDayIndex: 101, maxStreakDays: 7, now.AddDays(1));
            Assert.True(res2.Allowed);
            Assert.Equal(2, res2.Streak);

            // Day 104 (gap of 3 days) -> resets to streak 1
            var resGap = await _storage.TryClaimDailyAsync(steamId, currentDayIndex: 104, maxStreakDays: 7, now.AddDays(4));
            Assert.True(resGap.Allowed);
            Assert.Equal(1, resGap.Streak);
        }

        [Fact]
        public async Task DailyClaims_Rollback_RestoresPreviousState()
        {
            ulong steamId = 76561198000000011;
            var now = DateTime.UtcNow;

            // Day 100 claim -> streak 1
            var res1 = await _storage.TryClaimDailyAsync(steamId, 100, 7, now);
            Assert.True(res1.Allowed);

            // Day 101 claim -> streak 2
            var res2 = await _storage.TryClaimDailyAsync(steamId, 101, 7, now.AddDays(1));
            Assert.True(res2.Allowed);
            Assert.Equal(2, res2.Streak);

            // Rollback to day 100, streak 1
            await _storage.RollbackDailyClaimAsync(steamId, previousDayIndex: 100, previousStreak: 1);

            // Can claim Day 101 again
            var resRetry = await _storage.TryClaimDailyAsync(steamId, 101, 7, now.AddDays(1));
            Assert.True(resRetry.Allowed);
            Assert.Equal(2, resRetry.Streak);
        }
    }

    public class SqliteStorageTests : StorageTestBase
    {
        public SqliteStorageTests() : base("db") { }

        protected override IKizStorage CreateStorage(string path)
        {
            return new SqliteStorage(path);
        }
    }

    public class LiteDbStorageTests : StorageTestBase
    {
        public LiteDbStorageTests() : base("litedb") { }

        protected override IKizStorage CreateStorage(string path)
        {
            return new LiteDbStorage(path);
        }
    }
}
