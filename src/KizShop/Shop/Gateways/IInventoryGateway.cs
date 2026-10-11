using System;
using System.Collections.Generic;
using SDG.Unturned;

namespace KizShop.Shop.Gateways
{
    public class InventoryItemInstance
    {
        public byte Page { get; set; }
        public byte Index { get; set; }
        public Item Item { get; set; }
        public ushort ItemId => Item?.id ?? 0;
        public byte Amount => Item?.amount ?? 0;
        public byte Quality => Item?.quality ?? 100;
        public byte[] State => Item?.state;
    }

    public interface IInventoryGateway
    {
        /// <summary>
        /// Retrieves unequipped items in the player's personal gear pages (backpack, vest, shirt, pants).
        /// </summary>
        List<InventoryItemInstance> GetGearItems(ulong steamId, ushort itemId);

        /// <summary>
        /// Checks if a gun has attachments or a loaded magazine.
        /// </summary>
        bool HasAttachmentsOrLoadedMagazine(ushort itemId, byte[] state);

        /// <summary>
        /// Gets the asset's default amount / stack size.
        /// </summary>
        byte GetDefaultAmount(ushort itemId);

        /// <summary>
        /// Simulates whether the player can fit the required quantity of items.
        /// </summary>
        bool CheckSpace(ulong steamId, ushort itemId, int count);

        /// <summary>
        /// Adds a single item instance to player inventory.
        /// </summary>
        bool TryAddItem(ulong steamId, Item item);

        /// <summary>
        /// Removes an item instance by reference from player inventory.
        /// </summary>
        bool RemoveItemByReference(ulong steamId, Item item);

        /// <summary>
        /// Removes an item instance at the specified page and index.
        /// </summary>
        bool RemoveItemAt(ulong steamId, byte page, byte index);
    }
}
