using System;
using System.Threading.Tasks;
using KizShop.Common;

namespace KizShop.API
{
    public static class KizShopAPI
    {
        public static event Action<ulong, decimal, decimal, string> OnBalanceChanged;

        internal static void RaiseBalanceChanged(ulong steamId, Money oldBalance, Money newBalance, string reason)
        {
            OnBalanceChanged?.Invoke(steamId, oldBalance.ToDecimal(), newBalance.ToDecimal(), reason);
        }

        // --- Synchronous methods (deadlock-proof, cache-first for reads) ---

        public static decimal GetBalance(ulong steamId)
        {
            if (KizShopPlugin.Instance == null || KizShopPlugin.Instance.Economy == null)
            {
                return 0m;
            }

            // Cache-first fast path
            var cached = KizShopPlugin.Instance.Economy.GetCachedBalance(steamId);
            if (cached.Cents > 0)
            {
                return cached.ToDecimal();
            }

            return Task.Run(() => KizShopPlugin.Instance.Economy.GetBalanceAsync(steamId)).GetAwaiter().GetResult().ToDecimal();
        }

        public static bool TryWithdraw(ulong steamId, decimal amount, string reason = "API")
        {
            if (KizShopPlugin.Instance == null || KizShopPlugin.Instance.Economy == null)
            {
                return false;
            }

            var maxTx = KizShopPlugin.Instance.Configuration.Instance.MaxTransactionAmount;
            if (!Money.TryFromDecimal(amount, maxTx, out var money, out _))
            {
                return false;
            }

            return Task.Run(() => KizShopPlugin.Instance.Economy.TryWithdrawAsync(steamId, money, reason)).GetAwaiter().GetResult().Success;
        }

        public static void Deposit(ulong steamId, decimal amount, string reason = "API")
        {
            if (KizShopPlugin.Instance == null || KizShopPlugin.Instance.Economy == null)
            {
                return;
            }

            var maxTx = KizShopPlugin.Instance.Configuration.Instance.MaxTransactionAmount;
            if (!Money.TryFromDecimal(amount, maxTx, out var money, out _))
            {
                return;
            }

            Task.Run(() => KizShopPlugin.Instance.Economy.DepositAsync(steamId, money, reason)).GetAwaiter().GetResult();
        }

        // --- Recommended Asynchronous Methods ---

        public static async Task<decimal> GetBalanceAsync(ulong steamId)
        {
            if (KizShopPlugin.Instance == null || KizShopPlugin.Instance.Economy == null)
            {
                return 0m;
            }
            var m = await KizShopPlugin.Instance.Economy.GetBalanceAsync(steamId).ConfigureAwait(false);
            return m.ToDecimal();
        }

        public static async Task<bool> TryWithdrawAsync(ulong steamId, decimal amount, string reason = "API")
        {
            if (KizShopPlugin.Instance == null || KizShopPlugin.Instance.Economy == null)
            {
                return false;
            }

            var maxTx = KizShopPlugin.Instance.Configuration.Instance.MaxTransactionAmount;
            if (!Money.TryFromDecimal(amount, maxTx, out var money, out _))
            {
                return false;
            }

            var result = await KizShopPlugin.Instance.Economy.TryWithdrawAsync(steamId, money, reason).ConfigureAwait(false);
            return result.Success;
        }

        public static async Task<decimal> DepositAsync(ulong steamId, decimal amount, string reason = "API")
        {
            if (KizShopPlugin.Instance == null || KizShopPlugin.Instance.Economy == null)
            {
                return 0m;
            }

            var maxTx = KizShopPlugin.Instance.Configuration.Instance.MaxTransactionAmount;
            if (!Money.TryFromDecimal(amount, maxTx, out var money, out _))
            {
                return 0m;
            }

            var result = await KizShopPlugin.Instance.Economy.DepositAsync(steamId, money, reason).ConfigureAwait(false);
            return result.NewBalance.ToDecimal();
        }
    }
}
