using System;

namespace KizShop.Common
{
    public class TopBalanceEntry
    {
        public ulong SteamId { get; set; }
        public decimal Balance { get; set; }

        public TopBalanceEntry() { }

        public TopBalanceEntry(ulong steamId, decimal balance)
        {
            SteamId = steamId;
            Balance = balance;
        }
    }
}
