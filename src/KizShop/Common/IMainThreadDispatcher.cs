using System;

namespace KizShop.Common
{
    public interface IMainThreadDispatcher
    {
        void QueueOnMainThread(Action action);
    }

    public class RocketMainThreadDispatcher : IMainThreadDispatcher
    {
        public void QueueOnMainThread(Action action)
        {
            if (action == null) return;
            Rocket.Core.Utils.TaskDispatcher.QueueOnMainThread(action);
        }
    }
}
