using System;
using Newtonsoft.Json;

namespace KizShop.Shop.Models
{
    public class ItemDynamicPricingConfig
    {
        public bool Enabled { get; set; } = false;
        public decimal SellDecayPerUnit { get; set; } = 0.01m;
        public decimal MinSellMultiplier { get; set; } = 0.5m;
        public decimal RecoveryPerHour { get; set; } = 0.05m;
        public decimal BuyIncreasePerUnit { get; set; } = 0.0m;
        public decimal MaxBuyMultiplier { get; set; } = 1.5m;

        public void Normalize()
        {
            if (SellDecayPerUnit < 0m) SellDecayPerUnit = 0m;
            MinSellMultiplier = Math.Clamp(MinSellMultiplier, 0m, 1m);
            if (MaxBuyMultiplier < 1m) MaxBuyMultiplier = 1m;
            if (RecoveryPerHour < 0m) RecoveryPerHour = 0m;
        }
    }

    public class ItemShopEntry
    {
        public ushort Id { get; set; }
        public string Guid { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Category { get; set; } = "General";
        public decimal BuyPrice { get; set; }
        public decimal? SellPrice { get; set; }
        public bool Enabled { get; set; } = true;
        public string Permission { get; set; } = string.Empty;
        public int? MaxAmountPerPurchase { get; set; }
        public ItemDynamicPricingConfig DynamicPricing { get; set; }

        [JsonIgnore]
        public bool Available { get; set; } = true;

        public bool CanBuy => Enabled && Available && BuyPrice > 0;
        public bool CanSell => Enabled && Available && SellPrice.HasValue && SellPrice.Value > 0;
    }
}
