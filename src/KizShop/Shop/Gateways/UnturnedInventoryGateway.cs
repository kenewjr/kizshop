using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Rocket.Unturned.Player;
using SDG.Unturned;
using Steamworks;
using KizShop.Common;

namespace KizShop.Shop.Gateways
{
    public class UnturnedInventoryGateway : IInventoryGateway
    {
        private readonly IMainThreadDispatcher _dispatcher;
        private readonly Func<KizShopConfiguration> _configProvider;
        private readonly Func<ulong, UnturnedPlayer> _playerResolver;

        public static readonly byte[] StoragePages = new byte[]
        {
            PlayerInventory.SLOTS,    // 2 (Hands / base player inventory grid)
            PlayerInventory.BACKPACK, // 3
            PlayerInventory.VEST,     // 4
            PlayerInventory.SHIRT,    // 5
            PlayerInventory.PANTS     // 6
        };

        public UnturnedInventoryGateway(
            IMainThreadDispatcher dispatcher = null,
            Func<KizShopConfiguration> configProvider = null,
            Func<ulong, UnturnedPlayer> playerResolver = null)
        {
            _dispatcher = dispatcher ?? KizShopPlugin.Instance?.Dispatcher ?? new RocketMainThreadDispatcher();
            _configProvider = configProvider ?? (() => KizShopPlugin.Instance?.Configuration?.Instance ?? new KizShopConfiguration());
            _playerResolver = playerResolver ?? (sId =>
            {
                try { return UnturnedPlayer.FromCSteamID(new CSteamID(sId)); }
                catch { return null; }
            });
        }

        private UnturnedPlayer GetPlayer(ulong steamId)
        {
            try
            {
                return _playerResolver(steamId);
            }
            catch
            {
                return null;
            }
        }

        private void DebugAssertMainThread(string methodName)
        {
            if (_dispatcher != null && !_dispatcher.IsMainThread)
            {
                KizLog.Error($"[KizShop] THREAD ASSERTION ERROR: {methodName} called off main thread (Thread {Thread.CurrentThread.ManagedThreadId})!");
            }
        }

        public async Task<List<InventoryItemInstance>> GetGearItemsAsync(ulong steamId, ushort itemId)
        {
            return await _dispatcher.RunOnMainThreadAsync(() =>
            {
                DebugAssertMainThread(nameof(GetGearItemsAsync));
                var list = new List<InventoryItemInstance>();
                var player = GetPlayer(steamId);
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
            }).ConfigureAwait(false);
        }

        public async Task<bool> HasAttachmentsOrLoadedMagazineAsync(ushort itemId, byte[] state)
        {
            return await _dispatcher.RunOnMainThreadAsync(() =>
            {
                DebugAssertMainThread(nameof(HasAttachmentsOrLoadedMagazineAsync));
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
            }).ConfigureAwait(false);
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

        public async Task<byte> GetDefaultAmountAsync(ushort itemId)
        {
            return await _dispatcher.RunOnMainThreadAsync(() =>
            {
                DebugAssertMainThread(nameof(GetDefaultAmountAsync));
                return GetDefaultAmount(itemId);
            }).ConfigureAwait(false);
        }

        public byte GetDefaultAmount(ushort itemId)
        {
            var asset = Assets.find(EAssetType.ITEM, itemId) as ItemAsset;
            return (asset != null && asset.amount > 0) ? asset.amount : (byte)1;
        }

        public async Task<Item> CreateItemAsync(ushort itemId)
        {
            return await _dispatcher.RunOnMainThreadAsync(() =>
            {
                DebugAssertMainThread(nameof(CreateItemAsync));
                return new Item(itemId, true);
            }).ConfigureAwait(false);
        }

        public async Task<CheckSpaceResult> CheckSpaceAsync(ulong steamId, ushort itemId, int count)
        {
            return await _dispatcher.RunOnMainThreadAsync(() =>
            {
                DebugAssertMainThread(nameof(CheckSpaceAsync));
                return CheckSpaceInternal(steamId, itemId, count);
            }).ConfigureAwait(false);
        }

        private CheckSpaceResult CheckSpaceInternal(ulong steamId, ushort itemId, int count)
        {
            if (count <= 0) return CheckSpaceResult.Success();

            var player = GetPlayer(steamId);
            if (player == null)
            {
                return CheckSpaceResult.Fail(CheckSpaceReason.PlayerNull, "Player not found");
            }

            var inv = player.Inventory;
            if (inv == null || inv.items == null)
            {
                return CheckSpaceResult.Fail(CheckSpaceReason.InventoryNull, "Player inventory is null");
            }

            var asset = Assets.find(EAssetType.ITEM, itemId) as ItemAsset;
            if (asset == null)
            {
                return CheckSpaceResult.Fail(CheckSpaceReason.AssetNotFound, $"Item asset {itemId} not found");
            }

            byte sizeX = asset.size_x > 0 ? asset.size_x : (byte)1;
            byte sizeY = asset.size_y > 0 ? asset.size_y : (byte)1;
            string assetName = asset.itemName ?? itemId.ToString();

            bool fits = SimulatePlacement(inv, sizeX, sizeY, count, out string details);

            var cfg = _configProvider();
            bool verbose = cfg.VerboseInventoryLogging;

            if (verbose)
            {
                KizLog.Info($"[KizShop] CheckSpace DEBUG: SteamID {steamId}, Item {itemId} ('{assetName}', {sizeX}x{sizeY}), Count {count}, Fits={fits}, Thread={Thread.CurrentThread.ManagedThreadId}. Details: {details}");
            }

            if (!fits)
            {
                KizLog.Warning($"[KizShop] CheckSpace NO SPACE for SteamID {steamId}, Item {itemId} ('{assetName}', {sizeX}x{sizeY}), Count {count}. Thread={Thread.CurrentThread.ManagedThreadId}. Details: {details}");
                return CheckSpaceResult.Fail(CheckSpaceReason.NoSpace, details);
            }

            return CheckSpaceResult.Success();
        }

        // NOTE: PlayerInventory.tryFindSpace cannot reserve cells across multiple instances in-memory
        // without mutating live inventory; 2D grid reservation simulation is used to guarantee accuracy.
        public static bool SimulatePlacement(
            PlayerInventory inv,
            byte sizeX,
            byte sizeY,
            int count,
            out string details)
        {
            var sb = new StringBuilder();
            if (count <= 0)
            {
                details = "Count <= 0";
                return true;
            }

            if (ReferenceEquals(inv, null) || inv.items == null)
            {
                details = "Inventory is null";
                return false;
            }

            // Build occupancy grids per storage page
            var grids = new Dictionary<byte, bool[,]>();
            var dims = new Dictionary<byte, (byte w, byte h)>();

            foreach (byte page in StoragePages)
            {
                if (page >= inv.items.Length || inv.items[page] == null) continue;
                var pageItems = inv.items[page];
                byte w = pageItems.width;
                byte h = pageItems.height;
                if (w == 0 || h == 0) continue;

                dims[page] = (w, h);
                var grid = new bool[w, h];
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

                grids[page] = grid;
                sb.Append($"[P{page}: {w}x{h}, {itemCount} items] ");
            }

            int placed = 0;

            // Simulate placing required instances one by one
            for (int i = 0; i < count; i++)
            {
                bool instancePlaced = false;

                foreach (byte page in StoragePages)
                {
                    if (!grids.TryGetValue(page, out var grid)) continue;
                    var (w, h) = dims[page];

                    // Unrotated
                    for (int y = 0; y <= h - sizeY && !instancePlaced; y++)
                    {
                        for (int x = 0; x <= w - sizeX && !instancePlaced; x++)
                        {
                            if (CanPlace(grid, x, y, sizeX, sizeY))
                            {
                                Place(grid, x, y, sizeX, sizeY);
                                instancePlaced = true;
                                placed++;
                                break;
                            }
                        }
                    }

                    // Rotated (if non-square)
                    if (!instancePlaced && sizeX != sizeY)
                    {
                        for (int y = 0; y <= h - sizeX && !instancePlaced; y++)
                        {
                            for (int x = 0; x <= w - sizeY && !instancePlaced; x++)
                            {
                                if (CanPlace(grid, x, y, sizeY, sizeX))
                                {
                                    Place(grid, x, y, sizeY, sizeX);
                                    instancePlaced = true;
                                    placed++;
                                    break;
                                }
                            }
                        }
                    }

                    if (instancePlaced) break;
                }

                if (!instancePlaced)
                {
                    sb.Append($"Failed at instance {i + 1}/{count}");
                    details = sb.ToString();
                    return false;
                }
            }

            sb.Append($"Placed {placed}/{count} instances successfully");
            details = sb.ToString();
            return true;
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

        public async Task<bool> TryAddItemAsync(ulong steamId, Item item)
        {
            return await _dispatcher.RunOnMainThreadAsync(() =>
            {
                DebugAssertMainThread(nameof(TryAddItemAsync));
                var player = GetPlayer(steamId);
                if (player == null || player.Inventory == null || item == null) return false;

                return player.Inventory.tryAddItem(item, true);
            }).ConfigureAwait(false);
        }

        public async Task<bool> RemoveItemByReferenceAsync(ulong steamId, Item item)
        {
            return await _dispatcher.RunOnMainThreadAsync(() =>
            {
                DebugAssertMainThread(nameof(RemoveItemByReferenceAsync));
                if (item == null) return false;
                var player = GetPlayer(steamId);
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
            }).ConfigureAwait(false);
        }

        public async Task<bool> RemoveItemAtAsync(ulong steamId, byte page, byte index)
        {
            return await _dispatcher.RunOnMainThreadAsync(() =>
            {
                DebugAssertMainThread(nameof(RemoveItemAtAsync));
                var player = GetPlayer(steamId);
                if (player == null || player.Inventory == null) return false;

                player.Inventory.removeItem(page, index);
                return true;
            }).ConfigureAwait(false);
        }
    }
}
