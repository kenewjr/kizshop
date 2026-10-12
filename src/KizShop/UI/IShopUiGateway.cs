using System;
using System.Collections.Generic;

namespace KizShop.UI
{
    public enum ShopUiTab
    {
        Items = 0,
        Vehicles = 1
    }

    public class ShopUiPlayerState
    {
        public int Page { get; set; } = 1;
        public ShopUiTab Tab { get; set; } = ShopUiTab.Items;
        public int Quantity { get; set; } = 1;
    }

    public class ShopUiItemViewModel
    {
        public ushort Id { get; set; }
        public string Name { get; set; }
        public decimal BuyPrice { get; set; }
        public decimal? SellPrice { get; set; }
        public bool CanBuy { get; set; }
        public bool CanSell { get; set; }
    }

    public interface IShopUiGateway
    {
        System.Threading.Tasks.Task<bool> OpenUiAsync(ulong steamId, ushort effectId, short key);
        System.Threading.Tasks.Task CloseUiAsync(ulong steamId, ushort effectId, short key);
        System.Threading.Tasks.Task UpdateTextAsync(ulong steamId, short key, string elementName, string text);
        System.Threading.Tasks.Task UpdateVisibilityAsync(ulong steamId, short key, string elementName, bool visible);
    }
}
