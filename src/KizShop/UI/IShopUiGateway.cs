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
        bool OpenUi(ulong steamId, ushort effectId, short key);
        void CloseUi(ulong steamId, ushort effectId, short key);
        void UpdateText(ulong steamId, short key, string elementName, string text);
        void UpdateVisibility(ulong steamId, short key, string elementName, bool visible);
    }
}
