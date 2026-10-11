using System.Collections.Generic;
using System.Threading.Tasks;
using Rocket.API;
using Rocket.Unturned.Chat;
using Rocket.Unturned.Player;
using KizShop.Common;

namespace KizShop.Commands
{
    public class CommandBaltop : IRocketCommand
    {
        public AllowedCaller AllowedCaller => AllowedCaller.Both;
        public string Name => "baltop";
        public string Help => "Show the top richest players.";
        public string Syntax => "";
        public List<string> Aliases => new List<string>();
        public List<string> Permissions => new List<string> { "kizshop.baltop" };

        public void Execute(IRocketPlayer caller, string[] command)
        {
            CommandRunner.Run(caller, async () =>
            {
                var plugin = KizShopPlugin.Instance;
                int count = plugin.Configuration.Instance.BaltopCount;
                if (count <= 0) count = 10;

                var list = await plugin.Economy.GetTopBalancesAsync(count).ConfigureAwait(false);

                plugin.Dispatcher.QueueOnMainThread(() =>
                {
                    if (list == null || list.Count == 0)
                    {
                        KizChat.Say(caller, plugin.Translate("baltop_empty"), UnityEngine.Color.yellow);
                        return;
                    }

                    KizChat.Say(caller, plugin.Translate("baltop_header", list.Count), UnityEngine.Color.cyan);
                    for (int i = 0; i < list.Count; i++)
                    {
                        var entry = list[i];
                        string displayName = entry.SteamId.ToString();
                        var onlinePlayer = UnturnedPlayer.FromCSteamID(new Steamworks.CSteamID(entry.SteamId));
                        if (onlinePlayer != null)
                        {
                            displayName = onlinePlayer.CharacterName;
                        }

                        var formatted = plugin.FormatMoney(entry.Balance);
                        KizChat.Say(caller, plugin.Translate("baltop_entry", i + 1, displayName, formatted), UnityEngine.Color.white);
                    }
                });
            });
        }
    }
}
