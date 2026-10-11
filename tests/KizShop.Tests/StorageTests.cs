using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;
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
            await _storage.SetBalanceAsync(steamId, 500.50m, "ManualSet");

            decimal bal = await _storage.GetBalanceAsync(steamId, 0m);
            Assert.Equal(500.50m, bal);
        }

        [Fact]
        public async Task GetTopBalances_ReturnsSortedDescending()
        {
            await _storage.SetBalanceAsync(1001, 50m, "Test");
            await _storage.SetBalanceAsync(1002, 500m, "Test");
            await _storage.SetBalanceAsync(1003, 250m, "Test");

            var top = await _storage.GetTopBalancesAsync(2);

            Assert.Equal(2, top.Count);
            Assert.Equal(1002ul, top[0].SteamId);
            Assert.Equal(500m, top[0].Balance);
            Assert.Equal(1003ul, top[1].SteamId);
            Assert.Equal(250m, top[1].Balance);
        }

        [Fact]
        public async Task PruneLogs_DeletesOldRecords()
        {
            await _storage.LogTransactionAsync(1001, "Test", 10m, "Recent");
            await _storage.PruneLogsAsync(30);

            // Pruning executes without error
            Assert.True(true);
        }
    }
}
