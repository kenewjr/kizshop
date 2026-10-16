using System.Collections.Generic;

namespace KizShop.Shop.Models
{
    public class ShopData
    {
        public List<ItemShopEntry> Items { get; set; } = new List<ItemShopEntry>();
        public List<VehicleShopEntry> Vehicles { get; set; } = new List<VehicleShopEntry>();
        public List<KitShopEntry> Kits { get; set; } = new List<KitShopEntry>();
    }
}
