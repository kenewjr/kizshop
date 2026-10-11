namespace KizShop.Common
{
    public class TopBalanceEntry
    {
        public ulong SteamId { get; set; }
        public Money Balance { get; set; }

        public TopBalanceEntry() { }

        public TopBalanceEntry(ulong steamId, Money balance)
        {
            SteamId = steamId;
            Balance = balance;
        }
    }
}
