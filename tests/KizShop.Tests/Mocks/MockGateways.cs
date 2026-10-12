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

        public Task<List<InventoryItemInstance>> GetGearItemsAsync(ulong steamId, ushort itemId)
        {
            return Task.FromResult(GearItems.Where(i => i.ItemId == itemId).ToList());
        }

        public Task<bool> HasAttachmentsOrLoadedMagazineAsync(ushort itemId, byte[] state)
        {
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
            return Task.FromResult(new Item(itemId, defaultAmount, 100, new byte[16]));
        }

        public Task<bool> TryAddItemAsync(ulong steamId, Item item)
        {
            if (ThrowOnDelivery) throw new InvalidOperationException("Simulated inventory crash");
            if (DeliveredItems.Count >= MaxDeliveryCountBeforeFailure) return Task.FromResult(false);

            DeliveredItems.Add(item);
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
