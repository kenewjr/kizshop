using System;
using KizShop.Common;

namespace KizShop.Economy.Models
{
    public class TransactionRecord
    {
        public DateTime Timestamp { get; set; }
        public ulong SteamId { get; set; }
        public string Type { get; set; }
        public Money Amount { get; set; }
        public string Details { get; set; }
    }
}
