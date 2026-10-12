using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KizShop.Common;
using KizShop.Economy.Models;
using KizShop.Shop.Models;

namespace KizShop.Storage
{
    public interface IKizStorage : IDisposable
    {
        Task InitializeAsync();
        Task<bool> AccountExistsAsync(ulong steamId);
        Task<Money> GetBalanceAsync(ulong steamId, Money defaultStartingBalance);
        Task<WithdrawResult> TryWithdrawAsync(ulong steamId, Money amount, string reason, Money defaultStartingBalance);
        Task<DepositResult> DepositAsync(ulong steamId, Money amount, string reason, Money defaultStartingBalance, Money? maxBalance = null, bool ignoreMaxBalance = false);
        Task<TransferResult> TransferAsync(ulong fromSteamId, ulong toSteamId, Money amount, string reason, Money defaultStartingBalance, Money? maxBalance = null);
        Task<List<TopBalanceEntry>> GetTopBalancesAsync(int count);
        Task SetBalanceAsync(ulong steamId, Money amount, string reason);
        Task LogTransactionAsync(ulong steamId, string type, Money amount, string details);
        Task PruneLogsAsync(int retentionDays);

        // Boss Rewards
        Task<bool> HasRewardEventAsync(string eventKey);
        Task RecordRewardEventAsync(string eventKey, string bossName, Money pool, DateTime timestampUtc);

        // Dynamic Pricing
        Task<ItemPriceState> GetItemPriceStateAsync(ushort itemId);
        Task UpdateItemPricePressureAsync(ushort itemId, decimal sellDelta, decimal buyDelta, DateTime nowUtc);
        Task ResetItemPriceStateAsync(ushort? itemId);

        // Bounties
        Task<int> CreateBountyAsync(ulong placerSteamId, ulong targetSteamId, Money escrowAmount, Money netAmount, DateTime createdUtc);
        Task<List<BountyEntry>> GetActiveBountiesAsync(int limit = 50);
        Task<Money> GetTotalBountyOnTargetAsync(ulong targetSteamId);
        Task<Money> ClaimBountiesAsync(ulong targetSteamId, ulong killerSteamId);
        Task<List<BountyEntry>> RefundExpiredBountiesAsync(DateTime cutoffUtc);

        // Daily Claims
        Task<DailyClaimResult> TryClaimDailyAsync(ulong steamId, int currentDayIndex, int maxStreakDays, DateTime nowUtc);
        Task RollbackDailyClaimAsync(ulong steamId, int previousDayIndex, int previousStreak);
    }
}
