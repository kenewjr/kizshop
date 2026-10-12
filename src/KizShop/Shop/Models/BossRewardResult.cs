using System.Collections.Generic;
using KizShop.Common;

namespace KizShop.Shop.Models
{
    public class BossRewardPayout
    {
        public ulong SteamId { get; set; }
        public Money Amount { get; set; }
        public string Role { get; set; }
        public bool Success { get; set; }
    }

    public class BossRewardResult
    {
        public bool Success { get; set; }
        public string EventKey { get; set; }
        public string BossName { get; set; }
        public Money TotalPool { get; set; }
        public List<BossRewardPayout> Payouts { get; set; } = new List<BossRewardPayout>();
        public string Message { get; set; }
    }
}
