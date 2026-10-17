namespace KizShop.Shop.Models
{
    public class CatalogSearchResult
    {
        public string Kind { get; set; } // "Item", "Vehicle", "Kit"
        public ushort Id { get; set; }
        public string Name { get; set; }
        public decimal BuyPrice { get; set; }
        public decimal SellPrice { get; set; }
    }

    public class NormalizedCatalogEntry
    {
        public string Kind { get; set; }
        public ushort Id { get; set; }
        public string Name { get; set; }
        public string NormalizedName { get; set; }
        public decimal BuyPrice { get; set; }
        public decimal SellPrice { get; set; }
        public bool Enabled { get; set; }
    }
}
