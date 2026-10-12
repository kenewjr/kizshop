using System;
using Newtonsoft.Json;

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

        [JsonIgnore]
        public bool Available { get; set; } = true;

        public bool CanBuy => Enabled && Available && BuyPrice > 0;
        public bool CanSell => Enabled && Available && SellPrice.HasValue && SellPrice.Value > 0;
    }
}
