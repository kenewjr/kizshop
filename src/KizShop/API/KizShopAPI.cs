using System;

namespace KizShop.API
{
    public static class KizShopAPI
    {
        public static event Action<ulong, decimal, decimal, string> OnBalanceChanged;

        internal static void RaiseBalanceChanged(ulong steamId, decimal oldBalance, decimal newBalance, string reason)
        {
            OnBalanceChanged?.Invoke(steamId, oldBalance, newBalance, reason);
        }

        public static decimal GetBalance(ulong steamId)
        {
            if (KizShopPlugin.Instance == null || KizShopPlugin.Instance.Economy == null)
            {
                return 0m;
            }
            return KizShopPlugin.Instance.Economy.GetBalanceAsync(steamId).GetAwaiter().GetResult();
        }

        public static bool TryWithdraw(ulong steamId, decimal amount, string reason = "API")
        {
            if (KizShopPlugin.Instance == null || KizShopPlugin.Instance.Economy == null)
            {
                return false;
            }
            return KizShopPlugin.Instance.Economy.TryWithdrawAsync(steamId, amount, reason).GetAwaiter().GetResult();
        }

        public static void Deposit(ulong steamId, decimal amount, string reason = "API")
        {
            if (KizShopPlugin.Instance == null || KizShopPlugin.Instance.Economy == null)
            {
                return;
            }
            KizShopPlugin.Instance.Economy.DepositAsync(steamId, amount, reason).GetAwaiter().GetResult();
        }
    }
}
