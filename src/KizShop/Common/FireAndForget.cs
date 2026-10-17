using System;
using System.Threading.Tasks;

namespace KizShop.Common
{
    public static class FireAndForget
    {
        public static void Run(Func<Task> work, string context)
        {
            if (work == null) return;
            Task.Run(async () =>
            {
                try
                {
                    var task = work();
                    if (task != null)
                    {
                        await task.ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    KizLog.Error($"[KizShop] FireAndForget failed for [{context ?? "unnamed"}]: {ex}");
                }
            });
        }
    }
}
