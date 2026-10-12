using System;
using SDG.Unturned;
using KizShop.Shop.Gateways;

namespace KizShop.Shop
{
    // ponytail: Legacy helper kept for backward compatibility; internal logic uses IInventoryGateway.
    public static class InventoryHelper
    {
        public static bool CheckSpaceForItem(PlayerInventory inventory, ushort itemId, int count)
        {
            if (inventory == null) return false;
            var asset = Assets.find(EAssetType.ITEM, itemId) as ItemAsset;
            byte sizeX = asset != null && asset.size_x > 0 ? asset.size_x : (byte)1;
            byte sizeY = asset != null && asset.size_y > 0 ? asset.size_y : (byte)1;
            return UnturnedInventoryGateway.SimulatePlacement(inventory, sizeX, sizeY, count, out _);
        }
    }
}
