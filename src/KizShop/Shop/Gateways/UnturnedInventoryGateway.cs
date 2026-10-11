using System;
using System.Collections.Generic;
using Rocket.Unturned.Player;
using SDG.Unturned;
using Steamworks;

namespace KizShop.Shop.Gateways
{
    public class UnturnedInventoryGateway : IInventoryGateway
    {
        private static readonly byte[] GearPages = new byte[]
        {
            PlayerInventory.BACKPACK, // 3
            PlayerInventory.VEST,     // 4
            PlayerInventory.SHIRT,    // 5
            PlayerInventory.PANTS     // 6
        };

        public List<InventoryItemInstance> GetGearItems(ulong steamId, ushort itemId)
        {
            var list = new List<InventoryItemInstance>();
            var player = UnturnedPlayer.FromCSteamID(new CSteamID(steamId));
            if (player == null || player.Inventory == null) return list;

            var inv = player.Inventory;
            var equipment = player.Player.equipment;

            foreach (byte page in GearPages)
            {
                if (page >= inv.items.Length || inv.items[page] == null) continue;

                var pageItems = inv.items[page];
                byte count = pageItems.getItemCount();

                for (byte i = 0; i < count; i++)
                {
                    var jar = pageItems.getItem(i);
                    if (jar == null || jar.item == null || jar.item.id != itemId) continue;

                    // Exclude equipped item
                    if (equipment != null && equipment.HasValidUseable && equipment.equippedPage == page && equipment.equipped_x == jar.x && equipment.equipped_y == jar.y)
                    {
                        continue;
                    }

                    list.Add(new InventoryItemInstance
                    {
                        Page = page,
                        Index = i,
                        Item = jar.item
                    });
                }
            }

            return list;
        }

        public bool HasAttachmentsOrLoadedMagazine(ushort itemId, byte[] state)
        {
            var asset = Assets.find(EAssetType.ITEM, itemId) as ItemGunAsset;
            if (asset == null || state == null || state.Length < 10) return false;

            ushort sight = BitConverter.ToUInt16(state, 0);
            ushort tactical = BitConverter.ToUInt16(state, 2);
            ushort grip = BitConverter.ToUInt16(state, 4);
            ushort barrel = BitConverter.ToUInt16(state, 6);
            ushort magazine = BitConverter.ToUInt16(state, 8);

            if (sight != 0 && sight != asset.sightID) return true;
            if (tactical != 0 && tactical != asset.tacticalID) return true;
            if (grip != 0 && grip != asset.gripID) return true;
            if (barrel != 0 && barrel != asset.barrelID) return true;
            if (magazine != 0) return true;

            return false;
        }

        public byte GetDefaultAmount(ushort itemId)
        {
            var asset = Assets.find(EAssetType.ITEM, itemId) as ItemAsset;
            return (asset != null && asset.amount > 0) ? asset.amount : (byte)1;
        }

        public bool CheckSpace(ulong steamId, ushort itemId, int count)
        {
            var player = UnturnedPlayer.FromCSteamID(new CSteamID(steamId));
            if (player == null || player.Inventory == null) return false;

            var asset = Assets.find(EAssetType.ITEM, itemId) as ItemAsset;
            if (asset == null) return true;

            byte sizeX = asset.size_x;
            byte sizeY = asset.size_y;
            byte maxStack = asset.amount > 0 ? asset.amount : (byte)1;
            int stacksNeeded = (int)Math.Ceiling((double)count / maxStack);

            int availableSlots = 0;
            var inv = player.Inventory;
            foreach (byte page in GearPages)
            {
                if (page >= inv.items.Length || inv.items[page] == null) continue;
                if (inv.items[page].tryFindSpace(sizeX, sizeY, out _, out _, out _))
                {
                    availableSlots++;
                    if (availableSlots >= stacksNeeded) return true;
                }
            }

            return availableSlots >= stacksNeeded;
        }

        public bool TryAddItem(ulong steamId, Item item)
        {
            var player = UnturnedPlayer.FromCSteamID(new CSteamID(steamId));
            if (player == null || player.Inventory == null || item == null) return false;

            return player.Inventory.tryAddItem(item, true);
        }

        public bool RemoveItemByReference(ulong steamId, Item item)
        {
            if (item == null) return false;
            var player = UnturnedPlayer.FromCSteamID(new CSteamID(steamId));
            if (player == null || player.Inventory == null) return false;

            var inv = player.Inventory;
            for (byte page = 0; page < inv.items.Length; page++)
            {
                var pageItems = inv.items[page];
                if (pageItems == null) continue;

                for (int i = pageItems.getItemCount() - 1; i >= 0; i--)
                {
                    var jar = pageItems.getItem((byte)i);
                    if (jar != null && ReferenceEquals(jar.item, item))
                    {
                        inv.removeItem(page, (byte)i);
                        return true;
                    }
                }
            }

            return false;
        }

        public bool RemoveItemAt(ulong steamId, byte page, byte index)
        {
            var player = UnturnedPlayer.FromCSteamID(new CSteamID(steamId));
            if (player == null || player.Inventory == null) return false;

            player.Inventory.removeItem(page, index);
            return true;
        }
    }
}
