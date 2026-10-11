using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using KizShop.Common;
using KizShop.Economy;
using KizShop.Storage;
using KizShop.Tests.Mocks;

namespace KizShop.Tests
{
    public class ConcurrencyTests : IDisposable
    {
        private readonly string _dbPath;
        private readonly LiteDbStorage _storage;
        private readonly EconomyService _economy;

        public ConcurrencyTests()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"kizshop_conc_test_{Guid.NewGuid():N}.litedb");
            _storage = new LiteDbStorage(_dbPath);
            _storage.InitializeAsync().GetAwaiter().GetResult();
            _economy = new EconomyService(_storage, () => Money.Zero, () => null, true, new TestMainThreadDispatcher());
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
        public async Task ParallelWithdrawals_FiftyBalanceWithHundredWithdrawals_ExactlyFiftySucceed()
        {
            ulong steamId = 76561198000000099;
            await _economy.SetBalanceAsync(steamId, Money.FromDecimal(50m), "Initial");

            var tasks = Enumerable.Range(0, 100).Select(_ =>
                _economy.TryWithdrawAsync(steamId, Money.FromDecimal(1m), "ParallelTest")
            ).ToArray();

            var results = await Task.WhenAll(tasks);

            int successes = results.Count(r => r.Success);
            int failures = results.Count(r => !r.Success);

            Assert.Equal(50, successes);
            Assert.Equal(50, failures);

            var finalBal = await _economy.GetBalanceAsync(steamId);
            Assert.Equal(0m, finalBal.ToDecimal());

            // Storage direct balance must match cache
            var storageBal = await _storage.GetBalanceAsync(steamId, Money.Zero);
            Assert.Equal(finalBal, storageBal);
        }

        [Fact]
        public async Task ConcurrentTransfers_MaintainsTotalMoneyInvariant()
        {
            ulong userA = 76561198000000101;
            ulong userB = 76561198000000102;

            await _economy.SetBalanceAsync(userA, Money.FromDecimal(100m), "InitialA");
            await _economy.SetBalanceAsync(userB, Money.FromDecimal(100m), "InitialB");

            // Total money in system = 200m
            var tasks = new Task[50];
            for (int i = 0; i < 50; i++)
            {
                if (i % 2 == 0)
                {
                    tasks[i] = _economy.TransferAsync(userA, userB, Money.FromDecimal(2m), "AtoB");
                }
                else
                {
                    tasks[i] = _economy.TransferAsync(userB, userA, Money.FromDecimal(2m), "BtoA");
                }
            }

            await Task.WhenAll(tasks);

            var balA = await _economy.GetBalanceAsync(userA);
            var balB = await _economy.GetBalanceAsync(userB);

            Assert.Equal(200m, balA.ToDecimal() + balB.ToDecimal());
        }
    }
}
