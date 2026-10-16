using System.Collections.Generic;

namespace KizShop.Shop.Models
{
    public class KitItemEntry
    {
        public ushort Id { get; set; }
        public byte Amount { get; set; } = 1;

        public KitItemEntry() { }

        public KitItemEntry(ushort id, byte amount = 1)
        {
            Id = id;
            Amount = amount;
        }
    }

    public class KitShopEntry
    {
        public string Name { get; set; }
        public string DisplayName { get; set; }
        public string Description { get; set; }
        public decimal BuyPrice { get; set; }
        public bool Enabled { get; set; } = true;
        public string Permission { get; set; }
        public int CooldownSeconds { get; set; } = 0;
        public List<KitItemEntry> Items { get; set; } = new List<KitItemEntry>();
    }
}
