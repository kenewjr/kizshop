using System;

namespace KizShop.Shop.Models
{
    public class ItemShopEntry
    {
        public ushort Id { get; set; }
        public string Guid { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public decimal BuyPrice { get; set; }
        public decimal? SellPrice { get; set; }
        public bool Enabled { get; set; } = true;
        public string Permission { get; set; } = string.Empty;
        public int? MaxAmountPerPurchase { get; set; }

        public bool CanBuy => Enabled && BuyPrice > 0;
        public bool CanSell => Enabled && SellPrice.HasValue && SellPrice.Value > 0;
    }
}
