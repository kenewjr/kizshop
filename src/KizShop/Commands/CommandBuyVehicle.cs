using System.Collections.Generic;
using System.Threading.Tasks;
using Rocket.API;
using Rocket.Unturned.Chat;
using Rocket.Unturned.Player;
using KizShop.Common;

namespace KizShop.Commands
{
    public class CommandBuyVehicle : IRocketCommand
    {
        public AllowedCaller AllowedCaller => AllowedCaller.Player;
        public string Name => "buyvehicle";
        public string Help => "Buy and spawn a vehicle safely.";
        public string Syntax => "<name|id|guid>";
        public List<string> Aliases => new List<string> { "buyv", "vehicle" };
        public List<string> Permissions => new List<string> { "kizshop.vehicle", "kizshop.buy" };

        public void Execute(IRocketPlayer caller, string[] command)
        {
            CommandRunner.Run(caller, async () =>
            {
                var plugin = KizShopPlugin.Instance;
                var player = (UnturnedPlayer)caller;

                if (command.Length < 1)
                {
                    plugin.Dispatcher.QueueOnMainThread(() =>
                        KizChat.Say(caller, plugin.Translate("vehicle_usage"), UnityEngine.Color.yellow));
                    return;
                }

                string query = command[0];
                var result = await plugin.Transactions.ExecuteBuyVehicleAsync(
                    player.CSteamID.m_SteamID,
                    query,
                    node => caller.HasPermission(node),
                    caller.IsAdmin).ConfigureAwait(false);

                plugin.Dispatcher.QueueOnMainThread(() =>
                {
                    if (result.Success)
                    {
                        var costStr = plugin.FormatMoney(result.Price);
                        KizChat.Say(caller, plugin.Translate("vehicle_success", result.VehicleEntry.Name, costStr), UnityEngine.Color.green);
                    }
                    else if (result.CandidateNames != null && result.CandidateNames.Count > 0)
                    {
                        string candidates = string.Join(", ", result.CandidateNames);
                        KizChat.Say(caller, plugin.Translate("ambiguous_match", candidates), UnityEngine.Color.yellow);
                    }
                    else
                    {
                        KizChat.Say(caller, plugin.Translate(result.ErrorKey, result.ErrorArgs ?? new object[0]), UnityEngine.Color.red);
                    }
                });
            });
        }
    }
}
