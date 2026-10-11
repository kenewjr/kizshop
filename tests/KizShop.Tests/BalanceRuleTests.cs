using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using KizShop.Common;
using KizShop.Economy;
using KizShop.Storage;
using KizShop.Tests.Mocks;

namespace KizShop.Tests
{
    public class BalanceRuleTests : IDisposable
    {
        private readonly string _dbPath;
        private readonly SqliteStorage _storage;
        private readonly EconomyService _economy;

        public BalanceRuleTests()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"kizshop_test_{Guid.NewGuid():N}.db");
            _storage = new SqliteStorage(_dbPath);
            _storage.InitializeAsync().GetAwaiter().GetResult();
            _economy = new EconomyService(_storage, () => Money.FromDecimal(100.00m), () => Money.FromDecimal(1000.00m), true, new TestMainThreadDispatcher());
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
        public async Task GetBalance_NewPlayer_ReturnsStartingBalance()
        {
            ulong steamId = 76561198000000001;
            var bal = await _economy.GetBalanceAsync(steamId);

            Assert.Equal(100.00m, bal.ToDecimal());
        }

        [Fact]
        public async Task TryWithdraw_SufficientFunds_SucceedsAndFiresEvent()
        {
            ulong steamId = 76561198000000002;
            bool eventFired = false;
            Money oldBalance = Money.Zero;
            Money newBalance = Money.Zero;

            _economy.OnBalanceChanged += (id, oldB, newB, reason) =>
            {
                if (id == steamId)
                {
                    eventFired = true;
                    oldBalance = oldB;
                    newBalance = newB;
                }
            };

            var result = await _economy.TryWithdrawAsync(steamId, Money.FromDecimal(40.00m), "TestWithdraw");

            Assert.True(result.Success);
            Assert.True(eventFired);
            Assert.Equal(100.00m, oldBalance.ToDecimal());
            Assert.Equal(60.00m, newBalance.ToDecimal());

            var currentBal = await _economy.GetBalanceAsync(steamId);
            Assert.Equal(60.00m, currentBal.ToDecimal());
        }

        [Fact]
        public async Task TryWithdraw_InsufficientFunds_Fails()
        {
            ulong steamId = 76561198000000003;
            var result = await _economy.TryWithdrawAsync(steamId, Money.FromDecimal(250.00m), "TestWithdraw");

            Assert.False(result.Success);
            var currentBal = await _economy.GetBalanceAsync(steamId);
            Assert.Equal(100.00m, currentBal.ToDecimal());
        }

        [Fact]
        public async Task Deposit_EnforcesMaxBalanceCap()
        {
            ulong steamId = 76561198000000005;
            // Starting: 100, Max: 1000. Deposit 950 -> should cap at 1000.
            var result = await _economy.DepositAsync(steamId, Money.FromDecimal(950.00m), "TestDeposit");

            Assert.Equal(1000.00m, result.NewBalance.ToDecimal());
        }

        [Fact]
        public async Task Transfer_ValidAmount_TransfersAtomicallyBetweenPlayers()
        {
            ulong sender = 76561198000000006;
            ulong receiver = 76561198000000007;

            var result = await _economy.TransferAsync(sender, receiver, Money.FromDecimal(50.00m), "TestTransfer");

            Assert.True(result.Success);
            var senderBal = await _economy.GetBalanceAsync(sender);
            var receiverBal = await _economy.GetBalanceAsync(receiver);

            Assert.Equal(50.00m, senderBal.ToDecimal());
            Assert.Equal(150.00m, receiverBal.ToDecimal());
        }

        [Fact]
        public async Task Transfer_SelfPay_Fails()
        {
            ulong steamId = 76561198000000008;
            var result = await _economy.TransferAsync(steamId, steamId, Money.FromDecimal(25.00m), "SelfTransfer");

            Assert.False(result.Success);
        }
    }
}
