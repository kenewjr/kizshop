using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KizShop.Common;

namespace KizShop.Storage
{
    public interface IKizStorage : IDisposable
    {
        Task InitializeAsync();
        Task<decimal> GetBalanceAsync(ulong steamId, decimal defaultStartingBalance);
        Task<bool> TryWithdrawAsync(ulong steamId, decimal amount, string reason);
        Task<decimal> DepositAsync(ulong steamId, decimal amount, string reason, decimal defaultStartingBalance, decimal? maxBalance = null);
        Task<bool> TransferAsync(ulong fromSteamId, ulong toSteamId, decimal amount, string reason, decimal defaultStartingBalance, decimal? maxBalance = null);
        Task<List<TopBalanceEntry>> GetTopBalancesAsync(int count);
        Task SetBalanceAsync(ulong steamId, decimal amount, string reason);
        Task LogTransactionAsync(ulong steamId, string type, decimal amount, string details);
        Task PruneLogsAsync(int retentionDays);
    }
}
