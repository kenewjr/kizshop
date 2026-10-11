using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KizShop.Common;

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
    }
}
