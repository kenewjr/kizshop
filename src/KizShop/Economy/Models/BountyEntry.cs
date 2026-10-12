using System;
using KizShop.Common;

namespace KizShop.Economy.Models
{
    public class BountyEntry
    {
        public int Id { get; set; }
        public ulong PlacerSteamId { get; set; }
        public ulong TargetSteamId { get; set; }
        public Money EscrowAmount { get; set; }
        public Money NetAmount { get; set; }
        public DateTime CreatedUtc { get; set; }
    }
}
