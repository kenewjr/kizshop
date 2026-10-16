using System;

namespace KizShop.Shop.Models
{
    public class SaleEvent
    {
        public decimal BuyMultiplier { get; set; } = 1.0m;
        public decimal SellMultiplier { get; set; } = 1.0m;
        public DateTime ExpiresUtc { get; set; }
        public string Name { get; set; } = "Happy Hour";

        public bool IsActive(DateTime nowUtc)
        {
            return nowUtc < ExpiresUtc && (BuyMultiplier != 1.0m || SellMultiplier != 1.0m);
        }
    }
}
