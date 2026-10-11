using System;

namespace KizShop.API
{
    /// <summary>
    /// Thin compatibility wrapper providing Uconomy-style method signatures
    /// for plugins migrating to or integrating with KizShop.
    /// </summary>
    public static class UconomyCompat
    {
        public static decimal GetBalance(string steamId)
        {
            if (ulong.TryParse(steamId, out var id))
            {
                return KizShopAPI.GetBalance(id);
            }
            return 0m;
        }

        public static bool TryWithdraw(string steamId, decimal amount)
        {
            if (ulong.TryParse(steamId, out var id))
            {
                return KizShopAPI.TryWithdraw(id, amount, "UconomyCompat");
            }
            return false;
        }

        public static void Deposit(string steamId, decimal amount)
        {
            if (ulong.TryParse(steamId, out var id))
            {
                KizShopAPI.Deposit(id, amount, "UconomyCompat");
            }
        }
    }
}
