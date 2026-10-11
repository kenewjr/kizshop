using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using KizShop.Economy;
using KizShop.Storage;

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
            _economy = new EconomyService(_storage, () => 100.00m, () => 1000.00m);
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
            decimal bal = await _economy.GetBalanceAsync(steamId);

            Assert.Equal(100.00m, bal);
        }

        [Fact]
        public async Task TryWithdraw_SufficientFunds_SucceedsAndFiresEvent()
        {
            ulong steamId = 76561198000000002;
            bool eventFired = false;
            decimal oldBalance = 0;
            decimal newBalance = 0;

            _economy.OnBalanceChanged += (id, oldB, newB, reason) =>
            {
                if (id == steamId)
                {
                    eventFired = true;
                    oldBalance = oldB;
                    newBalance = newB;
                }
            };

            bool success = await _economy.TryWithdrawAsync(steamId, 40.00m, "TestWithdraw");

            Assert.True(success);
            Assert.True(eventFired);
            Assert.Equal(100.00m, oldBalance);
            Assert.Equal(60.00m, newBalance);

            decimal currentBal = await _economy.GetBalanceAsync(steamId);
            Assert.Equal(60.00m, currentBal);
        }

        [Fact]
        public async Task TryWithdraw_InsufficientFunds_Fails()
        {
            ulong steamId = 76561198000000003;
            bool success = await _economy.TryWithdrawAsync(steamId, 250.00m, "TestWithdraw");

            Assert.False(success);
            decimal currentBal = await _economy.GetBalanceAsync(steamId);
            Assert.Equal(100.00m, currentBal);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-50)]
        public async Task TryWithdraw_ZeroOrNegative_Fails(decimal invalidAmount)
        {
            ulong steamId = 76561198000000004;
            bool success = await _economy.TryWithdrawAsync(steamId, invalidAmount, "TestWithdraw");

            Assert.False(success);
        }

        [Fact]
        public async Task Deposit_EnforcesMaxBalanceCap()
        {
            ulong steamId = 76561198000000005;
            // Starting: 100, Max: 1000. Deposit 950 -> should cap at 1000.
            decimal newBal = await _economy.DepositAsync(steamId, 950.00m, "TestDeposit");

            Assert.Equal(1000.00m, newBal);
        }

        [Fact]
        public async Task Transfer_ValidAmount_TransfersAtomicallyBetweenPlayers()
        {
            ulong sender = 76561198000000006;
            ulong receiver = 76561198000000007;

            bool success = await _economy.TransferAsync(sender, receiver, 50.00m, "TestTransfer");

            Assert.True(success);
            decimal senderBal = await _economy.GetBalanceAsync(sender);
            decimal receiverBal = await _economy.GetBalanceAsync(receiver);

            Assert.Equal(50.00m, senderBal);
            Assert.Equal(150.00m, receiverBal);
        }

        [Fact]
        public async Task Transfer_SelfPay_Fails()
        {
            ulong steamId = 76561198000000008;
            bool success = await _economy.TransferAsync(steamId, steamId, 25.00m, "SelfTransfer");

            Assert.False(success);
        }
    }
}
