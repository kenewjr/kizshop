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
        Task<bool> TryRecordRewardEventAsync(string eventKey, string bossName, Money pool, DateTime utc);
        Task<BossRewardEventEntry> GetRewardEventAsync(string eventKey);
        Task RecordRewardPayoutAsync(string eventKey, ulong steamId, Money amount, string status, DateTime utc);
        Task<List<RewardPayoutEntry>> GetUnpaidRewardPayoutsAsync();
        Task MarkRewardPayoutPaidAsync(int id, DateTime utc);

        // Dynamic Pricing
        Task<ItemPriceState> GetItemPriceStateAsync(ushort itemId);
        Task UpdateItemPricePressureAsync(ushort itemId, decimal sellDelta, decimal buyDelta, DateTime nowUtc, decimal recoveryPerHour);
        Task ResetItemPriceStateAsync(ushort? itemId);

        // Bounties
        Task<int> CreateBountyAsync(ulong placerSteamId, ulong targetSteamId, Money escrowAmount, Money netAmount, DateTime createdUtc);
        Task<(bool Created, Money CurrentTotal)> CreateBountyIfWithinLimitAsync(ulong placerSteamId, ulong targetSteamId, Money escrowAmount, Money netAmount, DateTime createdUtc, Money maxTotalNet);
        Task<List<BountyEntry>> GetActiveBountiesAsync(int limit = 50);
        Task<Money> GetTotalBountyOnTargetAsync(ulong targetSteamId);
        Task<(List<BountyEntry> ClaimedBounties, Money TotalAmount)> ClaimBountiesAsync(ulong targetSteamId, ulong killerSteamId);
        Task<List<BountyEntry>> GetAndMarkExpiredBountiesAsync(DateTime cutoffUtc);
        Task ConfirmBountyRefundAsync(int bountyId);

        // Daily Claims
        Task<DailyClaimResult> TryClaimDailyAsync(ulong steamId, int currentDayIndex, int maxStreakDays, DateTime nowUtc);
        Task RollbackDailyClaimAsync(ulong steamId, int previousDayIndex, int previousStreak);
    }
}
