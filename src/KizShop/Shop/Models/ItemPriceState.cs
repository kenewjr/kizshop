using System;

namespace KizShop.Shop.Models
{
    public class ItemPriceState
    {
        public ushort ItemId { get; set; }
        public decimal SellPressure { get; set; }
        public decimal BuyPressure { get; set; }
        public DateTime LastUpdatedUtc { get; set; }
    }
}
