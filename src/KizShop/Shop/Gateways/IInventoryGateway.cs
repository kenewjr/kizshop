using System;
using System.Collections.Generic;
using System.Threading.Tasks;
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

    public enum CheckSpaceReason
    {
        Fits,
        PlayerNull,
        InventoryNull,
        AssetNotFound,
        NoSpace
    }

    public class CheckSpaceResult
    {
        public bool Fits { get; set; }
        public CheckSpaceReason Reason { get; set; }
        public string PageDetails { get; set; }

        public static CheckSpaceResult Success() =>
            new CheckSpaceResult { Fits = true, Reason = CheckSpaceReason.Fits };

        public static CheckSpaceResult Fail(CheckSpaceReason reason, string details = null) =>
            new CheckSpaceResult { Fits = false, Reason = reason, PageDetails = details };
    }

    public interface IInventoryGateway
    {
        Task<List<InventoryItemInstance>> GetGearItemsAsync(ulong steamId, ushort itemId);
        Task<bool> HasAttachmentsOrLoadedMagazineAsync(ushort itemId, byte[] state);
        Task<byte> GetDefaultAmountAsync(ushort itemId);
        Task<CheckSpaceResult> CheckSpaceAsync(ulong steamId, ushort itemId, int count);
        Task<Item> CreateItemAsync(ushort itemId);
        Task<bool> TryAddItemAsync(ulong steamId, Item item);
        Task<bool> RemoveItemByReferenceAsync(ulong steamId, Item item);
        Task<bool> RemoveItemAtAsync(ulong steamId, byte page, byte index);
    }
}
