using System;
using System.Collections.Generic;
using Rocket.API;
using Rocket.Core.Utils;
using Rocket.Unturned.Chat;
using Rocket.Unturned.Player;
using SDG.Unturned;

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
            var plugin = KizShopPlugin.Instance;
            if (plugin == null || plugin.Shop == null || plugin.Economy == null) return;

            var player = (UnturnedPlayer)caller;
            if (command.Length < 1)
            {
                UnturnedChat.Say(caller, plugin.Translate("vehicle_usage"), UnityEngine.Color.yellow);
                return;
            }

            string query = command[0];
            var entry = plugin.Shop.FindVehicle(query);
            if (entry == null)
            {
                UnturnedChat.Say(caller, plugin.Translate("vehicle_not_found", query), UnityEngine.Color.red);
                return;
            }

            if (!entry.CanBuy)
            {
                UnturnedChat.Say(caller, plugin.Translate("vehicle_disabled"), UnityEngine.Color.red);
                return;
            }

            if (!string.IsNullOrEmpty(entry.Permission) && !caller.HasPermission(entry.Permission) && !caller.IsAdmin)
            {
                UnturnedChat.Say(caller, plugin.Translate("vehicle_no_permission"), UnityEngine.Color.red);
                return;
            }

            decimal cost = entry.BuyPrice;

            // Check balance async
            _ = plugin.Economy.GetBalanceAsync(player.CSteamID.m_SteamID).ContinueWith(t =>
            {
                var curBal = t.Result;
                TaskDispatcher.QueueOnMainThread(async () =>
                {
                    if (curBal < cost)
                    {
                        var req = plugin.FormatMoney(cost);
                        var have = plugin.FormatMoney(curBal);
                        UnturnedChat.Say(caller, plugin.Translate("vehicle_insufficient_funds", req, entry.Name, have), UnityEngine.Color.red);
                        return;
                    }

                    // Calculate safe spawn position in front of player
                    float spawnDistance = plugin.Configuration.Instance.VehicleSpawnDistance;
                    if (spawnDistance <= 0) spawnDistance = 5.0f;

                    var forward = player.Player.transform.forward;
                    var spawnPos = player.Player.transform.position + (forward * spawnDistance) + new UnityEngine.Vector3(0, 1.0f, 0);
                    var rotation = player.Player.transform.rotation;

                    InteractableVehicle vehicle;
                    try
                    {
                        vehicle = VehicleManager.spawnLockedVehicleForPlayerV2(entry.Id, spawnPos, rotation, player.Player);
                    }
                    catch (Exception ex)
                    {
                        Rocket.Core.Logging.Logger.LogError($"[KizShop] Error spawning vehicle {entry.Id}: {ex.Message}");
                        vehicle = null;
                    }

                    if (vehicle == null)
                    {
                        UnturnedChat.Say(caller, plugin.Translate("vehicle_spawn_failed"), UnityEngine.Color.red);
                        return;
                    }

                    // Vehicle spawned, now deduct funds
                    bool deducted = await plugin.Economy.TryWithdrawAsync(player.CSteamID.m_SteamID, cost, $"ShopBuyVehicle:{entry.Id}");
                    if (!deducted)
                    {
                        // Rollback: destroy spawned vehicle to prevent free vehicle
                        VehicleManager.askVehicleDestroy(vehicle);
                        UnturnedChat.Say(caller, plugin.Translate("vehicle_spawn_failed"), UnityEngine.Color.red);
                        return;
                    }

                    var costStr = plugin.FormatMoney(cost);
                    UnturnedChat.Say(caller, plugin.Translate("vehicle_success", entry.Name, costStr), UnityEngine.Color.green);
                });
            });
        }
    }
}
