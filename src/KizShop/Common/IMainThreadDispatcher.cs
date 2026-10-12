using System;
using System.Threading;
using System.Threading.Tasks;

namespace KizShop.Common
{
    public interface IMainThreadDispatcher
    {
        bool IsMainThread { get; }
        void QueueOnMainThread(Action action);
        Task<T> RunOnMainThreadAsync<T>(Func<T> func);
        Task RunOnMainThreadAsync(Action action);
    }

    public class RocketMainThreadDispatcher : IMainThreadDispatcher
    {
        private int _mainThreadId;
        private readonly Action<Action> _queueDelegate;

        public RocketMainThreadDispatcher(int? mainThreadId = null, Action<Action> queueDelegate = null)
        {
            _mainThreadId = mainThreadId ?? Thread.CurrentThread.ManagedThreadId;
            _queueDelegate = queueDelegate ?? (act => Rocket.Core.Utils.TaskDispatcher.QueueOnMainThread(act));
        }

        public void SetMainThreadId(int id)
        {
            _mainThreadId = id;
        }

        public int MainThreadId => _mainThreadId;

        public bool IsMainThread => Thread.CurrentThread.ManagedThreadId == _mainThreadId;

        public void QueueOnMainThread(Action action)
        {
            if (action == null) return;
            _queueDelegate(action);
        }

        public Task<T> RunOnMainThreadAsync<T>(Func<T> func)
        {
            if (func == null) throw new ArgumentNullException(nameof(func));

            if (IsMainThread)
            {
                try
                {
                    return Task.FromResult(func());
                }
                catch (Exception ex)
                {
                    return Task.FromException<T>(ex);
                }
            }

            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _queueDelegate(() =>
            {
                try
                {
                    tcs.SetResult(func());
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });
            return tcs.Task;
        }

        public Task RunOnMainThreadAsync(Action action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));

            if (IsMainThread)
            {
                try
                {
                    action();
                    return Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    return Task.FromException(ex);
                }
            }

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _queueDelegate(() =>
            {
                try
                {
                    action();
                    tcs.SetResult(true);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });
            return tcs.Task;
        }
    }
}
