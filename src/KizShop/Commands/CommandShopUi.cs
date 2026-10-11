using System.Collections.Generic;
using Rocket.API;
using Rocket.Unturned.Player;
using KizShop.Common;

namespace KizShop.Commands
{
    public class CommandShopUi : IRocketCommand
    {
        public AllowedCaller AllowedCaller => AllowedCaller.Player;
        public string Name => "shopui";
        public string Help => "Opens the visual interactive KizShop UI";
        public string Syntax => "";
        public List<string> Aliases => new List<string> { "sui", "vshop" };
        public List<string> Permissions => new List<string> { "kizshop.shop", "kizshop.shopui" };

        public void Execute(IRocketPlayer caller, string[] command)
        {
            CommandRunner.Run(caller, async () =>
            {
                var unturnedPlayer = (UnturnedPlayer)caller;
                var plugin = KizShopPlugin.Instance;
                if (plugin?.ShopUi == null) return;

                await plugin.ShopUi.OpenShopAsync(unturnedPlayer.CSteamID.m_SteamID).ConfigureAwait(false);
            });
        }
    }
}
