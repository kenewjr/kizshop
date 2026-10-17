using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using KizShop.Common;
using KizShop.Economy.Models;
using KizShop.Shop;
using KizShop.Shop.Models;

namespace KizShop.Storage
{
    public class InstrumentedKizStorage : IKizStorage
    {
        private readonly IKizStorage _inner;
        private readonly Func<int> _slowCallWarnMs;
        private readonly Action<ulong> _onTransactionLogged;
        private readonly StorageMetrics _metrics;

        public IKizStorage InnerStorage => _inner;

        public InstrumentedKizStorage(
            IKizStorage inner,
            Func<int> slowCallWarnMs,
            Action<ulong> onTransactionLogged = null,
            StorageMetrics metrics = null)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _slowCallWarnMs = slowCallWarnMs ?? (() => 500);
            _onTransactionLogged = onTransactionLogged;
            _metrics = metrics ?? StorageMetrics.Instance;
        }

        private async Task<T> MeasureAsync<T>(string operation, Func<Task<T>> action)
        {
            long start = Stopwatch.GetTimestamp();
            try
            {
                return await action().ConfigureAwait(false);
            }
            finally
            {
                double elapsedMs = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
                _metrics.Record(operation, elapsedMs, _slowCallWarnMs());
            }
        }

        private async Task MeasureAsync(string operation, Func<Task> action)
        {
            long start = Stopwatch.GetTimestamp();
            try
            {
                await action().ConfigureAwait(false);
            }
            finally
            {
                double elapsedMs = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
                _metrics.Record(operation, elapsedMs, _slowCallWarnMs());
            }
        }

        public Task InitializeAsync() => _inner.InitializeAsync();

        public Task<bool> AccountExistsAsync(ulong steamId) =>
            MeasureAsync(nameof(AccountExistsAsync), () => _inner.AccountExistsAsync(steamId));

        public Task<Money> GetBalanceAsync(ulong steamId, Money defaultStartingBalance) =>
            MeasureAsync(nameof(GetBalanceAsync), () => _inner.GetBalanceAsync(steamId, defaultStartingBalance));

        public async Task<WithdrawResult> TryWithdrawAsync(ulong steamId, Money amount, string reason, Money defaultStartingBalance)
        {
            var res = await MeasureAsync(nameof(TryWithdrawAsync), () => _inner.TryWithdrawAsync(steamId, amount, reason, defaultStartingBalance)).ConfigureAwait(false);
            if (res.Success) _onTransactionLogged?.Invoke(steamId);
            return res;
        }

        public async Task<DepositResult> DepositAsync(ulong steamId, Money amount, string reason, Money defaultStartingBalance, Money? maxBalance = null, bool ignoreMaxBalance = false)
        {
            var res = await MeasureAsync(nameof(DepositAsync), () => _inner.DepositAsync(steamId, amount, reason, defaultStartingBalance, maxBalance, ignoreMaxBalance)).ConfigureAwait(false);
            if (res.Success) _onTransactionLogged?.Invoke(steamId);
            return res;
        }

        public async Task<TransferResult> TransferAsync(ulong fromSteamId, ulong toSteamId, Money amount, string reason, Money defaultStartingBalance, Money? maxBalance = null)
        {
            var res = await MeasureAsync(nameof(TransferAsync), () => _inner.TransferAsync(fromSteamId, toSteamId, amount, reason, defaultStartingBalance, maxBalance)).ConfigureAwait(false);
            if (res.Success)
            {
                _onTransactionLogged?.Invoke(fromSteamId);
                _onTransactionLogged?.Invoke(toSteamId);
            }
            return res;
        }

        public Task<List<TopBalanceEntry>> GetTopBalancesAsync(int count) =>
            MeasureAsync(nameof(GetTopBalancesAsync), () => _inner.GetTopBalancesAsync(count));

        public async Task SetBalanceAsync(ulong steamId, Money amount, string reason)
        {
            await MeasureAsync(nameof(SetBalanceAsync), () => _inner.SetBalanceAsync(steamId, amount, reason)).ConfigureAwait(false);
            _onTransactionLogged?.Invoke(steamId);
        }

        public async Task LogTransactionAsync(ulong steamId, string type, Money amount, string details)
        {
            await MeasureAsync(nameof(LogTransactionAsync), () => _inner.LogTransactionAsync(steamId, type, amount, details)).ConfigureAwait(false);
            _onTransactionLogged?.Invoke(steamId);
        }

        public Task PruneLogsAsync(int retentionDays) =>
            MeasureAsync(nameof(PruneLogsAsync), () => _inner.PruneLogsAsync(retentionDays));

        public Task<List<TransactionRecord>> GetTransactionHistoryAsync(ulong steamId, int limit, int offset) =>
            MeasureAsync(nameof(GetTransactionHistoryAsync), () => _inner.GetTransactionHistoryAsync(steamId, limit, offset));

        public Task<List<TransactionRecord>> GetAllTransactionsAsync(int limit, int offset) =>
            MeasureAsync(nameof(GetAllTransactionsAsync), () => _inner.GetAllTransactionsAsync(limit, offset));

        public Task<List<(ulong SteamId, Money Balance)>> GetAllAccountsAsync() =>
            MeasureAsync(nameof(GetAllAccountsAsync), () => _inner.GetAllAccountsAsync());

        public Task<bool> TryRecordRewardEventAsync(string eventKey, string bossName, Money pool, DateTime utc) =>
            MeasureAsync(nameof(TryRecordRewardEventAsync), () => _inner.TryRecordRewardEventAsync(eventKey, bossName, pool, utc));

        public Task<BossRewardEventEntry> GetRewardEventAsync(string eventKey) =>
            MeasureAsync(nameof(GetRewardEventAsync), () => _inner.GetRewardEventAsync(eventKey));

        public Task RecordRewardPayoutAsync(string eventKey, ulong steamId, Money amount, string status, DateTime utc) =>
            MeasureAsync(nameof(RecordRewardPayoutAsync), () => _inner.RecordRewardPayoutAsync(eventKey, steamId, amount, status, utc));

        public Task<List<RewardPayoutEntry>> GetUnpaidRewardPayoutsAsync() =>
            MeasureAsync(nameof(GetUnpaidRewardPayoutsAsync), () => _inner.GetUnpaidRewardPayoutsAsync());

        public Task MarkRewardPayoutPaidAsync(int id, DateTime utc) =>
            MeasureAsync(nameof(MarkRewardPayoutPaidAsync), () => _inner.MarkRewardPayoutPaidAsync(id, utc));

        public Task<ItemPriceState> GetItemPriceStateAsync(ushort itemId) =>
            MeasureAsync(nameof(GetItemPriceStateAsync), () => _inner.GetItemPriceStateAsync(itemId));

        public Task UpdateItemPricePressureAsync(ushort itemId, decimal sellDelta, decimal buyDelta, DateTime nowUtc, decimal recoveryPerHour) =>
            MeasureAsync(nameof(UpdateItemPricePressureAsync), () => _inner.UpdateItemPricePressureAsync(itemId, sellDelta, buyDelta, nowUtc, recoveryPerHour));

        public Task ResetItemPriceStateAsync(ushort? itemId) =>
            MeasureAsync(nameof(ResetItemPriceStateAsync), () => _inner.ResetItemPriceStateAsync(itemId));

        public Task<int> CreateBountyAsync(ulong placerSteamId, ulong targetSteamId, Money escrowAmount, Money netAmount, DateTime createdUtc) =>
            MeasureAsync(nameof(CreateBountyAsync), () => _inner.CreateBountyAsync(placerSteamId, targetSteamId, escrowAmount, netAmount, createdUtc));

        public Task<(bool Created, Money CurrentTotal)> CreateBountyIfWithinLimitAsync(ulong placerSteamId, ulong targetSteamId, Money escrowAmount, Money netAmount, DateTime createdUtc, Money maxTotalNet) =>
            MeasureAsync(nameof(CreateBountyIfWithinLimitAsync), () => _inner.CreateBountyIfWithinLimitAsync(placerSteamId, targetSteamId, escrowAmount, netAmount, createdUtc, maxTotalNet));

        public Task<List<BountyEntry>> GetActiveBountiesAsync(int limit = 50) =>
            MeasureAsync(nameof(GetActiveBountiesAsync), () => _inner.GetActiveBountiesAsync(limit));

        public Task<Money> GetTotalBountyOnTargetAsync(ulong targetSteamId) =>
            MeasureAsync(nameof(GetTotalBountyOnTargetAsync), () => _inner.GetTotalBountyOnTargetAsync(targetSteamId));

        public Task<(List<BountyEntry> ClaimedBounties, Money TotalAmount)> ClaimBountiesAsync(ulong targetSteamId, ulong killerSteamId) =>
            MeasureAsync(nameof(ClaimBountiesAsync), () => _inner.ClaimBountiesAsync(targetSteamId, killerSteamId));

        public Task<List<BountyEntry>> GetAndMarkExpiredBountiesAsync(DateTime cutoffUtc) =>
            MeasureAsync(nameof(GetAndMarkExpiredBountiesAsync), () => _inner.GetAndMarkExpiredBountiesAsync(cutoffUtc));

        public Task ConfirmBountyRefundAsync(int bountyId) =>
            MeasureAsync(nameof(ConfirmBountyRefundAsync), () => _inner.ConfirmBountyRefundAsync(bountyId));

        public Task<bool> CanClaimDailyAsync(ulong steamId, int currentDayIndex) =>
            MeasureAsync(nameof(CanClaimDailyAsync), () => _inner.CanClaimDailyAsync(steamId, currentDayIndex));

        public Task<DailyClaimResult> TryClaimDailyAsync(ulong steamId, int currentDayIndex, int maxStreakDays, DateTime nowUtc) =>
            MeasureAsync(nameof(TryClaimDailyAsync), () => _inner.TryClaimDailyAsync(steamId, currentDayIndex, maxStreakDays, nowUtc));

        public Task RollbackDailyClaimAsync(ulong steamId, int previousDayIndex, int previousStreak) =>
            MeasureAsync(nameof(RollbackDailyClaimAsync), () => _inner.RollbackDailyClaimAsync(steamId, previousDayIndex, previousStreak));

        public Task<DateTime?> GetKitCooldownAsync(ulong steamId, string kitName) =>
            MeasureAsync(nameof(GetKitCooldownAsync), () => _inner.GetKitCooldownAsync(steamId, kitName));

        public Task SetKitCooldownAsync(ulong steamId, string kitName, DateTime lastPurchaseUtc) =>
            MeasureAsync(nameof(SetKitCooldownAsync), () => _inner.SetKitCooldownAsync(steamId, kitName, lastPurchaseUtc));

        public Task ClearKitCooldownAsync(ulong steamId, string kitName) =>
            MeasureAsync(nameof(ClearKitCooldownAsync), () => _inner.ClearKitCooldownAsync(steamId, kitName));

        public Task<int> PurgeTransactionsOlderThanAsync(DateTime cutoffUtc) =>
            MeasureAsync(nameof(PurgeTransactionsOlderThanAsync), () => _inner.PurgeTransactionsOlderThanAsync(cutoffUtc));

        public Task<int> GetAccountCountAsync() =>
            MeasureAsync(nameof(GetAccountCountAsync), () => _inner.GetAccountCountAsync());

        public Task<int> GetTransactionCountAsync() =>
            MeasureAsync(nameof(GetTransactionCountAsync), () => _inner.GetTransactionCountAsync());

        public Task<int> GetPendingPayoutCountAsync() =>
            MeasureAsync(nameof(GetPendingPayoutCountAsync), () => _inner.GetPendingPayoutCountAsync());

        public Task<int> GetPendingBountyRefundCountAsync() =>
            MeasureAsync(nameof(GetPendingBountyRefundCountAsync), () => _inner.GetPendingBountyRefundCountAsync());

        public void Dispose()
        {
            _inner.Dispose();
        }
    }
}
