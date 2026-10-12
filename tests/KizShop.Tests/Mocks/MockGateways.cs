using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SDG.Unturned;
using KizShop.Common;
using KizShop.Shop.Gateways;

namespace KizShop.Tests.Mocks
{
    public class MockClock : IClock
    {
        public DateTime UtcNow { get; set; } = new DateTime(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);
    }

    public class TestMainThreadDispatcher : IMainThreadDispatcher
    {
        public int CapturedThreadId { get; set; } = System.Threading.Thread.CurrentThread.ManagedThreadId;
        public bool IsMainThread => System.Threading.Thread.CurrentThread.ManagedThreadId == CapturedThreadId;

        public void QueueOnMainThread(System.Action action)
        {
            action?.Invoke();
        }

        public Task<T> RunOnMainThreadAsync<T>(Func<T> func)
        {
            if (func == null) return Task.FromResult(default(T));
            try
            {
                return Task.FromResult(func());
            }
            catch (Exception ex)
            {
                var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
                tcs.SetException(ex);
                return tcs.Task;
            }
        }

        public Task RunOnMainThreadAsync(System.Action action)
        {
            if (action == null) return Task.CompletedTask;
            try
            {
                action();
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                tcs.SetException(ex);
                return tcs.Task;
            }
        }
    }

    public class MockInventoryGateway : IInventoryGateway
    {
        public List<InventoryItemInstance> GearItems { get; set; } = new List<InventoryItemInstance>();
        public bool HasSpace { get; set; } = true;
        public CheckSpaceReason SpaceReason { get; set; } = CheckSpaceReason.Fits;
        public int MaxDeliveryCountBeforeFailure { get; set; } = int.MaxValue;
        public bool ThrowOnDelivery { get; set; } = false;

        public List<Item> DeliveredItems { get; } = new List<Item>();
        public List<Item> RemovedByReferenceItems { get; } = new List<Item>();
        public HashSet<ushort> GunItemsWithAttachments { get; } = new HashSet<ushort>();
        public Dictionary<ushort, byte> DefaultAmounts { get; } = new Dictionary<ushort, byte>();
        public HashSet<ushort> GunItemIds { get; } = new HashSet<ushort>();
        public Dictionary<ushort, byte[]> DefaultGunStates { get; } = new Dictionary<ushort, byte[]>();
        public Func<KizShopConfiguration> ConfigProvider { get; set; }
        public bool AutoAddToGearOnDelivery { get; set; } = false;

        public Task<List<InventoryItemInstance>> GetGearItemsAsync(ulong steamId, ushort itemId)
        {
            return Task.FromResult(GearItems.Where(i => i.ItemId == itemId).ToList());
        }

        public Task<bool> HasAttachmentsOrLoadedMagazineAsync(ushort itemId, byte[] state)
        {
            if (GunItemIds.Contains(itemId) || DefaultGunStates.ContainsKey(itemId))
            {
                if (state == null || state.Length < 10) return Task.FromResult(false);

                byte[] defState = DefaultGunStates.TryGetValue(itemId, out var ds) ? (byte[])ds.Clone() : new byte[16];
                bool incMag = ConfigProvider?.Invoke()?.GunPurchaseIncludesMagazine ?? false;
                if (!incMag)
                {
                    if (defState.Length > 9)
                    {
                        defState[8] = 0;
                        defState[9] = 0;
                    }
                    if (defState.Length > 10)
                    {
                        defState[10] = 0;
                    }
                }

                for (int i = 0; i < 10; i++)
                {
                    if (state[i] != defState[i])
                    {
                        return Task.FromResult(true);
                    }
                }
                return Task.FromResult(false);
            }

            return Task.FromResult(GunItemsWithAttachments.Contains(itemId));
        }

        public Task<byte> GetDefaultAmountAsync(ushort itemId)
        {
            return Task.FromResult(DefaultAmounts.TryGetValue(itemId, out var amt) ? amt : (byte)1);
        }

        public Task<CheckSpaceResult> CheckSpaceAsync(ulong steamId, ushort itemId, int count)
        {
            if (HasSpace)
            {
                return Task.FromResult(CheckSpaceResult.Success());
            }
            return Task.FromResult(CheckSpaceResult.Fail(SpaceReason, "Mock detail"));
        }

        public Task<Item> CreateItemAsync(ushort itemId)
        {
            byte defaultAmount = DefaultAmounts.TryGetValue(itemId, out var amt) ? amt : (byte)1;
            byte[] state = new byte[16];
            if (DefaultGunStates.TryGetValue(itemId, out var defState))
            {
                state = (byte[])defState.Clone();
            }

            if (GunItemIds.Contains(itemId) || DefaultGunStates.ContainsKey(itemId))
            {
                bool incMag = ConfigProvider?.Invoke()?.GunPurchaseIncludesMagazine ?? false;
                if (!incMag)
                {
                    if (state.Length > 9)
                    {
                        state[8] = 0;
                        state[9] = 0;
                    }
                    if (state.Length > 10)
                    {
                        state[10] = 0;
                    }
                }
            }

            return Task.FromResult(new Item(itemId, defaultAmount, 100, state));
        }

        public List<bool> RecordedAutoEquipFlags { get; } = new List<bool>();

        public Task<bool> TryAddItemAsync(ulong steamId, Item item, bool autoEquip = false)
        {
            RecordedAutoEquipFlags.Add(autoEquip);
            if (ThrowOnDelivery) throw new InvalidOperationException("Simulated inventory crash");
            if (DeliveredItems.Count >= MaxDeliveryCountBeforeFailure) return Task.FromResult(false);

            DeliveredItems.Add(item);
            if (AutoAddToGearOnDelivery)
            {
                GearItems.Add(new InventoryItemInstance
                {
                    Page = PlayerInventory.BACKPACK,
                    Index = (byte)GearItems.Count,
                    Item = item
                });
            }
            return Task.FromResult(true);
        }

        public Task<bool> RemoveItemByReferenceAsync(ulong steamId, Item item)
        {
            RemovedByReferenceItems.Add(item);
            GearItems.RemoveAll(i => ReferenceEquals(i.Item, item));
            return Task.FromResult(true);
        }

        public Task<bool> RemoveItemAtAsync(ulong steamId, byte page, byte index)
        {
            var match = GearItems.FirstOrDefault(i => i.Page == page && i.Index == index);
            if (match != null)
            {
                GearItems.Remove(match);
                return Task.FromResult(true);
            }
            return Task.FromResult(false);
        }
    }

    public class MockVehicleGateway : IVehicleGateway
    {
        public int ActiveVehicleCount { get; set; } = 0;
        public bool SpawnSucceeds { get; set; } = true;
        public bool DestroyCalled { get; private set; } = false;

        public Task<int> GetPlayerVehicleCountAsync(ulong steamId) => Task.FromResult(ActiveVehicleCount);

        public Task<VehicleSpawnResult> TrySpawnVehicleAsync(ulong steamId, ushort vehicleId, float desiredDistance)
        {
            if (!SpawnSucceeds) return Task.FromResult(VehicleSpawnResult.Failed("Terrain collision"));
            ActiveVehicleCount++;
            return Task.FromResult(VehicleSpawnResult.Succeeded(new object()));
        }

        public Task DestroyVehicleAsync(object vehicleHandle)
        {
            DestroyCalled = true;
            if (ActiveVehicleCount > 0) ActiveVehicleCount--;
            return Task.CompletedTask;
        }
    }
}
