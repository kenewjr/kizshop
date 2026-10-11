using System;
using System.Collections.Generic;
using Rocket.Unturned.Player;
using SDG.Unturned;
using Steamworks;

namespace KizShop.Shop.Gateways
{
    public class UnturnedInventoryGateway : IInventoryGateway
    {
        public static readonly byte[] StoragePages = new byte[]
        {
            PlayerInventory.SLOTS,    // 2 (Hands / base player inventory grid)
            PlayerInventory.BACKPACK, // 3
            PlayerInventory.VEST,     // 4
            PlayerInventory.SHIRT,    // 5
            PlayerInventory.PANTS     // 6
        };

        private static readonly byte[] GearPages = StoragePages;

        public List<InventoryItemInstance> GetGearItems(ulong steamId, ushort itemId)
        {
            var list = new List<InventoryItemInstance>();
            var player = UnturnedPlayer.FromCSteamID(new CSteamID(steamId));
            if (player == null || player.Inventory == null) return list;

            var inv = player.Inventory;
            var equipment = player.Player.equipment;

            foreach (byte page in StoragePages)
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
            if (stacksNeeded <= 0) return true;

            var inv = player.Inventory;

            // Fast path for 1 stack: native Unturned tryFindSpace across all valid storage pages (SLOTS..PAGES-2)
            if (stacksNeeded == 1)
            {
                if (inv.tryFindSpace(sizeX, sizeY, out _, out _, out _, out _))
                {
                    return true;
                }

                // Check auto-equip weapon into empty primary/secondary
                if (asset is ItemWeaponAsset weaponAsset)
                {
                    if (weaponAsset.slot == ESlotType.PRIMARY && (inv.items[0] == null || inv.items[0].getItemCount() == 0)) return true;
                    if (weaponAsset.slot == ESlotType.SECONDARY && (inv.items[1] == null || inv.items[1].getItemCount() == 0)) return true;
                    if (weaponAsset.slot == ESlotType.ANY && ((inv.items[0] == null || inv.items[0].getItemCount() == 0) || (inv.items[1] == null || inv.items[1].getItemCount() == 0))) return true;
                }

                // Check auto-equip clothing into empty clothing slot
                var clothing = player.Player.clothing;
                if (clothing != null && asset is ItemClothingAsset clothingAsset)
                {
                    if (clothingAsset.type == EItemType.BACKPACK && clothing.backpack == 0) return true;
                    if (clothingAsset.type == EItemType.VEST && clothing.vest == 0) return true;
                    if (clothingAsset.type == EItemType.SHIRT && clothing.shirt == 0) return true;
                    if (clothingAsset.type == EItemType.PANTS && clothing.pants == 0) return true;
                    if (clothingAsset.type == EItemType.HAT && clothing.hat == 0) return true;
                    if (clothingAsset.type == EItemType.MASK && clothing.mask == 0) return true;
                    if (clothingAsset.type == EItemType.GLASSES && clothing.glasses == 0) return true;
                }

                return false;
            }

            // Multi-stack check: simulate placement across all storage pages (Hands, Backpack, Vest, Shirt, Pants)
            return CheckMultiStackSpace(inv, sizeX, sizeY, stacksNeeded);
        }

        public static bool CheckMultiStackSpace(PlayerInventory inv, byte sizeX, byte sizeY, int stacksNeeded)
        {
            if (stacksNeeded <= 0) return true;
            if (ReferenceEquals(inv, null) || inv.items == null) return false;

            int remaining = stacksNeeded;

            foreach (byte page in StoragePages)
            {
                if (page >= inv.items.Length || inv.items[page] == null) continue;

                var pageItems = inv.items[page];
                byte w = pageItems.width;
                byte h = pageItems.height;
                if (w == 0 || h == 0) continue;

                // Build occupancy grid for this page
                bool[,] grid = new bool[w, h];
                byte itemCount = pageItems.getItemCount();
                for (byte i = 0; i < itemCount; i++)
                {
                    var jar = pageItems.getItem(i);
                    if (jar == null) continue;

                    for (int x = jar.x; x < jar.x + jar.size_x && x < w; x++)
                    {
                        for (int y = jar.y; y < jar.y + jar.size_y && y < h; y++)
                        {
                            grid[x, y] = true;
                        }
                    }
                }

                // Simulate placing items on this page
                while (remaining > 0)
                {
                    bool placed = false;

                    // Unrotated
                    for (int y = 0; y <= h - sizeY && !placed; y++)
                    {
                        for (int x = 0; x <= w - sizeX && !placed; x++)
                        {
                            if (CanPlace(grid, x, y, sizeX, sizeY))
                            {
                                Place(grid, x, y, sizeX, sizeY);
                                placed = true;
                                remaining--;
                                if (remaining <= 0) return true;
                            }
                        }
                    }

                    // Rotated (if not square)
                    if (!placed && sizeX != sizeY)
                    {
                        for (int y = 0; y <= h - sizeX && !placed; y++)
                        {
                            for (int x = 0; x <= w - sizeY && !placed; x++)
                            {
                                if (CanPlace(grid, x, y, sizeY, sizeX))
                                {
                                    Place(grid, x, y, sizeY, sizeX);
                                    placed = true;
                                    remaining--;
                                    if (remaining <= 0) return true;
                                }
                            }
                        }
                    }

                    if (!placed)
                    {
                        break;
                    }
                }
            }

            return remaining <= 0;
        }

        private static bool CanPlace(bool[,] grid, int startX, int startY, byte sizeX, byte sizeY)
        {
            for (int x = startX; x < startX + sizeX; x++)
            {
                for (int y = startY; y < startY + sizeY; y++)
                {
                    if (grid[x, y]) return false;
                }
            }
            return true;
        }

        private static void Place(bool[,] grid, int startX, int startY, byte sizeX, byte sizeY)
        {
            for (int x = startX; x < startX + sizeX; x++)
            {
                for (int y = startY; y < startY + sizeY; y++)
                {
                    grid[x, y] = true;
                }
            }
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
