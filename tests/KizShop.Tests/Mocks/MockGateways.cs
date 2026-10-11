using System;
using System.Collections.Generic;
using System.Linq;
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
        public void QueueOnMainThread(System.Action action)
        {
            action?.Invoke();
        }
    }

    public class MockInventoryGateway : IInventoryGateway
    {
        public List<InventoryItemInstance> GearItems { get; set; } = new List<InventoryItemInstance>();
        public bool HasSpace { get; set; } = true;
        public int MaxDeliveryCountBeforeFailure { get; set; } = int.MaxValue;
        public bool ThrowOnDelivery { get; set; } = false;

        public List<Item> DeliveredItems { get; } = new List<Item>();
        public List<Item> RemovedByReferenceItems { get; } = new List<Item>();
        public HashSet<ushort> GunItemsWithAttachments { get; } = new HashSet<ushort>();
        public Dictionary<ushort, byte> DefaultAmounts { get; } = new Dictionary<ushort, byte>();

        public List<InventoryItemInstance> GetGearItems(ulong steamId, ushort itemId)
        {
            return GearItems.Where(i => i.ItemId == itemId).ToList();
        }

        public bool HasAttachmentsOrLoadedMagazine(ushort itemId, byte[] state)
        {
            return GunItemsWithAttachments.Contains(itemId);
        }

        public byte GetDefaultAmount(ushort itemId)
        {
            return DefaultAmounts.TryGetValue(itemId, out var amt) ? amt : (byte)1;
        }

        public bool CheckSpace(ulong steamId, ushort itemId, int count)
        {
            return HasSpace;
        }

        public bool TryAddItem(ulong steamId, Item item)
        {
            if (ThrowOnDelivery) throw new InvalidOperationException("Simulated inventory crash");
            if (DeliveredItems.Count >= MaxDeliveryCountBeforeFailure) return false;

            DeliveredItems.Add(item);
            return true;
        }

        public bool RemoveItemByReference(ulong steamId, Item item)
        {
            RemovedByReferenceItems.Add(item);
            GearItems.RemoveAll(i => ReferenceEquals(i.Item, item));
            return true;
        }

        public bool RemoveItemAt(ulong steamId, byte page, byte index)
        {
            var match = GearItems.FirstOrDefault(i => i.Page == page && i.Index == index);
            if (match != null)
            {
                GearItems.Remove(match);
                return true;
            }
            return false;
        }
    }

    public class MockVehicleGateway : IVehicleGateway
    {
        public int ActiveVehicleCount { get; set; } = 0;
        public bool SpawnSucceeds { get; set; } = true;
        public bool DestroyCalled { get; private set; } = false;

        public int GetPlayerVehicleCount(ulong steamId) => ActiveVehicleCount;

        public VehicleSpawnResult TrySpawnVehicle(ulong steamId, ushort vehicleId, float desiredDistance)
        {
            if (!SpawnSucceeds) return VehicleSpawnResult.Failed("Terrain collision");
            ActiveVehicleCount++;
            return VehicleSpawnResult.Succeeded(new object());
        }

        public void DestroyVehicle(object vehicleHandle)
        {
            DestroyCalled = true;
            if (ActiveVehicleCount > 0) ActiveVehicleCount--;
        }
    }
}
