using System;
using System.Threading.Tasks;
using Rocket.API;
using Rocket.Unturned.Chat;

namespace KizShop.Common
{
    public static class CommandRunner
    {
        private static DateTime _lastErrorLog = DateTime.MinValue;
        private static readonly object _errorLock = new object();

        public static void Run(IRocketPlayer caller, Func<Task> asyncAction)
        {
            var plugin = KizShopPlugin.Instance;
            if (plugin == null || plugin.Economy == null)
            {
                string offlineMsg = plugin != null ? plugin.Translate("plugin_offline") : "KizShop is offline.";
                KizChat.Say(caller, offlineMsg, UnityEngine.Color.red);
                return;
            }

            Task.Run(async () =>
            {
                try
                {
                    await asyncAction().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    lock (_errorLock)
                    {
                        if ((DateTime.UtcNow - _lastErrorLog).TotalSeconds >= 5)
                        {
                            _lastErrorLog = DateTime.UtcNow;
                            KizLog.Error($"[KizShop] Command error for {caller?.DisplayName}: {ex}");
                        }
                    }

                    plugin.Dispatcher?.QueueOnMainThread(() =>
                    {
                        string msg = plugin.Translate("command_error_generic");
                        KizChat.Say(caller, msg, UnityEngine.Color.red);
                    });
                }
            });
        }
    }
}
