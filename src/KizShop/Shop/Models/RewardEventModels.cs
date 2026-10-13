using System;
using KizShop.Common;

namespace KizShop.Shop.Models
{
    public class BossRewardEventEntry
    {
        public string EventKey { get; set; }
        public string BossName { get; set; }
        public Money Pool { get; set; }
        public DateTime TimestampUtc { get; set; }
    }

    public class RewardPayoutEntry
    {
        public int Id { get; set; }
        public string EventKey { get; set; }
        public ulong SteamId { get; set; }
        public Money Amount { get; set; }
        public string Status { get; set; } // "Pending", "Paid"
        public DateTime CreatedUtc { get; set; }
    }
}
