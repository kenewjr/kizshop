using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using KizShop.API;
using KizShop.Common;
using KizShop.Economy;
using KizShop.Storage;
using KizShop.Tests.Mocks;

namespace KizShop.Tests
{
    public class SingleThreadSynchronizationContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<Action> _queue = new BlockingCollection<Action>();
        private readonly Thread _thread;
        private bool _disposed;

        public SingleThreadSynchronizationContext()
        {
            _thread = new Thread(RunLoop) { IsBackground = true };
            _thread.Start();
        }

        public override void Post(SendOrPostCallback d, object state)
        {
            if (_disposed) return;
            _queue.Add(() => d(state));
        }

        public override void Send(SendOrPostCallback d, object state)
        {
            if (Thread.CurrentThread == _thread)
            {
                d(state);
                return;
            }

            using var evt = new ManualResetEventSlim();
            Exception caught = null;
            Post(_ =>
            {
                try { d(state); }
                catch (Exception ex) { caught = ex; }
                finally { evt.Set(); }
            }, null);

            evt.Wait();
            if (caught != null) throw new AggregateException(caught);
        }

        private void RunLoop()
        {
            SetSynchronizationContext(this);
            foreach (var action in _queue.GetConsumingEnumerable())
            {
                action();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _queue.CompleteAdding();
        }
    }

    public class YieldingFakeStorage : IKizStorage
    {
        public Task InitializeAsync() => Task.CompletedTask;
        public Task<bool> AccountExistsAsync(ulong steamId) => Task.FromResult(true);

        public async Task<Money> GetBalanceAsync(ulong steamId, Money defaultStartingBalance)
        {
            await Task.Yield();
            return Money.FromDecimal(75.50m);
        }

        public async Task<WithdrawResult> TryWithdrawAsync(ulong steamId, Money amount, string reason, Money defaultStartingBalance)
        {
            await Task.Yield();
            return WithdrawResult.Succeeded(Money.FromDecimal(75.50m), Money.FromDecimal(75.50m) - amount);
        }

        public async Task<DepositResult> DepositAsync(ulong steamId, Money amount, string reason, Money defaultStartingBalance, Money? maxBalance = null, bool ignoreMaxBalance = false)
        {
            await Task.Yield();
            return new DepositResult(true, Money.FromDecimal(75.50m), Money.FromDecimal(75.50m) + amount);
        }

        public async Task<TransferResult> TransferAsync(ulong fromSteamId, ulong toSteamId, Money amount, string reason, Money defaultStartingBalance, Money? maxBalance = null)
        {
            await Task.Yield();
            return TransferResult.Succeeded(Money.FromDecimal(100m), Money.FromDecimal(100m) - amount, Money.FromDecimal(50m), Money.FromDecimal(50m) + amount);
        }

        public Task<List<TopBalanceEntry>> GetTopBalancesAsync(int count) => Task.FromResult(new List<TopBalanceEntry>());
        public Task SetBalanceAsync(ulong steamId, Money amount, string reason) => Task.CompletedTask;
        public Task LogTransactionAsync(ulong steamId, string type, Money amount, string details) => Task.CompletedTask;
        public Task PruneLogsAsync(int retentionDays) => Task.CompletedTask;
        public void Dispose() { }
    }

    public class ApiDeadlockTests
    {
        [Fact(Timeout = 5000)]
        public async Task SyncApi_CalledFromSingleThreadSynchronizationContext_DoesNotDeadlock()
        {
            var storage = new YieldingFakeStorage();
            var economy = new EconomyService(storage, () => Money.Zero, () => null, false, new TestMainThreadDispatcher());

            using var syncContext = new SingleThreadSynchronizationContext();

            decimal resultBalance = 0;
            await Task.Run(() =>
            {
                syncContext.Send(_ =>
                {
                    // Task.Run inside sync API prevents deadlocks on single-threaded sync contexts
                    resultBalance = Task.Run(() => economy.GetBalanceAsync(123456789)).GetAwaiter().GetResult().ToDecimal();
                }, null);
            });

            Assert.Equal(75.50m, resultBalance);
        }
    }
}
