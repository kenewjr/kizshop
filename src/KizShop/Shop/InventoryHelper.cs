using System;
using System.Collections.Generic;
using SDG.Unturned;

namespace KizShop.Shop
{
    public static class InventoryHelper
    {
        public static int CountItems(PlayerInventory inventory, ushort itemId)
        {
            if (inventory == null) return 0;

            int count = 0;
            var pages = inventory.items;
            if (pages == null) return 0;

            for (byte page = 0; page < pages.Length; page++)
            {
                var pageItems = pages[page];
                if (pageItems == null) continue;

                var itemCount = pageItems.getItemCount();
                for (byte index = 0; index < itemCount; index++)
                {
                    var jar = pageItems.getItem(index);
                    if (jar != null && jar.item != null && jar.item.id == itemId)
                    {
                        count += jar.item.amount;
                    }
                }
            }

            return count;
        }

        public static bool RemoveItems(PlayerInventory inventory, ushort itemId, int amountToRemove)
        {
            if (inventory == null || amountToRemove <= 0) return false;

            int totalAvailable = CountItems(inventory, itemId);
            if (totalAvailable < amountToRemove)
            {
                return false;
            }

            int remaining = amountToRemove;
            var pages = inventory.items;

            for (byte page = 0; page < pages.Length && remaining > 0; page++)
            {
                var pageItems = pages[page];
                if (pageItems == null) continue;

                // Loop backward to safely remove items without invalidating earlier indices
                for (int index = pageItems.getItemCount() - 1; index >= 0 && remaining > 0; index--)
                {
                    var jar = pageItems.getItem((byte)index);
                    if (jar == null || jar.item == null || jar.item.id != itemId) continue;

                    if (jar.item.amount <= remaining)
                    {
                        remaining -= jar.item.amount;
                        inventory.removeItem(page, (byte)index);
                    }
                    else
                    {
                        byte newAmount = (byte)(jar.item.amount - remaining);
                        jar.item.amount = newAmount;
                        pageItems.updateAmount((byte)index, newAmount);
                        remaining = 0;
                        break;
                    }
                }
            }

            return remaining == 0;
        }

        public static bool CheckSpaceForItem(PlayerInventory inventory, ushort itemId, int count)
        {
            if (inventory == null) return false;

            var asset = Assets.find(EAssetType.ITEM, itemId) as ItemAsset;
            if (asset == null) return true; // Let GiveItem handle unknown assets

            byte sizeX = asset.size_x;
            byte sizeY = asset.size_y;
            byte maxStack = asset.amount > 0 ? asset.amount : (byte)1;

            int stacksNeeded = (int)Math.Ceiling((double)count / maxStack);
            if (stacksNeeded <= 0) return true;

            if (stacksNeeded == 1)
            {
                return inventory.tryFindSpace(sizeX, sizeY, out _, out _, out _, out _);
            }

            return Gateways.UnturnedInventoryGateway.CheckMultiStackSpace(inventory, sizeX, sizeY, stacksNeeded);
        }
    }
}
