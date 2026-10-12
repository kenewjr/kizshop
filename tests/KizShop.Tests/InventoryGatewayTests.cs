using System;
using System.Linq;
using Xunit;
using SDG.Unturned;
using KizShop.Shop.Gateways;

namespace KizShop.Tests
{
    public class InventoryGatewayTests
    {
        [Fact]
        public void StoragePages_ContainsHandsAndAllGearPages()
        {
            Assert.Contains(PlayerInventory.SLOTS, UnturnedInventoryGateway.StoragePages);    // 2 (Hands)
            Assert.Contains(PlayerInventory.BACKPACK, UnturnedInventoryGateway.StoragePages); // 3 (Backpack)
            Assert.Contains(PlayerInventory.VEST, UnturnedInventoryGateway.StoragePages);     // 4 (Vest)
            Assert.Contains(PlayerInventory.SHIRT, UnturnedInventoryGateway.StoragePages);    // 5 (Shirt)
            Assert.Contains(PlayerInventory.PANTS, UnturnedInventoryGateway.StoragePages);    // 6 (Pants)
        }

        private static PlayerInventory CreateTestInventory()
        {
            var inv = new PlayerInventory();
            var pages = new Items[PlayerInventory.PAGES];
            for (byte i = 0; i < PlayerInventory.PAGES; i++)
            {
                pages[i] = new Items(i);
            }
            var field = typeof(PlayerInventory).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .FirstOrDefault(f => f.FieldType == typeof(Items[]));
            Assert.NotNull(field);
            field.SetValue(inv, pages);
            return inv;
        }

        [Fact]
        public void CheckMultiStackSpace_SimulatesPlacementAcrossPages()
        {
            var inv = CreateTestInventory();
            Assert.NotNull(inv.items);
            Assert.NotNull(inv.items[PlayerInventory.SLOTS]);

            // Page 2 (Hands): 2x2 grid (4 slots -> can hold two 1x2 items)
            inv.items[PlayerInventory.SLOTS].resize(2, 2);
            Assert.Equal(2, inv.items[PlayerInventory.SLOTS].width);
            Assert.Equal(2, inv.items[PlayerInventory.SLOTS].height);

            // Page 3 (Backpack): 2x2 grid (4 slots -> can hold two 1x2 items)
            inv.items[PlayerInventory.BACKPACK].resize(2, 2);

            Assert.True(UnturnedInventoryGateway.SimulatePlacement(inv, 1, 2, 1, out _));
            Assert.True(UnturnedInventoryGateway.SimulatePlacement(inv, 1, 2, 2, out _));
            Assert.True(UnturnedInventoryGateway.SimulatePlacement(inv, 1, 2, 3, out _));
            Assert.True(UnturnedInventoryGateway.SimulatePlacement(inv, 1, 2, 4, out _));

            // 5 items cannot fit in 2x2 + 2x2
            Assert.False(UnturnedInventoryGateway.SimulatePlacement(inv, 1, 2, 5, out _));
        }

        [Fact]
        public void CheckMultiStackSpace_RotatesItemToFitGrid()
        {
            var inv = CreateTestInventory();

            // Hands grid: 1 wide x 3 high
            inv.items[PlayerInventory.SLOTS].resize(1, 3);

            // Item is 3 wide x 1 high (must rotate to 1x3 to fit)
            Assert.True(UnturnedInventoryGateway.SimulatePlacement(inv, 3, 1, 1, out _));
            // Two 3x1 items cannot fit in a 1x3 grid
            Assert.False(UnturnedInventoryGateway.SimulatePlacement(inv, 3, 1, 2, out _));
        }

        [Fact]
        public void CheckMultiStackSpace_RespectsExistingItemsInGrid()
        {
            var inv = CreateTestInventory();
            var hands = inv.items[PlayerInventory.SLOTS];
            hands.resize(2, 2);

            // Create ItemJar: byte new_x, byte new_y, byte new_rot, Item new_item
            // Or byte new_x, byte new_y, byte new_rot, byte new_size_x, byte new_size_y, Item new_item
            ItemJar jar = null;
            var item = (Item)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Item));
            var ctors = typeof(ItemJar).GetConstructors();
            foreach (var c in ctors)
            {
                var p = c.GetParameters();
                if (p.Length == 6)
                {
                    jar = (ItemJar)c.Invoke(new object[] { (byte)0, (byte)0, (byte)0, (byte)1, (byte)2, item });
                    break;
                }
                if (p.Length == 4)
                {
                    jar = (ItemJar)c.Invoke(new object[] { (byte)0, (byte)0, (byte)0, item });
                    jar.size_x = 1;
                    jar.size_y = 2;
                    break;
                }
            }
            Assert.NotNull(jar);
            hands.items.Add(jar);

            // Column 0 is taken. Column 1 (1,0 and 1,1) has space for one 1x2 item.
            Assert.True(UnturnedInventoryGateway.SimulatePlacement(inv, 1, 2, 1, out _));
            // A second 1x2 item cannot fit
            Assert.False(UnturnedInventoryGateway.SimulatePlacement(inv, 1, 2, 2, out _));
        }
    }
}
