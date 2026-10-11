using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using KizShop.Common;
using KizShop.Storage;

namespace KizShop.Tests
{
    public class StorageTests : IDisposable
    {
        private readonly string _dbPath;
        private readonly SqliteStorage _storage;

        public StorageTests()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"kizshop_storage_test_{Guid.NewGuid():N}.db");
            _storage = new SqliteStorage(_dbPath);
            _storage.InitializeAsync().GetAwaiter().GetResult();
        }

        public void Dispose()
        {
            _storage?.Dispose();
            if (File.Exists(_dbPath))
            {
                try { File.Delete(_dbPath); } catch { }
            }
        }

        [Fact]
        public async Task SetBalance_SetsAndPersistsCorrectly()
        {
            ulong steamId = 76561198000000010;
            await _storage.SetBalanceAsync(steamId, Money.FromDecimal(500.50m), "ManualSet");

            var bal = await _storage.GetBalanceAsync(steamId, Money.Zero);
            Assert.Equal(500.50m, bal.ToDecimal());
        }

        [Fact]
        public async Task GetTopBalances_ReturnsSortedDescending()
        {
            await _storage.SetBalanceAsync(1001, Money.FromDecimal(50m), "Test");
            await _storage.SetBalanceAsync(1002, Money.FromDecimal(500m), "Test");
            await _storage.SetBalanceAsync(1003, Money.FromDecimal(250m), "Test");

            var top = await _storage.GetTopBalancesAsync(2);

            Assert.Equal(2, top.Count);
            Assert.Equal(1002ul, top[0].SteamId);
            Assert.Equal(500m, top[0].Balance.ToDecimal());
            Assert.Equal(1003ul, top[1].SteamId);
            Assert.Equal(250m, top[1].Balance.ToDecimal());
        }
    }
}
